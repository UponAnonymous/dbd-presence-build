// Covers: C2 (vision primitives: image buffer, normalised regions, resolution independence per PLAN_V2 D17).
// Mechanics only: synthetic pixels, no accuracy claims.
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Imaging;

public sealed class ImageBufferTests
{
    private static ImageBuffer Gradient(int w, int h) => ImageBuffer.FromFunction(w, h, (x, y) => (byte)((x * 255 / (w - 1) + y * 0) % 256));

    [Fact]
    public void Constructor_ValidatesSizeAndLength_AndCopiesTheInput()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var image = new ImageBuffer(2, 2, PixelFormat.Gray8, data);
        data[0] = 99;

        Assert.Equal(1, image.GrayAt(0, 0)); // the buffer is immutable: later changes to the source do not show
        Assert.Throws<ArgumentException>(() => new ImageBuffer(2, 2, PixelFormat.Gray8, new byte[3]));
        Assert.Throws<ArgumentException>(() => new ImageBuffer(2, 2, PixelFormat.Rgba32, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBuffer(0, 2, PixelFormat.Gray8, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBuffer(2, 2, (PixelFormat)9, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GrayAt(2, 0));
    }

    [Fact]
    public void Rgba_ConvertsToGrayWithBt601Weights()
    {
        var image = new ImageBuffer(3, 1, PixelFormat.Rgba32, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]);

        Assert.Equal(76, image.GrayAt(0, 0)); // 0.299 * 255
        Assert.Equal(150, image.GrayAt(1, 0)); // 0.587 * 255
        Assert.Equal(29, image.GrayAt(2, 0)); // 0.114 * 255
        Assert.Equal((255, 0, 0), image.RgbAt(0, 0));
        var gray = image.ToGray();
        Assert.Equal(PixelFormat.Gray8, gray.Format);
        Assert.Same(gray, gray.ToGray());
    }

    [Fact]
    public void Crop_CopiesTheRectangle_AndRejectsOutOfBoundsRegions()
    {
        var image = ImageBuffer.FromFunction(10, 8, (x, y) => (byte)((y * 10) + x));

        var crop = image.Crop(new PixelRect(2, 3, 4, 2));

        Assert.Equal((4, 2), (crop.Width, crop.Height));
        Assert.Equal(32, crop.GrayAt(0, 0));
        Assert.Equal(45, crop.GrayAt(3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(new PixelRect(8, 0, 4, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(new PixelRect(-1, 0, 2, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(new PixelRect(0, 0, 0, 2)));
    }

    [Fact]
    public void Crop_KeepsAllFourChannelsOfRgba()
    {
        var image = new ImageBuffer(2, 2, PixelFormat.Rgba32, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        var crop = image.Crop(new PixelRect(1, 1, 1, 1));

        Assert.Equal(PixelFormat.Rgba32, crop.Format);
        Assert.Equal([13, 14, 15, 16], crop.Pixels.ToArray());
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]
    public void NormalisedRegion_SelectsTheSameFraction_AtEveryResolution(int width, int height)
    {
        var region = new NormalizedRect(0.25, 0.5, 0.5, 0.25);

        var pixels = region.ToPixels(width, height);

        Assert.Equal(width / 4.0, pixels.X, precision: 0);
        Assert.Equal(height / 2.0, pixels.Y, precision: 0);
        Assert.Equal(width / 2.0, pixels.Width, precision: 0);
        Assert.Equal(height / 4.0, pixels.Height, precision: 0);
    }

    [Fact]
    public void NormalisedCrop_OfTheSameLayout_IsEquivalentAcrossResolutions()
    {
        // D17: the same layout rendered at two sizes yields crops that, brought to one size, are nearly identical.
        var small = SyntheticScenes.Render(DbdPresence.Core.Detection.Scene.Lobby, 640, noiseOneIn: 0).Crop(SyntheticScenes.LobbyRegion).Resize(128, 64);
        var large = SyntheticScenes.Render(DbdPresence.Core.Detection.Scene.Lobby, 960, noiseOneIn: 0).Crop(SyntheticScenes.LobbyRegion).Resize(128, 64);

        double difference = 0;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                difference += Math.Abs(small.GrayAt(x, y) - large.GrayAt(x, y));
            }
        }

        Assert.True(difference / (128 * 64) < 12, $"mean absolute difference {difference / (128 * 64):0.0} is too large");
    }

    [Fact]
    public void NormalizedRect_RejectsRegionsOutsideTheUnitSquare()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(-0.1, 0, 0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(0.6, 0, 0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(0, 0, 0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedRect(0, 0, double.NaN, 0.5));
        Assert.Equal(new PixelRect(0, 0, 10, 10), new NormalizedRect(0, 0, 1, 1).ToPixels(10, 10));
    }

    [Fact]
    public void ToPixels_NeverReturnsAnEmptyRectangle()
    {
        var tiny = new NormalizedRect(0.999, 0.999, 0.001, 0.001).ToPixels(100, 100);

        Assert.True(tiny.Width >= 1 && tiny.Height >= 1);
        Assert.True(tiny.Right <= 100 && tiny.Bottom <= 100);
    }

    [Fact]
    public void Resize_ShrinkingAveragesTheCoveredArea()
    {
        var image = ImageBuffer.FromFunction(4, 4, (x, _) => (byte)(x < 2 ? 0 : 200));

        var shrunk = image.Resize(2, 2);

        Assert.Equal(0, shrunk.GrayAt(0, 0));
        Assert.Equal(200, shrunk.GrayAt(1, 0));
        Assert.Equal(100, image.Resize(1, 1).GrayAt(0, 0)); // mean of half 0 and half 200
    }

    [Fact]
    public void Resize_EnlargingInterpolates_AndKeepsAConstantImageConstant()
    {
        var constant = ImageBuffer.CreateGray(5, 5, 77).Resize(13, 9);
        Assert.All(Enumerable.Range(0, 13 * 9), i => Assert.Equal(77, constant.GrayAt(i % 13, i / 13)));

        var ramp = Gradient(4, 1).Resize(8, 1);
        Assert.True(Enumerable.Range(0, 7).All(x => ramp.GrayAt(x, 0) <= ramp.GrayAt(x + 1, 0)));
    }

    [Fact]
    public void Resize_ToTheSameSizeReturnsTheSameImage_AndRgbaChannelsStaySeparate()
    {
        var image = ImageBuffer.CreateRgba(4, 4, 10, 20, 30);

        Assert.Same(image, image.Resize(4, 4));
        var small = image.Resize(2, 2);
        Assert.Equal((10, 20, 30), small.RgbAt(1, 1));
        Assert.Equal(2, image.ResizeToWidth(2).Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Resize(0, 1));
    }
}
