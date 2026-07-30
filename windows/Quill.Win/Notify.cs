namespace Quill;

/// Best-effort user-visible notification.
///
/// The delivery mechanism is a swappable delegate so the pure pipeline (and its
/// tests) don't drag in WinForms. Phase 4 points Handler at the tray icon's
/// balloon tip, which is the honest analogue of the macOS build's
/// `osascript display notification` — a real toast would need an AUMID and a
/// Start Menu shortcut, i.e. exactly the app bundle this project refuses.
internal static class Notify
{
    public static Action<string, string> Handler { get; set; } = static (title, body) =>
        Console.Error.WriteLine($"{title} — {body}");

    public static void User(string title, string body)
    {
        try
        {
            Handler(title, body);
        }
        catch (Exception e)
        {
            // A failed notification must never take down a recording or a
            // transcription job.
            Console.Error.WriteLine($"notification failed: {e.Message}");
        }
    }
}
