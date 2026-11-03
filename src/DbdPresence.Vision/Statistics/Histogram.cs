using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Statistics;

/// <summary>Ways to compare two histograms.</summary>
public enum HistogramMetric
{
    /// <summary>Pearson correlation of the bins, in [-1, 1]; 1 means the same shape.</summary>
    Correlation,

    /// <summary>Sum of the bin minima, in [0, 1]; 1 means identical.</summary>
    Intersection,

    /// <summary>Symmetric chi-square distance, in [0, 2]; 0 means identical.</summary>
    ChiSquare,

    /// <summary>Bhattacharyya distance, in [0, 1]; 0 means identical.</summary>
    Bhattacharyya,
}

/// <summary>A normalised (sum 1) luma histogram.</summary>
public sealed class Histogram
{
    private readonly double[] _bins;

    private Histogram(double[] bins)
    {
        _bins = bins;
    }

    /// <summary>Gets the number of bins.</summary>
    public int BinCount => _bins.Length;

    /// <summary>Gets the normalised bin values.</summary>
    public IReadOnlyList<double> Bins => _bins;

    /// <summary>Builds a luma histogram with <paramref name="bins"/> equal-width bins over 0..255.</summary>
    public static Histogram FromImage(ImageBuffer image, int bins = 32)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (bins is < 2 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(bins), "Use 2 to 256 bins.");
        }

        var counts = new double[bins];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                counts[image.GrayAt(x, y) * bins / 256]++;
            }
        }

        var total = (double)image.Width * image.Height;
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] /= total;
        }

        return new Histogram(counts);
    }

    /// <summary>Wraps bin values (they are normalised to sum 1).</summary>
    public static Histogram FromValues(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2 || values.Any(v => v < 0 || !double.IsFinite(v)))
        {
            throw new ArgumentException("A histogram needs at least two finite, non-negative bins.", nameof(values));
        }

        var sum = values.Sum();
        if (sum <= 0)
        {
            throw new ArgumentException("A histogram must not be empty.", nameof(values));
        }

        return new Histogram([.. values.Select(v => v / sum)]);
    }

    /// <summary>Compares two histograms with the same bin count.</summary>
    public static double Compare(Histogram a, Histogram b, HistogramMetric metric)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.BinCount != b.BinCount)
        {
            throw new ArgumentException("Histograms must have the same number of bins.", nameof(b));
        }

        return metric switch
        {
            HistogramMetric.Correlation => Correlation(a._bins, b._bins),
            HistogramMetric.Intersection => a._bins.Zip(b._bins, Math.Min).Sum(),
            HistogramMetric.ChiSquare => a._bins.Zip(b._bins, (p, q) => p + q > 0 ? (p - q) * (p - q) / (p + q) : 0).Sum(),
            HistogramMetric.Bhattacharyya => BhattacharyyaDistance(a._bins, b._bins),
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
    }

    /// <summary>A similarity in [0, 1] (1 = identical) built on the Bhattacharyya distance. Used by detector features.</summary>
    public static double Similarity(Histogram a, Histogram b) => 1.0 - Compare(a, b, HistogramMetric.Bhattacharyya);

    private static double Correlation(double[] a, double[] b)
    {
        var meanA = a.Average();
        var meanB = b.Average();
        double num = 0;
        double varA = 0;
        double varB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            num += (a[i] - meanA) * (b[i] - meanB);
            varA += (a[i] - meanA) * (a[i] - meanA);
            varB += (b[i] - meanB) * (b[i] - meanB);
        }

        if (varA <= 0 || varB <= 0)
        {
            return varA <= 0 && varB <= 0 && a.SequenceEqual(b) ? 1.0 : 0.0;
        }

        return num / Math.Sqrt(varA * varB);
    }

    private static double BhattacharyyaDistance(double[] a, double[] b)
    {
        var coefficient = a.Zip(b, (p, q) => Math.Sqrt(p * q)).Sum();
        return Math.Sqrt(Math.Max(0.0, 1.0 - coefficient));
    }
}
