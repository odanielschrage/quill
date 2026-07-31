using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace Quill.Transcription;

/// Whisper via whisper.cpp (Whisper.net). Multilingual, which is the reason it
/// leads on Windows rather than Parakeet: Parakeet TDT v2 is English-only and
/// Core ML, and the macOS README already lists Whisper as the intended fallback.
///
/// The tracks are already mono 16 kHz PCM WAV — exactly whisper.cpp's input
/// format — so transcription reads them straight off disk with no decode or
/// resample step. That's the payoff for resampling once at capture time.
internal sealed class WhisperEngine : ITranscriptionEngine
{
    private readonly GgmlType _type;
    private readonly QuantizationType _quantization;
    private readonly string _language;
    private readonly int _threads;
    private readonly bool _useVad;

    private WhisperFactory? _factory;
    private WhisperVadFactory? _vadFactory;

    public WhisperEngine(
        GgmlType type,
        QuantizationType quantization,
        string language,
        int? threads = null,
        bool useVad = false)
    {
        _type = type;
        _quantization = quantization;
        _language = language;
        _threads = threads ?? Math.Max(1, Environment.ProcessorCount);
        _useVad = useVad;
    }

    public static WhisperEngine FromConfig() => new(
        WhisperModels.Resolve(Config.TranscriptionModel()),
        WhisperModels.Quantization,
        Config.TranscriptionLanguage(),
        useVad: Config.TranscriptionVad());

    public string Name => "whisper";

    public string Model => WhisperModels.Identifier(_type, _quantization);

    /// Seconds of audio the last transcription skipped as silence. Diagnostics,
    /// and how `vadtest` reports the win.
    public double SecondsSkipped { get; private set; }

