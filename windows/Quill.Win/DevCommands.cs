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
        ["record", "transcribe", "gaptest", "bench", "icons", "vadtest", "devicetest", "clean",
         "status"];

    /// Re-run the hallucination filter over a transcript that already exists, so
    /// a 40-minute transcription doesn't have to happen again to benefit from it.
    ///
    /// Reports by default and only rewrites with --write: this edits a file the
    /// user may already have read and acted on.
    private static int Clean(string sessionDir, bool write)
    {
        var path = Path.Combine(sessionDir, "transcript.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no transcript.json in {sessionDir}");
            return 64;
        }

        var doc = System.Text.Json.JsonSerializer.Deserialize<Transcript>(
            File.ReadAllText(path), Json.Options);
        if (doc is null)
        {
            Console.Error.WriteLine($"couldn't parse {path}");
            return 1;
        }

        // Clean per speaker, matching how it runs in the pipeline: a repeat is
        // only visible as consecutive segments within one track.
        var cleaned = new List<Transcript.Segment>();
        foreach (var speaker in doc.Segments.Select(s => s.Speaker).Distinct())
        {
            var track = doc.Segments.Where(s => s.Speaker == speaker).ToList();
            var asSegments = track
                .Select(s => new TranscriptSegment(
                    TimeSpan.FromMilliseconds(s.StartMs),
                    TimeSpan.FromMilliseconds(s.EndMs),
                    s.Text))
                .ToList();

            var result = TranscriptCleaner.Clean(asSegments, Console.WriteLine);
            cleaned.AddRange(result.Select(s => new Transcript.Segment
            {
                Speaker = speaker,
                StartMs = (int)s.Start.TotalMilliseconds,
                EndMs = (int)s.End.TotalMilliseconds,
                Text = s.Text,
            }));
        }

        var ordered = cleaned.OrderBy(s => s.StartMs).ToList();
        Console.WriteLine();
        Console.WriteLine($"  {doc.Segments.Count} segments → {ordered.Count} "
                          + $"({doc.Segments.Count - ordered.Count} removed)");

        if (!write)
        {
            Console.WriteLine("  (report only — pass --write to rewrite the transcript)");
            return 0;
        }

        new Transcript
        {
            CreatedAt = doc.CreatedAt,
            Engine = doc.Engine,
            Model = doc.Model,
            Segments = ordered,
        }.Write(sessionDir);
        Console.WriteLine($"  rewritten: {path}");
        return 0;
    }

    public static bool Handles(string command) => Names.Contains(command);

    public static async Task<int> RunAsync(string[] args) => args switch
    {
        ["record", ..] => Record(args),
        ["transcribe", var dir, ..] => Transcribe(dir),
        ["gaptest", ..] => GapTest(),
        ["bench", var audio, ..] => await BenchAsync(audio, args.Length > 2 ? args[2] : null),
        ["icons", var dir, ..] => DumpIcons(dir),
        ["clean", var session, ..] => Clean(session, args.Contains("--write")),
        ["vadtest", var speech, ..] => await VadTestAsync(speech),
        ["devicetest", ..] => DeviceTest(),
        _ => Status(),
    };

    /// Phase 7 acceptance check for surviving a device change.
    ///
    /// Unplugging a headset can't be automated, so this drives the same code path
    /// directly: capture loopback with a tone playing throughout, force the
    /// capture to reopen twice mid-recording, and check that the track comes out
    /// full length with audio still arriving at the end. The reopen gap should
    /// show up as ledger padding rather than as a truncated track — that's what
    /// keeps the two tracks sharing a clock when someone swaps headphones.
    private static int DeviceTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quill-devicetest");
        Directory.CreateDirectory(dir);
        var track = Path.Combine(dir, "system.wav");

        using var output = new WasapiOut(AudioClientShareMode.Shared, 100);
        output.Init(new SignalGenerator(48_000, 2)
        {
            Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.2,
        }.ToWaveProvider());

        using var loopback = new SystemAudioRecorder();
        Console.Error.WriteLine("● capturing 12s with a 440 Hz tone, forcing 2 reopens");
        loopback.Start(track);
        output.Play();

        Thread.Sleep(TimeSpan.FromSeconds(4));
        loopback.RequestRestart("simulated device change");
        Thread.Sleep(TimeSpan.FromSeconds(4));
        loopback.RequestRestart("simulated device change");
        Thread.Sleep(TimeSpan.FromSeconds(4));

        output.Stop();
        loopback.Stop();

        var padded = (double)loopback.SamplesPadded / TrackWriter.SampleRate;
        var restarts = loopback.RestartCount;

        double duration, tailPeak;
        using (var reader = new WaveFileReader(track))
        {
            duration = reader.TotalTime.TotalSeconds;
            tailPeak = PeakOfLastSeconds(reader, 2);
        }

        Console.WriteLine();
        Console.WriteLine($"  duration  {duration,6:F2}s  (expected ~12)");
        Console.WriteLine($"  reopens   {restarts,6}    (expected 2)");
        Console.WriteLine($"  padded    {padded,6:F2}s  (the reopen gaps)");
        Console.WriteLine($"  tail peak {tailPeak,6:F3}    (audio still arriving after the last reopen)");
        Console.WriteLine();

        var lengthOk = duration > 10.5;
        var reopenedOk = restarts == 2;
        var stillCapturing = tailPeak > 0.001;

        Console.WriteLine($"  timeline  {(lengthOk ? "PASS" : "FAIL")} — track kept its full length");
        Console.WriteLine($"  reopen    {(reopenedOk ? "PASS" : "FAIL")} — capture came back both times");
        Console.WriteLine($"  audio     {(stillCapturing ? "PASS" : "FAIL")} — recording after the reopen");

        return lengthOk && reopenedOk && stillCapturing ? 0 : 1;
    }

    private static float PeakOfLastSeconds(WaveFileReader reader, int seconds)
    {
        var from = Math.Max(0, reader.Length - (seconds * TrackWriter.SampleRate * 2L));
        reader.Position = from - from % 2;
        return Peak(reader);
    }

    /// Phase 7 acceptance check for skipping silence.
    ///
    /// Builds a track with a known amount of silence in front of real speech, then
    /// transcribes it both ways. The speed-up is the point of the feature, but the
    /// timestamp is the thing that can go silently wrong: transcribing only the
    /// speech hands Whisper a shortened clip numbered from zero, so if the shift
    /// back onto the track's timeline is missing or wrong, the first segment lands
    /// at 0:00 instead of where it was spoken — and the two tracks stop sharing a
    /// clock.
    private static async Task<int> VadTestAsync(string speechPath)
    {
        if (!File.Exists(speechPath))
        {
            Console.Error.WriteLine($"no such file: {speechPath}");
            return 64;
        }

        var lead = TimeSpan.FromSeconds(30);
        var tail = TimeSpan.FromSeconds(15);
        var dir = Path.Combine(Path.GetTempPath(), "quill-vadtest");
        Directory.CreateDirectory(dir);
        var track = Path.Combine(dir, "padded.wav");

        double trackSeconds;
        using (var source = new WaveFileReader(speechPath))
        {
            if (source.WaveFormat.SampleRate != SpeechRegion.SampleRate
                || source.WaveFormat.Channels != 1
                || source.WaveFormat.BitsPerSample != 16)
            {
                Console.Error.WriteLine("vadtest needs a mono 16-bit 16 kHz wav");
                return 64;
            }

            using var writer = new WaveFileWriter(
                track, new WaveFormat(SpeechRegion.SampleRate, 16, 1));
            WriteSilence(writer, lead);
            source.CopyTo(writer);
            WriteSilence(writer, tail);
            trackSeconds = writer.Length / 2.0 / SpeechRegion.SampleRate;
        }

        Console.WriteLine($"track {trackSeconds:F1}s — {lead.TotalSeconds:F0}s silence, "
                          + $"speech, {tail.TotalSeconds:F0}s silence");
        Console.WriteLine($"language {Config.TranscriptionLanguage()} · "
                          + $"model {Config.TranscriptionModel()}");
        Console.WriteLine();

        var whole = await MeasureAsync(track, useVad: false);
        var speechOnly = await MeasureAsync(track, useVad: true);

        Console.WriteLine("  mode        elapsed   skipped   first segment   text");
        Console.WriteLine("  ----------------------------------------------------------");
        Report("whole", whole);
        Report("vad", speechOnly);
        Console.WriteLine();

        // The speech starts exactly `lead` into the track. A correct shift puts
        // the first segment there; a missing one puts it at zero.
        var drift = Math.Abs(speechOnly.FirstSegment.TotalSeconds - lead.TotalSeconds);
        var aligned = drift < 2.0;
        var faster = speechOnly.Elapsed < whole.Elapsed;

        Console.WriteLine($"  timeline  {(aligned ? "PASS" : "FAIL")} — first segment at "
                          + $"{speechOnly.FirstSegment.TotalSeconds:F1}s, expected "
                          + $"~{lead.TotalSeconds:F0}s (drift {drift:F1}s)");
        Console.WriteLine($"  speed     {(faster ? "PASS" : "FAIL")} — "
                          + $"{whole.Elapsed.TotalSeconds:F1}s → {speechOnly.Elapsed.TotalSeconds:F1}s "
                          + $"({whole.Elapsed.TotalSeconds / Math.Max(0.1, speechOnly.Elapsed.TotalSeconds):F2}× faster)");

        return aligned && faster ? 0 : 1;

        static void Report(string mode, Measurement m) => Console.WriteLine(
            $"  {mode,-10} {m.Elapsed.TotalSeconds,7:F1}s {m.Skipped,8:F1}s "
            + $"{m.FirstSegment.TotalSeconds,14:F1}s   {Truncate(m.FirstText, 40)}");
    }

    private readonly record struct Measurement(
        TimeSpan Elapsed, double Skipped, TimeSpan FirstSegment, string FirstText);

    private static async Task<Measurement> MeasureAsync(string track, bool useVad)
    {
        var engine = new WhisperEngine(
            WhisperModels.Resolve(Config.TranscriptionModel()),
            WhisperModels.Quantization,
            Config.TranscriptionLanguage(),
            useVad: useVad);
        try
        {
            await engine.PrepareAsync();
            var clock = Stopwatch.StartNew();
            var segments = await engine.TranscribeAsync(track);
            clock.Stop();

            var first = segments.Count > 0 ? segments[0] : default;
            return new Measurement(
                clock.Elapsed, engine.SecondsSkipped, first.Start, first.Text ?? "");
        }
        finally
        {
            await engine.ReleaseAsync();
        }
    }

    private static void WriteSilence(WaveFileWriter writer, TimeSpan duration)
    {
        var samples = (int)(duration.TotalSeconds * SpeechRegion.SampleRate);
        var chunk = new byte[SpeechRegion.SampleRate * 2];
        while (samples > 0)
        {
            var take = Math.Min(samples, SpeechRegion.SampleRate);
            writer.Write(chunk, 0, take * 2);
            samples -= take;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

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
                          + $"language={Config.TranscriptionLanguage()} "
                          + $"vad={Config.TranscriptionVad()}");
        Console.WriteLine($"  models:     {WhisperModels.CacheDirectory}");
        Console.WriteLine($"              {WhisperModels.VadModel,-15} "
                          + $"{(WhisperModels.IsVadCached() ? "cached" : "not downloaded")}");
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
