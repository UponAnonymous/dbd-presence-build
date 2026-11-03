// Covers: PLAN_V2 D17 (windowed mode in scope), FR-CAP-02 (normalised regions map onto the game's own picture), SAFETY_AND_PRIVACY 2.1 (window metadata only).
// Mechanics only: these are rectangles, not game pictures, and say nothing about detector accuracy.
using DbdPresence.Core.Capture;

namespace DbdPresence.Core.Tests.Capture;

public sealed class ClientAreaCropTests
{
    // The owner's windowed study recordings: 1922x1112 around a 1920x1080 game (1 px border each side and below, 31 px title bar).
    private static readonly ScreenRect s_ownerFrame = new(100, 50, 2022, 1162);
    private static readonly ScreenRect s_ownerClient = new(101, 81, 2021, 1161);

    [Fact]
    public void TheOwnersWindowedGame_CutsTheTitleBarAndBorder()
    {
        var rect = ClientAreaCrop.Compute(s_ownerFrame, s_ownerClient, 1922, 1112);

        Assert.Equal(new PixelRect(1, 31, 1920, 1080), rect);
    }

    [Fact]
    public void AWindowWithoutAFrame_KeepsTheWholeFrame()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(new PixelRect(0, 0, 1920, 1080), ClientAreaCrop.Compute(screen, screen, 1920, 1080));
    }

    [Fact]
    public void ADifferentCapturedSizeWithinTheTolerance_IsStillTheSameWindow()
    {
        var rect = ClientAreaCrop.Compute(s_ownerFrame, s_ownerClient, 1921, 1113);

        Assert.Equal(new PixelRect(1, 31, 1920, 1080), rect);
    }

    [Fact]
    public void AProcessThatIsNotDpiAware_ScalesTheOffsetsUniformly()
    {
        // Bounds reported at 100% (1922x1112 virtual units) while the capture is 150% larger.
        var rect = ClientAreaCrop.Compute(s_ownerFrame, s_ownerClient, 2883, 1668);

        Assert.Equal(new PixelRect(2, 47, 2880, 1620), rect);
    }

    [Fact]
    public void ANonUniformScale_IsRefusedNotGuessed()
    {
        Assert.Null(ClientAreaCrop.Compute(s_ownerFrame, s_ownerClient, 2883, 1112));
    }

    [Theory]
    [InlineData(99, 81, 2021, 1161)] // client starts left of the frame
    [InlineData(101, 49, 2021, 1161)] // client starts above the frame
    [InlineData(101, 81, 2030, 1161)] // client ends far right of the frame
    [InlineData(101, 81, 2021, 1170)] // client ends far below the frame
    public void AClientRectangleOutsideTheFrame_IsRefused(int left, int top, int right, int bottom)
    {
        Assert.Null(ClientAreaCrop.Compute(s_ownerFrame, new ScreenRect(left, top, right, bottom), 1922, 1112));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(5, 5, 5, 9)]
    public void AnEmptyClientRectangle_IsRefused(int left, int top, int right, int bottom)
    {
        Assert.Null(ClientAreaCrop.Compute(s_ownerFrame, new ScreenRect(left, top, right, bottom), 1922, 1112));
    }

    [Fact]
    public void AnEmptyFrameOrCapture_IsRefused()
    {
        Assert.Null(ClientAreaCrop.Compute(default, s_ownerClient, 1922, 1112));
        Assert.Null(ClientAreaCrop.Compute(s_ownerFrame, s_ownerClient, 0, 1112));
    }

    [Fact]
    public void TheCroppedRectangle_NeverLeavesTheCapturedFrame()
    {
        // The client area may end a pixel or two past the captured frame; the crop is clamped to what was captured.
        var client = new ScreenRect(101, 81, 2023, 1163);

        var rect = ClientAreaCrop.Compute(s_ownerFrame, client, 1922, 1112);

        Assert.NotNull(rect);
        Assert.True(rect.Value.X + rect.Value.Width <= 1922);
        Assert.True(rect.Value.Y + rect.Value.Height <= 1112);
    }

    [Fact]
    public void CropAndRegions_PutTheNormalisedRegionOnTheGamesOwnPicture()
    {
        // A 1922x1112 frame whose game area is red and whose title bar and border are white. A region cut after the client-area crop is all red;
        // the same region cut from the whole frame reaches into the title bar. This is the failure the crop exists to prevent.
        var frame = Frame(1922, 1112, (x, y) => x >= 1 && x < 1921 && y >= 31 && y < 1111 ? (byte)255 : (byte)0);
        var rect = ClientAreaCrop.Compute(s_ownerFrame, s_ownerClient, 1922, 1112)!.Value;
        var region = new NormalizedRegion(0.0, 0.0, 0.05, 0.05);

        var inGame = frame.Crop(rect).Crop(region.ToPixels(rect.Width, rect.Height));
        var wholeFrame = frame.Crop(region.ToPixels(frame.Width, frame.Height));

        Assert.True(AllRed(inGame));
        Assert.False(AllRed(wholeFrame));
    }

    [Fact]
    public void TheCaptureState_SaysWhetherTheFrameIsClientArea_AndDefaultsToNo()
    {
        Assert.False(new CaptureState(CaptureStatus.Ready, "x").ClientAreaOnly);
        Assert.True(new CaptureState(CaptureStatus.Ready, "x", ClientAreaOnly: true).ClientAreaOnly);
    }

    private static PixelBuffer Frame(int width, int height, Func<int, int, byte> redness)
    {
        var bytes = new byte[width * height * PixelBuffer.BytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * PixelBuffer.BytesPerPixel;
                var isGame = redness(x, y) == 255;
                bytes[i] = isGame ? (byte)0 : (byte)255; // B
                bytes[i + 1] = isGame ? (byte)0 : (byte)255; // G
                bytes[i + 2] = 255; // R
                bytes[i + 3] = 255;
            }
        }

        return new PixelBuffer(width, height, bytes);
    }

    private static bool AllRed(PixelBuffer buffer)
    {
        for (var y = 0; y < buffer.Height; y++)
        {
            for (var x = 0; x < buffer.Width; x++)
            {
                var (r, g, b, _) = buffer.GetPixel(x, y);
                if (r != 255 || g != 0 || b != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
