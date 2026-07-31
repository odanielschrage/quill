using NAudio.Wave;
using Quill.Audio;
using Xunit;

namespace Quill.Tests;

/// A controllable stand-in for the capture Stopwatch, so the gap-filling tests
/// cover a 60-second scenario in milliseconds.
internal sealed class FakeClock : IMonotonicClock
{
    public TimeSpan Elapsed { get; private set; }
    public void Restart() => Elapsed = TimeSpan.Zero;
    public void Advance(TimeSpan by) => Elapsed += by;
}

internal sealed class ArraySampleProvider(WaveFormat format, float[] data) : ISampleProvider
{
    private int _position;

    public WaveFormat WaveFormat => format;

    public int Read(float[] buffer, int offset, int count)
    {
        var take = Math.Min(count, data.Length - _position);
        if (take <= 0) return 0;
        Array.Copy(data, _position, buffer, offset, take);
        _position += take;
        return take;
    }
}

public sealed class TrackWriterTests : IDisposable
{
    private const int Rate = TrackWriter.SampleRate;
    private static readonly TimeSpan Chunk = TimeSpan.FromMilliseconds(100);

    private readonly string _root = Temp.Dir();
    private readonly FakeClock _clock = new();

    public void Dispose() => Temp.Nuke(_root);

    /// The R1 acceptance case: a 60-second session with audio only in seconds
    /// 0–5 and 50–55 must produce a ~60s track, not a ~10s one. Without the
    /// ledger the silence collapses and the system track's timestamps drift out
    /// of step with the mic's, which is what would break speaker attribution.
    [Fact]
    public void SilenceGapsArePaddedSoTheTimelineMatchesRealTime()
    {
        var path = Path.Combine(_root, "system.wav");
        long padded;
        using (var writer = new TrackWriter(path, "system", _clock))
        {
            Play(writer, TimeSpan.FromSeconds(5));   // 0s → 5s
            Idle(TimeSpan.FromSeconds(45));          // 5s → 50s, no callbacks at all
            Play(writer, TimeSpan.FromSeconds(5));   // 50s → 55s
            Idle(TimeSpan.FromSeconds(5));           // 55s → 60s
            writer.FinishPadding();
            padded = writer.SamplesPadded;
        }

        Assert.Equal(60.0, Duration(path).TotalSeconds, precision: 1);

        // 10s of real audio, 50s of inserted silence.
        Assert.Equal(50.0, (double)padded / Rate, precision: 1);
    }

    [Fact]
    public void ContinuousAudioIsNeverPadded()
    {
        var path = Path.Combine(_root, "mic.wav");
        long padded;
        using (var writer = new TrackWriter(path, "mic", _clock))
        {
            Play(writer, TimeSpan.FromSeconds(10));
            writer.FinishPadding();
            padded = writer.SamplesPadded;
        }

        // Normal buffer timing and resampler latency must not look like a gap.
        Assert.Equal(0, padded);
        Assert.Equal(10.0, Duration(path).TotalSeconds, precision: 1);
    }

    [Fact]
    public void TrailingSilenceIsClosedOnStop()
    {
        var path = Path.Combine(_root, "system.wav");
        using (var writer = new TrackWriter(path, "system", _clock))
        {
            Play(writer, TimeSpan.FromSeconds(2));
            Idle(TimeSpan.FromSeconds(8)); // playback stopped, then Stop() arrives
            writer.FinishPadding();
        }

        Assert.Equal(10.0, Duration(path).TotalSeconds, precision: 1);
    }

    /// Silence *before* the first buffer is not the ledger's job — meta.json's
    /// start_offset_ms carries that skew, so padding it here would double-count.
    [Fact]
    public void SilenceBeforeTheFirstBufferIsNotPadded()
    {
        var path = Path.Combine(_root, "system.wav");
        DateTimeOffset? firstBufferAt;
        long padded;

        Idle(TimeSpan.FromSeconds(10)); // nothing played for the first 10s
        using (var writer = new TrackWriter(path, "system", _clock))
        {
            Play(writer, TimeSpan.FromSeconds(5));
            writer.FinishPadding();
            padded = writer.SamplesPadded;
            firstBufferAt = writer.FirstBufferAt;
        }

        Assert.Equal(0, padded);
        Assert.Equal(5.0, Duration(path).TotalSeconds, precision: 1);
        Assert.NotNull(firstBufferAt);
    }

