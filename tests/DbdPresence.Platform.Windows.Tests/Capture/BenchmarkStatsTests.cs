// Mechanics: the median and 95th-percentile arithmetic used by the detection benchmark, on known numbers.
namespace DbdPresence.Platform.Windows.Tests.Capture;

public sealed class BenchmarkStatsTests
{
    [Fact]
    public void OddCount_MedianIsTheMiddleValue()
    {
        var stats = BenchmarkStats.From([5, 1, 3]);

        Assert.Equal(3, stats.Count);
        Assert.Equal(3, stats.MedianMs);
        Assert.Equal(1, stats.MinMs);
        Assert.Equal(5, stats.MaxMs);
    }

    [Fact]
    public void EvenCount_MedianIsTheMeanOfTheTwoMiddleValues() =>
        Assert.Equal(2.5, BenchmarkStats.From([4, 1, 3, 2]).MedianMs);

    [Fact]
    public void P95_IsTheNearestRankValue()
    {
        var stats = BenchmarkStats.From(Enumerable.Range(1, 100).Select(i => (double)i).ToArray());

        Assert.Equal(95, stats.P95Ms);
        Assert.Equal(50.5, stats.MedianMs);
    }

    [Fact]
    public void NoSamples_ReportsZeroCountAndNoNumbers()
    {
        var stats = BenchmarkStats.From([]);

        Assert.Equal(0, stats.Count);
        Assert.True(double.IsNaN(stats.MedianMs));
        Assert.Equal("n=0 (no samples)", stats.ToString());
    }
}
