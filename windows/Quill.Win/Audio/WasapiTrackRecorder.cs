using NAudio.CoreAudioApi;
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
internal abstract class WasapiTrackRecorder : IAudioRecorder
{
    private readonly object _gate = new();
    private readonly IMonotonicClock? _clock;

    private WasapiCapture? _capture;
    private TrackWriter? _writer;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _pipeline;
    // Fully qualified: with WinForms in scope later, a bare Timer is ambiguous.
    private System.Threading.Timer? _livenessTimer;

    private DateTimeOffset? _firstBufferAt;
    private long _samplesPadded;

    /// One second of mono at the output rate — the resampler never produces more
    /// than that from a single capture buffer.
    private readonly float[] _mono = new float[TrackWriter.SampleRate];

    protected WasapiTrackRecorder(IMonotonicClock? clock = null) => _clock = clock;

    public bool IsRecording { get; private set; }

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

    /// Create the endpoint capture. Called once per Start.
    protected abstract WasapiCapture CreateCapture();

    /// Whether a first second of digital silence is worth warning about. True
    /// for the mic, where it means a muted or blocked device; false for
    /// loopback, where silence just means nothing was playing.
    protected virtual bool WarnOnSilentStart => false;

    public void Start(string path)
    {
        if (IsRecording) return;

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

        TrackWriter writer;
        try
        {
            writer = new TrackWriter(path, Label, _clock);
        }
        catch (Exception e)
        {
            capture.Dispose();
            throw new IOException($"{Label} file creation failed: {e.Message}", e);
        }

        lock (_gate)
        {
            _buffered = buffered;
            _pipeline = chain;
            _writer = writer;
        }

        capture.DataAvailable += OnDataAvailable;
        try
        {
            capture.StartRecording();
        }
        catch (Exception e)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            lock (_gate)
            {
                writer.Dispose();
                _writer = null;
                _buffered = null;
                _pipeline = null;
            }
            throw new IOException($"{Label} capture start failed: {e.Message}", e);
        }

        _capture = capture;
        IsRecording = true;

        Console.Error.WriteLine(
            $"{Label}: {format.SampleRate} Hz {format.Channels}ch {format.Encoding} "
            + $"→ {TrackWriter.SampleRate} Hz mono");

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

        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
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
                _writer.Dispose();
                _writer = null;
            }
            _buffered = null;
            _pipeline = null;
        }
    }

    public void Dispose() => Stop();

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
}

/// The default input device.
internal sealed class MicRecorder(IMonotonicClock? clock = null) : WasapiTrackRecorder(clock)
{
    protected override string Label => "mic";
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
    protected override WasapiCapture CreateCapture() => new WasapiLoopbackCapture();
}
