// Covers: FR-RPC-05 (clear activity on graceful exit), NFR-REL-02 (500 ms clear budget, then close; ARCHITECTURE §6 shutdown order), idempotent shutdown.
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherShutdownTests
{
    [Fact]
    public async Task Shutdown_ClearsTheActivityThenClosesTheConnection()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("On screen"));
        await fixture.AdvanceSecondsAsync(2);

        await fixture.Publisher.ShutdownAsync().Guard();

        var connection = fixture.Server.Latest;
        var last = connection.Received[^1];
        Assert.True(last.IsClear);
        Assert.Equal(2, connection.SetActivityFrames.Count);
        Assert.True(connection.ClosedByClient);
        Assert.Equal(RpcStatus.Disconnected, fixture.Publisher.Health.Status);
        Assert.Null(fixture.Publisher.Health.Endpoint);
        Assert.Empty(fixture.Server.Violations);
    }

    [Fact]
    public async Task SecondShutdown_IsANoOp()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("On screen"));
        await fixture.AdvanceSecondsAsync(2);
        await fixture.Publisher.ShutdownAsync().Guard();
        var frames = fixture.Server.FrameCount;
        var health = fixture.Publisher.Health;

        await fixture.Publisher.ShutdownAsync().Guard();
        await fixture.Publisher.DisposeAsync().Guard();

        Assert.Equal(frames, fixture.Server.FrameCount);
        Assert.Equal(health, fixture.Publisher.Health);
        Assert.Single(fixture.Server.Connections);
    }

    [Fact]
    public async Task ConcurrentShutdowns_ClearOnlyOnce()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("On screen"));
        await fixture.AdvanceSecondsAsync(2);

        await Task.WhenAll(
            fixture.Publisher.ShutdownAsync().Guard(),
            fixture.Publisher.ShutdownAsync().Guard(),
            fixture.Publisher.DisposeAsync().Guard());

        Assert.Single(fixture.Server.Latest.Received, f => f.IsClear);
    }

    [Fact]
    public async Task Shutdown_WhenNothingWasEverShown_ClosesWithoutSendingAClear()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();

        await fixture.Publisher.ShutdownAsync().Guard();

        Assert.Empty(fixture.Server.SetActivityFrames);
        Assert.True(fixture.Server.Latest.ClosedByClient);
    }

    [Fact]
    public async Task Shutdown_AfterTheActivityWasAlreadyCleared_DoesNotClearAgain()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Visible"));
        await fixture.AdvanceSecondsAsync(2);
        fixture.Publisher.Publish(null);
        await fixture.AdvanceSecondsAsync(2);

        await fixture.Publisher.ShutdownAsync().Guard();

        Assert.Equal(2, fixture.Server.SetActivityFrames.Count);
    }

    [Fact]
    public async Task Shutdown_WhenNotConfigured_CompletesWithoutConnecting()
    {
        var fixture = new PublisherFixture();

        await fixture.Publisher.ShutdownAsync().Guard();

        Assert.Empty(fixture.Server.ConnectAttempts);
        Assert.Equal(RpcStatus.NotConfigured, fixture.Publisher.Health.Status);
    }

    [Fact]
    public async Task Shutdown_WithAnUnresponsiveDiscord_GivesUpAfterTheHalfSecondClearBudget()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("On screen"));
        await fixture.AdvanceSecondsAsync(2);
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Ignore;

        var shutdown = fixture.Publisher.ShutdownAsync().AsTask();
        await fixture.SettleAsync();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(499));
        await fixture.SettleAsync();
        Assert.False(shutdown.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
        await shutdown.Guard();

        Assert.True(fixture.Server.Latest.Received[^1].IsClear);
        Assert.True(fixture.Server.Latest.ClosedByClient);
        Assert.Contains("500 ms", fixture.Publisher.Health.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_WhileAnUpdateAwaitsItsAck_StillClearsAndCloses()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Ignore;
        fixture.Publisher.Publish(TestActivities.Simple("Unacknowledged"));
        await fixture.AdvanceSecondsAsync(1.5);
        Assert.Single(fixture.Server.SetActivityFrames);
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Accept;

        await fixture.Publisher.ShutdownAsync().Guard();

        Assert.True(fixture.Server.Latest.SetActivityFrames[^1].IsClear);
        Assert.True(fixture.Server.Latest.ClosedByClient);
    }

    [Fact]
    public async Task Shutdown_DuringBackoff_StopsRetryingPromptly()
    {
        await using var fixture = new PublisherFixture();
        fixture.Server.Accepting = false;
        await fixture.ConfigureAsync();
        var attempts = fixture.Server.ConnectAttempts.Count;

        await fixture.Publisher.ShutdownAsync().Guard();
        await fixture.AdvanceSecondsAsync(300, 5);

        Assert.Equal(attempts, fixture.Server.ConnectAttempts.Count);
        Assert.Equal(RpcStatus.Disconnected, fixture.Publisher.Health.Status);
    }

    [Fact]
    public async Task PublishAndConfigure_AfterShutdown_AreIgnored()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        await fixture.Publisher.ShutdownAsync().Guard();
        var frames = fixture.Server.FrameCount;

        fixture.Publisher.Publish(TestActivities.Simple("Too late"));
        fixture.Publisher.SetApplicationId("987654321098765432");
        await fixture.AdvanceSecondsAsync(30, 1);

        Assert.Equal(frames, fixture.Server.FrameCount);
        Assert.Single(fixture.Server.Connections);
        Assert.Equal(IpcOpcode.Handshake, fixture.Server.Latest.Received[0].Opcode);
    }
}
