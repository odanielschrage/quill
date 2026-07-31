using NAudio.Wave;

namespace Quill.Audio;

/// Streams one mono 16 kHz track to a WAV file, keeping the file's timeline
/// honest against elapsed time.
///
/// The timeline part is the whole point. WASAPI loopback stops delivering
/// buffers entirely while nothing is playing, unlike the macOS process tap which
/// hands over continuous audio. Left alone, every silence in a meeting would
/// collapse: the system track would come out shorter than real time, its
/// timestamps would drift out of step with the mic track, and merging the two by
/// timestamp — which is how quill gets two-party diarization for free — would
/// interleave the wrong speakers.
///
/// So this keeps a ledger. Each incoming buffer is compared against where the
/// clock says the timeline should be, and any shortfall beyond
/// GapTolerance is closed with digital silence before the buffer is written.
/// A silence that never ends before Stop() is closed by FinishPadding().
///
/// Silence *before* the first buffer needs no ledger: RecordingSession records
/// each track's FirstBufferAt and writes the skew to meta.json as
/// start_offset_ms, so a track that only starts producing audio ten minutes in
/// is shifted correctly at merge time.
internal sealed class TrackWriter : IDisposable
{
    public const int SampleRate = 16_000;

    /// Well under any conversational pause, well over thread-scheduling jitter
    /// and resampler latency — so real silence is padded and normal buffer
    /// timing never is.
    private static readonly TimeSpan GapTolerance = TimeSpan.FromMilliseconds(250);

    /// A crash loses at most this much. WAV can't be finalized incrementally the
    /// way CAF can, so the recovery story is rebuilding the RIFF header of a
    /// truncated file — which needs the samples to actually be on disk.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly WaveFileWriter _writer;
    private readonly IMonotonicClock _clock;
    private readonly string _label;

    private long _samplesWritten;
    private TimeSpan _lastFlush;
    private byte[] _bytes = [];

    /// Wall-clock time of the first captured buffer — the track's true start,
    /// used to offset-align the two tracks' transcript timestamps.
    public DateTimeOffset? FirstBufferAt { get; private set; }

    /// Peak amplitude over the first second of real audio. A route that hands
    /// over callbacks full of digital zeros is the failure rca-001 was about;
    /// on Windows it usually means a muted or privacy-blocked device.
    public float LivenessPeak { get; private set; }
    public bool LivenessSettled { get; private set; }

    /// Silence inserted to keep the timeline honest — surfaced for diagnostics
    /// and asserted on by the gap tests.
    public long SamplesPadded { get; private set; }

    /// Samples that arrived at or beyond full scale. A clipped track transcribes
    /// as noise, and the transcript gives no hint why — Whisper just emits
    /// "(speaking in foreign language)" or nonsense. Worth telling the user
    /// while they can still fix their levels.
    public long SamplesClipped { get; private set; }

    /// Fraction of real (non-padded) audio that clipped.
    public double ClippedFraction
    {
        get
        {
            var real = _samplesWritten - SamplesPadded;
            return real <= 0 ? 0 : (double)SamplesClipped / real;
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samplesWritten / SampleRate);

    public TrackWriter(string path, string label, IMonotonicClock? clock = null)
    {
        _label = label;
        _clock = clock ?? new StopwatchClock();
        _writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
    }

    /// Append mono float samples, closing any timeline gap first.
    public void Write(ReadOnlySpan<float> mono)
    {
        if (mono.IsEmpty) return;

        if (FirstBufferAt is null)
        {
            // The buffer describes audio captured a few milliseconds ago, so the
            // clock starts marginally late. That bias is constant and an order of
            // magnitude below GapTolerance.
            FirstBufferAt = DateTimeOffset.Now;
            _clock.Restart();
        }
        else
        {
            PadIfBehind(mono.Length);
        }

        TrackLiveness(mono);
        WriteSamples(mono);
        FlushIfDue();
    }

    /// Close a trailing silence so the file's length matches the session's.
    /// Nothing downstream depends on it — merge alignment comes from the leading
    /// offset — but two tracks of equal length is what anyone inspecting the
    /// folder expects.
    public void FinishPadding()
    {
        if (FirstBufferAt is null) return;
        PadIfBehind(0);
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    // MARK: -

    private void PadIfBehind(int incomingSamples)
    {
        var expected = (long)(_clock.Elapsed.TotalSeconds * SampleRate);
        var deficit = expected - incomingSamples - _samplesWritten;
        if (deficit <= (long)(GapTolerance.TotalSeconds * SampleRate)) return;

        WriteZeros(deficit);
        SamplesPadded += deficit;
    }

    private void WriteZeros(long samples)
    {
        // One second at a time: a long gap shouldn't mean a huge allocation.
        var chunk = new byte[SampleRate * 2];
        while (samples > 0)
        {
            var take = (int)Math.Min(samples, SampleRate);
            _writer.Write(chunk, 0, take * 2);
            _samplesWritten += take;
            samples -= take;
        }
    }

    private void TrackLiveness(ReadOnlySpan<float> mono)
    {
        if (LivenessSettled) return;
        foreach (var sample in mono)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > LivenessPeak) LivenessPeak = magnitude;
        }
        if (_samplesWritten + mono.Length >= SampleRate) LivenessSettled = true;
    }

    private void WriteSamples(ReadOnlySpan<float> mono)
    {
        var needed = mono.Length * 2;
        if (_bytes.Length < needed) _bytes = new byte[needed];

        for (var i = 0; i < mono.Length; i++)
        {
            var value = Math.Clamp(mono[i], -1f, 1f);
            if (Math.Abs(value) >= 0.999f) SamplesClipped++;
            var sample = (short)(value * short.MaxValue);
            _bytes[i * 2] = (byte)(sample & 0xFF);
            _bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        _writer.Write(_bytes, 0, needed);
        _samplesWritten += mono.Length;
    }

    private void FlushIfDue()
    {
        var now = _clock.Elapsed;
        if (now - _lastFlush < FlushInterval) return;
        _lastFlush = now;
        try
        {
            _writer.Flush();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{_label} track flush failed: {e.Message}");
        }
    }
}
