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
