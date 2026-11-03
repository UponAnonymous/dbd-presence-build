// Covers: C3 (capture adapter seam, pure parts), FR-CAP-02 (normalised regions), PLAN_V2 D17 (any resolution, window modes).
using DbdPresence.Core.Capture;

namespace DbdPresence.Core.Tests.Capture;

public sealed class CaptureSeamTests
{
    [Theory]
    [InlineData(1920, 1080, 0.0, 0.0, 0.5, 0.5, 0, 0, 960, 540)]
    [InlineData(1280, 720, 0.25, 0.25, 0.5, 0.5, 320, 180, 640, 360)]
    [InlineData(3840, 2160, 0.9, 0.9, 0.1, 0.1, 3456, 1944, 384, 216)]
    [InlineData(100, 100, 0.333, 0.333, 0.001, 0.001, 33, 33, 1, 1)]
    public void ToPixels_MapsNormalisedRegionsAtEveryResolution(int w, int h, double x, double y, double rw, double rh, int px, int py, int pw, int ph)
    {
        var rect = new NormalizedRegion(x, y, rw, rh).ToPixels(w, h);

        Assert.Equal(new PixelRect(px, py, pw, ph), rect);
    }

    [Theory]
    [InlineData(-0.1, 0, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(0.6, 0, 0.6, 0.5)]
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    public void ToPixels_RejectsRegionsOutsideTheFrame(double x, double y, double w, double h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRegion(x, y, w, h).ToPixels(100, 100));
    }

    [Fact]
    public void Crop_CopiesTheRequestedPixelsInBgraOrder()
    {
        var bytes = new byte[4 * 3 * 4];
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = ((y * 4) + x) * 4;
                bytes[i] = (byte)(x + 1);      // B
                bytes[i + 1] = (byte)(y + 1);  // G
                bytes[i + 2] = 200;            // R
                bytes[i + 3] = 255;            // A
            }
        }

        var crop = new PixelBuffer(4, 3, bytes).Crop(new PixelRect(1, 1, 2, 2));

        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(((byte)200, (byte)2, (byte)2, (byte)255), crop.GetPixel(0, 0));
        Assert.Equal(((byte)200, (byte)3, (byte)3, (byte)255), crop.GetPixel(1, 1));
    }

    [Fact]
    public void Crop_RefusesRectanglesOutsideTheImage()
    {
        var buffer = new PixelBuffer(2, 2, new byte[16]);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Crop(new PixelRect(1, 1, 2, 2)));
    }

    [Fact]
    public void Constructor_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new PixelBuffer(2, 2, new byte[15]));
    }

    [Fact]
    public void IsEffectivelyBlack_SeesAnyNonBlackPixel()
    {
        var bytes = new byte[16];
        Assert.True(new PixelBuffer(2, 2, bytes).IsEffectivelyBlack());

        bytes[13] = 40;

        Assert.False(new PixelBuffer(2, 2, bytes).IsEffectivelyBlack());
    }

    [Fact]
    public void Classify_TellsFramelessFullMonitorFromWindowedAndHandlesBadBounds()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(WindowDisplayMode.FullscreenOrBorderless, WindowModeClassifier.Classify(monitor, monitor, monitor));
        Assert.Equal(WindowDisplayMode.Windowed, WindowModeClassifier.Classify(new ScreenRect(100, 100, 1380, 860), new ScreenRect(108, 131, 1372, 852), monitor));
        Assert.Equal(WindowDisplayMode.Windowed, WindowModeClassifier.Classify(monitor, new ScreenRect(0, 31, 1920, 1080), monitor));
        Assert.Equal(WindowDisplayMode.Unknown, WindowModeClassifier.Classify(monitor, default, monitor));
    }

    [Fact]
    public void Classify_UsesTheMonitorOfASecondScreen()
    {
        var second = new ScreenRect(1920, 0, 3840, 1080);

        Assert.Equal(WindowDisplayMode.FullscreenOrBorderless, WindowModeClassifier.Classify(second, second, second));
    }
}
