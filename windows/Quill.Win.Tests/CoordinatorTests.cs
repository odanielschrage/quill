using System.Text.Json;
using Quill.Transcription;
using Xunit;

namespace Quill.Tests;

/// Exercises the merge/offset/keep-going behaviour that makes two-track
/// diarization work, with a fake engine standing in for Whisper.
public sealed class CoordinatorTests : IDisposable
{
    private readonly string _root = Temp.Dir();
    private readonly string? _previousConfig =
        Environment.GetEnvironmentVariable(Config.PathVariable);

    public CoordinatorTests() => UseConfig(null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Config.PathVariable, _previousConfig);
        Temp.Nuke(_root);
    }

    [Fact]
    public async Task MergesBothTracksOnOneClockSortedByStart()
    {
        var engine = new FakeEngine();
        engine.Segments[RecordingSession.MicFile] =
            [new TranscriptSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "eu falo")];
        engine.Segments[RecordingSession.SystemFile] =
            [new TranscriptSegment(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), "eles falam")];

        // The mic track opened 500 ms late, so its segments shift forward and the
        // system segment must land first in the merged transcript.
        var dir = WriteSession("2026.07.29-1200", micOffsetMs: 500);
        var coordinator = new TranscriptionCoordinator(() => engine);
        coordinator.Enqueue(dir);

        var segments = await ReadSegments(dir);

        Assert.Equal(2, segments.Count);
        Assert.Equal(("them", 500, 1000, "eles falam"), segments[0]);
        Assert.Equal(("me", 1500, 2500, "eu falo"), segments[1]);
    }

    [Fact]
    public async Task MissingTrackIsLoggedAndTheOtherStillTranscribes()
    {
        var engine = new FakeEngine();
        engine.Segments[RecordingSession.SystemFile] =
            [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "só eles")];

        var dir = WriteSession("2026.07.29-1300", writeMicFile: false);
        new TranscriptionCoordinator(() => engine).Enqueue(dir);

        var segments = await ReadSegments(dir);

        Assert.Single(segments);
        Assert.Equal("them", segments[0].Speaker);
        Assert.Contains(
            "skipping missing track mic.wav",
            File.ReadAllText(Path.Combine(dir, "transcribe.log")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneFailingTrackDoesNotCostTheOtherItsTranscript()
    {
        var engine = new FakeEngine();
        engine.ThrowFor.Add(RecordingSession.MicFile);
        engine.Segments[RecordingSession.SystemFile] =
            [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "sobrevivi")];

        var dir = WriteSession("2026.07.29-1400");
        new TranscriptionCoordinator(() => engine).Enqueue(dir);

        var segments = await ReadSegments(dir);

        Assert.Single(segments);
        Assert.Equal("sobrevivi", segments[0].Text);
        Assert.Contains(
            "skipping mic.wav",
            File.ReadAllText(Path.Combine(dir, "transcribe.log")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineIsReleasedOnceTheQueueDrains()
    {
        var engine = new FakeEngine();
        var coordinator = new TranscriptionCoordinator(() => engine);

        var first = WriteSession("2026.07.29-1500");
        var second = WriteSession("2026.07.29-1600");
        coordinator.Enqueue(first);
        coordinator.Enqueue(second);

        await Temp.WaitFor(
            () => File.Exists(Path.Combine(second, "transcript.json")), "second transcript");
        await Temp.WaitFor(() => engine.ReleaseCount == 1, "engine release");

        // Prepared once for the whole drain, not once per session.
        Assert.Equal(1, engine.PrepareCount);
    }

    [Fact]
    public async Task ResumePendingPicksUpSessionsWithNoTranscript()
    {
        var engine = new FakeEngine();
        var pending = WriteSession("2026.07.29-1700");

        var alreadyDone = WriteSession("2026.07.29-1800");
        File.WriteAllText(Path.Combine(alreadyDone, "transcript.json"), "{}");

        var neverFinished = Path.Combine(_root, "2026.07.29-1900");
        Directory.CreateDirectory(neverFinished); // no meta.json — not a session yet

        new TranscriptionCoordinator(() => engine).ResumePending(_root);

        await Temp.WaitFor(
            () => File.Exists(Path.Combine(pending, "transcript.json")), "pending transcript");

        // The finished one was left alone and the meta-less folder was ignored.
        Assert.Equal("{}", File.ReadAllText(Path.Combine(alreadyDone, "transcript.json")));
        Assert.False(File.Exists(Path.Combine(neverFinished, "transcript.json")));
    }

    [Fact]
    public async Task TranscriptionDisabledSkipsTheEngineEntirely()
    {
        UseConfig("""{ "transcription": { "enabled": false } }""");

        var engine = new FakeEngine();
        var dir = WriteSession("2026.07.29-2000");
        new TranscriptionCoordinator(() => engine).Enqueue(dir);

        await Task.Delay(300);

        Assert.Equal(0, engine.PrepareCount);
        Assert.False(File.Exists(Path.Combine(dir, "transcript.json")));
    }

    // MARK: -

    /// Point Config at a written config file, or at a path that doesn't exist so
    /// the suite sees library defaults instead of whatever config this machine
    /// happens to have installed.
    private void UseConfig(string? json)
    {
        var path = Path.Combine(_root, "config.json");
        if (json is null)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, json);
        }
        Environment.SetEnvironmentVariable(Config.PathVariable, path);
    }

    private string WriteSession(
        string name, int micOffsetMs = 0, int systemOffsetMs = 0, bool writeMicFile = true)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (writeMicFile)
        {
            File.WriteAllText(Path.Combine(dir, RecordingSession.MicFile), "not really audio");
        }
        File.WriteAllText(Path.Combine(dir, RecordingSession.SystemFile), "not really audio");
        File.WriteAllText(Path.Combine(dir, "meta.json"), $$"""
        {
          "duration_seconds": 10,
          "ended": "2026-07-29T12:00:10Z",
          "files": { "mic": "{{RecordingSession.MicFile}}", "system": "{{RecordingSession.SystemFile}}" },
          "start_offset_ms": { "mic": {{micOffsetMs}}, "system": {{systemOffsetMs}} },
          "started": "2026-07-29T12:00:00Z"
        }
        """);
        return dir;
    }

    private static async Task<List<(string Speaker, int StartMs, int EndMs, string Text)>>
        ReadSegments(string dir)
    {
        var path = Path.Combine(dir, "transcript.json");
        await Temp.WaitFor(() => File.Exists(path), $"transcript.json in {dir}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("segments").EnumerateArray()
            .Select(s => (
                s.GetProperty("speaker").GetString()!,
                s.GetProperty("start_ms").GetInt32(),
                s.GetProperty("end_ms").GetInt32(),
                s.GetProperty("text").GetString()!))
            .ToList();
    }
}
