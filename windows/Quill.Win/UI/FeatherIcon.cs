using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace Quill.UI;

/// The Lucide feather, drawn with GDI+ and packed into an in-memory .ico.
///
/// The macOS build inlines the SVG and lets NSImage.isTemplate handle tinting
/// and light/dark adaptation. Windows has neither: NotifyIcon wants an Icon, and
/// the notification area is whatever colour the user's theme makes it. So the
/// path is transcribed from the same SVG and the colour is chosen from the
/// system theme — a white feather vanishes on a light taskbar and a black one
/// vanishes on a dark one.
///
/// Drawing it rather than shipping .ico files keeps the executable genuinely
/// self-contained, which is the same reason the macOS build inlines its SVG.
internal static class FeatherIcon
{
    /// The SVG's viewBox. All coordinates below are in these units and scaled to
    /// whatever pixel size is being rendered.
    private const float Grid = 24f;

    /// Red reads as "live" on both a light and a dark taskbar, so unlike the
    /// idle colour it needs no theme check.
    private static readonly Color RecordingRed = Color.FromArgb(232, 62, 62);

    /// Sizes baked into the .ico. 16 is the notification area at 100% DPI, 32
    /// covers 200% and the "show hidden icons" flyout.
    private static readonly int[] Sizes = [16, 20, 24, 32];

    public static Icon Idle() => Build(ForegroundForTaskbar());

    public static Icon Recording() => Build(RecordingRed);

    /// Windows 10/11 expose the taskbar's light/dark choice separately from the
    /// app theme; absent key means dark, which is the shipping default.
    private static Color ForegroundForTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var lightTaskbar = key?.GetValue("SystemUsesLightTheme") as int? ?? 0;
            return lightTaskbar == 1 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240);
        }
        catch (Exception)
        {
            return Color.FromArgb(240, 240, 240);
        }
    }

    private static Icon Build(Color color)
    {
        var frames = Sizes.Select(size => Render(size, color)).ToArray();
        using var stream = new MemoryStream();
        WriteIco(stream, frames);
        stream.Position = 0;
        return new Icon(stream);
    }

    private static byte[] Render(int size, Color color)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var scale = size / Grid;
            g.ScaleTransform(scale, scale);

            // Heavier than the SVG's 1.5: at 16 px a hairline outline turns to
            // mush, and the tray is mostly viewed at 16 px.
            using var pen = new Pen(color, 2.1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            using var blade = new GraphicsPath();
            blade.AddLine(6f, 19f, 14.09f, 18.41f);
            blade.AddLine(14.09f, 18.41f, 20.24f, 12.24f);
            // The SVG's `a6 6 0 0 0-8.49-8.49`: a half-turn of the circle centred
            // on (16,8) with r=6, bulging up and to the right. Negative sweep so
            // it runs counter-clockwise, through 315°, rather than cutting across.
            blade.AddArc(10f, 2f, 12f, 12f, 45f, -180f);
            blade.AddLine(11.75f, 3.75f, 5f, 11.33f);
            blade.AddLine(5f, 11.33f, 5f, 18f);
            blade.CloseFigure();
            g.DrawPath(pen, blade);

            // The shaft, running past the blade to the quill point.
            g.DrawLine(pen, 16f, 8f, 2.6f, 21.4f);
            // The vane crossbar.
            g.DrawLine(pen, 17.5f, 15f, 9f, 15f);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    /// Minimal multi-size .ico around PNG frames — the format Vista and later
    /// accept directly, so there's no BMP/AND-mask encoding to get wrong.
    private static void WriteIco(Stream stream, byte[][] frames)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)frames.Length);

        var offset = 6 + (16 * frames.Length);
        for (var i = 0; i < frames.Length; i++)
        {
            var size = Sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // palette size: none
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // colour planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames) writer.Write(frame);
        writer.Flush();
    }
}
