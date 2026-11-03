// Covers: C2/C3 seam (FR-DET-13: detectors declare normalised regions, the capture side crops only those; PLAN_V2 D17).
// Mechanics only: synthetic pixels, no accuracy claims.
using DbdPresence.Core.Capture;
using DbdPresence.Vision.Imaging;
using CapturePixelRect = DbdPresence.Core.Capture.PixelRect;
using VisionPixelRect = DbdPresence.Vision.Imaging.PixelRect;

namespace DbdPresence.Vision.Tests.Imaging;

public sealed class CaptureConversionsTests
{
    [Fact]
    public void OpaqueBgraPixels_BecomeRgbaWithTheSameColours()
    {
        // B, G, R, A per pixel, as the capture adapter delivers them.
        var buffer = new PixelBuffer(2, 1, [10, 20, 30, 255, 200, 150, 100, 255]);

        var image = buffer.ToImageBuffer();

        Assert.Equal(PixelFormat.Rgba32, image.Format);
        Assert.Equal((2, 1), (image.Width, image.Height));
        Assert.Equal((30, 20, 10), image.RgbAt(0, 0));
        Assert.Equal((100, 150, 200), image.RgbAt(1, 0));
        Assert.Equal([30, 20, 10, 255, 100, 150, 200, 255], image.Pixels.ToArray());
    }

    [Fact]
    public void PremultipliedAlpha_IsUndone_AndFullyTransparentPixelsAreBlack()
    {
        // R 25, G 100, B 50 premultiplied by alpha 128 of 255: straight values 25*255/128 = 49.8, 199.2 and 99.6, rounded.
        var buffer = new PixelBuffer(2, 1, [50, 100, 25, 128, 7, 7, 7, 0]);

        var image = buffer.ToImageBuffer();

        Assert.Equal([50, 199, 100, 128, 0, 0, 0, 0], image.Pixels.ToArray());
    }

    [Fact]
    public void Regions_RoundTripBetweenTheDetectorAndTheCaptureTypes()
    {
        var rect = new NormalizedRect(0.125, 0.25, 0.5, 0.375);

        var region = rect.ToCaptureRegion();

        Assert.Equal(new NormalizedRegion(0.125, 0.25, 0.5, 0.375), region);
        Assert.True(region.IsValid);
        Assert.Equal(rect, region.ToNormalizedRect());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRegion(0.8, 0, 0.5, 0.5).ToNormalizedRect());
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    public void AlignedRegion_CropsTheSamePixelsOnBothSides(int width, int height)
    {
        var frame = new PixelBuffer(width, height, Pattern(width, height));
        var rect = new NormalizedRect(0.25, 0.5, 0.5, 0.25);

        var capturedCrop = frame.Crop(rect.ToCaptureRegion().ToPixels(width, height)).ToImageBuffer();
        var visionCrop = frame.ToImageBuffer().Crop(rect);

        Assert.Equal((visionCrop.Width, visionCrop.Height), (capturedCrop.Width, capturedCrop.Height));
        Assert.Equal(visionCrop.Pixels.ToArray(), capturedCrop.Pixels.ToArray());
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(800, 600)]
    public void UnalignedRegion_DiffersByAtMostOnePixelPerEdge_BecauseTheRoundingRulesDiffer(int width, int height)
    {
        // The capture side rounds outwards (Core.Capture.NormalizedRegion.ToPixels); the vision side rounds to the nearest
        // pixel (NormalizedRect.ToPixels). The live pipeline crops at the source, so a region can be one pixel wider there.
        var rect = new NormalizedRect(0.1234, 0.4321, 0.3333, 0.0777);

        CapturePixelRect captured = rect.ToCaptureRegion().ToPixels(width, height);
        VisionPixelRect vision = rect.ToPixels(width, height);

        Assert.InRange(vision.X - captured.X, 0, 1);
        Assert.InRange(vision.Y - captured.Y, 0, 1);
        Assert.InRange((captured.X + captured.Width) - vision.Right, 0, 1);
        Assert.InRange((captured.Y + captured.Height) - vision.Bottom, 0, 1);
    }

    private static byte[] Pattern(int width, int height)
    {
        var bytes = new byte[width * height * PixelBuffer.BytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * PixelBuffer.BytesPerPixel;
                bytes[i] = (byte)(x % 251);
                bytes[i + 1] = (byte)(y % 241);
                bytes[i + 2] = (byte)((x + y) % 239);
                bytes[i + 3] = 255;
            }
        }

        return bytes;
    }
}
