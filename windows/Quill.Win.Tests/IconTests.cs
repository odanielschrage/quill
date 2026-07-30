using System.Drawing;
using Quill.UI;
using Xunit;

namespace Quill.Tests;

/// The tray icon is drawn in code rather than shipped as a file, so these check
/// that it is a structurally valid multi-size icon. Whether it still *reads* as a
/// feather at 16 px is a human question — `quill icons <dir>` writes the PNGs out
/// for that.
public sealed class FeatherIconTests
{
    [Fact]
    public void IdleIconCarriesEveryTraySize()
    {
        using var icon = FeatherIcon.Idle();

        // Extracting an exact size only succeeds if that frame is really in the
        // .ico; otherwise Windows hands back the nearest one scaled.
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var sized = new Icon(icon, size, size);
            Assert.Equal(size, sized.Width);
            Assert.Equal(size, sized.Height);
        }
    }

    [Fact]
    public void RecordingIconIsRedAndDistinctFromIdle()
    {
        using var idle = FeatherIcon.Idle();
        using var recording = FeatherIcon.Recording();

        using var idleBitmap = new Icon(idle, 32, 32).ToBitmap();
        using var recordingBitmap = new Icon(recording, 32, 32).ToBitmap();

        var idleInk = Ink(idleBitmap);
        var recordingInk = Ink(recordingBitmap);

        Assert.NotEmpty(idleInk);
        Assert.NotEmpty(recordingInk);

        // Red, not merely "some other colour" — the recording state has to be
        // obvious at a glance on a crowded taskbar.
        var average = recordingInk.Aggregate(
            (r: 0, g: 0, b: 0),
            (sum, c) => (sum.r + c.R, sum.g + c.G, sum.b + c.B));
        var count = recordingInk.Count;
        Assert.True(average.r / count > 150, "recording icon should be predominantly red");
        Assert.True(average.r / count > 2 * (average.g / count), "red should dominate green");
    }

    /// The two states must not render identically, or the tray conveys nothing.
    [Fact]
    public void StatesAreVisuallyDifferent()
    {
        using var idle = new Icon(FeatherIcon.Idle(), 32, 32).ToBitmap();
        using var recording = new Icon(FeatherIcon.Recording(), 32, 32).ToBitmap();

        var different = false;
        for (var x = 0; x < 32 && !different; x++)
        {
            for (var y = 0; y < 32; y++)
            {
                if (idle.GetPixel(x, y) == recording.GetPixel(x, y)) continue;
                different = true;
                break;
            }
        }
        Assert.True(different);
    }

    /// Opaque pixels only: the icon is mostly transparent, and averaging the
    /// empty space in would wash out any colour check.
    private static List<Color> Ink(Bitmap bitmap)
    {
        var pixels = new List<Color>();
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 200) pixels.Add(pixel);
            }
        }
        return pixels;
    }
}
