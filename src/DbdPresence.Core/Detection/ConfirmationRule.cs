namespace DbdPresence.Core.Detection;

/// <summary>
/// K-of-N confirmation parameters for one field (ARCHITECTURE §4.4). A value is confirmed when at least
/// <see cref="K"/> of the last <see cref="N"/> readable observations equal it, every one of those has a score of at
/// least <see cref="MinScore"/> and they span at least <see cref="Dwell"/>, and the best competing value has fewer
/// than <c>K / 2</c> supporters. All defaults are <b>initial guesses</b> (<see cref="ThresholdProvenance.InitialGuess"/>):
/// they are replaced by measured values from the tuning split before any detector can publish.
/// </summary>
/// <param name="K">Agreeing observations needed.</param>
/// <param name="N">Window length in observations.</param>
/// <param name="Dwell">Minimum time between the first and the last agreeing observation.</param>
/// <param name="MinScore">Minimum raw score (θ) of every agreeing observation.</param>
/// <param name="MaxAge">Observations older than this no longer count.</param>
public sealed record ConfirmationRule(int K, int N, TimeSpan Dwell, double MinScore, TimeSpan MaxAge)
{
    /// <summary>Gets the default scene rule: K=3, N=4, dwell 1.5 s (ARCHITECTURE §4.2).</summary>
    public static ConfirmationRule DefaultScene { get; } = new(3, 4, TimeSpan.FromSeconds(1.5), 0.6, TimeSpan.FromSeconds(10));

    /// <summary>Gets the default rule for role, map and generators: K=3, N=4, no dwell.</summary>
    public static ConfirmationRule DefaultField { get; } = new(3, 4, TimeSpan.Zero, 0.6, TimeSpan.FromSeconds(10));

    /// <summary>Throws when the rule is not usable.</summary>
    public void Validate()
    {
        if (K < 1 || N < K)
        {
            throw new ArgumentOutOfRangeException(nameof(K), "Require 1 <= K <= N.");
        }

        if (Dwell < TimeSpan.Zero || MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Dwell), "Dwell must be >= 0 and MaxAge > 0.");
        }

        if (double.IsNaN(MinScore) || MinScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinScore), "MinScore must be within [0, 1].");
        }
    }
}

/// <summary>The outcome of a successful confirmation check.</summary>
/// <param name="Value">The agreed value.</param>
/// <param name="Support">How many observations support it.</param>
/// <param name="FirstUtc">Timestamp of the first supporting observation.</param>
/// <param name="LastUtc">Timestamp of the last supporting observation.</param>
internal readonly record struct WindowVerdict(string Value, int Support, DateTimeOffset FirstUtc, DateTimeOffset LastUtc);

/// <summary>
/// A bounded ring of recent readable observations of one field. <see cref="Evaluate"/> implements the K-of-N rule;
/// the extended form (factor 2) checks 2K of the last 2N and is used for transitions that need doubled evidence.
/// </summary>
internal sealed class ObservationWindow
{
    private readonly List<Entry> _entries = [];
    private readonly int _capacity;

    public ObservationWindow(int capacity)
    {
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public void Clear() => _entries.Clear();

    /// <summary>Drops observations older than <paramref name="maxAge"/> relative to <paramref name="now"/>.</summary>
    public void Trim(DateTimeOffset now, TimeSpan maxAge) => _entries.RemoveAll(e => now - e.At > maxAge);

    public void Add(string value, double score, DateTimeOffset at)
    {
        _entries.Add(new Entry(value, score, at));
        if (_entries.Count > _capacity)
        {
            _entries.RemoveRange(0, _entries.Count - _capacity);
        }
    }

    /// <summary>Returns the confirmed value, or null when the rule is not met (or two values tie).</summary>
    public WindowVerdict? Evaluate(ConfirmationRule rule, int factor)
    {
        var need = rule.K * factor;
        var span = Math.Min(rule.N * factor, _entries.Count);
        if (span < need)
        {
            return null;
        }

        var window = _entries.GetRange(_entries.Count - span, span);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in window)
        {
            counts[entry.Value] = counts.GetValueOrDefault(entry.Value) + 1;
        }

        WindowVerdict? best = null;
        var ambiguous = false;
        foreach (var (value, count) in counts)
        {
            if (count < need)
            {
                continue;
            }

            var competitor = counts.Where(c => !string.Equals(c.Key, value, StringComparison.Ordinal)).Select(c => c.Value).DefaultIfEmpty(0).Max();
            if (competitor * 2 >= need)
            {
                continue;
            }

            var supporters = window.Where(e => string.Equals(e.Value, value, StringComparison.Ordinal)).ToList();
            if (supporters.Any(e => e.Score < rule.MinScore))
            {
                continue;
            }

            var first = supporters[0].At;
            var last = supporters[^1].At;
            if (last - first < rule.Dwell)
            {
                continue;
            }

            if (best is not null)
            {
                ambiguous = true;
                continue;
            }

            best = new WindowVerdict(value, count, first, last);
        }

        return ambiguous ? null : best;
    }

    private readonly record struct Entry(string Value, double Score, DateTimeOffset At);
}
