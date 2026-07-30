using System.Text.Json;
using Xunit;

namespace Quill.Tests;

/// The on-disk format is shared with the macOS build. These tests are the thing
/// that keeps it that way — including the two details that silently break it:
/// property declaration order (standing in for Swift's .sortedKeys) and the JSON
/// encoder's non-ASCII escaping.
public sealed class ContractTests : IDisposable
{
    private readonly string _root = Temp.Dir();

    public void Dispose() => Temp.Nuke(_root);

    [Fact]
    public void MetaJson_MatchesSwiftKeyOrderAndShape()
    {
        var mic = new FakeRecorder();
        var system = new FakeRecorder();
        using var session = new RecordingSession(_root, mic, system);
        session.Start();

        // The system track opens first, the mic 250 ms later — exactly the skew
        // start_offset_ms exists to record.
        var t0 = DateTimeOffset.Now;
        system.FirstBufferAt = t0;
        mic.FirstBufferAt = t0.AddMilliseconds(250);
        session.Stop();

        var text = File.ReadAllText(Path.Combine(session.Dir, "meta.json"));

        AssertKeyOrder(text, "duration_seconds", "ended", "files", "start_offset_ms", "started");
        Assert.Contains("\n  \"ended\"", text, StringComparison.Ordinal); // two-space indent

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.Equal("mic.wav", root.GetProperty("files").GetProperty("mic").GetString());
        Assert.Equal("system.wav", root.GetProperty("files").GetProperty("system").GetString());

        var offsets = root.GetProperty("start_offset_ms");
        Assert.Equal(0, offsets.GetProperty("system").GetInt32());
        Assert.Equal(250, offsets.GetProperty("mic").GetInt32());

        // UTC, second precision, literal Z — what ISO8601DateFormatter emits.
        foreach (var key in new[] { "started", "ended" })
        {
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$",
                root.GetProperty(key).GetString());
        }
    }

    [Fact]
    public void MetaJson_NestedTrackKeysAreAlphabetical()
    {
        using var session = new RecordingSession(_root, new FakeRecorder(), new FakeRecorder());
        session.Start();
        session.Stop();

        var text = File.ReadAllText(Path.Combine(session.Dir, "meta.json"));
        var files = text[text.IndexOf("\"files\"", StringComparison.Ordinal)..
                         text.IndexOf("\"start_offset_ms\"", StringComparison.Ordinal)];
        AssertKeyOrder(files, "mic", "system");
    }

    [Fact]
    public void SessionFolder_IsLocalTimestampSuffixedOnCollision()
    {
        using var first = new RecordingSession(_root, new FakeRecorder(), new FakeRecorder());
        using var second = new RecordingSession(_root, new FakeRecorder(), new FakeRecorder());
        using var third = new RecordingSession(_root, new FakeRecorder(), new FakeRecorder());

        var stamp = Path.GetFileName(first.Dir);
        Assert.Matches(@"^\d{4}\.\d{2}\.\d{2}-\d{4}$", stamp);
        Assert.Equal($"{stamp}-2", Path.GetFileName(second.Dir));
        Assert.Equal($"{stamp}-3", Path.GetFileName(third.Dir));

        // The folder label is local time, unlike the UTC timestamps inside.
        Assert.Equal(
            DateTime.Now.ToString("yyyy.MM.dd-HH", System.Globalization.CultureInfo.InvariantCulture),
            stamp[..13]);
    }

    [Fact]
    public void MicFailure_TearsDownTheSystemTrack()
    {
        var system = new FakeRecorder();
        var mic = new FakeRecorder { ThrowOnStart = true };
        using var session = new RecordingSession(_root, mic, system);

        Assert.Throws<IOException>(session.Start);

        // Never half a session recording silently.
        Assert.False(system.IsRecording);
        Assert.Equal(1, system.StopCount);
    }

    [Fact]
    public void TranscriptJson_MatchesSwiftKeyOrder()
    {
        var dir = Path.Combine(_root, "fixture");
        Directory.CreateDirectory(dir);

        new Transcript
        {
            CreatedAt = "2026-07-29T12:00:00Z",
            Engine = "fake",
            Model = "fake-v0",
            Segments = [new Transcript.Segment
            {
                Speaker = "me", StartMs = 0, EndMs = 1000, Text = "hi",
            }],
        }.Write(dir);

        var text = File.ReadAllText(Path.Combine(dir, "transcript.json"));
        AssertKeyOrder(text, "created_at", "engine", "model", "segments");
        AssertKeyOrder(text[text.IndexOf("\"segments\"", StringComparison.Ordinal)..],
            "end_ms", "speaker", "start_ms", "text");
    }

    [Fact]
    public void TranscriptJson_KeepsAccentedTextUnescaped()
    {
        var dir = Path.Combine(_root, "acentos");
        Directory.CreateDirectory(dir);

        const string spoken = "Não sei se você já viu a apresentação — é ótima.";
        new Transcript
        {
            CreatedAt = "2026-07-29T12:00:00Z",
            Engine = "fake",
            Model = "fake-v0",
            Segments = [new Transcript.Segment
            {
                Speaker = "them", StartMs = 0, EndMs = 1000, Text = spoken,
            }],
        }.Write(dir);

        var text = File.ReadAllText(Path.Combine(dir, "transcript.json"));
        Assert.Contains(spoken, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptMd_RendersExactly()
    {
        var dir = Path.Combine(_root, "fixture");
        Directory.CreateDirectory(dir);

        new Transcript
        {
            CreatedAt = "2026-07-29T12:00:00Z",
            Engine = "fake",
            Model = "fake-v0",
            Segments =
            [
                new Transcript.Segment { Speaker = "me", StartMs = 5000, EndMs = 6000, Text = "Olá" },
                new Transcript.Segment { Speaker = "them", StartMs = 3_661_000, EndMs = 3_662_000, Text = "tchau" },
            ],
        }.Write(dir);

        Assert.Equal(
            "# fixture\n"
            + "\n"
            + "engine: fake (fake-v0)\n"
            + "\n"
            + "**[0:05] me:** Olá\n"
            + "\n"
            + "**[1:01:01] them:** tchau\n",
            File.ReadAllText(Path.Combine(dir, "transcript.md")));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5_000, "0:05")]
    [InlineData(61_000, "1:01")]
    [InlineData(599_000, "9:59")]
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(3_661_000, "1:01:01")]
    public void Clock_MatchesSwiftFormat(int ms, string expected) =>
        Assert.Equal(expected, Transcript.Clock(ms));

    private static void AssertKeyOrder(string json, params string[] keys)
    {
        var previous = -1;
        foreach (var key in keys)
        {
            var index = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
            Assert.True(index > previous,
                $"key \"{key}\" is out of alphabetical order — declaration order in the "
                + $"serialized type must stand in for Swift's .sortedKeys.\n{json}");
            previous = index;
        }
    }
}
