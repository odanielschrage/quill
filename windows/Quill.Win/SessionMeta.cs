using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Quill;

/// meta.json as written on clean stop. Properties are declared alphabetically
/// on purpose; see Json.Options. Track members avoid the name `System` so they
/// can't shadow the namespace inside these types.
internal sealed class SessionMetaJson
{
    public sealed class TrackFiles
    {
        [JsonPropertyName("mic")] public string Mic { get; init; } = "";
        [JsonPropertyName("system")] public string SystemTrack { get; init; } = "";
    }

    public sealed class TrackOffsets
    {
        [JsonPropertyName("mic")] public int Mic { get; init; }
        [JsonPropertyName("system")] public int SystemTrack { get; init; }
    }

    [JsonPropertyName("duration_seconds")] public int DurationSeconds { get; init; }
    [JsonPropertyName("ended")] public string Ended { get; init; } = "";
    [JsonPropertyName("files")] public TrackFiles Files { get; init; } = new();
    [JsonPropertyName("start_offset_ms")] public TrackOffsets StartOffsetMs { get; init; } = new();
    [JsonPropertyName("started")] public string Started { get; init; } = "";
}

/// The slice of meta.json the coordinator needs: which files exist, who they
/// represent, and how far each track started after the earliest one.
internal sealed class SessionMeta
{
    public sealed record Track(string File, string Speaker, int OffsetMs);

    public IReadOnlyList<Track> Tracks { get; }

    private SessionMeta(IReadOnlyList<Track> tracks) => Tracks = tracks;

    public sealed class MetaException(string path)
        : Exception($"can't parse {path}");

    /// Read tolerantly rather than through the strongly typed writer: sessions
    /// recorded before offsets were captured have no start_offset_ms, and their
    /// tracks start within tens of milliseconds of each other anyway.
    public static SessionMeta Read(string dir)
    {
        var path = Path.Combine(dir, "meta.json");
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            // Reported below as an unparseable meta, same as a wrong shape.
        }

        if (json?["files"] as JsonObject is not { } files) throw new MetaException(path);

        var offsets = json["start_offset_ms"] as JsonObject;
        int Offset(string key) => offsets?[key]?.GetValue<int>() ?? 0;

        var tracks = new List<Track>();
        if (files["mic"]?.GetValue<string>() is { } mic)
            tracks.Add(new Track(mic, "me", Offset("mic")));
        if (files["system"]?.GetValue<string>() is { } system)
            tracks.Add(new Track(system, "them", Offset("system")));
        return new SessionMeta(tracks);
    }
}
