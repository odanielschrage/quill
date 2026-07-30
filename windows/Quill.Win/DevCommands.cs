using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Quill.Audio;
using Quill.Transcription;
using Whisper.net.Ggml;

namespace Quill;

/// The harnesses each phase was verified with. Not the product CLI — that's
/// phase 5 — but worth keeping: `gaptest` is the R1 acceptance check and `bench`
/// is where the model defaults come from.
internal static class DevCommands
{
    private static readonly string[] Names =
        ["record", "transcribe", "gaptest", "bench", "icons", "status"];

    public static bool Handles(string command) => Names.Contains(command);

    public static async Task<int> RunAsync(string[] args) => args switch
    {
        ["record", ..] => Record(args),
        ["transcribe", var dir, ..] => Transcribe(dir),
        ["gaptest", ..] => GapTest(),
        ["bench", var audio, ..] => await BenchAsync(audio, args.Length > 2 ? args[2] : null),
        ["icons", var dir, ..] => DumpIcons(dir),
        _ => Status(),
    };

    /// Write the generated tray icons out so they can actually be looked at.
    /// The feather is drawn in code rather than shipped as a file, so "does it
    /// still read as a feather at 16 px" is not something the compiler checks.
    private static int DumpIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, icon) in
                 new[] { ("idle", UI.FeatherIcon.Idle()), ("recording", UI.FeatherIcon.Recording()) })
        {
            using (icon)
            {
                foreach (var size in new[] { 16, 32 })
                {
                    using var sized = new System.Drawing.Icon(icon, size, size);
                    using var bitmap = sized.ToBitmap();
                    var path = Path.Combine(dir, $"{name}-{size}.png");
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"  {path}");
                }
            }
        }
        return 0;
    }

    /// Full pipeline: capture both tracks, then transcribe them.
    private static int Record(string[] args)
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

    /// Run the queue against an existing session folder. Re-transcription, and
    /// how the merge/offset path gets exercised without recording a meeting.
    private static int Transcribe(string sessionDir)
    {
        if (!File.Exists(Path.Combine(sessionDir, "meta.json")))
        {
            Console.Error.WriteLine($"not a session folder (no meta.json): {sessionDir}");
            return 64;
        }

        // Wait on the queue's own signal rather than polling for the file: a job
        // that fails never produces one, and polling turned a clear error in
        // transcribe.log into an hour-long hang. ManualResetEventSlim latches, so
        // a drain that finishes before the wait starts is not a race.
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

        var output = Path.Combine(sessionDir, "transcript.md");
        if (!finished.Wait(TimeSpan.FromHours(1)))
        {
            Console.Error.WriteLine("timed out waiting for the transcription queue");
        }

        Console.WriteLine();
        Console.WriteLine(File.Exists(output) ? File.ReadAllText(output) : "no transcript produced");
        var log = Path.Combine(sessionDir, "transcribe.log");
        if (File.Exists(log)) Console.WriteLine(File.ReadAllText(log));
        Console.WriteLine($"  session  {sessionDir}");
        return File.Exists(output) ? 0 : 1;
    }

    /// Phase 2 acceptance check for R1 on real hardware: capture loopback for 12s
    /// while a tone plays only during seconds 0-3 and 7-10. A correct ledger
    /// yields a ~12s track with ~6s of inserted silence; without it the track
    /// collapses to the ~6s that were audible, and the system transcript's
    /// timestamps drift out of step with the mic's.
    private static int GapTest()
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

    /// Phase 3 deliverable. Times every model against one file so the default
    /// comes from measurement rather than from the Apple Silicon numbers in the
    /// macOS README, which do not transfer to CPU.
    private static async Task<int> BenchAsync(string audioPath, string? referencePath)
    {
        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine($"no such file: {audioPath}");
            return 64;
        }

        var reference = referencePath is not null && File.Exists(referencePath)
            ? Normalize(File.ReadAllText(referencePath))
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

    private static int Status()
    {
        Console.WriteLine("quill (windows) — capture, transcription, tray, CLI");
        Console.WriteLine($"  config:     {Config.ExistingPath() ?? "(none)"}");
        Console.WriteLine($"              primary  {Config.PrimaryPath}");
        Console.WriteLine($"              fallback {Config.FallbackPath}");
        Console.WriteLine($"  recordings: {Config.ResolveRoot(null)}");
        Console.WriteLine($"  transcribe: enabled={Config.TranscriptionEnabled()} "
                          + $"engine={Config.TranscriptionEngine()} "
                          + $"model={Config.TranscriptionModel()} "
                          + $"language={Config.TranscriptionLanguage()}");
        Console.WriteLine($"  models:     {WhisperModels.CacheDirectory}");
        foreach (var name in new[] { "tiny", "base", "small", "medium", "large-v3-turbo" })
        {
            var cached = WhisperModels.IsCached(
                WhisperModels.Resolve(name), WhisperModels.Quantization);
            Console.WriteLine($"              {name,-15} {(cached ? "cached" : "not downloaded")}");
        }
        Console.WriteLine($"  on_stop:    {Config.OnStop() ?? "(none)"}");
        Console.WriteLine();
        Console.WriteLine("  quill                       run the tray daemon");
        Console.WriteLine("  quill record 15             capture + transcribe a test session");
        Console.WriteLine("  quill transcribe <dir>      re-run the queue on a session folder");
        Console.WriteLine("  quill gaptest               R1 acceptance check");
        Console.WriteLine("  quill bench <wav> [ref.txt] time every model");
        return 0;
    }

    // MARK: -

    /// Audible tone through the default render device, then a full stop so the
    /// endpoint goes idle — which is what makes loopback stop delivering buffers
    /// and gives the ledger something to close.
    private static void PlayTone(TimeSpan duration)
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

    private static void Report(string dir, string file, long samplesPadded)
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
    private static float Peak(WaveFileReader reader)
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
    /// rather than comma placement. Accents are kept: they are part of the word
    /// in Portuguese.
    private static string[] Normalize(string text)
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

    private static double WordErrorRate(string[] reference, string[] hypothesis)
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
}
