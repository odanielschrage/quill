using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using Quill.Audio;
using Quill.Transcription;
using Quill.UI;

namespace Quill;

/// Owns the tray icon, the current recording session, and the elapsed-time
/// ticker. Mirrors the macOS build's AppController; all state transitions happen
/// on the UI thread, except the shutdown path documented below.
internal sealed class AppController : IDisposable
{
    private readonly string _root;
    private readonly TrayController _tray = new();
    private readonly TranscriptionCoordinator _transcription;
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };

    private RecordingSession? _session;

    /// Captured after the tray exists, so the WinForms context is installed.
    /// Both the transcription queue and the notification hook are driven from
    /// background threads, and NotifyIcon is not thread-safe.
    private readonly SynchronizationContext? _ui;

    public AppController(string root)
    {
        _root = root;
        _ui = SynchronizationContext.Current;
        _transcription = new TranscriptionCoordinator(WhisperEngine.FromConfig);

        _tray.OnToggle = Toggle;
        _tray.OnOpenFolder = OpenFolder;
        _tray.OnQuit = Shutdown;
        _tray.Update(recording: false, elapsed: null);

        Notify.Handler = (title, body) => OnUi(() => _tray.Balloon(title, body));
        _ticker.Tick += (_, _) => Tick();

        _transcription.StatusHandler = ShowTranscription;

        // Logoff and shutdown are the Windows counterpart of the macOS build's
        // SIGINT handler: without this, powering off mid-meeting leaves WAVs with
        // no finalized header and no meta.json, so the session is invisible to
        // the transcription queue forever.
        SystemEvents.SessionEnding += OnSessionEnding;
        Console.CancelKeyPress += OnCancelKeyPress;

        Task.Run(() => _transcription.ResumePending(_root));
    }

    /// Stop any live session cleanly (finalizing files) and exit.
    public void Shutdown()
    {
        StopSession();
        System.Windows.Forms.Application.Exit();
    }

    private void Toggle()
    {
        if (_session is null) StartSession();
        else StopSession();
    }

    private void StartSession()
    {
        try
        {
            var session = new RecordingSession(_root, new MicRecorder(), new SystemAudioRecorder());
            session.Start();
            _session = session;
            Console.Error.WriteLine($"● recording → {session.Dir}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"recording start failed: {e}");
            Notify.User("quill — recording failed", e.Message);
            return;
        }

        _tray.Update(recording: true, elapsed: "0:00");
        _ticker.Start();
    }

    private void StopSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;

        session.Stop();
        var elapsed = Format(DateTimeOffset.Now - session.StartedAt);
        Console.Error.WriteLine($"○ stopped · {elapsed} · {session.Dir}");
        session.Dispose();

        _ticker.Stop();
        _tray.Update(recording: false, elapsed: null);

        _transcription.Enqueue(session.Dir);
    }

    /// Runs on the SystemEvents thread, inside the short window Windows allows
    /// before it kills the process.
    ///
    /// This deliberately finalizes the files directly instead of marshalling to
    /// the UI thread: the tray update is the only part that needs the UI thread,
    /// and waiting on a message loop that may already be tearing down is a poor
    /// trade against losing the recording. Transcription is not queued either —
    /// the session now has meta.json and no transcript.json, which is exactly
    /// what ResumePending() looks for on the next launch.
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;

        Console.Error.WriteLine($"session ending ({e.Reason}) — finalizing recording");
        try
        {
            session.Stop();
            session.Dispose();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"shutdown finalize failed: {error}");
        }
    }

    /// Only reachable when a console is attached (the dev harnesses). Mirrors the
    /// macOS build's ^C behaviour.
    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Console.Error.WriteLine();
        Console.Error.WriteLine("shutting down");
        var session = Interlocked.Exchange(ref _session, null);
        session?.Stop();
        session?.Dispose();
        System.Windows.Forms.Application.Exit();
    }

    /// The coordinator publishes from its drain task, not the UI thread.
    private void ShowTranscription(Status status) => OnUi(() =>
        _tray.UpdateTranscription(status.Kind switch
        {
            StatusKind.Transcribing when status.Queued > 0 =>
                $"transcribing {status.Session} · {status.Queued} queued",
            StatusKind.Transcribing => $"transcribing {status.Session}",
            StatusKind.Failed => $"transcription failed · {status.Session}",
            _ => null,
        }));

    /// Marshal onto the UI thread, or run inline when there isn't one (the dev
    /// harnesses drive the pipeline without a message loop).
    private void OnUi(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    private void Tick()
    {
        var session = _session;
        if (session is null) return;
        _tray.Update(recording: true, elapsed: Format(DateTimeOffset.Now - session.StartedAt));
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_root);
            Process.Start(new ProcessStartInfo(_root) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"couldn't open {_root}: {e.Message}");
        }
    }

    private static string Format(TimeSpan interval)
    {
        var total = (int)interval.TotalSeconds;
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }

    public void Dispose()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        Console.CancelKeyPress -= OnCancelKeyPress;
        _ticker.Dispose();
        _tray.Dispose();
    }
}
