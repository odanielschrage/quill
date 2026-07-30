using Whisper.net.Ggml;

namespace Quill.Transcription;

/// GGML weights on disk. Models are a runtime artifact, never part of the repo
/// or the binary — the same arrangement as the macOS build, where FluidAudio
/// downloads Parakeet into its own cache on first transcription. That's what
/// keeps quill.exe a binary rather than a half-gigabyte bundle.
internal static class WhisperModels
{
    /// Config name → GGML type. The names are what a user writes in
    /// transcription.model, so they stay lowercase and hyphenated.
    private static readonly (string Name, GgmlType Type)[] Known =
    [
        ("tiny", GgmlType.Tiny),
        ("base", GgmlType.Base),
        ("small", GgmlType.Small),
        ("medium", GgmlType.Medium),
        ("large-v3-turbo", GgmlType.LargeV3Turbo),
        ("large-v3", GgmlType.LargeV3),
    ];

    /// Q5_0 across the board: roughly a third the size of f16 for a quality loss
    /// that doesn't show up in meeting transcripts, which matters a lot more on a
    /// CPU-only machine than on Apple's Neural Engine.
    public const QuantizationType Quantization = QuantizationType.Q5_0;

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "quill",
        "models");

    /// Q5_0 download sizes, measured. Used by `doctor` to warn about disk space
    /// before a meeting rather than after one.
    public static int ApproximateSizeMb(GgmlType type) => type switch
    {
        GgmlType.Tiny => 28,
        GgmlType.Base => 53,
        GgmlType.Small => 167,
        GgmlType.Medium => 514,
        GgmlType.LargeV3Turbo => 547,
        _ => 1100,
    };

    public static string Slug(GgmlType type) =>
        Known.FirstOrDefault(m => m.Type == type).Name ?? type.ToString().ToLowerInvariant();

    public static string Slug(QuantizationType quantization) =>
        quantization == QuantizationType.NoQuantization
            ? "f16"
            : quantization.ToString().ToLowerInvariant();

    /// Provenance string recorded in transcript.json, e.g. "ggml-small-q5_0".
    public static string Identifier(GgmlType type, QuantizationType quantization) =>
        $"ggml-{Slug(type)}-{Slug(quantization)}";

    public static string PathFor(GgmlType type, QuantizationType quantization) =>
        Path.Combine(CacheDirectory, $"{Identifier(type, quantization)}.bin");

    public static bool IsCached(GgmlType type, QuantizationType quantization) =>
        File.Exists(PathFor(type, quantization));

    /// Parse a configured model name, warning and falling back to small rather
    /// than failing a recording that already happened.
    public static GgmlType Resolve(string configured)
    {
        foreach (var (name, type) in Known)
        {
            if (string.Equals(name, configured, StringComparison.OrdinalIgnoreCase)) return type;
        }
        Console.Error.WriteLine(
            $"warning: unknown transcription model \"{configured}\" — using small. "
            + $"known: {string.Join(", ", Known.Select(m => m.Name))}");
        return GgmlType.Small;
    }

    /// Weights are published as <quantization folder>/ggml-<model>.bin — the
    /// quantization lives in the path, not the file name.
    private const string BaseUrl = "https://huggingface.co/sandrohanea/whisper.net/resolve/main";

    /// Abort a download that has produced no bytes for this long. quill downloads
    /// in the background after a meeting; a stalled connection must surface as a
    /// failure in transcribe.log, not as a job that never finishes.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private const int MaxAttempts = 3;

    // Timeout.Infinite on the client because a whole-operation timeout would kill
    // a legitimately slow 500 MB download; the per-read stall timer below is the
    // right granularity.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string RemoteFolder(QuantizationType quantization) =>
        quantization == QuantizationType.NoQuantization
            ? "classic"
            : quantization.ToString().ToLowerInvariant();

    private static string Url(GgmlType type, QuantizationType quantization) =>
        $"{BaseUrl}/{RemoteFolder(quantization)}/ggml-{Slug(type)}.bin";

    /// Return the cached model path, downloading it first if needed.
    ///
    /// The download lands on a temp name and is renamed into place only once
    /// complete and size-verified. An interrupted download must not leave a
    /// truncated file that looks cached — that would fail every transcription
    /// from then on, with no obvious cause.
    ///
    /// This does the HTTP itself rather than calling WhisperGgmlDownloader, which
    /// has no timeout: a stalled connection there hung for fourteen hours without
    /// transferring a byte or reporting anything.
    public static Task<string> EnsureAsync(
        GgmlType type,
        QuantizationType quantization,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        EnsureFileAsync(
            Url(type, quantization),
            PathFor(type, quantization),
            Identifier(type, quantization),
            progress,
            ct);

    /// Silero VAD weights — about 2 MB, unrelated to the transcription model, and
    /// downloaded only when VAD is switched on.
    public const string VadModel = "ggml-silero-v6.2.0";

    public static string VadPath { get; } = Path.Combine(CacheDirectory, $"{VadModel}.bin");

    public static bool IsVadCached() => File.Exists(VadPath);

    public static Task<string> EnsureVadAsync(
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        EnsureFileAsync($"{BaseUrl}/vad/{VadModel}.bin", VadPath, VadModel, progress, ct);

    private static async Task<string> EnsureFileAsync(
        string url, string path, string name, IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(CacheDirectory);
        var temp = $"{path}.partial";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAsync(url, temp, name, progress, ct);
                break;
            }
            catch (Exception e) when (attempt < MaxAttempts && e is not OperationCanceledException)
            {
                // Keep the partial file: the next attempt resumes from it.
                Console.Error.WriteLine(
                    $"  download attempt {attempt} failed ({e.Message}) — retrying");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
        }

        File.Move(temp, path, overwrite: true);
        var megabytes = new FileInfo(path).Length / (1024.0 * 1024.0);
        Console.Error.WriteLine($"downloaded {name} ({megabytes:F0} MB) → {CacheDirectory}");
        return path;
    }

    private static async Task DownloadAsync(
        string url, string temp, string name, IProgress<string>? progress, CancellationToken ct)
    {
        // Resume where an interrupted attempt left off.
        var already = File.Exists(temp) ? new FileInfo(temp).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (already > 0) request.Headers.Range = new(already, null);

        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // A server that ignored the Range header restarts the file, so the
        // existing bytes must go.
        if (already > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            already = 0;
        }

        var remaining = response.Content.Headers.ContentLength;
        var total = remaining is null ? 0 : already + remaining.Value;
        Console.Error.WriteLine(total > 0
            ? $"downloading {name} — {total / (1024 * 1024)} MB"
            : $"downloading {name}");
        progress?.Report(total > 0
            ? $"downloading model — 0% of {total / (1024 * 1024)} MB"
            : $"downloading model");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(
            temp, already > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);

        // Re-armed after every successful read, so the token fires only when the
        // connection genuinely goes quiet.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        var buffer = new byte[81920];
        var written = already;
        var nextReport = written + 32L * 1024 * 1024;
        // The tray updates far more often than the log: someone watching a menu
        // wants to see it move, and a line every 32 MB reads as frozen.
        var nextTrayReport = written + 4L * 1024 * 1024;

        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"no data for {StallTimeout.TotalSeconds:F0}s at "
                    + $"{written / (1024 * 1024)} MB");
            }
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            stall.CancelAfter(StallTimeout);

            if (written >= nextTrayReport)
            {
                progress?.Report(total > 0
                    ? $"downloading model — {100.0 * written / total:F0}% of {total / (1024 * 1024)} MB"
                    : $"downloading model — {written / (1024 * 1024)} MB");
                nextTrayReport += 4L * 1024 * 1024;
            }

            if (written < nextReport) continue;
            Console.Error.WriteLine(total > 0
                ? $"  … {written / (1024 * 1024)} MB ({100.0 * written / total:F0}%)"
                : $"  … {written / (1024 * 1024)} MB");
            nextReport += 32L * 1024 * 1024;
        }

        await destination.FlushAsync(ct);

        // Catch a silently truncated transfer before it becomes a "cached" model.
        if (total > 0 && written != total)
        {
            throw new IOException($"truncated download: got {written} of {total} bytes");
        }
    }
}
