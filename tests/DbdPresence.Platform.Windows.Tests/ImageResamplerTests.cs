// Covers: FR-PACK-12 (picture fit never enlarges, keeps aspect, averages area without colour bleed, output is a strict clean PNG).
// PENDING-ENV on Linux: the helper project targets Windows, so these run in the Windows CI job.
using DbdPresence.Core.Images;
using DbdPresence.ImageHelper;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class ImageResamplerTests
{
    [Theory]
    [InlineData(3000, 2000, 1024, 1024, 683)]
    [InlineData(2000, 3000, 512, 341, 512)]
    [InlineData(800, 600, 1024, 800, 600)]
    [InlineData(5, 4000, 512, 1, 512)]
    public void FitSize_ScalesDownOnly(int w, int h, int edge, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), ImageResampler.FitSize(w, h, edge));
    }

    [Fact]
    public void Resample_AveragesTheCoveredArea()
    {
        // 2 x 1 pixels, black and white, opaque, shrunk to 1 x 1: mid grey.
        byte[] bgra = [0, 0, 0, 255, 255, 255, 255, 255];

        var result = ImageResampler.Resample(bgra, 2, 1, 1, 1);

        Assert.InRange(result[0], 127, 128);
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void Resample_TransparentPixelsDoNotBleedTheirColour()
    {
        // One opaque red pixel and one fully transparent white pixel: the result stays red, at half alpha.
        byte[] bgra = [0, 0, 255, 255, 255, 255, 255, 0];

        var result = ImageResampler.Resample(bgra, 2, 1, 1, 1);

        Assert.Equal((0, 0, 255), (result[0], result[1], result[2]));
        Assert.InRange(result[3], 127, 128);
    }

    [Fact]
    public void Resample_RefusesToEnlarge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageResampler.Resample(new byte[16], 2, 2, 4, 4));
    }

    [Fact]
    public void EncodeSmallest_WritesOpaqueImagesAsRgb_AndPassesTheStrictWalker()
    {
        var bgra = new byte[16 * 16 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(i % 251);
            bgra[i + 3] = 255;
        }

        var png = MinimalPng.EncodeSmallest(16, 16, bgra);

        Assert.True(StrictPng.TryValidate(png, out var w, out var h, out var failure), failure);
        Assert.Equal((16, 16), (w, h));
        Assert.Equal(2, png[25]);
    }

    [Fact]
    public void EncodeSmallest_KeepsAlphaWhenAnyPixelIsTransparent()
    {
        var bgra = new byte[4 * 4 * 4];
        bgra[3] = 10;

        var png = MinimalPng.EncodeSmallest(4, 4, bgra);

        Assert.True(StrictPng.TryValidate(png, out _, out _, out var failure), failure);
        Assert.Equal(6, png[25]);
    }
}
