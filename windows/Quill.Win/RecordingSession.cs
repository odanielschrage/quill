using System.Globalization;
using System.Text.Json;
using Quill.Audio;

namespace Quill;

/// One meeting recording: a timestamped folder holding two independent tracks
/// (mic = you, system = them) plus a meta.json written on clean stop. Tracks are
/// separate on purpose — speech models do better on clean single-source audio,
/// and two tracks give free two-party diarization.
///
/// WAV rather than macOS's AAC-in-CAF: it streams, it's what the ASR consumes
/// anyway, and a truncated WAV is recoverable by rebuilding its header — which
/// preserves the property that motivated CAF in the first place.
internal sealed class RecordingSession : IDisposable
{
    public const string MicFile = "mic.wav";
    public const string SystemFile = "system.wav";

    public string Dir { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    private readonly IAudioRecorder _mic;
    private readonly IAudioRecorder _system;

    /// Create the session folder under `root` (yyyy.MM.dd-HHmm, suffixed on
    /// collision) without starting capture yet.
    ///
    /// The folder name is local time — it's a label a human reads — while every
    /// timestamp inside meta.json is UTC. That split matches the macOS build,
    /// where DateFormatter defaults to the current time zone and
    /// ISO8601DateFormatter defaults to UTC.
    public RecordingSession(string root, IAudioRecorder mic, IAudioRecorder system)
    {
        _mic = mic;
        _system = system;

        var stamp = StartedAt.LocalDateTime.ToString(
            "yyyy.MM.dd-HHmm", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(root, stamp);
        for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = Path.Combine(root, $"{stamp}-{n}");
        }
        Directory.CreateDirectory(candidate);
        Dir = candidate;
    }

    /// Start both tracks. If the mic fails after the loopback capture started,
    /// the loopback is torn down so we never run half a session silently.
    public void Start()
    {
        _system.Start(Path.Combine(Dir, SystemFile));
        try
        {
            _mic.Start(Path.Combine(Dir, MicFile));
        }
        catch
        {
            _system.Stop();
            throw;
        }
    }

    /// Stop both tracks and write meta.json. Idempotent enough to be safe from
    /// both the tray's Stop item and a session-ending shutdown handler.
    public void Stop()
    {
        _mic.Stop();
        _system.Stop();

        var ended = DateTimeOffset.Now;

        // The tracks don't start on the same buffer; record how far each lags
        // the earliest so transcript timestamps share one clock.
        var micStart = _mic.FirstBufferAt ?? StartedAt;
        var systemStart = _system.FirstBufferAt ?? StartedAt;
        var earliest = micStart < systemStart ? micStart : systemStart;

        var meta = new SessionMetaJson
        {
            DurationSeconds = (int)(ended - StartedAt).TotalSeconds,
            Ended = Json.Iso8601(ended),
            Files = new SessionMetaJson.TrackFiles { Mic = MicFile, SystemTrack = SystemFile },
            StartOffsetMs = new SessionMetaJson.TrackOffsets
            {
                Mic = (int)(micStart - earliest).TotalMilliseconds,
                SystemTrack = (int)(systemStart - earliest).TotalMilliseconds,
            },
            Started = Json.Iso8601(StartedAt),
        };

        try
        {
            File.WriteAllText(
                Path.Combine(Dir, "meta.json"),
                JsonSerializer.Serialize(meta, Json.Options),
                Json.Utf8NoBom);
        }
        catch (Exception e)
        {
            // Without meta.json the session is invisible to the transcription
            // queue, so this is worth saying out loud rather than swallowing.
            Console.Error.WriteLine($"meta.json write failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        _mic.Dispose();
        _system.Dispose();
    }
}
