// Covers: FR-RPC-02 (reconnect with jittered exponential backoff 1 s to 60 s, reset only after a healthy session, re-publish after reconnect),
// FR-RPC-01 (next pipe after a failed handshake), FR-RPC-08 (handshake rejection surfaced), NFR-REL-01 (loop-boundary fault capture).
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherReconnectTests
{
    private static readonly TimeSpan s_step = TimeSpan.FromMilliseconds(250);

    private static double[] GapsInSeconds(DateTimeOffset origin, IReadOnlyList<DateTimeOffset> attempts)
    {
        var previous = origin;
        var gaps = new List<double>();
        foreach (var attempt in attempts)
        {
            gaps.Add((attempt - previous).TotalSeconds);
            previous = attempt;
        }

        return [.. gaps];
    }

    [Fact]
    public async Task DiscordNotRunning_RetriesWithBackoffThatDoublesWithJitterAndCapsAtSixtySeconds()
    {
        await using var fixture = new PublisherFixture(seed: 11);
        fixture.Server.Accepting = false;
        var origin = fixture.Time.GetUtcNow();

        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 11, 900, 0.25);

        // Attempt 0 is immediate; each later gap is 1, 2, 4, … seconds, ±20 %, never above 60 s.
        var gaps = GapsInSeconds(origin, fixture.Server.ConnectAttempts);
        Assert.Equal(0, gaps[0], 0.01);
        var tolerance = s_step.TotalSeconds;
        for (var k = 1; k < 11; k++)
        {
            var nominal = Math.Min(Math.Pow(2, k - 1), 60);
            var low = Math.Max(nominal * 0.8, 1) - tolerance;
            var high = Math.Min(nominal * 1.2, 60) + tolerance;
            Assert.InRange(gaps[k], low, high);
        }

        // The doubling really happens: the 5th gap is clearly longer than the 2nd.
        Assert.True(gaps[5] > gaps[2] * 3);
        Assert.Equal(RpcStatus.Disconnected, fixture.Publisher.Health.Status);
        Assert.NotNull(fixture.Publisher.Health.NextRetryUtc);
        Assert.Contains("not reachable", fixture.Publisher.Health.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutJitter_TheDelaysAreExactlyOneTwoFourEightSixteenThirtyTwoThenSixty()
    {
        await using var fixture = new PublisherFixture(new PresencePublisherOptions { ProcessId = 1, BackoffJitter = 0 });
        fixture.Server.Accepting = false;
        var origin = fixture.Time.GetUtcNow();

        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 10, 900, 0.25);

        var gaps = GapsInSeconds(origin, fixture.Server.ConnectAttempts);
        double[] expected = [0, 1, 2, 4, 8, 16, 32, 60, 60, 60];
        for (var k = 0; k < expected.Length; k++)
        {
            Assert.InRange(gaps[k], expected[k], expected[k] + s_step.TotalSeconds);
        }
    }

    [Fact]
    public async Task NextRetryUtc_AnnouncesWhenTheNextAttemptIsDue()
    {
        await using var fixture = new PublisherFixture(new PresencePublisherOptions { ProcessId = 1, BackoffJitter = 0 });
        fixture.Server.Accepting = false;

        await fixture.ConfigureAsync();

        var health = fixture.Publisher.Health;
        Assert.Equal(RpcStatus.Disconnected, health.Status);
        Assert.Equal(fixture.Time.GetUtcNow() + TimeSpan.FromSeconds(1), health.NextRetryUtc);
    }

    [Fact]
    public async Task DiscordRestart_ReconnectsAfterBackoffAndResendsTheCurrentActivity()
    {
        await using var fixture = new PublisherFixture(seed: 5);
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Survives restarts"));
        await fixture.AdvanceSecondsAsync(2);
        Assert.Single(fixture.Server.SetActivityFrames);

        fixture.Server.Stop();
        await fixture.AdvanceSecondsAsync(20, 0.25);
        Assert.NotEqual(RpcStatus.Connected, fixture.Publisher.Health.Status);
        Assert.True(fixture.Server.ConnectAttempts.Count > 1);
        fixture.Server.Accepting = true;
        await fixture.AdvanceUntilAsync(() => fixture.Server.Connections.Count == 2, 60, 0.25);
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
        var resent = Assert.Single(fixture.Server.Connections[1].SetActivityFrames);
        Assert.Equal("Survives restarts", resent.Details);
        Assert.Empty(fixture.Server.Violations);
    }

    [Fact]
    public async Task AfterAStableSession_TheBackoffStartsOverAtOneSecond()
    {
        await using var fixture = new PublisherFixture(seed: 9);
        await fixture.ConfigureAsync();
        fixture.Server.Stop();
        await fixture.AdvanceSecondsAsync(100, 0.25);
        fixture.Server.Accepting = true;
        await fixture.AdvanceUntilAsync(() => fixture.Server.Connections.Count == 2, 120, 0.25);
        await fixture.AdvanceSecondsAsync(31, 0.5);

        var attemptsBefore = fixture.Server.ConnectAttempts.Count;
        var droppedAt = fixture.Time.GetUtcNow();
        fixture.Server.Stop();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count > attemptsBefore, 20, 0.25);

        var firstGap = (fixture.Server.ConnectAttempts[attemptsBefore] - droppedAt).TotalSeconds;
        Assert.InRange(firstGap, 1 - 0.25, 1.2 + 0.25);
    }

    [Fact]
    public async Task DiscordRestartedQuickly_ReconnectsAfterAboutOneSecond()
    {
        await using var fixture = new PublisherFixture(seed: 21);
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Quick restart"));
        await fixture.AdvanceSecondsAsync(2);
        var droppedAt = fixture.Time.GetUtcNow();

        fixture.Server.Connections[0].Drop();
        await fixture.AdvanceUntilAsync(() => fixture.Server.Connections.Count == 2, 10, 0.1);

        Assert.InRange((fixture.Server.ConnectAttempts[^1] - droppedAt).TotalSeconds, 1 - 0.1, 1.2 + 0.1);
        await fixture.AdvanceSecondsAsync(2);
        Assert.Equal("Quick restart", Assert.Single(fixture.Server.Connections[1].SetActivityFrames).Details);
    }

    [Fact]
    public async Task RejectedHandshake_ShowsDiscordCodeAndMessage_AndKeepsRetryingUntilFixed()
    {
        await using var fixture = new PublisherFixture(seed: 3);
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Close(4000, "Invalid Client ID");

        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 3, 30, 0.25);

        var health = fixture.Publisher.Health;
        Assert.Equal(RpcStatus.Disconnected, health.Status);
        Assert.Contains("4000", health.LastError, StringComparison.Ordinal);
        Assert.Contains("Invalid Client ID", health.LastError, StringComparison.Ordinal);

        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Ready;
        await fixture.AdvanceUntilAsync(() => fixture.Publisher.Health.Status == RpcStatus.Connected, 90, 0.25);
        Assert.Null(fixture.Publisher.Health.LastError);
    }

    [Fact]
    public async Task ChangingTheIdDuringBackoff_RetriesImmediately()
    {
        await using var fixture = new PublisherFixture(seed: 4);
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Close(4000, "Invalid Client ID");
        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => fixture.Server.ConnectAttempts.Count >= 4, 60, 0.25);
        var attemptsBefore = fixture.Server.ConnectAttempts.Count;

        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Ready;
        await fixture.ConfigureAsync("111111111111111111");

        Assert.Equal(attemptsBefore + 1, fixture.Server.ConnectAttempts.Count);
        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
    }

    [Fact]
    public async Task UnacknowledgedUpdate_TimesOutAfterFiveSeconds_DropsTheConnectionAndRecovers()
    {
        await using var fixture = new PublisherFixture(seed: 8);
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Ignore;
        fixture.Publisher.Publish(TestActivities.Simple("Never acknowledged"));

        await fixture.AdvanceSecondsAsync(1);
        Assert.Single(fixture.Server.SetActivityFrames);
        await fixture.AdvanceSecondsAsync(4.8);
        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
        await fixture.AdvanceSecondsAsync(0.2);

        Assert.Equal(RpcStatus.Disconnected, fixture.Publisher.Health.Status);
        Assert.Contains("acknowledge", fixture.Publisher.Health.LastError, StringComparison.Ordinal);
        Assert.True(fixture.Server.Connections[0].ClosedByClient);

        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        await fixture.AdvanceUntilAsync(() => fixture.Server.Connections.Count == 2, 10, 0.25);
        await fixture.AdvanceSecondsAsync(1);

        Assert.Equal("Never acknowledged", Assert.Single(fixture.Server.Connections[1].SetActivityFrames).Details);
        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
        Assert.NotNull(fixture.Publisher.Health.LastAckUtc);
    }

    [Fact]
    public async Task UnexpectedExceptionInTheLoop_IsRecordedAsFaulted_AndTheLoopKeepsRunning()
    {
        var factory = new ThrowingFactory();
        await using var fixture = new PublisherFixture(seed: 2, factory: factory);
        factory.Inner = fixture.Server;

        await fixture.ConfigureAsync();
        await fixture.AdvanceUntilAsync(() => factory.Calls >= 3, 30, 0.25);

        var health = fixture.Publisher.Health;
        Assert.Equal(RpcStatus.Faulted, health.Status);
        Assert.Equal("Unexpected InvalidOperationException in the Discord connection; it will be retried.", health.LastError);
        Assert.DoesNotContain("exploded", health.LastError, StringComparison.Ordinal);
        Assert.NotNull(health.NextRetryUtc);

        factory.Throw = false;
        fixture.Publisher.Publish(TestActivities.Simple("Back"));
        await fixture.AdvanceUntilAsync(() => fixture.Publisher.Health.Status == RpcStatus.Connected, 90, 0.25);
        Assert.Contains(RpcStatus.Faulted, fixture.HealthEvents.Select(h => h.Status));
    }

    private sealed class ThrowingFactory : IDiscordTransportFactory
    {
        private int _calls;

        public FakeDiscordServer? Inner { get; set; }

        public bool Throw { get; set; } = true;

        public int Calls => Volatile.Read(ref _calls);

        public IDiscordTransport Create()
        {
            Interlocked.Increment(ref _calls);
            return Throw ? throw new InvalidOperationException("transport factory exploded") : Inner!.Create();
        }
    }
}
