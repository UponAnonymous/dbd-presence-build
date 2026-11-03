// Covers: FR-RPC-03 (at most 4 updates per 20 s sliding window; pure property test over seeded random schedules).
namespace DbdPresence.Discord.Tests.Publishing;

public sealed class SendRateLimiterTests
{
    private static readonly TimeSpan s_window = TimeSpan.FromSeconds(20);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    [Fact]
    public void FourSendsFitInTheWindow_TheFifthWaitsUntilTheFirstExpires()
    {
        var limiter = new SendRateLimiter(4, s_window);
        foreach (var at in new[] { 0.0, 1.0, 2.0, 3.0 })
        {
            Assert.Equal(Seconds(at), limiter.NextAllowedAt(Seconds(at)));
            limiter.Record(Seconds(at));
        }

        Assert.Equal(Seconds(20), limiter.NextAllowedAt(Seconds(4)));
        Assert.Equal(4, limiter.CountInWindow(Seconds(4)));
    }

    [Fact]
    public void ASendBecomesPossibleExactlyWhenTheOldestLeavesTheWindow()
    {
        var limiter = new SendRateLimiter(4, s_window);
        foreach (var at in new[] { 0.0, 5.0, 10.0, 15.0 })
        {
            limiter.Record(Seconds(at));
        }

        Assert.Equal(Seconds(20), limiter.NextAllowedAt(Seconds(19.999)));
        Assert.Equal(Seconds(20), limiter.NextAllowedAt(Seconds(20)));
        Assert.Equal(3, limiter.CountInWindow(Seconds(20)));
    }

    [Fact]
    public void NoBurstAtAWindowBoundary_BecauseTheWindowSlides()
    {
        var limiter = new SendRateLimiter(4, s_window);
        foreach (var at in new[] { 0.0, 0.0, 0.0, 0.0 })
        {
            limiter.Record(Seconds(at));
        }

        // A refilling bucket would allow 4 more at t=20; sliding window allows them only because all 4 expired together.
        Assert.Equal(Seconds(20), limiter.NextAllowedAt(Seconds(10)));
        foreach (var _ in Enumerable.Range(0, 4))
        {
            limiter.Record(limiter.NextAllowedAt(Seconds(20)));
        }

        Assert.Equal(Seconds(40), limiter.NextAllowedAt(Seconds(21)));
    }

    [Fact]
    public void RandomSchedules_NeverPutMoreThanTheLimitInAnyWindow()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var random = new Random(seed);
            var limiter = new SendRateLimiter(4, s_window);
            var sends = new List<TimeSpan>();
            var now = TimeSpan.Zero;
            for (var i = 0; i < 300; i++)
            {
                now += TimeSpan.FromMilliseconds(random.Next(0, 4000));
                var at = limiter.NextAllowedAt(now);
                now = at;
                limiter.Record(now);
                sends.Add(now);
            }

            for (var i = 0; i + 4 < sends.Count; i++)
            {
                Assert.True(sends[i + 4] - sends[i] >= s_window, $"seed {seed}: sends {i}..{i + 4}");
            }
        }
    }

    [Fact]
    public void InvalidConfiguration_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SendRateLimiter(0, s_window));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SendRateLimiter(4, TimeSpan.Zero));
    }

    [Fact]
    public void OverAThousandSeededRandomSchedules_NoWindowEverHoldsMoreThanFourGrants()
    {
        for (var seed = 0; seed < 1000; seed++)
        {
            var random = new Random(seed);
            var limiter = new SendRateLimiter(4, s_window);
            var grants = new List<TimeSpan>();
            var now = TimeSpan.Zero;

            for (var step = 0; step < 80; step++)
            {
                now += TimeSpan.FromMilliseconds(random.Next(0, 9000));
                if (random.Next(0, 4) == 0)
                {
                    continue;
                }

                // A caller that wants to send either waits for the earliest allowed time or gives up for now.
                var at = limiter.NextAllowedAt(now);
                if (at > now)
                {
                    if (random.Next(0, 2) == 0)
                    {
                        continue;
                    }

                    now = at;
                }

                Assert.Equal(now, limiter.NextAllowedAt(now));
                limiter.Record(now);
                grants.Add(now);
                Assert.True(limiter.CountInWindow(now) <= 4, $"seed {seed}: window count {limiter.CountInWindow(now)}");
            }

            for (var i = 0; i + 4 < grants.Count; i++)
            {
                Assert.True(
                    grants[i + 4] - grants[i] >= s_window,
                    $"seed {seed}: grants {i} and {i + 4} are only {grants[i + 4] - grants[i]} apart.");
            }
        }
    }
}
