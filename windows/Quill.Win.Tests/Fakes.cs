using Quill.Audio;
using Quill.Transcription;

namespace Quill.Tests;

/// Stands in for a WASAPI capture source. Writes a placeholder file so
/// File.Exists checks downstream behave like a real session.
internal sealed class FakeRecorder : IAudioRecorder
{
    public bool IsRecording { get; private set; }
    public DateTimeOffset? FirstBufferAt { get; set; }
    public string? WrittenTo { get; private set; }
    public bool ThrowOnStart { get; set; }
    public bool WriteFile { get; set; } = true;
    public int StopCount { get; private set; }

    public void Start(string path)
    {
        if (ThrowOnStart) throw new IOException("capture unavailable");
        WrittenTo = path;
        IsRecording = true;
        if (WriteFile) File.WriteAllText(path, "not really audio");
    }

    public void Stop()
    {
        StopCount++;
        IsRecording = false;
    }

    public void Dispose() { }
}

/// Returns canned segments per audio file name, or throws for names added to
/// ThrowFor — enough to exercise the coordinator's merge, offset, and
/// keep-going-on-failure paths without a model.
internal sealed class FakeEngine : ITranscriptionEngine
{
    public string Name => "fake";
    public string Model => "fake-v0";

    public Dictionary<string, IReadOnlyList<TranscriptSegment>> Segments { get; } = [];
    public HashSet<string> ThrowFor { get; } = [];
    public int PrepareCount { get; private set; }
    public int ReleaseCount { get; private set; }

    public Task PrepareAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        PrepareCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath, CancellationToken ct = default)
    {
        var name = Path.GetFileName(audioPath);
        if (ThrowFor.Contains(name)) throw new InvalidOperationException($"bad track {name}");
        return Task.FromResult(
            Segments.TryGetValue(name, out var segs) ? segs : []);
    }

    public Task ReleaseAsync()
    {
        ReleaseCount++;
        return Task.CompletedTask;
    }
}

internal static class Temp
{
    /// Fresh scratch directory, deleted by the caller's Dispose.
    public static string Dir()
    {
        var path = Path.Combine(Path.GetTempPath(), "quill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Nuke(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    /// Poll until `condition` holds. The coordinator drains on a background
    /// task, so tests wait on its observable output rather than on internals.
    public static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"timed out waiting for {what}");
    }
}
