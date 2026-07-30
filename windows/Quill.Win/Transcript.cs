using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill;

/// Canonical transcript. Property names are the JSON schema — this type exists
/// to be serialized. Properties are declared alphabetically on purpose; see
/// Json.Options.
internal sealed class Transcript
{
    /// One timed, speaker-tagged span on the session's shared clock.
    public sealed class Segment
    {
        [JsonPropertyName("end_ms")] public int EndMs { get; init; }
        [JsonPropertyName("speaker")] public string Speaker { get; init; } = "";
        [JsonPropertyName("start_ms")] public int StartMs { get; init; }
        [JsonPropertyName("text")] public string Text { get; init; } = "";
    }

    [JsonPropertyName("created_at")] public string CreatedAt { get; init; } = "";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("segments")] public IReadOnlyList<Segment> Segments { get; init; } = [];

    /// Write transcript.json and render transcript.md. Both writes are atomic,
    /// so a partially written transcript never exists on disk.
    public void Write(string dir)
    {
        Json.WriteAtomic(
            Path.Combine(dir, "transcript.json"),
            JsonSerializer.Serialize(this, Json.Options));
        Json.WriteAtomic(
            Path.Combine(dir, "transcript.md"),
            Render(new DirectoryInfo(dir).Name));
    }

    private string Render(string title)
    {
        var lines = new List<string> { $"# {title}", "", $"engine: {Engine} ({Model})", "" };
        foreach (var seg in Segments)
        {
            lines.Add($"**[{Clock(seg.StartMs)}] {seg.Speaker}:** {seg.Text}");
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    internal static string Clock(int ms)
    {
        var total = ms / 1000;
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }
}
