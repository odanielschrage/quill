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
    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;

    public Action? OnToggle { get; set; }
    public Action? OnOpenFolder { get; set; }
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
        var quit = new ToolStripMenuItem("Quit quill", null, (_, _) => OnQuit?.Invoke());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([
            _state,
            _transcription,
            new ToolStripSeparator(),
            _toggle,
            openFolder,
            new ToolStripSeparator(),
            quit,
        ]);

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
