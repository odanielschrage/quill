using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill;
using Quill.Audio;
using Quill.Transcription;
using Whisper.net.Ggml;

// Placeholder entry point. The real CLI — `run --out`, `doctor`, `install
// --launch-at-login` / `--uninstall` — arrives with the tray daemon.
//
// These are the harnesses each layer was verified with.

// Full pipeline: capture both tracks, then transcribe them.
if (args is ["record", ..])
{
    var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 15;
    var root = Config.ResolveRoot(null);
    Directory.CreateDirectory(root);

    var mic = new MicRecorder();
    var system = new SystemAudioRecorder();
    using var session = new RecordingSession(root, mic, system);

    Console.Error.WriteLine($"● recording {seconds}s → {session.Dir}");
    session.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    session.Stop();
    Console.Error.WriteLine("○ stopped");

    Console.WriteLine();
    Report(session.Dir, RecordingSession.MicFile, mic.SamplesPadded);
    Report(session.Dir, RecordingSession.SystemFile, system.SamplesPadded);
    Console.WriteLine($"  first buffer  mic {mic.FirstBufferAt:HH:mm:ss.fff}  "
                      + $"system {system.FirstBufferAt?.ToString("HH:mm:ss.fff") ?? "never"}");

    return Transcribe(session.Dir);
}

// Run the queue against an existing session folder. Re-transcription, and how the
// merge/offset path gets exercised without recording a meeting.
if (args is ["transcribe", var sessionDir, ..])
{
    return Transcribe(sessionDir);
}

// The acceptance check for the silence ledger, driven end to end on real
// hardware: capture loopback for 12s while a tone plays only during seconds 0-3
// and 7-10. A correct ledger yields a ~12s track with ~6s of inserted silence.
if (args is ["gaptest", ..])
{
    var root = Path.Combine(Path.GetTempPath(), "quill-gaptest");
    Directory.CreateDirectory(root);
    var track = Path.Combine(root, "system.wav");

    using var loopback = new SystemAudioRecorder();
    Console.Error.WriteLine("● capturing loopback 12s — a 440 Hz tone will play twice");
    loopback.Start(track);

    PlayTone(TimeSpan.FromSeconds(3));      //  0s →  3s  audible
    Thread.Sleep(TimeSpan.FromSeconds(4));  //  3s →  7s  silent, no callbacks
    PlayTone(TimeSpan.FromSeconds(3));      //  7s → 10s  audible
    Thread.Sleep(TimeSpan.FromSeconds(2));  // 10s → 12s  silent

    loopback.Stop(); // closes the trailing silence, so read the ledger after it
    var padded = (double)loopback.SamplesPadded / TrackWriter.SampleRate;

    using var captured = new WaveFileReader(track);
    var duration = captured.TotalTime.TotalSeconds;
    Console.WriteLine();
    Console.WriteLine($"  duration {duration,6:F2}s  (expected ~12)");
    Console.WriteLine($"  padded   {padded,6:F2}s  (expected ~6)");
    Console.WriteLine($"  verdict  "
                      + (duration > 10.5 ? "PASS — timeline preserved" : "FAIL — silence collapsed"));
    return duration > 10.5 ? 0 : 1;
}

// Times every model against one file so the default comes from measurement
// rather than from the Apple Silicon numbers in the macOS README, which do not
// transfer to CPU.
if (args is ["bench", var audioPath, ..])
{
    if (!File.Exists(audioPath))
    {
        Console.Error.WriteLine($"no such file: {audioPath}");
        return 64;
    }

    var reference = args.Length > 2 && File.Exists(args[2])
        ? Normalize(File.ReadAllText(args[2]))
        : null;

    double audioSeconds;
    using (var probe = new WaveFileReader(audioPath)) audioSeconds = probe.TotalTime.TotalSeconds;

    var language = Config.TranscriptionLanguage();
    Console.WriteLine($"audio {audioSeconds:F1}s · language {language} · "
                      + $"{Environment.ProcessorCount} logical cores");
    Console.WriteLine();
    Console.WriteLine("  model            load     run     xRT   WER   segments");
    Console.WriteLine("  ---------------------------------------------------------");

    GgmlType[] candidates =
    [
        GgmlType.Tiny, GgmlType.Base, GgmlType.Small, GgmlType.Medium, GgmlType.LargeV3Turbo,
    ];

    var transcripts = new List<(string Model, string Text)>();
    foreach (var type in candidates)
    {
        var engine = new WhisperEngine(type, WhisperModels.Quantization, language);
        try
        {
            var loading = Stopwatch.StartNew();
            await engine.PrepareAsync();
            loading.Stop();

            var running = Stopwatch.StartNew();
            var segments = await engine.TranscribeAsync(audioPath);
            running.Stop();

            var text = string.Join(" ", segments.Select(s => s.Text));
            var realtime = audioSeconds / running.Elapsed.TotalSeconds;
            var wer = reference is null
                ? "  —  "
                : $"{100.0 * WordErrorRate(reference, Normalize(text)):F1}%";

            Console.WriteLine(
                $"  {WhisperModels.Slug(type),-14} "
                + $"{loading.Elapsed.TotalSeconds,6:F1}s {running.Elapsed.TotalSeconds,6:F1}s "
                + $"{realtime,6:F2} {wer,6} {segments.Count,6}");
            transcripts.Add((WhisperModels.Slug(type), text));
        }
        catch (Exception e)
        {
            Console.WriteLine($"  {WhisperModels.Slug(type),-14} failed: {e.Message}");
        }
        finally
        {
            await engine.ReleaseAsync();
        }
    }

    Console.WriteLine();
    Console.WriteLine("xRT = seconds of audio per second of compute; higher is faster.");
    Console.WriteLine();
    foreach (var (model, text) in transcripts)
    {
        Console.WriteLine($"[{model}] {text}");
        Console.WriteLine();
    }
    return 0;
}

