using System.Drawing;
using System.Windows.Forms;

namespace Quill.UI;

/// Notification-area icon. Shows recording state at a glance and provides the
/// only persistent control surface for the daemon — the Windows counterpart of
/// the macOS build's NSStatusItem, and likewise the whole UI.
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _state;
    private readonly ToolStripMenuItem _transcription;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _language;
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;

    /// A short list, not Whisper's ninety-nine. Anything else can be typed into
    /// the config file, and a code set that way still shows up checked here.
    private static readonly (string Code, string Label)[] Languages =
    [
        ("auto", "Detect automatically"),
        ("pt", "Português"),
        ("en", "English"),
        ("es", "Español"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("it", "Italiano"),
    ];

    public Action? OnToggle { get; set; }
    public Action? OnOpenFolder { get; set; }
    public Action? OnOpenConfig { get; set; }
    public Action<string>? OnLanguageChosen { get; set; }
    public Action? OnQuit { get; set; }

    public TrayController()
    {
        _idleIcon = FeatherIcon.Idle();
        _recordingIcon = FeatherIcon.Recording();

        _state = new ToolStripMenuItem("idle") { Enabled = false };
        _transcription = new ToolStripMenuItem("") { Enabled = false, Available = false };
        _toggle = new ToolStripMenuItem("Start recording", null, (_, _) => OnToggle?.Invoke());

        var openFolder = new ToolStripMenuItem(
            "Open recordings folder", null, (_, _) => OnOpenFolder?.Invoke());

        // Language earns a menu of its own because it is the one setting whose
        // wrong value produces silently useless output — a Portuguese meeting
        // transcribed as French reads like nonsense with no clue why. Everything
        // else is discoverable from the config file.
        _language = new ToolStripMenuItem("Language");
        var openConfig = new ToolStripMenuItem(
            "Open config file…", null, (_, _) => OnOpenConfig?.Invoke());

        var quit = new ToolStripMenuItem("Quit quill", null, (_, _) => OnQuit?.Invoke());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([
            _state,
            _transcription,
            new ToolStripSeparator(),
            _toggle,
            openFolder,
            new ToolStripSeparator(),
            _language,
            openConfig,
            new ToolStripSeparator(),
            quit,
        ]);

        // The file can change under us — someone edits it, or a second profile
        // via QUILL_CONFIG. Re-read on open so the tick is never stale.
        _menu.Opening += (_, _) => SetLanguage(Config.TranscriptionLanguage());

        _icon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "quill",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        // Windows only opens the menu on right-click by default. The macOS build
        // opens on any click, and doing the same here costs one handler.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _menu.Show(Cursor.Position);
        };
    }

    /// Reflect recording state in the icon, the tooltip and the menu titles.
    /// Called once a second while recording.
    ///
    /// The elapsed counter goes in the tooltip as well as the menu because the
    /// notification area, unlike the macOS menu bar, has no room for inline text.
    public void Update(bool recording, string? elapsed)
    {
        _state.Text = recording ? $"● recording · {elapsed ?? "0:00"}" : "idle";
        _toggle.Text = recording ? "Stop recording" : "Start recording";
        _icon.Icon = recording ? _recordingIcon : _idleIcon;
        // NotifyIcon.Text is capped at 63 characters.
        _icon.Text = recording ? $"quill · recording {elapsed ?? "0:00"}" : "quill";
    }

    /// Rebuild the language menu with `current` ticked.
    ///
    /// A code the user typed into the config by hand — "ru", "ja" — is appended
    /// rather than ignored, so the menu always shows the truth instead of
    /// silently ticking nothing.
    public void SetLanguage(string current)
    {
        // Rebuilt on every menu open, so the old items have to go with it:
        // Clear() detaches without disposing, and this daemon stays up for days.
        foreach (ToolStripItem stale in _language.DropDownItems) stale.Dispose();
        _language.DropDownItems.Clear();

        var known = Languages.Any(l =>
            string.Equals(l.Code, current, StringComparison.OrdinalIgnoreCase));
        var entries = known ? Languages : [.. Languages, (current, current)];

        foreach (var (code, label) in entries)
        {
            var item = new ToolStripMenuItem(
                label, null, (_, _) => OnLanguageChosen?.Invoke(code))
            {
                Checked = string.Equals(code, current, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
            };
            _language.DropDownItems.Add(item);
        }
    }

    /// Show transcription progress or failure as a second status line; null
    /// hides it. Independent of recording state — a new recording can run while
    /// the last one transcribes.
    public void UpdateTranscription(string? text)
    {
        _transcription.Text = text ?? "";
        _transcription.Available = text is not null;
    }

    /// Best-effort toast. A balloon tip is the honest analogue of the macOS
    /// build's `osascript display notification`: a real Windows toast needs an
    /// AUMID and a Start Menu shortcut, which is exactly the app bundle this
    /// project refuses to become.
    public void Balloon(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        // Explicitly hide: a NotifyIcon left visible lingers in the tray as a
        // ghost until the user hovers over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _idleIcon.Dispose();
        _recordingIcon.Dispose();
    }
}
