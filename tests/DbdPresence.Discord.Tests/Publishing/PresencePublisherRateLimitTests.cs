// Covers: FR-RPC-03 (at most 4 updates per 20 s window, latest state always delivered eventually), FR-RPC-05 and NFR-REL-02 (the shutdown clear stays within Discord's 5 per 20 s).
using DbdPresence.Core.Presence;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherRateLimitTests
{
    private static readonly TimeSpan s_window = TimeSpan.FromSeconds(20);

    private static void AssertNeverMoreThanFourInAnyWindow(IReadOnlyList<ReceivedFrame> sends)
    {
        // If the fifth send after send i were less than a window after send i, five sends would fit in one window.
        for (var i = 0; i + 4 < sends.Count; i++)
        {
            Assert.True(
                sends[i + 4].At - sends[i].At >= s_window,
                $"Sends {i} and {i + 4} are only {sends[i + 4].At - sends[i].At} apart.");
        }
    }

    [Fact]
    public async Task SteadyChanges_AreSpacedSoNoWindowHoldsMoreThanFourSends()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        var start = fixture.Time.GetUtcNow();

        for (var i = 0; i < 40; i++)
        {
            fixture.Publisher.Publish(TestActivities.Simple($"change {i}"));
            await fixture.AdvanceSecondsAsync(2.5);
        }

        await fixture.AdvanceSecondsAsync(60, 1);
        var sends = fixture.Server.SetActivityFrames;
        AssertNeverMoreThanFourInAnyWindow(sends);
        Assert.True(sends.Count > 4);
        Assert.Equal("change 39", sends[^1].Details);

        // The first four go out as soon as the debounce allows; the fifth must wait for the window to slide.
        Assert.InRange(sends[3].At - start, TimeSpan.FromSeconds(8.5), TimeSpan.FromSeconds(9));
        Assert.True(sends[4].At - sends[0].At >= s_window);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RandomUpdateSchedules_NeverExceedTheLimit_AndAlwaysConvergeOnTheLatestActivity(int seed)
    {
        var random = new Random(seed);
        await using var fixture = new PublisherFixture(seed: seed);
        await fixture.ConfigureAsync();
        ValidatedActivity? desired = null;

        var elapsed = TimeSpan.Zero;
        while (elapsed < TimeSpan.FromSeconds(150))
        {
            var pick = random.Next(0, 7);
            desired = pick == 6 ? null : TestActivities.Simple($"variant {pick}");
            fixture.Publisher.Publish(desired);

            var gap = TimeSpan.FromMilliseconds(random.Next(0, 3000));
            await fixture.AdvanceAsync(gap, TimeSpan.FromMilliseconds(500));
            elapsed += gap;
        }

        await fixture.AdvanceSecondsAsync(90, 1);

        var sends = fixture.Server.SetActivityFrames;
        AssertNeverMoreThanFourInAnyWindow(sends);
        var shownByDiscord = sends.Count == 0 || sends[^1].IsClear ? null : sends[^1].Details;
        Assert.Equal(desired?.Details, shownByDiscord);
        Assert.Empty(fixture.Server.Violations);
    }

    [Fact]
    public async Task RateLimit_AlsoHoldsAcrossAReconnect()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        for (var i = 0; i < 4; i++)
        {
            fixture.Publisher.Publish(TestActivities.Simple($"before {i}"));
            await fixture.AdvanceSecondsAsync(1.5);
        }

        fixture.Server.Connections[0].Drop();
        fixture.Publisher.Publish(TestActivities.Simple("after reconnect"));
        await fixture.AdvanceUntilAsync(() => fixture.Server.SetActivityFrames.Count >= 5, 60, 0.25);

        AssertNeverMoreThanFourInAnyWindow(fixture.Server.SetActivityFrames);
        Assert.Equal(2, fixture.Server.Connections.Count);
    }
}