Console.WriteLine("quill (windows) — capture + transcription, no tray yet");
Console.WriteLine($"  config:     {Config.ExistingPath() ?? "(none)"}");
Console.WriteLine($"  recordings: {Config.ResolveRoot(null)}");
Console.WriteLine($"  transcribe: enabled={Config.TranscriptionEnabled()} "
                  + $"model={Config.TranscriptionModel()} "
                  + $"language={Config.TranscriptionLanguage()}");
Console.WriteLine($"  models:     {WhisperModels.CacheDirectory}");
Console.WriteLine();
Console.WriteLine("  try: quill record 15 · quill gaptest · quill bench <wav> [ref.txt]");
return 0;

static int Transcribe(string sessionDir)
{
    if (!File.Exists(Path.Combine(sessionDir, "meta.json")))
    {
        Console.Error.WriteLine($"not a session folder (no meta.json): {sessionDir}");
        return 64;
    }

    // Wait on the queue's own signal rather than polling for the file: a job that
    // fails never produces one. ManualResetEventSlim latches, so a drain that
    // finishes before the wait starts is not a race.
    using var finished = new ManualResetEventSlim(false);
    var queue = new TranscriptionCoordinator(WhisperEngine.FromConfig)
    {
        StatusHandler = status =>
        {
            Console.Error.WriteLine($"  [{status.Kind}] {status.Session}");
            if (status.Kind is StatusKind.Idle or StatusKind.Failed) finished.Set();
        },
    };
    queue.Enqueue(sessionDir);

    if (!finished.Wait(TimeSpan.FromHours(1)))
    {
        Console.Error.WriteLine("timed out waiting for the transcription queue");
    }

    var output = Path.Combine(sessionDir, "transcript.md");
    Console.WriteLine();
    Console.WriteLine(File.Exists(output) ? File.ReadAllText(output) : "no transcript produced");
    var log = Path.Combine(sessionDir, "transcribe.log");
    if (File.Exists(log)) Console.WriteLine(File.ReadAllText(log));
    Console.WriteLine($"  session  {sessionDir}");
    return File.Exists(output) ? 0 : 1;
}

/// Audible tone through the default render device, then a full stop so the
/// endpoint goes idle — which is what makes loopback stop delivering buffers and
/// gives the ledger something to close.
static void PlayTone(TimeSpan duration)
{
    using var output = new WasapiOut(AudioClientShareMode.Shared, 100);
    var tone = new SignalGenerator(48_000, 2)
    {
        Type = SignalGeneratorType.Sin,
        Frequency = 440,
        Gain = 0.2,
    };
    output.Init(tone.ToWaveProvider());
    output.Play();
    Thread.Sleep(duration);
    output.Stop();
}

static void Report(string dir, string file, long samplesPadded)
{
    var path = Path.Combine(dir, file);
    if (!File.Exists(path))
    {
        Console.WriteLine($"  {file,-12} missing");
        return;
    }

    try
    {
        using var reader = new WaveFileReader(path);
        var padded = (double)samplesPadded / TrackWriter.SampleRate;
        Console.WriteLine(
            $"  {file,-12} {reader.TotalTime.TotalSeconds,6:F2}s  "
            + $"{new FileInfo(path).Length / 1024,6} KB  "
            + $"padded {padded,6:F2}s  peak {Peak(reader):F3}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"  {file,-12} unreadable: {e.Message}");
    }
}

static float Peak(WaveFileReader reader)
{
    var peak = 0f;
    var frame = reader.ReadNextSampleFrame();
    while (frame is not null)
    {
        foreach (var sample in frame)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }
        frame = reader.ReadNextSampleFrame();
    }
    return peak;
}

/// Lowercase, strip punctuation, split on whitespace — so WER measures words
/// rather than comma placement. Accents are kept: they are part of the word in
/// Portuguese.
static string[] Normalize(string text)
{
    var builder = new StringBuilder(text.Length);
    foreach (var character in text.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(character)) builder.Append(character);
        else if (char.IsWhiteSpace(character) || character is '-') builder.Append(' ');
    }
    return builder.ToString()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static double WordErrorRate(string[] reference, string[] hypothesis)
{
    if (reference.Length == 0) return hypothesis.Length == 0 ? 0 : 1;

    var distance = new int[reference.Length + 1, hypothesis.Length + 1];
    for (var i = 0; i <= reference.Length; i++) distance[i, 0] = i;
    for (var j = 0; j <= hypothesis.Length; j++) distance[0, j] = j;

    for (var i = 1; i <= reference.Length; i++)
    {
        for (var j = 1; j <= hypothesis.Length; j++)
        {
            var substitution = distance[i - 1, j - 1]
                               + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1);
            distance[i, j] = Math.Min(
                Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), substitution);
        }
    }
    return (double)distance[reference.Length, hypothesis.Length] / reference.Length;
}
