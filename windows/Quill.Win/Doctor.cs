using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Quill.Transcription;

namespace Quill;

internal enum CheckLevel { Ok, Warn, Fail }

internal readonly record struct Check(
    string Name, CheckLevel Level, string Detail, string? Remediation = null);

/// Pre-flight checks. Warnings never block; only hard failures do.
///
/// The shape mirrors the macOS build, but the contents barely overlap. The one
/// that simply disappears is system audio: on macOS its TCC state is unknowable
/// without side effects, so `doctor` can only describe the flow. WASAPI loopback
/// needs no consent at all — what it needs is a render endpoint to listen to,
/// which is checkable.
internal static class DoctorReport
{
    private const string ConsentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static IReadOnlyList<Check> Run(string recordingsRoot) =>
    [
        CheckMicrophonePermission(),
        CheckCaptureDevice(),
        CheckRenderDevice(),
        CheckRecordingsRoot(recordingsRoot),
        CheckTranscription(),
    ];

    /// Windows gates the microphone in three places, and any one of them denies.
    /// The one people miss is "Let desktop apps access your microphone", stored
    /// separately under NonPackaged — quill is not a packaged app.
    public static Check CheckMicrophonePermission()
    {
        try
        {
            using var consent = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (consent is null)
            {
                return new Check("microphone", CheckLevel.Warn,
                    "permission state unavailable — will prompt or fail on first recording");
            }

            if (consent.GetValue("Value") as string == "Deny")
            {
                return new Check("microphone", CheckLevel.Fail, "denied for this account",
                    "Settings → Privacy & security → Microphone → turn on microphone access");
            }

            using var nonPackaged = consent.OpenSubKey("NonPackaged");
            if (nonPackaged?.GetValue("Value") as string == "Deny")
            {
                return new Check("microphone", CheckLevel.Fail, "denied for desktop apps",
                    "Settings → Privacy & security → Microphone → "
                    + "\"Let desktop apps access your microphone\"");
            }

            // Per-executable entries key on the full path with backslashes
            // replaced by #.
            var exe = Environment.ProcessPath;
            if (exe is not null && nonPackaged is not null)
            {
                using var mine = nonPackaged.OpenSubKey(exe.Replace('\\', '#'));
                if (mine?.GetValue("Value") as string == "Deny")
                {
                    return new Check("microphone", CheckLevel.Fail, "denied for quill specifically",
                        "Settings → Privacy & security → Microphone → find quill in the list");
                }
            }

            return new Check("microphone", CheckLevel.Ok, "allowed");
        }
        catch (Exception e)
        {
            return new Check("microphone", CheckLevel.Warn, $"couldn't read permission state: {e.Message}");
        }
    }

    public static Check CheckCaptureDevice() =>
        DefaultEndpoint(DataFlow.Capture, "input device",
            "connect a microphone, or set one as default in Settings → System → Sound");

    /// Loopback records whatever the default render endpoint is playing, so with
    /// no output device there is no system track — even though the capture itself
    /// needs no permission.
    public static Check CheckRenderDevice() =>
        DefaultEndpoint(DataFlow.Render, "system audio",
            "connect speakers or headphones — loopback captures the default output device");

    private static Check DefaultEndpoint(DataFlow flow, string name, string remediation)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Console))
            {
                return new Check(name, CheckLevel.Fail, "no default device", remediation);
            }
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            return new Check(name, CheckLevel.Ok, device.FriendlyName);
        }
        catch (Exception e)
        {
            return new Check(name, CheckLevel.Fail, $"enumeration failed: {e.Message}", remediation);
        }
    }

    public static Check CheckRecordingsRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception)
        {
            return new Check("recordings folder", CheckLevel.Fail, $"can't create {root}",
                "check permissions on the parent directory");
        }

        // Writability is not inferable from attributes on Windows; the honest
        // test is to write something.
        var probe = Path.Combine(root, $".quill-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception)
        {
            return new Check("recordings folder", CheckLevel.Fail, $"{root} is not writable",
                "check permissions on the directory");
        }

        return new Check("recordings folder", CheckLevel.Ok, root);
    }

    /// Never discover a missing model after an important meeting.
    public static Check CheckTranscription()
    {
        if (!Config.TranscriptionEnabled())
        {
            return new Check("transcription", CheckLevel.Warn, "disabled in config");
        }

        var model = WhisperModels.Resolve(Config.TranscriptionModel());
        var identifier = WhisperModels.Identifier(model, WhisperModels.Quantization);

        var language = Config.TranscriptionLanguage();
        if (WhisperModels.IsCached(model, WhisperModels.Quantization))
        {
            // Whisper decides the language from the opening seconds, and on a
            // short or noisy greeting it guesses wrong — "alô, alô, tá me
            // escutando" came back as French in testing. Naming the language
            // removes the guess entirely.
            var hint = language is "auto" or ""
                ? "set transcription.language (\"pt\", \"en\", …) — auto-detection "
                  + "misreads short or noisy openings"
                : null;
            return new Check("transcription", CheckLevel.Ok,
                $"{identifier} cached · language {language}", hint);
        }

        var needed = WhisperModels.ApproximateSizeMb(model);
        var free = FreeMegabytes(WhisperModels.CacheDirectory);
        if (free is not null && free < needed * 2)
        {
            return new Check("transcription", CheckLevel.Fail,
                $"{identifier} not downloaded and only {free} MB free (needs ~{needed} MB)",
                "free up disk space, or set transcription.model to a smaller model");
        }

        return new Check("transcription", CheckLevel.Warn,
            $"{identifier} not downloaded (~{needed} MB)",
            "downloads automatically on first transcription — record a short test session while online");
    }

    private static long? FreeMegabytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return null;
            return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Print(IReadOnlyList<Check> checks)
    {
        foreach (var check in checks)
        {
            var mark = check.Level switch
            {
                CheckLevel.Ok => "✓",
                CheckLevel.Warn => "!",
                _ => "✗",
            };
            Console.WriteLine($"{mark} {check.Name}: {check.Detail}");
            if (check.Remediation is { } remediation)
            {
                Console.WriteLine($"    → {remediation}");
            }
        }
    }

    /// True if no check is in a hard-fail state. Warnings don't block.
    public static bool AllOk(IReadOnlyList<Check> checks) =>
        checks.All(c => c.Level != CheckLevel.Fail);
}
