namespace DbdPresence.Vision.Evaluation;

/// <summary>A closed interval.</summary>
/// <param name="Lower">Lower bound.</param>
/// <param name="Upper">Upper bound.</param>
public readonly record struct Interval(double Lower, double Upper);

/// <summary>Two-sided 95% Wilson score interval for a binomial proportion (DETECTION_VALIDATION §6).</summary>
public static class WilsonInterval
{
    /// <summary>The 97.5th percentile of the standard normal distribution.</summary>
    public const double Z95 = 1.959963984540054;

    /// <summary>Computes the interval for <paramref name="successes"/> out of <paramref name="trials"/>.</summary>
    public static Interval TwoSided95(int successes, int trials)
    {
        if (trials < 1 || successes < 0 || successes > trials)
        {
            throw new ArgumentOutOfRangeException(nameof(trials), "Require trials >= 1 and 0 <= successes <= trials.");
        }

        var n = (double)trials;
        var p = successes / n;
        var z2 = Z95 * Z95;
        var centre = p + (z2 / (2 * n));
        var margin = Z95 * Math.Sqrt((p * (1 - p) / n) + (z2 / (4 * n * n)));
        var denominator = 1 + (z2 / n);
        return new Interval(Math.Max(0.0, (centre - margin) / denominator), Math.Min(1.0, (centre + margin) / denominator));
    }

    /// <summary>
    /// The smallest number of trials with exactly <paramref name="errors"/> errors whose Wilson lower bound reaches
    /// <paramref name="lowerBound"/>. With 0, 1, 2, 3 errors and 0.99 this reproduces the table in DETECTION_VALIDATION §6.
    /// </summary>
    public static int MinimumTrials(int errors, double lowerBound)
    {
        if (errors < 0 || lowerBound is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(errors));
        }

        for (var n = Math.Max(1, errors); n < 1_000_000; n++)
        {
            if (TwoSided95(n - errors, n).Lower >= lowerBound)
            {
                return n;
            }
        }

        throw new InvalidOperationException("No trial count below one million reaches the requested bound.");
    }
}

/// <summary>Agreement statistics for the independent re-check of labels (DETECTION_VALIDATION §4).</summary>
public static class AgreementStatistics
{
    /// <summary>
    /// Cohen's kappa for two labellers (or two passes) over the same items. Returns null when chance agreement is 1
    /// (all labels identical), where kappa is undefined.
    /// </summary>
    public static double? CohensKappa(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Count != second.Count || first.Count == 0)
        {
            throw new ArgumentException("Both label lists must have the same, non-zero length.");
        }

        var n = (double)first.Count;
        var observed = first.Zip(second).Count(p => string.Equals(p.First, p.Second, StringComparison.Ordinal)) / n;
        var expected = first.Concat(second).Distinct(StringComparer.Ordinal)
            .Sum(label => (first.Count(l => string.Equals(l, label, StringComparison.Ordinal)) / n) * (second.Count(l => string.Equals(l, label, StringComparison.Ordinal)) / n));
        return expected >= 1.0 ? null : (observed - expected) / (1.0 - expected);
    }
}
