using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill;
using Quill.Audio;

// Placeholder entry point. The real CLI — `run --out`, `doctor`, `install
// --launch-at-login` / `--uninstall` — arrives with the tray daemon.
//
// `record` and `gaptest` are the harnesses this capture layer was verified with.

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
    Console.WriteLine($"  meta.json     {File.ReadAllText(Path.Combine(session.Dir, "meta.json"))
        .Replace("\n", "\n                ", StringComparison.Ordinal)}");
    Console.WriteLine($"  session       {session.Dir}");
    return 0;
}

// The acceptance check for the silence ledger, driven end to end on real
// hardware: capture loopback for 12s while a tone plays only during seconds 0-3
// and 7-10. A correct ledger yields a ~12s track with ~6s of inserted silence.
// Without it the track collapses to the ~6s that were audible, and the system
// transcript's timestamps drift out of step with the mic's.
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

Console.WriteLine("quill (windows) — capture only, no transcription yet");
Console.WriteLine($"  config:     {Config.ExistingPath() ?? "(none)"}");
Console.WriteLine($"              primary  {Config.PrimaryPath}");
Console.WriteLine($"              fallback {Config.FallbackPath}");
Console.WriteLine($"  recordings: {Config.ResolveRoot(null)}");
Console.WriteLine($"  on_stop:    {Config.OnStop() ?? "(none)"}");
Console.WriteLine();
Console.WriteLine("  try: quill record 15 · quill gaptest");
return 0;

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

/// Decoded peak amplitude — the difference between "recorded silence" and
/// "recorded nothing", which is the thing worth knowing after a test capture.
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
