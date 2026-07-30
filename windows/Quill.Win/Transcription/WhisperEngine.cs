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

    private WhisperFactory? _factory;

    public WhisperEngine(
        GgmlType type,
        QuantizationType quantization,
        string language,
        int? threads = null)
    {
        _type = type;
        _quantization = quantization;
        _language = language;
        _threads = threads ?? Math.Max(1, Environment.ProcessorCount);
    }

    public static WhisperEngine FromConfig() => new(
        WhisperModels.Resolve(Config.TranscriptionModel()),
        WhisperModels.Quantization,
        Config.TranscriptionLanguage());

    public string Name => "whisper";

    public string Model => WhisperModels.Identifier(_type, _quantization);

    public async Task PrepareAsync(CancellationToken ct = default)
    {
        if (_factory is not null) return;
        var path = await WhisperModels.EnsureAsync(_type, _quantization, ct);

        // The factory owns the model weights — expensive, loaded once, reused
        // across every track in the queue.
        _factory = WhisperFactory.FromPath(path);
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath, CancellationToken ct = default)
    {
        if (_factory is null) throw new InvalidOperationException("whisper engine used before prepare");

        EnsureHasAudio(audioPath);

        // A processor per track, not per engine. The factory holds the weights,
        // so this is cheap — and it stops whisper's rolling context from bleeding
        // the mic track's words into the start of the system track.
        var builder = _factory.CreateBuilder().WithThreads(_threads);
        builder = _language is "auto" or ""
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(_language);

        await using var processor = builder.Build();
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
        return Task.CompletedTask;
    }
}
