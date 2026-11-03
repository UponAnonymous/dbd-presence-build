// Covers: FR-RPC-08 (Discord errors surfaced in health, a rejected payload is not retried until it changes or the connection is renewed), FR-RPC-02 (re-publish after reconnect).
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherErrorTests
{
    [Fact]
    public async Task DiscordError_IsRecordedInHealth_AndTheIdenticalPayloadIsNotRetried()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Error(4000, "Invalid activity payload");

        fixture.Publisher.Publish(TestActivities.Simple("Rejected"));
        await fixture.AdvanceSecondsAsync(2);

        var health = fixture.Publisher.Health;
        Assert.Equal(RpcStatus.Connected, health.Status);
        Assert.Contains("4000", health.LastError, StringComparison.Ordinal);
        Assert.Contains("Invalid activity payload", health.LastError, StringComparison.Ordinal);
        Assert.Null(health.LastAckUtc);

        await fixture.AdvanceSecondsAsync(600, 5);
        fixture.Publisher.Publish(TestActivities.Simple("Rejected"));
        await fixture.AdvanceSecondsAsync(60, 1);

        Assert.Single(fixture.Server.SetActivityFrames);
        Assert.Single(fixture.Server.Connections);
    }

    [Fact]
    public async Task AfterAnError_ADifferentPayloadIsSent_AndClearsTheErrorOnSuccess()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Error(4000, "Invalid activity payload");
        fixture.Publisher.Publish(TestActivities.Simple("Rejected"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        fixture.Publisher.Publish(TestActivities.Simple("Accepted"));
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(2, fixture.Server.SetActivityFrames.Count);
        Assert.Null(fixture.Publisher.Health.LastError);
        Assert.NotNull(fixture.Publisher.Health.LastAckUtc);
    }

    [Fact]
    public async Task ARejectedPayloadCanBeSentAgainOnceAnotherPayloadWasAccepted()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Error(4000, "Invalid activity payload");
        fixture.Publisher.Publish(TestActivities.Simple("Flaky"));
        await fixture.AdvanceSecondsAsync(2);
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;
        fixture.Publisher.Publish(TestActivities.Simple("Fine"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Publisher.Publish(TestActivities.Simple("Flaky"));
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(["Flaky", "Fine", "Flaky"], fixture.Server.SetActivityFrames.Select(f => f.Details));
    }

    [Fact]
    public async Task ErrorWithoutNonce_IsStillAttributedToTheUpdateInFlight()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = (connection, _) => connection.SendError(null, 4002, "Invalid payload");

        fixture.Publisher.Publish(TestActivities.Simple("Anonymous error"));
        await fixture.AdvanceSecondsAsync(2);
        await fixture.AdvanceSecondsAsync(60, 1);

        Assert.Single(fixture.Server.SetActivityFrames);
        Assert.Equal(RpcStatus.Connected, fixture.Publisher.Health.Status);
        Assert.Contains("4002", fixture.Publisher.Health.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerCloseDuringPublishing_ReconnectsAndKeepsTheLatestActivity()
    {
        await using var fixture = new PublisherFixture(seed: 13);
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Before close"));
        await fixture.AdvanceSecondsAsync(2);

        fixture.Server.Latest.SendClose(1000, "Discord restarting");
        await fixture.SettleAsync();
        Assert.Equal(RpcStatus.Disconnected, fixture.Publisher.Health.Status);
        Assert.Contains("Discord restarting", fixture.Publisher.Health.LastError, StringComparison.Ordinal);
        await fixture.AdvanceUntilAsync(() => fixture.Server.Connections.Count == 2, 10, 0.25);
        await fixture.AdvanceSecondsAsync(1);

        Assert.Equal("Before close", Assert.Single(fixture.Server.Connections[1].SetActivityFrames).Details);
    }
}
