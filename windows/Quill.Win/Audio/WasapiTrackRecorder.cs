using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Quill.Audio;

/// Shared WASAPI capture plumbing for both tracks: take whatever mix format the
/// endpoint reports, fold it down to mono 16 kHz, and stream it through a
/// TrackWriter.
///
/// 16 kHz mono is chosen rather than the device rate because it is exactly what
/// the ASR consumes — resampling once at capture beats storing three times the
/// bytes and resampling again at transcription time.
///
/// The conversion runs inside the capture callback rather than on a worker: the
/// work is a downmix plus a resample on ~10 ms buffers, and doing it inline
/// keeps a single ordered writer with no second queue to reason about.
///
/// The capture is reopened when the audio device changes underneath it — see
/// RequestRestart. The TrackWriter deliberately outlives that: the file, the
/// ledger and FirstBufferAt all survive, so a device switch becomes a gap the
/// ledger fills rather than the end of the track.
internal abstract class WasapiTrackRecorder : IAudioRecorder
{
    private const int MaxRestartAttempts = 5;

    /// Windows needs a moment after an endpoint changes before the new default is
    /// reported and openable; reopening instantly just fails.
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(400);

    private readonly object _gate = new();
    private readonly IMonotonicClock? _clock;

    private WasapiCapture? _capture;
    private TrackWriter? _writer;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _pipeline;
    // Fully qualified: with WinForms in scope, a bare Timer is ambiguous.
    private System.Threading.Timer? _livenessTimer;
    private MMDeviceEnumerator? _enumerator;
    private DeviceWatcher? _watcher;

    private int _restarting;
    private DateTimeOffset? _firstBufferAt;
    private long _samplesPadded;

    /// One second of mono at the output rate — the resampler never produces more
    /// than that from a single capture buffer.
    private readonly float[] _mono = new float[TrackWriter.SampleRate];

    protected WasapiTrackRecorder(IMonotonicClock? clock = null) => _clock = clock;

    public bool IsRecording { get; private set; }

    /// How many times the capture was reopened because the device changed.
    public int RestartCount { get; private set; }

    /// Deliberately outlives Stop(): RecordingSession stops both tracks and only
    /// then reads the two skews to write start_offset_ms. Reading this straight
    /// off the (disposed) writer reported null there, which silently collapsed
    /// both offsets to zero and mis-aligned the merged transcript.
    public DateTimeOffset? FirstBufferAt
    {
        get { lock (_gate) { return _writer?.FirstBufferAt ?? _firstBufferAt; } }
    }

    /// Silence inserted to keep the timeline honest. Diagnostics only, and
    /// likewise readable after Stop().
    public long SamplesPadded
    {
        get { lock (_gate) { return _writer?.SamplesPadded ?? _samplesPadded; } }
    }

    /// Short name used in log lines: "mic" or "system".
    protected abstract string Label { get; }

    /// Which side of the audio graph this track follows, so a change of *that*
    /// default is what triggers a reopen.
    protected abstract DataFlow Flow { get; }

    /// Create the endpoint capture. Called on start and again on every reopen,
    /// so it must resolve the current default device each time rather than
    /// caching one.
    protected abstract WasapiCapture CreateCapture();

    /// Whether a first second of digital silence is worth warning about. True
    /// for the mic, where it means a muted or blocked device; false for
    /// loopback, where silence just means nothing was playing.
    protected virtual bool WarnOnSilentStart => false;

