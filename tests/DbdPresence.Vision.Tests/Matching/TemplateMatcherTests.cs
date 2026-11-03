// Covers: C2 (template matching at multiple scales with normalised cross-correlation). Mechanics only: synthetic pixels.
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Matching;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Matching;

public sealed class TemplateMatcherTests
{
    private static ImageBuffer Scene(int width, int height, ImageBuffer sprite, int x, int y, byte background = 60)
    {
        var data = new byte[width * height];
        Array.Fill(data, background);
        for (var sy = 0; sy < sprite.Height; sy++)
        {
            for (var sx = 0; sx < sprite.Width; sx++)
            {
                data[((y + sy) * width) + x + sx] = sprite.GrayAt(sx, sy);
            }
        }

        return new ImageBuffer(width, height, PixelFormat.Gray8, data);
    }

    private static ImageBuffer Sprite() => ImageBuffer.FromFunction(12, 16, (x, y) => (byte)(((x * 17) + (y * 5) + (x * y)) % 200 + 20));

    [Fact]
    public void ExactCopy_ScoresOne_AtTheTruePosition()
    {
        var sprite = Sprite();
        var image = Scene(80, 60, sprite, 33, 21);

        var match = TemplateMatcher.Match(image, sprite);

        Assert.NotNull(match);
        Assert.Equal((33, 21), (match.Value.X, match.Value.Y));
        Assert.Equal(1.0, match.Value.Score, precision: 9);
        Assert.Equal(1.0, match.Value.Scale);
    }

    [Fact]
    public void Score_IsInvariantToBrightnessAndContrastChanges()
    {
        var sprite = Sprite();
        var image = Scene(80, 60, sprite, 10, 40);
        var brighter = ImageBuffer.FromFunction(80, 60, (x, y) => (byte)Math.Clamp((image.GrayAt(x, y) * 0.5) + 70, 0, 255));

        var match = TemplateMatcher.Match(brighter, sprite);

        Assert.Equal((10, 40), (match!.Value.X, match.Value.Y));
        Assert.True(match.Value.Score > 0.99, $"score was {match.Value.Score}");
    }

    [Fact]
    public void InvertedPattern_ScoresNegative()
    {
        var sprite = Sprite();
        var inverted = ImageBuffer.FromFunction(12, 16, (x, y) => (byte)(255 - sprite.GrayAt(x, y)));
        var image = Scene(40, 40, inverted, 5, 5);

        var match = TemplateMatcher.Match(image, sprite);

        // At the inverted copy the correlation is -1; the best placement elsewhere is some weaker positive value, never 1.
        Assert.True(match!.Value.Score < 0.9);
    }

    [Fact]
    public void FlatWindows_ScoreZero_AndFlatTemplatesAreRejected()
    {
        var flat = ImageBuffer.CreateGray(40, 40, 100);

        var match = TemplateMatcher.Match(flat, Sprite());

        Assert.Equal(0.0, match!.Value.Score);
        Assert.Throws<ArgumentException>(() => TemplateMatcher.Match(flat, ImageBuffer.CreateGray(8, 8, 50)));
    }

    [Fact]
    public void TemplateLargerThanTheImage_HasNoMatch()
    {
        Assert.Null(TemplateMatcher.Match(ImageBuffer.CreateGray(10, 10, 5), Sprite()));
        Assert.Null(TemplateMatcher.MatchMultiScale(ImageBuffer.CreateGray(10, 10, 5), Sprite(), [1.0]));
    }

    [Fact]
    public void MultiScale_FindsATemplateThatAppearsLargerInTheImage()
    {
        var sprite = Sprite();
        var enlarged = sprite.Resize(18, 24); // 1.5x
        var image = Scene(100, 80, enlarged, 41, 30);

        var single = TemplateMatcher.Match(image, sprite);
        var multi = TemplateMatcher.MatchMultiScale(image, sprite, [0.75, 1.0, 1.25, 1.5, 2.0]);

        Assert.Equal(1.5, multi!.Value.Scale);
        Assert.True(multi.Value.Score > 0.97, $"multi-scale score {multi.Value.Score}");
        Assert.True(multi.Value.Score > single!.Value.Score);
        Assert.Equal((41, 30), (multi.Value.X, multi.Value.Y));
    }

    [Fact]
    public void MultiScale_FindsATemplateThatAppearsSmaller()
    {
        var sprite = Sprite().Resize(24, 32);
        var shrunk = sprite.Resize(12, 16);
        var image = Scene(60, 50, shrunk, 7, 9);

        var multi = TemplateMatcher.MatchMultiScale(image, sprite, [1.0, 0.75, 0.5]);

        Assert.Equal(0.5, multi!.Value.Scale);
        Assert.True(multi.Value.Score > 0.97);
    }

    [Fact]
    public void MultiScale_SkipsTooSmallScales_KeepsTheFirstOnATie_AndRejectsBadScales()
    {
        var sprite = Sprite();
        var image = Scene(60, 50, sprite, 7, 9);

        Assert.Null(TemplateMatcher.MatchMultiScale(image, sprite, [0.1])); // 1 x 2 pixels: below the minimum side
        Assert.Equal(1.0, TemplateMatcher.MatchMultiScale(image, sprite, [1.0, 1.0])!.Value.Scale);
        Assert.Throws<ArgumentOutOfRangeException>(() => TemplateMatcher.MatchMultiScale(image, sprite, [0.0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TemplateMatcher.MatchMultiScale(image, sprite, [double.NaN]));
    }

    [Fact]
    public void Matching_IsDeterministic()
    {
        var sprite = Sprite();
        var image = Scene(70, 50, sprite, 20, 12);

        var a = TemplateMatcher.MatchMultiScale(image, sprite, [0.9, 1.0, 1.1]);
        var b = TemplateMatcher.MatchMultiScale(image, sprite, [0.9, 1.0, 1.1]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void RenderedGlyph_IsFoundAcrossRenderResolutions()
    {
        // The same text-like glyph template found in frames of different sizes: the template comes from a 640-wide render.
        var template = SyntheticScenes.Render(SceneKind.Lobby, 640, noiseOneIn: 0).Crop(SyntheticScenes.LobbyRegion).ToGray();
        foreach (var width in new[] { 320, 640, 960 })
        {
            var frame = SyntheticScenes.Render(SceneKind.Lobby, width, brightness: 20, seed: 7).Crop(SyntheticScenes.LobbyRegion);
            var match = TemplateMatcher.MatchMultiScale(frame.ResizeToWidth(128), template.ResizeToWidth(100), [0.9, 1.0, 1.1, 1.25]);

            Assert.True(match!.Value.Score > 0.8, $"width {width}: score {match.Value.Score}");
        }
    }
}
