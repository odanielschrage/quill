using System.Diagnostics;

namespace Quill.Transcription;

internal enum StatusKind { Idle, Transcribing, Failed }

/// `Message` carries a sub-status for the tray — currently model-download
/// progress, which on a first run is hundreds of megabytes with nowhere else to
/// report itself.
internal readonly record struct Status(
    StatusKind Kind, string Session = "", int Queued = 0, string? Message = null);

/// Post-recording pipeline: a serial queue of session folders to transcribe.
/// mic → "me", system → "them"; each track's segments are shifted by its start
/// offset, merged by timestamp, and written as transcript.json (canonical) plus
/// transcript.md (readable). The filesystem is the queue — ResumePending()
/// rescans at launch, so a crash or quit mid-transcription just retries on next
/// run. Failures append to the session's transcribe.log and never block later
/// jobs.
///
/// The macOS build gets serialization from being an actor; here a single gate
/// lock plus a `_draining` flag does the same job — only one drain loop runs at
/// a time, and only one job inside it.
internal sealed class TranscriptionCoordinator(Func<ITranscriptionEngine> engineFactory)
{
    private readonly object _gate = new();
    private readonly List<string> _queue = [];
    private bool _draining;
    private string? _lastFailure;
    private Status _current;
    private ITranscriptionEngine? _engine;

    public Action<Status>? StatusHandler { get; set; }

    /// Queue a finished session. With transcription disabled in config, the
    /// on_stop hook still fires — it just gets an untranscribed folder.
    public void Enqueue(string sessionDir)
    {
        if (!Config.TranscriptionEnabled())
        {
            RunHook(sessionDir);
            return;
        }
        lock (_gate) { _queue.Add(sessionDir); }
        DrainIfIdle();
    }

    /// Scan the recordings root for sessions that finished (meta.json exists)
    /// but were never transcribed. Folder names sort chronologically, so
    /// oldest-first is a name sort.
    public void ResumePending(string root)
    {
        if (!Config.TranscriptionEnabled()) return;

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(root);
        }
        catch (Exception)
        {
            return;
        }

        var pending = entries
            .Where(d => File.Exists(Path.Combine(d, "meta.json"))
                        && !File.Exists(Path.Combine(d, "transcript.json")))
            .OrderBy(SessionName, StringComparer.Ordinal)
            .ToList();

