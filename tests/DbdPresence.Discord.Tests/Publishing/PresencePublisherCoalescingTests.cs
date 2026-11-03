// Covers: FR-RPC-03 (dedupe against the acknowledged activity, 1 s debounce, latest wins, thread-safe non-blocking publish).
using DbdPresence.Core.Presence;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherCoalescingTests
{
    [Fact]
    public async Task FirstUpdate_IsSentOneSecondAfterItWasPublished()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();

        fixture.Publisher.Publish(TestActivities.Simple("Debounced"));
        await fixture.AdvanceSecondsAsync(0.9);
        Assert.Empty(fixture.Server.SetActivityFrames);
        await fixture.AdvanceSecondsAsync(0.1);

        Assert.Equal("Debounced", Assert.Single(fixture.Server.SetActivityFrames).Details);
    }

    [Fact]
    public async Task EqualActivityPublishedAgain_IsNotSentAgain()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Same"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Publisher.Publish(TestActivities.Simple("Same"));
        await fixture.AdvanceSecondsAsync(120, 1);

        Assert.Single(fixture.Server.SetActivityFrames);
    }

    [Fact]
    public async Task ChangeThatIsRevertedBeforeTheDebounceEnds_SendsNothing()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Original"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Publisher.Publish(TestActivities.Simple("Flicker"));
        await fixture.AdvanceSecondsAsync(0.5);
        fixture.Publisher.Publish(TestActivities.Simple("Original"));
        await fixture.AdvanceSecondsAsync(30, 1);

        Assert.Single(fixture.Server.SetActivityFrames);
    }

    [Fact]
    public async Task BurstOfOneHundredUpdatesInOneSecond_SendsOnlyTheLastOneAfterTheDebounce()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();

        for (var i = 0; i < 100; i++)
        {
            fixture.Publisher.Publish(TestActivities.Simple($"update {i}"));
            await fixture.AdvanceAsync(TimeSpan.FromMilliseconds(10));
        }

        Assert.Empty(fixture.Server.SetActivityFrames);
        await fixture.AdvanceSecondsAsync(0.9);
        Assert.Empty(fixture.Server.SetActivityFrames);
        await fixture.AdvanceSecondsAsync(0.2);
        await fixture.AdvanceSecondsAsync(30, 1);

        Assert.Equal("update 99", Assert.Single(fixture.Server.SetActivityFrames).Details);
    }

    [Fact]
    public async Task SteadyStreamOfChanges_CannotStarveThePresenceBeyondTheMaximumCoalesceDelay()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        var start = fixture.Time.GetUtcNow();

        // A change every 500 ms never lets the 1 s debounce expire; the 5 s cap must still force a send.
        for (var i = 0; i < 12; i++)
        {
            fixture.Publisher.Publish(TestActivities.Simple($"tick {i}"));
            await fixture.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        }

        var first = fixture.Server.SetActivityFrames[0];
        Assert.InRange(first.At - start, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5.1));
        Assert.Equal("tick 9", first.Details);
    }

    [Fact]
    public async Task ClearingAfterAnActivity_IsSentAsNullActivity()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Shown"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Publisher.Publish(null);
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(2, fixture.Server.SetActivityFrames.Count);
        Assert.True(fixture.Server.SetActivityFrames[1].IsClear);
    }

    [Fact]
    public async Task ClearingWhenNothingIsShown_SendsNothing()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();

        fixture.Publisher.Publish(null);
        await fixture.AdvanceSecondsAsync(30, 1);

        Assert.Empty(fixture.Server.SetActivityFrames);
    }

    [Fact]
    public async Task PublishFromManyThreads_NeverThrowsAndSendsOneOfThePublishedActivities()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        var published = Enumerable.Range(0, 500).Select(i => $"thread update {i}").ToArray();

        Parallel.ForEach(published, details => fixture.Publisher.Publish(TestActivities.Simple(details)));
        await fixture.AdvanceSecondsAsync(10, 1);

        var sent = Assert.Single(fixture.Server.SetActivityFrames);
        Assert.Contains(sent.Details, published);
    }

    [Fact]
    public async Task EveryFieldCountsForEquality_SoAChangedButtonIsSent()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(Draft("https://example.com/a"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Publisher.Publish(Draft("https://example.com/b"));
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(2, fixture.Server.SetActivityFrames.Count);

        static ValidatedActivity Draft(string url) =>
            TestActivities.From(new ActivityDraft { Details = "Buttons", Buttons = [new ButtonDraft("Open", url)] });
    }
}
