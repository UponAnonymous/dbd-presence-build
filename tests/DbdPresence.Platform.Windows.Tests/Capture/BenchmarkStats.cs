using System.Globalization;

namespace DbdPresence.Platform.Windows.Tests.Capture;

/// <summary>Summary of a list of timings in milliseconds: count, median, 95th percentile (nearest rank), minimum and maximum.</summary>
internal readonly record struct BenchmarkStats(int Count, double MedianMs, double P95Ms, double MinMs, double MaxMs)
{
    /// <summary>Summarises the samples. An empty list gives a count of 0 and NaN values.</summary>
    public static BenchmarkStats From(IReadOnlyCollection<double> samplesMs)
    {
        ArgumentNullException.ThrowIfNull(samplesMs);
        if (samplesMs.Count == 0)
        {
            return new BenchmarkStats(0, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        var sorted = samplesMs.Order().ToArray();
        var mid = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        var p95Rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return new BenchmarkStats(sorted.Length, median, sorted[p95Rank - 1], sorted[0], sorted[^1]);
    }

    /// <summary>One line for the report, for example <c>n=30 median=2.10 ms p95=3.40 ms min=1.90 ms max=3.80 ms</c>.</summary>
    public override string ToString() =>
        Count == 0
            ? "n=0 (no samples)"
            : string.Create(CultureInfo.InvariantCulture, $"n={Count} median={MedianMs:F2} ms p95={P95Ms:F2} ms min={MinMs:F2} ms max={MaxMs:F2} ms");
}