    public void Start(string path)
    {
        if (IsRecording) return;

        TrackWriter writer;
        try
        {
            writer = new TrackWriter(path, Label, _clock);
        }
        catch (Exception e)
        {
            throw new IOException($"{Label} file creation failed: {e.Message}", e);
        }

        lock (_gate) { _writer = writer; }
        IsRecording = true;

        try
        {
            OpenCapture();
        }
        catch
        {
            IsRecording = false;
            lock (_gate)
            {
                writer.Dispose();
                _writer = null;
            }
            throw;
        }

        WatchForDeviceChanges();

        if (WarnOnSilentStart)
        {
            _livenessTimer = new System.Threading.Timer(
                _ => CheckLiveness(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;

        _livenessTimer?.Dispose();
        _livenessTimer = null;

        StopWatchingDeviceChanges();
        CloseCapture();

        lock (_gate)
        {
            if (_writer is not null)
            {
                _writer.FinishPadding();

                // Latch both before the writer goes away — see FirstBufferAt.
                _firstBufferAt = _writer.FirstBufferAt;
                _samplesPadded = _writer.SamplesPadded;

                if (_samplesPadded > 0)
                {
                    var seconds = (double)_samplesPadded / TrackWriter.SampleRate;
                    Console.Error.WriteLine(
                        $"{Label}: padded {seconds:F1}s of silence to keep the timeline aligned");
                }

                // A clipped track transcribes as noise and the transcript gives no
                // hint why, so this is worth interrupting someone over — it is
                // usually a fixable level, or acoustic feedback between two
                // devices in the same room.
                var clipped = _writer.ClippedFraction;
                if (clipped > 0.005)
                {
                    Console.Error.WriteLine(
                        $"{Label}: {clipped:P1} of the track clipped — the transcript will suffer");
                    Notify.User(
                        $"quill — {Label} audio was too loud",
                        $"{clipped:P0} of the track clipped; lower the volume, and use headphones "
                        + "if two devices are in the same room");
                }

                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose() => Stop();

    // MARK: - capture lifecycle

    private void OpenCapture()
    {
        var capture = CreateCapture();
        var format = capture.WaveFormat;

        var buffered = new BufferedWaveProvider(format)
        {
            // ReadFully: false is what lets the drain loop below terminate —
            // otherwise the provider pads with silence forever and Read never
            // reports the buffer as empty.
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
        };

        ISampleProvider chain = buffered.ToSampleProvider();
        if (format.Channels > 1) chain = new DownmixSampleProvider(chain);
        if (format.SampleRate != TrackWriter.SampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, TrackWriter.SampleRate);
        }

        lock (_gate)
        {
            _buffered = buffered;
            _pipeline = chain;
        }

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        try
        {
            capture.StartRecording();
        }
        catch (Exception e)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            throw new IOException($"{Label} capture start failed: {e.Message}", e);
        }

        _capture = capture;

        Console.Error.WriteLine(
            $"{Label}: {format.SampleRate} Hz {format.Channels}ch {format.Encoding} "
            + $"→ {TrackWriter.SampleRate} Hz mono");
    }

    /// Tear down the capture only. The writer, the file and the ledger stay.
    private void CloseCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            // Unsubscribe before stopping: StopRecording raises RecordingStopped,
            // which would otherwise be read as a device failure and start another
            // reopen on top of this one.
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.StopRecording();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{Label} capture stop failed: {e.Message}");
            }
            capture.Dispose();
        }

        lock (_gate)
        {
            _buffered = null;
            _pipeline = null;
        }
    }

    /// Reopen the capture on whatever the default device is now.
    ///
    /// Unplugging a headset mid-meeting is routine on Windows, and there are two
    /// distinct failures. The device can be invalidated, which surfaces through
    /// RecordingStopped. Or the *default* can move while the old device stays
    /// perfectly valid — plug in headphones and loopback keeps dutifully
    /// recording the speakers, which are now silent. The second one is worse
    /// because nothing errors; the track just goes quiet.
    ///
    /// Either way the gap becomes silence the ledger fills, so the track stays
    /// the right length and the two tracks keep sharing a clock.
    internal void RequestRestart(string reason)
    {
        if (!IsRecording) return;
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                Console.Error.WriteLine($"{Label}: {reason} — reopening capture");
                CloseCapture();

                for (var attempt = 1; attempt <= MaxRestartAttempts; attempt++)
                {
                    Thread.Sleep(RestartDelay);
                    if (!IsRecording) return;
                    try
                    {
                        OpenCapture();
                        RestartCount++;
                        Console.Error.WriteLine($"{Label}: capture reopened");
                        return;
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine(
                            $"{Label}: reopen attempt {attempt} failed ({e.Message})");
                    }
                }

                // The track is not lost: it keeps its length because Stop() pads
                // to the elapsed time, so the other track's timestamps still line
                // up. It just has nothing in it from here on.
                Console.Error.WriteLine(
                    $"{Label}: gave up reopening — the rest of this track will be silence");
                Notify.User(
                    "quill — audio device lost",
                    $"the {Label} track stopped recording; the session is still running");
            }
            finally
            {
                Interlocked.Exchange(ref _restarting, 0);
            }
        });
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // A stop we asked for has already unsubscribed this handler, so reaching
        // here means the device went away underneath us.
        if (!IsRecording) return;
        RequestRestart(e.Exception is { } error
            ? $"capture stopped ({error.Message})"
            : "capture stopped unexpectedly");
    }

    private void WatchForDeviceChanges()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _watcher = new DeviceWatcher(Flow, () => RequestRestart("default device changed"));
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch (Exception e)
        {
            // Losing the notification only costs the second failure mode; the
            // RecordingStopped path still covers a device that dies outright.
            Console.Error.WriteLine(
                $"warning: {Label} can't watch for device changes ({e.Message})");
        }
    }

    private void StopWatchingDeviceChanges()
    {
        try
        {
            if (_enumerator is not null && _watcher is not null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_watcher);
            }
            _enumerator?.Dispose();
        }
        catch (Exception)
        {
            // Shutting down; nothing useful to do about it.
        }
        _enumerator = null;
        _watcher = null;
    }

    // MARK: -

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (!IsRecording || _buffered is null || _pipeline is null || _writer is null) return;

            try
            {
                if (e.BytesRecorded > 0)
                {
                    _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }

                // Drain everything the resampler can produce; it keeps its own
                // phase across calls, so gaps are closed by the writer's ledger
                // rather than by resetting the chain.
                int read;
                while ((read = _pipeline.Read(_mono, 0, _mono.Length)) > 0)
                {
                    _writer.Write(_mono.AsSpan(0, read));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Label} track write failed: {ex.Message}");
            }
        }
    }

    private void CheckLiveness()
    {
        bool silent;
        lock (_gate)
        {
            // Nothing arrived at all, or a full second of digital zeros.
            silent = _writer is not null && _writer.LivenessPeak == 0;
        }
        if (!silent) return;

        Console.Error.WriteLine(
            $"warning: {Label} delivered a second of digital silence — "
            + "check Settings → Privacy → Microphone, and that the device isn't muted");
        Notify.User("quill — microphone may be muted", "the mic track is recording silence");
    }

    /// Watches only the default this track follows, and only the Console role —
    /// Windows raises the notification once per role, and acting on all three
    /// would trigger three reopens for one change.
    internal sealed class DeviceWatcher(DataFlow flow, Action onDefaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
        {
            if (dataFlow == flow && role == Role.Console) onDefaultChanged();
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

/// The default input device.
internal sealed class MicRecorder(IMonotonicClock? clock = null) : WasapiTrackRecorder(clock)
{
    protected override string Label => "mic";
    protected override DataFlow Flow => DataFlow.Capture;
    protected override bool WarnOnSilentStart => true;
    protected override WasapiCapture CreateCapture() => new WasapiCapture();
}

/// Everything the machine is playing, via WASAPI loopback on the default render
/// device. Unlike the macOS Core Audio process tap this needs no consent prompt,
/// no aggregate device, and no entry in an Info.plist — but it also stops
/// delivering buffers during silence, which is what TrackWriter's ledger exists
/// to correct.
///
/// Like the macOS build, the capture is global: notification dings and music land
/// in the track too. Per-process loopback would need Windows 10 build 20348 or
/// later.
internal sealed class SystemAudioRecorder(IMonotonicClock? clock = null) : WasapiTrackRecorder(clock)
{
    protected override string Label => "system";
    protected override DataFlow Flow => DataFlow.Render;
    protected override WasapiCapture CreateCapture() => new WasapiLoopbackCapture();
}