    [Fact]
    public void TrackIsMono16BitAt16kHz()
    {
        var path = Path.Combine(_root, "mic.wav");
        using (var writer = new TrackWriter(path, "mic", _clock))
        {
            Play(writer, TimeSpan.FromSeconds(1));
        }

        using var reader = new WaveFileReader(path);
        Assert.Equal(Rate, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
    }

    [Fact]
    public void DigitalSilenceIsDetectedByTheLivenessPeak()
    {
        using var writer = new TrackWriter(Path.Combine(_root, "mic.wav"), "mic", _clock);

        writer.Write(new float[Rate]); // a full second of zeros

        Assert.True(writer.LivenessSettled);
        Assert.Equal(0f, writer.LivenessPeak);
    }

    [Fact]
    public void RealAudioClearsTheLivenessPeak()
    {
        using var writer = new TrackWriter(Path.Combine(_root, "mic.wav"), "mic", _clock);

        writer.Write(Level(Rate, 0.25f));

        Assert.True(writer.LivenessPeak > 0.2f);
    }

    /// A clipped track transcribes as noise, and the transcript gives no hint
    /// why — a real Meet test came back reading "(speaking in foreign language)"
    /// because acoustic feedback between two devices had pinned the system track
    /// at full scale for five seconds.
    [Fact]
    public void ClippingIsCounted()
    {
        using var writer = new TrackWriter(Path.Combine(_root, "system.wav"), "system", _clock);

        writer.Write(Level(Rate / 2, 0.3f));   // half a second of healthy audio
        writer.Write(Level(Rate / 2, 1.0f));   // half a second pinned at full scale

        Assert.Equal(Rate / 2, writer.SamplesClipped);
        Assert.Equal(0.5, writer.ClippedFraction, precision: 2);
    }

    [Fact]
    public void HealthyAudioIsNeverFlaggedAsClipped()
    {
        using var writer = new TrackWriter(Path.Combine(_root, "mic.wav"), "mic", _clock);

        writer.Write(Level(Rate, 0.95f));

        Assert.Equal(0, writer.SamplesClipped);
        Assert.Equal(0.0, writer.ClippedFraction);
    }

    /// Inserted silence is not audio the user recorded, so it must not dilute the
    /// ratio the clipping warning is judged on.
    [Fact]
    public void PaddedSilenceIsExcludedFromTheClippedFraction()
    {
        using var writer = new TrackWriter(Path.Combine(_root, "system.wav"), "system", _clock);

        Play(writer, TimeSpan.FromSeconds(1));   // 0.5 amplitude, no clipping
        Idle(TimeSpan.FromSeconds(60));          // a minute of ledger padding
        writer.Write(Level(Rate, 1.0f));         // a second at full scale

        // One clipped second out of two real seconds, not out of sixty-two.
        Assert.True(writer.ClippedFraction > 0.4,
            $"expected roughly half, got {writer.ClippedFraction:P1}");
    }

    [Fact]
    public void OutOfRangeSamplesClampInsteadOfWrapping()
    {
        var path = Path.Combine(_root, "mic.wav");
        using (var writer = new TrackWriter(path, "mic", _clock))
        {
            writer.Write([2f, -2f]);
        }

        using var reader = new WaveFileReader(path);
        var buffer = new byte[4];
        Assert.Equal(4, reader.Read(buffer, 0, 4));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(buffer, 0));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(buffer, 2));
    }

    // MARK: -

    /// Feed audio in 100 ms buffers, moving the clock along with it — the shape
    /// a real capture callback delivers.
    private void Play(TrackWriter writer, TimeSpan duration)
    {
        var chunks = (int)(duration.TotalMilliseconds / Chunk.TotalMilliseconds);
        var samples = Level((int)(Rate * Chunk.TotalSeconds), 0.5f);
        for (var i = 0; i < chunks; i++)
        {
            writer.Write(samples);
            _clock.Advance(Chunk);
        }
    }

    /// Time passing with no capture callbacks at all — WASAPI loopback while
    /// nothing is playing.
    private void Idle(TimeSpan duration) => _clock.Advance(duration);

    private static float[] Level(int samples, float value)
    {
        var buffer = new float[samples];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static TimeSpan Duration(string path)
    {
        using var reader = new WaveFileReader(path);
        return reader.TotalTime;
    }
}

public sealed class DownmixTests
{
    [Fact]
    public void StereoAveragesToMono()
    {
        var source = new ArraySampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2),
            [1f, 0f, 0.5f, 0.5f, -1f, 1f]);
        var downmix = new DownmixSampleProvider(source);

        var buffer = new float[8];
        var read = downmix.Read(buffer, 0, buffer.Length);

        Assert.Equal(1, downmix.WaveFormat.Channels);
        Assert.Equal(3, read);
        Assert.Equal([0.5f, 0.5f, 0f], buffer[..3]);
    }

    /// A surround endpoint reports six or eight channels, and rca-001 hit a
    /// nine-channel input device on the macOS side. NAudio's
    /// StereoToMonoSampleProvider handles two.
    [Fact]
    public void SurroundChannelCountsCollapseToMono()
    {
        var source = new ArraySampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 6),
            [1f, 1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f, 0f, 0f]);
        var downmix = new DownmixSampleProvider(source);

        var buffer = new float[4];
        var read = downmix.Read(buffer, 0, buffer.Length);

        Assert.Equal(2, read);
        Assert.Equal([1f, 0f], buffer[..2]);
    }

    [Fact]
    public void MonoSourcePassesThrough()
    {
        var source = new ArraySampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1), [0.25f, -0.25f]);
        var downmix = new DownmixSampleProvider(source);

        var buffer = new float[4];
        Assert.Equal(2, downmix.Read(buffer, 0, buffer.Length));
        Assert.Equal([0.25f, -0.25f], buffer[..2]);
    }
}
