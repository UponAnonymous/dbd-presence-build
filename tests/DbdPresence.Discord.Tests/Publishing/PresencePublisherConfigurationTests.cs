// Covers: FR-RPC-07 (application id handling, NotConfigured idle), FR-RPC-01 (pid), NFR-REL-01 (health snapshot, events, subscriber isolation).
using System.Text.Json;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Publishing;

public sealed class PresencePublisherConfigurationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("12345678901234567", true)]
    [InlineData("123456789012345678", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("123456789012345678901", false)]
    [InlineData("12345678901234567a", false)]
    [InlineData("1234567890 234567", false)]
    [InlineData("-1234567890123456", false)]
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢٣٤٥٦٧", false)]
    public void IsValidApplicationId_AcceptsOnlySnowflakesOf17To20AsciiDigits(string? id, bool expected) =>
        Assert.Equal(expected, PresencePublisher.IsValidApplicationId(id));

    [Fact]
    public async Task WithoutApplicationId_ItStaysNotConfiguredAndNeverConnects()
    {
        await using var fixture = new PublisherFixture();

        fixture.Publisher.Publish(TestActivities.Simple("Waiting for configuration"));
        await fixture.AdvanceSecondsAsync(600, 5);

        Assert.Equal(RpcStatus.NotConfigured, fixture.Publisher.Health.Status);
        Assert.Empty(fixture.Server.ConnectAttempts);
        Assert.Empty(fixture.Server.Connections);
        Assert.Empty(fixture.HealthEvents);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("not-a-snowflake")]
    [InlineData("123456789012345678901")]
    public async Task WithMalformedApplicationId_ItStaysNotConfiguredAndNeverConnects(string id)
    {
        await using var fixture = new PublisherFixture();

        await fixture.ConfigureAsync(id);
        fixture.Publisher.Publish(TestActivities.Simple("Still nothing"));
        await fixture.AdvanceSecondsAsync(120, 5);

        Assert.Equal(RpcStatus.NotConfigured, fixture.Publisher.Health.Status);
        Assert.Empty(fixture.Server.ConnectAttempts);
    }

    [Fact]
    public async Task ConfiguringAValidId_ConnectsAndReportsEndpointThroughHealthEvents()
    {
        await using var fixture = new PublisherFixture();

        await fixture.ConfigureAsync();

        Assert.Equal([RpcStatus.Connecting, RpcStatus.Connected], fixture.HealthEvents.Select(h => h.Status));
        var health = fixture.Publisher.Health;
        Assert.Equal(RpcStatus.Connected, health.Status);
        Assert.Equal("discord-ipc-0", health.Endpoint);
        Assert.Null(health.LastError);
        Assert.Null(health.NextRetryUtc);
        Assert.Null(health.LastAckUtc);
        var handshake = Assert.Single(fixture.Server.Latest.Received);
        Assert.Equal("""{"v":1,"client_id":"123456789012345678"}""", handshake.Text);
    }

    [Fact]
    public async Task Health_RecordsTheAcknowledgementAndTheUpdateCount()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        var start = fixture.Time.GetUtcNow();

        fixture.Publisher.Publish(TestActivities.Simple("Counted"));
        await fixture.AdvanceSecondsAsync(2);

        Assert.Equal(1, fixture.Publisher.Health.UpdatesInWindow);
        Assert.Equal(start + TimeSpan.FromSeconds(1), fixture.Publisher.Health.LastAckUtc);
    }

    [Fact]
    public async Task SetActivity_UsesTheConfiguredProcessId()
    {
        await using var fixture = new PublisherFixture(new PresencePublisherOptions { ProcessId = 31337 });
        await fixture.ConfigureAsync();

        fixture.Publisher.Publish(TestActivities.Simple("With pid"));
        await fixture.AdvanceSecondsAsync(2);

        var frame = Assert.Single(fixture.Server.SetActivityFrames);
        using var document = JsonDocument.Parse(frame.Payload);
        Assert.Equal(31337, document.RootElement.GetProperty("args").GetProperty("pid").GetInt32());
    }

    [Fact]
    public void DefaultOptions_MatchTheArchitectureNumbers()
    {
        var options = new PresencePublisherOptions();

        Assert.Equal(Environment.ProcessId, options.ProcessId);
        Assert.Equal(TimeSpan.FromSeconds(5), options.AckTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Debounce);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.ClearBudget);
        Assert.Equal(TimeSpan.FromSeconds(1), options.MinBackoff);
        Assert.Equal(TimeSpan.FromSeconds(60), options.MaxBackoff);
        Assert.Equal(4, options.UpdatesPerWindow);
        Assert.Equal(TimeSpan.FromSeconds(20), options.UpdateWindow);
    }

    [Fact]
    public async Task SettingTheSameIdAgain_DoesNotReconnect()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();

        await fixture.ConfigureAsync();
        await fixture.ConfigureAsync($" {TestActivities.ApplicationId} ");

        Assert.Single(fixture.Server.Connections);
    }

    [Fact]
    public async Task ChangingTheId_ReconnectsWithTheNewIdAndResendsTheCurrentActivity()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();
        fixture.Publisher.Publish(TestActivities.Simple("Carry over"));
        await fixture.AdvanceSecondsAsync(2);

        await fixture.ConfigureAsync("987654321098765432");
        await fixture.SettleAsync();

        Assert.Equal(2, fixture.Server.Connections.Count);
        Assert.True(fixture.Server.Connections[0].ClosedByClient);
        Assert.Equal("""{"v":1,"client_id":"987654321098765432"}""", fixture.Server.Connections[1].Received[0].Text);
        var resent = Assert.Single(fixture.Server.Connections[1].SetActivityFrames);
        Assert.Equal("Carry over", resent.Details);
    }

    [Fact]
    public async Task RemovingTheId_ClosesTheConnectionAndGoesIdle()
    {
        await using var fixture = new PublisherFixture();
        await fixture.ConfigureAsync();

        fixture.Publisher.SetApplicationId(null);
        await fixture.AdvanceSecondsAsync(300, 5);

        Assert.Equal(RpcStatus.NotConfigured, fixture.Publisher.Health.Status);
        Assert.Null(fixture.Publisher.Health.Endpoint);
        Assert.True(fixture.Server.Latest.ClosedByClient);
        Assert.Single(fixture.Server.ConnectAttempts);
    }
}
