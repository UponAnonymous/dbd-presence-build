// Covers: C2 (colour statistics and histogram compare). Mechanics only: synthetic pixels.
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Statistics;

namespace DbdPresence.Vision.Tests.Statistics;

public sealed class ColorAndHistogramTests
{
    [Fact]
    public void Summarize_ReturnsHandComputedMeansAndDeviation()
    {
        // Two pixels: (200, 100, 0) and (0, 100, 200).
        var image = new ImageBuffer(2, 1, PixelFormat.Rgba32, [200, 100, 0, 255, 0, 100, 200, 255]);

        var s = ColorStatistics.Summarize(image);

        Assert.Equal(2, s.Pixels);
        Assert.Equal(100, s.MeanR);
        Assert.Equal(100, s.MeanG);
        Assert.Equal(100, s.MeanB);
        // Luma of the two pixels: (299*200 + 587*100 + 0 + 500) / 1000 = 119, and (0 + 58700 + 22800 + 500) / 1000 = 82.
        Assert.Equal(119, image.GrayAt(0, 0));
        Assert.Equal(82, image.GrayAt(1, 0));
        Assert.Equal(100.5, s.MeanLuma);
        Assert.Equal(18.5, s.StdLuma, precision: 9);
        Assert.Equal((82, 119), (s.MinLuma, s.MaxLuma));
    }

    [Fact]
    public void Summarize_OfAFlatImageHasZeroDeviation()
    {
        var s = ColorStatistics.Summarize(ImageBuffer.CreateGray(5, 5, 90));

        Assert.Equal(0, s.StdLuma, precision: 9);
        Assert.Equal(90, s.MeanLuma);
    }

    [Fact]
    public void FractionNear_CountsPixelsWithinTheTolerance()
    {
        var image = ImageBuffer.FromFunction(10, 1, (x, _) => (byte)(x < 3 ? 100 : 200));

        Assert.Equal(0.3, ColorStatistics.FractionNear(image, 105, 105, 105, 10));
        Assert.Equal(0.0, ColorStatistics.FractionNear(image, 105, 105, 105, 4));
        Assert.Equal(1.0, ColorStatistics.FractionNear(image, 150, 150, 150, 50));
        Assert.Equal(0.7, ColorStatistics.FractionBrighterThan(image, 150));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorStatistics.FractionNear(image, 0, 0, 0, 300));
    }

    [Fact]
    public void EdgeDensity_IsZeroForFlatAndPositiveForStructure()
    {
        Assert.Equal(0, ColorStatistics.EdgeDensity(ImageBuffer.CreateGray(20, 20, 128)));
        Assert.Equal(0, ColorStatistics.EdgeDensity(ImageBuffer.CreateGray(2, 2, 128)));

        var stripes = ImageBuffer.FromFunction(20, 20, (x, _) => (byte)(x % 4 < 2 ? 0 : 255));
        Assert.Equal(1.0, ColorStatistics.EdgeDensity(stripes));
    }

    [Fact]
    public void Histogram_IsNormalised_AndBinsFollowLuma()
    {
        var image = ImageBuffer.FromFunction(4, 1, (x, _) => (byte)(x < 2 ? 10 : 250));

        var h = Histogram.FromImage(image, bins: 4);

        Assert.Equal([0.5, 0, 0, 0.5], h.Bins);
        Assert.Throws<ArgumentOutOfRangeException>(() => Histogram.FromImage(image, 1));
    }

    [Fact]
    public void Compare_IdenticalHistograms_ScorePerfectlyOnEveryMetric()
    {
        var h = Histogram.FromValues([1, 2, 3, 4]);

        Assert.Equal(1.0, Histogram.Compare(h, h, HistogramMetric.Correlation), precision: 12);
        Assert.Equal(1.0, Histogram.Compare(h, h, HistogramMetric.Intersection), precision: 12);
        Assert.Equal(0.0, Histogram.Compare(h, h, HistogramMetric.ChiSquare), precision: 12);
        Assert.Equal(0.0, Histogram.Compare(h, h, HistogramMetric.Bhattacharyya), precision: 6);
        Assert.Equal(1.0, Histogram.Similarity(h, h), precision: 6);
    }

    [Fact]
    public void Compare_DisjointHistograms_ScoreWorstOnEveryMetric()
    {
        var a = Histogram.FromValues([1, 0, 0, 0]);
        var b = Histogram.FromValues([0, 0, 0, 1]);

        Assert.Equal(0.0, Histogram.Compare(a, b, HistogramMetric.Intersection), precision: 12);
        Assert.Equal(2.0, Histogram.Compare(a, b, HistogramMetric.ChiSquare), precision: 12);
        Assert.Equal(1.0, Histogram.Compare(a, b, HistogramMetric.Bhattacharyya), precision: 12);
        Assert.True(Histogram.Compare(a, b, HistogramMetric.Correlation) < 0);
    }

    [Fact]
    public void Compare_HandComputedExample()
    {
        // a = [0.5, 0.5, 0, 0], b = [0.25, 0.25, 0.25, 0.25]
        var a = Histogram.FromValues([2, 2, 0, 0]);
        var b = Histogram.FromValues([1, 1, 1, 1]);

        Assert.Equal(0.5, Histogram.Compare(a, b, HistogramMetric.Intersection), precision: 12);
        // chi-square: 2 * (0.25^2 / 0.75) + 2 * (0.25^2 / 0.25) = 1/6 + 1/2 = 2/3
        Assert.Equal(2.0 / 3.0, Histogram.Compare(a, b, HistogramMetric.ChiSquare), precision: 12);
        // Bhattacharyya coefficient: 2 * sqrt(0.5 * 0.25) = 0.7071..., distance = sqrt(1 - BC)
        Assert.Equal(Math.Sqrt(1 - (2 * Math.Sqrt(0.125))), Histogram.Compare(a, b, HistogramMetric.Bhattacharyya), precision: 12);
        // correlation: a deviates (+.25,+.25,-.25,-.25) from its mean .25, b is constant, so b has zero variance -> 0
        Assert.Equal(0.0, Histogram.Compare(a, b, HistogramMetric.Correlation), precision: 12);
    }

    [Fact]
    public void Compare_RejectsMismatchedBinCounts_AndBadValues()
    {
        Assert.Throws<ArgumentException>(() => Histogram.Compare(Histogram.FromValues([1, 1]), Histogram.FromValues([1, 1, 1]), HistogramMetric.Intersection));
        Assert.Throws<ArgumentException>(() => Histogram.FromValues([0, 0]));
        Assert.Throws<ArgumentException>(() => Histogram.FromValues([1, -1]));
        Assert.Throws<ArgumentException>(() => Histogram.FromValues([1]));
    }

    [Fact]
    public void Similarity_RanksAnIdenticalImageAboveADifferentOne()
    {
        var ramp = ImageBuffer.FromFunction(32, 32, (x, _) => (byte)(x * 8));
        var sameRamp = ImageBuffer.FromFunction(32, 32, (x, _) => (byte)(x * 8));
        var reference = Histogram.FromImage(ramp);

        Assert.Equal(1.0, Histogram.Similarity(reference, Histogram.FromImage(sameRamp)), precision: 6);
        Assert.True(Histogram.Similarity(reference, Histogram.FromImage(ImageBuffer.CreateGray(32, 32, 250))) < 0.5);
    }
}
