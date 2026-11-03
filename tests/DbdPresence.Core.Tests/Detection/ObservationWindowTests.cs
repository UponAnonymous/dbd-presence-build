// Covers: C1 (K-of-N confirmation arithmetic, ARCHITECTURE §4.4). Mechanics only.
using DbdPresence.Core.Detection;

namespace DbdPresence.Core.Tests.Detection;

public sealed class ObservationWindowTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ObservationWindow Fill(int capacity, params (string Value, double Score)[] entries)
    {
        var window = new ObservationWindow(capacity);
        for (var i = 0; i < entries.Length; i++)
        {
            window.Add(entries[i].Value, entries[i].Score, s_t0.AddSeconds(i));
        }

        return window;
    }

    private static ConfirmationRule Rule(int k, int n, double dwellSeconds = 0, double minScore = 0.5) =>
        new(k, n, TimeSpan.FromSeconds(dwellSeconds), minScore, TimeSpan.FromMinutes(1));

    [Fact]
    public void Verdict_ReportsValueSupportAndSupportingTimeSpan()
    {
        var window = Fill(8, ("a", 0.9), ("b", 0.9), ("a", 0.9), ("a", 0.9));

        var verdict = window.Evaluate(Rule(3, 4), 1);

        Assert.NotNull(verdict);
        Assert.Equal("a", verdict.Value.Value);
        Assert.Equal(3, verdict.Value.Support);
        Assert.Equal(s_t0, verdict.Value.FirstUtc);
        Assert.Equal(s_t0.AddSeconds(3), verdict.Value.LastUtc);
    }

    [Fact]
    public void Competitor_MustHaveFewerThanHalfOfKSupporters()
    {
        // K = 2: a single competitor is 1 = K/2, which is not "fewer than K/2", so there is no confirmation.
        Assert.Null(Fill(8, ("a", 0.9), ("b", 0.9), ("a", 0.9)).Evaluate(Rule(2, 3), 1));

        // K = 4: two competitors equal K/2 and block; one competitor is allowed.
        Assert.Null(Fill(12, ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9), ("b", 0.9), ("b", 0.9)).Evaluate(Rule(4, 6), 1));
        Assert.Equal("a", Fill(12, ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9), ("b", 0.9)).Evaluate(Rule(4, 5), 1)!.Value.Value);
    }

    [Fact]
    public void OnlyTheLastNEntriesCount()
    {
        var window = Fill(8, ("a", 0.9), ("a", 0.9), ("a", 0.9), ("b", 0.9), ("b", 0.9), ("b", 0.9));

        var verdict = window.Evaluate(Rule(3, 4), 1);

        Assert.NotNull(verdict);
        Assert.Equal("b", verdict.Value.Value); // the last 4 are a,b,b,b
    }

    [Fact]
    public void Extended_ChecksTwoKOfTwoN()
    {
        var window = Fill(8, ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9));

        Assert.Null(window.Evaluate(Rule(3, 4), 2)); // 5 < 2K
        window.Add("a", 0.9, s_t0.AddSeconds(5));
        Assert.Equal("a", window.Evaluate(Rule(3, 4), 2)!.Value.Value);
    }

    [Fact]
    public void AnySupportingFrameBelowTheScoreFloor_BlocksConfirmation()
    {
        Assert.Null(Fill(8, ("a", 0.9), ("a", 0.49), ("a", 0.9)).Evaluate(Rule(3, 4, minScore: 0.5), 1));
        Assert.NotNull(Fill(8, ("a", 0.9), ("a", 0.5), ("a", 0.9)).Evaluate(Rule(3, 4, minScore: 0.5), 1));
    }

    [Fact]
    public void Dwell_IsMeasuredBetweenTheFirstAndLastSupportingFrame()
    {
        var window = Fill(8, ("a", 0.9), ("a", 0.9), ("a", 0.9)); // frames 0 s, 1 s, 2 s

        Assert.NotNull(window.Evaluate(Rule(3, 4, dwellSeconds: 2), 1));
        Assert.Null(window.Evaluate(Rule(3, 4, dwellSeconds: 2.1), 1));
    }

    [Fact]
    public void TooFewEntries_NeverConfirm_AndTrimDropsOldOnes()
    {
        Assert.Null(Fill(8, ("a", 0.9), ("a", 0.9)).Evaluate(Rule(3, 4), 1));

        var window = Fill(8, ("a", 0.9), ("a", 0.9), ("a", 0.9));
        window.Trim(s_t0.AddSeconds(2 + 61), TimeSpan.FromMinutes(1));
        Assert.Equal(0, window.Count);
        Assert.Null(window.Evaluate(Rule(3, 4), 1));
    }

    [Fact]
    public void Capacity_IsBounded()
    {
        var window = Fill(4, ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9), ("a", 0.9));

        Assert.Equal(4, window.Count);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(5, 4)]
    public void RuleValidation_RejectsImpossibleKOfN(int k, int n) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfirmationRule(k, n, TimeSpan.Zero, 0.5, TimeSpan.FromSeconds(1)).Validate());

    [Fact]
    public void RuleValidation_RejectsBadScoresAndTimes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfirmationRule(1, 1, TimeSpan.Zero, 1.5, TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfirmationRule(1, 1, TimeSpan.Zero, double.NaN, TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfirmationRule(1, 1, TimeSpan.FromSeconds(-1), 0.5, TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfirmationRule(1, 1, TimeSpan.Zero, 0.5, TimeSpan.Zero).Validate());
    }
}