        var added = 0;
        lock (_gate)
        {
            foreach (var dir in pending.Where(dir => !_queue.Contains(dir)))
            {
                _queue.Add(dir);
                added++;
            }
        }
        if (added > 0)
        {
            Console.Error.WriteLine($"resuming {added} untranscribed session(s)");
        }
        DrainIfIdle();
    }

    // MARK: -

    private void DrainIfIdle()
    {
        lock (_gate)
        {
            if (_draining || _queue.Count == 0) return;
            _draining = true;
            _lastFailure = null;
        }
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (TryDequeue(out var dir, out var queued))
        {
            var name = SessionName(dir);
            _current = new Status(StatusKind.Transcribing, name, queued);
            Publish(_current);
            try
            {
                await TranscribeAsync(dir);
                Notify.User("quill — transcript ready", name);
                RunHook(dir);
            }
            catch (Exception e)
            {
                Log(dir, $"transcription failed: {e}");
                lock (_gate) { _lastFailure = name; }
                Notify.User("quill — transcription failed", $"{name} — see transcribe.log");
            }
        }

        if (_engine is not null)
        {
            await _engine.ReleaseAsync();
            _engine = null;
        }

        string? failure;
        lock (_gate)
        {
            failure = _lastFailure;
            _draining = false;
        }
        Publish(failure is null
            ? new Status(StatusKind.Idle)
            : new Status(StatusKind.Failed, failure));

        // An enqueue that landed between the loop exiting and the release
        // finishing would otherwise sit until the next enqueue.
        DrainIfIdle();
    }

    private bool TryDequeue(out string dir, out int queued)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                dir = "";
                queued = 0;
                return false;
            }
            dir = _queue[0];
            _queue.RemoveAt(0);
            queued = _queue.Count;
            return true;
        }
    }

    private async Task TranscribeAsync(string dir)
    {
        var meta = SessionMeta.Read(dir);
        var engine = await PreparedEngineAsync();

        var merged = new List<Transcript.Segment>();
        foreach (var track in meta.Tracks)
        {
            var audio = Path.Combine(dir, track.File);
            if (!File.Exists(audio))
            {
                Log(dir, $"skipping missing track {track.File}");
                continue;
            }

            Log(dir, $"transcribing {track.File} ({engine.Name})");

            // One bad track (empty, truncated) shouldn't cost us the other's
            // transcript — log it and keep going.
            IReadOnlyList<TranscriptSegment> segments;
            try
            {
                segments = await engine.TranscribeAsync(
                    audio, message => Log(dir, $"{track.File}: {message}"));
            }
            catch (Exception e)
            {
                Log(dir, $"skipping {track.File}: {e.Message}");
                continue;
            }

            // Per track, before the merge: a hallucination loop is only visible
            // as consecutive segments, and interleaving the two tracks would
            // separate a run with the other speaker's words.
            segments = TranscriptCleaner.Clean(
                segments, message => Log(dir, $"{track.File}: {message}"));

            var offset = TimeSpan.FromMilliseconds(track.OffsetMs);
            merged.AddRange(segments.Select(s => new Transcript.Segment
            {
                Speaker = track.Speaker,
                StartMs = (int)(s.Start + offset).TotalMilliseconds,
                EndMs = (int)(s.End + offset).TotalMilliseconds,
                Text = s.Text,
            }));
        }

        // Stable sort, unlike the macOS build's: when a mic and a system segment
        // start on the same millisecond, ordering stays deterministic instead of
        // flipping between runs.
        var ordered = merged.OrderBy(s => s.StartMs).ToList();

        // Only worth doing once both tracks are on one clock, which is exactly
        // here. Every removal is written to transcribe.log rather than silently
        // dropped.
        if (Config.TranscriptionEchoSuppression())
        {
            ordered = EchoSuppressor.Apply(ordered, message => Log(dir, message));
        }

        var transcript = new Transcript
        {
            CreatedAt = Json.Iso8601(DateTimeOffset.Now),
            Engine = engine.Name,
            Model = engine.Model,
            Segments = ordered,
        };
        transcript.Write(dir);
        Log(dir, $"done — {ordered.Count} segments");
    }

    private async Task<ITranscriptionEngine> PreparedEngineAsync()
    {
        if (_engine is not null) return _engine;

        var configured = Config.TranscriptionEngine();
        if (configured != "whisper")
        {
            Console.Error.WriteLine(
                $"warning: unknown transcription engine \"{configured}\" — using whisper");
        }

        var engine = engineFactory();
        // Model download reports through here so the tray can say what it's
        // doing; without it the first run looks like a job that hung.
        await engine.PrepareAsync(
            new Progress<string>(text => Publish(_current with { Message = text })));
        _engine = engine;
        return engine;
    }

    /// Fires the configured on_stop command with the session directory as its
    /// sole argument, after the transcript exists (or immediately after
    /// recording when transcription is disabled).
    ///
    /// The macOS build passes the directory as $0 so /bin/sh quotes it for us.
    /// cmd.exe has no equivalent, so this leans on `/s`: with it, cmd strips
    /// only the outermost pair of quotes and treats the remainder verbatim,
    /// which is the one documented way to pass a quoted path through reliably.
    /// Windows paths cannot contain a double quote, so nothing can escape it.
    private void RunHook(string dir)
    {
        var cmd = Config.OnStop();
        if (cmd is null) return;

        var info = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/s /c \"{cmd} \"{dir}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            Process.Start(info);
        }
        catch (Exception e)
        {
            Log(dir, $"on_stop hook failed to launch: {e.Message}");
        }
    }

    private void Log(string dir, string message)
    {
        var line = $"{Json.Iso8601(DateTimeOffset.Now)} {message}\n";
        try
        {
            File.AppendAllText(Path.Combine(dir, "transcribe.log"), line, Json.Utf8NoBom);
        }
        catch (Exception)
        {
            // The log is diagnostics; losing a line must not fail a job.
        }
    }

    private void Publish(Status status) => StatusHandler?.Invoke(status);

    /// Trailing-separator-safe last path component.
    private static string SessionName(string dir) => new DirectoryInfo(dir).Name;
}
