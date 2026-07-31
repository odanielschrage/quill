using System.Text.Json.Nodes;

namespace Quill;

/// Optional user config. The Windows-native location is
/// %APPDATA%\quill\config.json; ~/.config/quill/config.json is also read so a
/// synced dotfiles setup works unchanged on both platforms.
///
///     {
///       "recordings_dir": "~/Recordings",
///       "transcription": {
///         "enabled": true,
///         "engine": "whisper",
///         "model": "small",
///         "language": "auto"
///       },
///       "mic_voice_processing": false,
///       "on_stop": "my-hook"
///     }
///
/// Resolution order for the recordings root: --out flag > config file >
/// %USERPROFILE%\Recordings.
///
/// Like the macOS build, every accessor re-reads the file rather than caching
/// it, so editing the config takes effect on the next recording without
/// restarting the daemon. The file is a few hundred bytes.
internal static class Config
{
    /// Native location, and the one `quill install` documents.
    public static string PrimaryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "quill",
        "config.json");

    /// The macOS path, honored so one dotfiles repo can serve both platforms.
    public static string FallbackPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "quill",
        "config.json");

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Recordings");

    /// Environment override, checked before either standard location. Lets you
    /// run a second profile (`QUILL_CONFIG=...\meetings.json quill run`) without
    /// touching the installed config.
    public const string PathVariable = "QUILL_CONFIG";

    /// The config file actually in use, or null when none exists.
    public static string? ExistingPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrEmpty(overridePath))
        {
            var expanded = ExpandPath(overridePath);
            return File.Exists(expanded) ? expanded : null;
        }
        if (File.Exists(PrimaryPath)) return PrimaryPath;
        if (File.Exists(FallbackPath)) return FallbackPath;
        return null;
    }

    /// The configured recordings root, or null if no config file / no key.
    public static string? RecordingsDir()
    {
        var dir = Load()?["recordings_dir"]?.GetValue<string>();
        return string.IsNullOrEmpty(dir) ? null : ExpandPath(dir);
    }

    /// Shell command to spawn after each session's transcript is written (or
    /// after recording, if transcription is disabled), or null.
    public static string? OnStop()
    {
        var cmd = Load()?["on_stop"]?.GetValue<string>();
        return string.IsNullOrEmpty(cmd) ? null : cmd;
    }

    /// Whether finished recordings are transcribed automatically. Default on.
    public static bool TranscriptionEnabled() =>
        Transcription()?["enabled"]?.GetValue<bool>() ?? true;

    /// Configured engine name. Only "whisper" ships on Windows; the coordinator
    /// warns and falls back for anything else.
    public static string TranscriptionEngine() =>
        Transcription()?["engine"]?.GetValue<string>() ?? "whisper";

    /// GGML model size — the speed/accuracy dial. Default set by the phase 3
    /// benchmark rather than by assumption; "small" until that lands.
    public static string TranscriptionModel() =>
        Transcription()?["model"]?.GetValue<string>() ?? "small";

    /// Spoken language hint, or "auto" to let Whisper detect it. Windows-only
    /// key: the macOS Parakeet engine is English-only and has no equivalent.
    public static string TranscriptionLanguage() =>
        Transcription()?["language"]?.GetValue<string>() ?? "auto";

    /// Skip silence before inference using Silero VAD. Default on: the system
    /// track is largely silence the capture ledger inserted, and Whisper
    /// otherwise pays to transcribe it. Set false to feed every second of both
    /// tracks to the model.
    public static bool TranscriptionVad() =>
        Transcription()?["vad"]?.GetValue<bool>() ?? true;

    /// Remove mic segments that merely echo the system track, once both are
    /// transcribed. Default on: recording through speakers otherwise puts the
    /// other person's words in the transcript twice, the second time attributed
    /// to you. Costs nothing on headphones, where there is no echo to find.
    public static bool TranscriptionEchoSuppression() =>
        Transcription()?["echo_suppression"]?.GetValue<bool>() ?? true;

    /// Acoustic echo cancellation on the mic. Default off, matching macOS after
    /// rca-001; the Windows Voice Capture DSP is a post-MVP phase.
    public static bool MicVoiceProcessing() =>
        Load()?["mic_voice_processing"]?.GetValue<bool>() ?? false;

    /// Resolve the recordings root from an optional CLI override.
    public static string ResolveRoot(string? cliOverride) =>
        !string.IsNullOrEmpty(cliOverride)
            ? ExpandPath(cliOverride)
            : RecordingsDir() ?? DefaultRoot;

    /// Expand both conventions a user might reasonably write here: a leading ~
    /// (portable, and what the macOS config uses) and %VAR% (what a Windows
    /// user expects). Neither platform's config has to be rewritten.
    public static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal)
            || expanded.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length <= 1 ? home : Path.Combine(home, expanded[2..]);
        }
        return expanded;
    }

    /// The file a write lands in.
    ///
    /// An explicit QUILL_CONFIG wins even when that file doesn't exist yet —
    /// ExistingPath() deliberately returns null for a missing override, which is
    /// right for reading and wrong for writing: falling back would create a
    /// config at the native path and quietly ignore the override the user asked
    /// for. Otherwise it's whichever config is already in use, because writing to
    /// the primary path while one sits in ~/.config would shadow it silently.
    public static string PathToWrite()
    {
        var overridePath = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrEmpty(overridePath)) return ExpandPath(overridePath);
        return ExistingPath() ?? PrimaryPath;
    }

    /// Create the config with a commented starter if none exists, and return the
    /// path either way. The comments are the point — the tray menu opens this
    /// file, and someone who has never seen it needs to learn what can go in it.
    public static string EnsureExists()
    {
        if (ExistingPath() is { } existing) return existing;

        var path = PathToWrite();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Template, Json.Utf8NoBom);
        return path;
    }

    /// Change one setting, preserving every other key in the file.
    ///
    /// Comments do not survive this. The parser accepts them but System.Text.Json
    /// cannot round-trip them, so anyone who annotates their config by hand
    /// should edit the file rather than use the menu — which is exactly why the
    /// menu offers to open it.
    public static void Update(Action<JsonObject> mutate)
    {
        var path = PathToWrite();
        var root = Load();

        // A config that exists but doesn't parse must never be silently replaced
        // with a fresh one — that would throw away settings the user can still
        // rescue by fixing a stray comma.
        if (root is null && File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{path} is not valid JSON — fix it by hand before changing settings here");
        }

        root ??= new JsonObject();
        mutate(root);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        // Temp then rename, so an interrupted write can't leave a truncated
        // config that stops quill reading any of its settings.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json + Environment.NewLine, Json.Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// Set one key inside the "transcription" object, creating it if absent.
    public static void SetTranscription(string key, string value) => Update(root =>
    {
        if (root["transcription"] is not JsonObject transcription)
        {
            transcription = [];
            root["transcription"] = transcription;
        }
        transcription[key] = value;
    });

    private const string Template = """
        {
          // Where sessions land. --out overrides this.
          // "recordings_dir": "~/Recordings",

          "transcription": {
            // false to record without transcribing.
            "enabled": true,

            // Spoken language: "pt", "en", "es", … or "auto".
            // Naming it is worth doing — auto-detection reads only the opening
            // seconds and misjudges a short or noisy start.
            "language": "auto",

            // tiny · base · small · medium · large-v3-turbo
            // Bigger is more accurate and much slower. Run `quill bench` before
            // assuming; on a slow machine "base" is often the right answer.
            "model": "small",

            // Skip silence before transcribing. Also stops Whisper inventing
            // content in long quiet stretches.
            "vad": true,

            // Drop mic segments that are just the far end coming back through
            // the speakers. Every removal is logged in the session's
            // transcribe.log.
            "echo_suppression": true
          },

          // Command run with the session folder as its argument, after the
          // transcript is written. Wire it to summarising, filing, indexing.
          // "on_stop": "python summarise.py"
        }

        """;

    // MARK: -

    private static JsonObject? Transcription() => Load()?["transcription"] as JsonObject;

    /// Parse the config file. A malformed config is reported on stderr rather
    /// than silently ignored — recordings landing in an unexpected place is
    /// worse than a warning.
    private static JsonObject? Load()
    {
        var path = ExistingPath();
        if (path is null) return null;

        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                });
        }
        catch (Exception)
        {
            // Fall through to the same warning as a structurally wrong config.
        }

        if (node as JsonObject is { } obj) return obj;

        Console.Error.WriteLine($"warning: {path} is not valid JSON — ignoring config");
        return null;
    }
}
