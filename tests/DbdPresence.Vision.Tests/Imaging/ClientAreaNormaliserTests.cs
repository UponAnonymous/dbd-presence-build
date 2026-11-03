// Covers: PLAN_V2 D17 (windowed mode in scope), FR-DET-16 (the evaluator reads recordings), DETECTION_VALIDATION section 5 (labels are never changed).
// MECHANICS ONLY: the pictures are flat colours and noise drawn around a title bar. They prove the heuristic's rules, not that it finds the owner's real
// title bar; that is UNKNOWN until it has been run on the three real 1922x1112 recordings and checked by eye (PENDING-ENV).
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Imaging;

public sealed class ClientAreaNormaliserTests
{
    /// <summary>A "game" picture with texture on every row, so no row is flat and the first game row never looks like a title bar.</summary>
    private static ImageBuffer Textured(int width, int height, int seed)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var v = (byte)(((x * 31) + (y * 17) + (seed * 7) + ((x ^ y) * 3)) % 200);
                data[i] = v;
                data[i + 1] = (byte)(v / 2);
                data[i + 2] = (byte)(255 - v);
                data[i + 3] = 255;
            }
        }

        return new ImageBuffer(width, height, PixelFormat.Rgba32, data);
    }

    [Fact]
    public void TheOwnersWindowedSize_IsCutBackToTheGamesOwnPicture()
    {
        var game = Textured(1920, 1080, 1);
        var framed = WindowFrames.Around(game);
        Assert.Equal((1922, 1112), (framed.Width, framed.Height));

        var decision = ClientAreaNormaliser.Decide([framed]);

        Assert.Equal(ClientAreaOutcome.Applied, decision.Outcome);
        Assert.Equal(new PixelRect(1, 31, 1920, 1080), decision.Crop);
        Assert.Equal((1920, 1080), (decision.EffectiveWidth, decision.EffectiveHeight));
        Assert.True(WindowFrames.SamePixels(game, ClientAreaNormaliser.Apply(framed, decision)));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    [InlineData(1366, 768)]
    public void OtherStandardSizes_AreCutToo(int width, int height)
    {
        var game = Textured(width, height, 2);

        var decision = ClientAreaNormaliser.Decide([WindowFrames.Around(game)]);

        Assert.Equal(ClientAreaOutcome.Applied, decision.Outcome);
        Assert.True(WindowFrames.SamePixels(game, ClientAreaNormaliser.Apply(WindowFrames.Around(game), decision)));
    }

    [Fact]
    public void ABiggerTitleBar_AtAHigherDisplayScale_IsStillFound()
    {
        var game = Textured(1920, 1080, 3);
        var framed = WindowFrames.Around(game, titleBar: 46, border: 2);

        var decision = ClientAreaNormaliser.Decide([framed]);

        Assert.Equal(ClientAreaOutcome.Applied, decision.Outcome);
        Assert.Equal(new PixelRect(2, 46, 1920, 1080), decision.Crop);
    }

    [Fact]
    public void ADarkTitleBar_IsFoundJustTheSame()
    {
        var game = Textured(1920, 1080, 4);

        var decision = ClientAreaNormaliser.Decide([WindowFrames.Around(game, bar: (32, 32, 32))]);

        Assert.Equal(ClientAreaOutcome.Applied, decision.Outcome);
    }

    [Fact]
    public void AStandardSizedPicture_IsLeftAlone()
    {
        var decision = ClientAreaNormaliser.Decide([Textured(1920, 1080, 5)]);

        Assert.Equal(ClientAreaOutcome.NotNeeded, decision.Outcome);
        Assert.Null(decision.Crop);
    }

    [Fact]
    public void ASizeThatIsOnlyAFramePlusAStandardSize_ButHasNoBar_IsNotCut()
    {
        // 1922x1112 with texture in every row: the size alone must never be enough.
        var decision = ClientAreaNormaliser.Decide([Textured(1922, 1112, 6)]);

        Assert.Equal(ClientAreaOutcome.Unverified, decision.Outcome);
        Assert.Null(decision.Crop);
        Assert.Equal((1922, 1112), (decision.EffectiveWidth, decision.EffectiveHeight));
    }

    [Fact]
    public void ABarThatIsNotFlat_IsNotCut()
    {
        var game = Textured(1920, 1080, 7);
        var framed = WindowFrames.Around(game);
        var noisy = Noisy(framed, rows: 5..20);

        Assert.Equal(ClientAreaOutcome.Unverified, ClientAreaNormaliser.Decide([noisy]).Outcome);
    }

    [Fact]
    public void AFlatGameTopThatMatchesTheBar_GivesNoEdge_AndIsNotCut()
    {
        // The game's own top rows are the same flat white as the title bar: nothing marks where the game starts, so nothing is cut.
        var game = Solid(1920, 1080, 255, 255, 255);

        Assert.Equal(ClientAreaOutcome.Unverified, ClientAreaNormaliser.Decide([WindowFrames.Around(game)]).Outcome);
    }

    [Fact]
    public void WhenOnlyOneSampledPictureLacksTheBar_NothingIsCut()
    {
        var game = Textured(1920, 1080, 8);
        var framed = WindowFrames.Around(game);
        var noBar = Textured(1922, 1112, 9);

        Assert.Equal(ClientAreaOutcome.Unverified, ClientAreaNormaliser.Decide([framed, framed, noBar]).Outcome);
    }

    [Fact]
    public void PicturesOfDifferentSizes_AreNotCut()
    {
        var decision = ClientAreaNormaliser.Decide([WindowFrames.Around(Textured(1920, 1080, 1)), WindowFrames.Around(Textured(1280, 720, 1))]);

        Assert.Equal(ClientAreaOutcome.Unverified, decision.Outcome);
    }

    [Fact]
    public void ANonStandardSize_IsReportedUnverified_NotSilentlyUsed()
    {
        var decision = ClientAreaNormaliser.Decide([Textured(1500, 900, 1)]);

        Assert.Equal(ClientAreaOutcome.Unverified, decision.Outcome);
        Assert.Contains("not a standard game size", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1920, 1080, 0)] // the game's own size
    [InlineData(1922, 1090, 0)] // a 10 px "title bar" is below the minimum
    [InlineData(1950, 1160, 0)] // a 30 px "border" is above the maximum
    [InlineData(1922, 1112, 1)] // the owner's windowed recordings
    [InlineData(1282, 752, 1)]
    [InlineData(1922, 1232, 1)] // 1920x1200 plus a frame; never read as 1920x1080 plus 150 rows
    public void TheSizeAlone_OnlyProposesAPlausibleFrame(int width, int height, int expected)
    {
        Assert.Equal(expected, ClientAreaNormaliser.Candidates(width, height).Count);
    }

    [Fact]
    public void ASizeThatFitsTwoStandardSizesPlusAFrame_IsNotCut_EvenWithAFlatBar()
    {
        // 1282x818 is 1280x720 plus a 98 px frame and also 1280x800 plus an 18 px frame (review F10, mutant M34).
        Assert.Equal(2, ClientAreaNormaliser.Candidates(1282, 818).Count);
        var picture = WindowFrames.Around(Textured(1280, 720, 14), titleBar: 97);

        var decision = ClientAreaNormaliser.Decide([picture]);

        Assert.Equal((1282, 818), (picture.Width, picture.Height));
        Assert.Equal(ClientAreaOutcome.Unverified, decision.Outcome);
        Assert.Contains("more than one", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStandardSizedPicture_WhoseTopIsAFlatBarWithAnEdge_IsReportedUnverified_NotNotNeeded()
    {
        // A windowed capture whose outer size happens to be 1920x1080: the title bar is still in the picture (review F15).
        var picture = WithFlatTop(Textured(1920, 1080, 10), rows: 32);

        var decision = ClientAreaNormaliser.Decide([picture]);

        Assert.Equal(ClientAreaOutcome.Unverified, decision.Outcome);
        Assert.Null(decision.Crop);
        Assert.Contains("title bar cannot be excluded", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStandardSizedPicture_WithAShortFlatTop_IsStillNotNeeded()
    {
        var picture = WithFlatTop(Textured(1920, 1080, 11), rows: 8);

        Assert.Equal(ClientAreaOutcome.NotNeeded, ClientAreaNormaliser.Decide([picture]).Outcome);
    }

    [Fact]
    public void AStandardSizedPicture_WhoseTopIsFlatOnlyInOneSample_IsStillNotNeeded()
    {
        var withBar = WithFlatTop(Textured(1920, 1080, 12), rows: 32);

        Assert.Equal(ClientAreaOutcome.NotNeeded, ClientAreaNormaliser.Decide([withBar, Textured(1920, 1080, 13)]).Outcome);
    }

    [Fact]
    public void AStandardSizedPicture_ThatIsFlatAllTheWay_HasNoEdge_AndIsNotCalledUnverified()
    {
        Assert.Equal(ClientAreaOutcome.NotNeeded, ClientAreaNormaliser.Decide([Solid(1920, 1080, 0, 0, 0)]).Outcome);
    }

    [Fact]
    public void AStandardSixteenByTenSize_IsLeftAlone_EvenThoughItIsAlso720pPlus80Rows()
    {
        Assert.Equal(ClientAreaOutcome.NotNeeded, ClientAreaNormaliser.Decide([Textured(1280, 800, 1)]).Outcome);
    }

    [Fact]
    public void ApplyingToAPictureOfAnotherSize_Throws()
    {
        var decision = ClientAreaNormaliser.Decide([WindowFrames.Around(Textured(1920, 1080, 1))]);

        _ = Assert.Throws<InvalidOperationException>(() => ClientAreaNormaliser.Apply(Textured(1280, 720, 1), decision));
    }

    [Fact]
    public void ApplyingAnUnverifiedOrNotNeededDecision_ReturnsThePictureItself()
    {
        var picture = Textured(1920, 1080, 1);

        Assert.Same(picture, ClientAreaNormaliser.Apply(picture, ClientAreaNormaliser.Decide([picture])));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(200, 5)]
    public void TheSamples_AreFewAndSpreadAcrossTheRecording(int frames, int expected)
    {
        var indexes = ClientAreaNormaliser.SampleIndexes(frames);

        Assert.Equal(expected, indexes.Count);
        if (frames > 0)
        {
            Assert.Equal(0, indexes[0]);
            Assert.Equal(frames - 1, indexes[^1]);
            Assert.Equal(indexes.Order().ToList(), indexes);
        }
    }

    private static ImageBuffer Solid(int width, int height, byte r, byte g, byte b) => ImageBuffer.CreateRgba(width, height, r, g, b);

    private static ImageBuffer WithFlatTop(ImageBuffer picture, int rows)
    {
        var data = picture.Pixels.ToArray();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                var i = ((y * picture.Width) + x) * 4;
                data[i] = y == 0 ? (byte)96 : (byte)255;
                data[i + 1] = y == 0 ? (byte)96 : (byte)255;
                data[i + 2] = y == 0 ? (byte)96 : (byte)255;
            }
        }

        return new ImageBuffer(picture.Width, picture.Height, PixelFormat.Rgba32, data);
    }

    private static ImageBuffer Noisy(ImageBuffer picture, Range rows)
    {
        var data = picture.Pixels.ToArray();
        var (start, end) = (rows.Start.Value, rows.End.Value);
        for (var y = start; y < end; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                var i = ((y * picture.Width) + x) * 4;
                data[i] = (byte)((x * 7) % 256);
            }
        }

        return new ImageBuffer(picture.Width, picture.Height, PixelFormat.Rgba32, data);
    }
}
