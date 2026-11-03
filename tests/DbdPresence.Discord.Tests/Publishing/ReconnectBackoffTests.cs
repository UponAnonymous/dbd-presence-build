// Covers: FR-RPC-02 (backoff 1 s doubling to a 60 s cap, jitter never below the first delay, reset).
namespace DbdPresence.Discord.Tests.Publishing;

public sealed class ReconnectBackoffTests
{
    private static readonly TimeSpan s_min = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_max = TimeSpan.FromSeconds(60);

    [Fact]
    public void WithoutJitter_DelaysDoubleFromOneSecondAndStopAtSixty()
    {
        var backoff = new ReconnectBackoff(s_min, s_max, 0, new Random(1));

        var delays = Enumerable.Range(0, 10).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60, 60], delays);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    public void WithJitter_EachDelayStaysWithinTwentyPercentAndNeverExceedsTheCap(int seed)
    {
        var backoff = new ReconnectBackoff(s_min, s_max, 0.2, new Random(seed));

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var nominal = Math.Min(Math.Pow(2, attempt), 60);
            var delay = backoff.Next().TotalSeconds;
            // The first delay is never below one second even with downward jitter (QA review Q-L5).
            Assert.InRange(delay, Math.Max(nominal * 0.8, 1), Math.Min(nominal * 1.2, 60));
            if (nominal >= 60)
            {
                Assert.True(delay <= 60);
            }
        }
    }

    [Fact]
    public void WithMaximumJitter_NoDelayIsEverBelowTheFirstDelay()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var backoff = new ReconnectBackoff(s_min, s_max, 1.0, new Random(seed));

            for (var attempt = 0; attempt < 10; attempt++)
            {
                var delay = backoff.Next();
                Assert.True(delay >= s_min && delay <= s_max, $"seed {seed}, attempt {attempt}: {delay}");
            }
        }
    }

    [Fact]
    public void Reset_StartsOverAtOneSecond()
    {
        var backoff = new ReconnectBackoff(s_min, s_max, 0, new Random(1));
        _ = backoff.Next();
        _ = backoff.Next();
        _ = backoff.Next();

        backoff.Reset();

        Assert.Equal(1, backoff.Next().TotalSeconds);
    }

    [Fact]
    public void SameSeedGivesTheSameSequence()
    {
        var first = new ReconnectBackoff(s_min, s_max, 0.2, new Random(99));
        var second = new ReconnectBackoff(s_min, s_max, 0.2, new Random(99));

        Assert.Equal(
            Enumerable.Range(0, 12).Select(_ => first.Next()),
            Enumerable.Range(0, 12).Select(_ => second.Next()));
    }

    [Fact]
    public void JitterActuallyVariesTheDelay()
    {
        var delays = Enumerable.Range(0, 20)
            .Select(seed =>
            {
                var backoff = new ReconnectBackoff(s_min, s_max, 0.2, new Random(seed + 100));
                _ = backoff.Next();
                return backoff.Next();
            })
            .Distinct()
            .Count();

        Assert.True(delays > 10);
    }
}