    public async Task PrepareAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_factory is null)
        {
            var path = await WhisperModels.EnsureAsync(_type, _quantization, progress, ct);
            // The factory owns the model weights — expensive, loaded once, reused
            // across every track in the queue.
            _factory = WhisperFactory.FromPath(path);
        }

        if (_useVad && _vadFactory is null)
        {
            try
            {
                _vadFactory = WhisperVadFactory.FromPath(
                    await WhisperModels.EnsureVadAsync(progress, ct));
            }
            catch (Exception e)
            {
                // Skipping silence is an optimization. Losing it costs time, not
                // transcripts, so it must never fail a job.
                Console.Error.WriteLine(
                    $"warning: VAD unavailable ({e.Message}) — transcribing whole tracks");
            }
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath, Action<string>? log = null, CancellationToken ct = default)
    {
        if (_factory is null) throw new InvalidOperationException("whisper engine used before prepare");

        SecondsSkipped = 0;
        EnsureHasAudio(audioPath);

        log?.Invoke($"language {(_language is "auto" or "" ? "auto-detected" : _language)}");

        // A processor per track, not per engine. The factory holds the weights,
        // so this is cheap — and it stops whisper's rolling context from bleeding
        // the mic track's words into the start of the system track.
        var builder = _factory.CreateBuilder().WithThreads(_threads);
        builder = _language is "auto" or ""
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(_language);

        await using var processor = builder.Build();

        return await TranscribeSpeechOnlyAsync(audioPath, processor, log, ct)
               ?? await TranscribeWholeAsync(audioPath, processor, ct);
    }

    /// Detect speech, transcribe only those spans, and shift each result back onto
    /// the track's timeline.
    ///
    /// Returns null — meaning "transcribe the whole thing instead" — whenever the
    /// fast path isn't safely applicable: VAD off or unavailable, a track that
    /// isn't quill's own mono 16-bit 16 kHz format the slicing assumes, or a
    /// detector that found no speech at all. That last one matters most: a track
    /// with audio in it must never come back with an empty transcript because the
    /// threshold was wrong.
    private async Task<IReadOnlyList<TranscriptSegment>?> TranscribeSpeechOnlyAsync(
        string audioPath, WhisperProcessor processor, Action<string>? log, CancellationToken ct)
    {
        if (_vadFactory is null) return null;

        using var reader = new WaveFileReader(audioPath);
        var format = reader.WaveFormat;
        if (format.SampleRate != SpeechRegion.SampleRate
            || format.Channels != 1
            || format.BitsPerSample != 16)
        {
            return null;
        }

        var totalSamples = reader.Length / 2;
        var trackSeconds = (double)totalSamples / SpeechRegion.SampleRate;

        List<SpeechRegion> regions;
        try
        {
            regions = await DetectSpeechAsync(audioPath, totalSamples, ct);
        }
        catch (Exception e)
        {
            log?.Invoke($"vad failed ({e.Message}) — transcribing whole track");
            return null;
        }

        if (regions.Count == 0)
        {
            log?.Invoke("vad found no speech — transcribing whole track anyway");
            return null;
        }

        var keptSeconds = regions.Sum(r => r.Duration.TotalSeconds);
        SecondsSkipped = Math.Max(0, trackSeconds - keptSeconds);
        log?.Invoke($"vad kept {keptSeconds:F1}s of speech in {trackSeconds:F1}s "
                    + $"across {regions.Count} region(s)");

        var segments = new List<TranscriptSegment>();
        foreach (var region in regions)
        {
            var samples = ReadSamples(reader, region);
            await foreach (var segment in processor.ProcessAsync(samples, ct))
            {
                var text = segment.Text.Trim();
                if (text.Length == 0) continue;

                // The shift that puts this region's words back where they were
                // spoken. Without it every region would start at zero.
                segments.Add(new TranscriptSegment(
                    segment.Start + region.Offset,
                    segment.End + region.Offset,
                    text));
            }
        }
        return segments;
    }

    private async Task<List<SpeechRegion>> DetectSpeechAsync(
        string audioPath, long totalSamples, CancellationToken ct)
    {
        // Tuned to lose nothing rather than to skip the most. A low threshold and
        // generous padding mean quiet or trailing-off speech still counts; only
        // silences longer than half a second split a region, so natural pauses
        // don't fragment a sentence.
        await using var vad = _vadFactory!.CreateBuilder()
            .WithThreads(_threads)
            .WithThreshold(0.35f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(250))
            .WithMinSilenceDuration(TimeSpan.FromMilliseconds(500))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(250))
            .Build();

        await using var stream = File.OpenRead(audioPath);
        var spans = await vad.DetectSpeechAsync(stream, ct);

        var regions = new List<SpeechRegion>();
        foreach (var span in spans)
        {
            if (SpeechRegion.Clamp(span.Start, span.End, totalSamples) is { } region)
            {
                regions.Add(region);
            }
        }

        // Sentence pauses fragment continuous talking into many spans; stitching
        // the near ones back together costs a little skipped silence and saves
        // more in per-region overhead.
        return SpeechRegion.Coalesce(regions, TimeSpan.FromSeconds(2));
    }

    private async Task<IReadOnlyList<TranscriptSegment>> TranscribeWholeAsync(
        string audioPath, WhisperProcessor processor, CancellationToken ct)
    {
        await using var audio = File.OpenRead(audioPath);

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in processor.ProcessAsync(audio, ct))
        {
            // Whisper pads segments with a leading space.
            var text = segment.Text.Trim();
            if (text.Length == 0) continue;
            segments.Add(new TranscriptSegment(segment.Start, segment.End, text));
        }
        return segments;
    }

    /// Read exactly one region's frames back out of the track. 16-bit mono, so
    /// the byte offset is the sample index doubled.
    private static float[] ReadSamples(WaveFileReader reader, SpeechRegion region)
    {
        reader.Position = region.StartSample * 2;

        var bytes = new byte[region.SampleCount * 2];
        var read = reader.Read(bytes, 0, bytes.Length);

        var samples = new float[read / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        }
        return samples;
    }

    /// A track with no frames is a real case, not a hypothetical: WASAPI loopback
    /// delivers nothing at all when the machine played nothing for the whole
    /// session, leaving a header-only WAV. The coordinator logs the throw and
    /// keeps the other track's transcript.
    internal static void EnsureHasAudio(string audioPath)
    {
        using var probe = new WaveFileReader(audioPath);
        if (probe.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"empty audio {Path.GetFileName(audioPath)} — nothing was captured");
        }
    }

    public Task ReleaseAsync()
    {
        _factory?.Dispose();
        _factory = null;
        _vadFactory?.Dispose();
        _vadFactory = null;
        return Task.CompletedTask;
    }
}
