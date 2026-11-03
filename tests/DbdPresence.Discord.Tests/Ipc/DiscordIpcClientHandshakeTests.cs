// Covers: FR-RPC-01 (handshake, pipe endpoint, ack timeout), FR-RPC-08 (failure surfaced), privacy (READY user never exposed).
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Ipc;

public sealed class DiscordIpcClientHandshakeTests
{
    [Fact]
    public async Task Handshake_SendsExactVersionAndClientIdJson()
    {
        await using var fixture = new ClientFixture();

        var result = await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.True(result.Success);
        var frame = Assert.Single(fixture.Server.Latest.Received);
        Assert.Equal(IpcOpcode.Handshake, frame.Opcode);
        Assert.Equal("""{"v":1,"client_id":"123456789012345678"}""", frame.Text);
        Assert.Empty(fixture.Server.Violations);
    }

    [Fact]
    public async Task Handshake_ReadyCompletesWithEndpointAndExposesNoUserData()
    {
        await using var fixture = new ClientFixture();

        var result = await fixture.ConnectAsync();

        Assert.Equal("discord-ipc-0", fixture.Client.Endpoint);
        Assert.Null(result.Message);
        Assert.Null(result.Code);
        Assert.DoesNotContain("private-user", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_CloseFrameSurfacesCodeAndMessage()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Close(4000, "Invalid Client ID");

        var result = await fixture.Client.ConnectAndHandshakeAsync("999999999999999999", CancellationToken.None).Guard();

        Assert.False(result.Success);
        Assert.Equal(4000, result.Code);
        Assert.Contains("Invalid Client ID", result.Message, StringComparison.Ordinal);
        Assert.Contains("4000", result.Message, StringComparison.Ordinal);
        var disconnect = await fixture.Client.Disconnected.Guard();
        Assert.Equal(4000, disconnect.Code);
    }

    [Fact]
    public async Task Handshake_ErrorEventSurfacesCodeAndMessage()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.OnHandshake = (connection, _) => connection.SendError(null, 4001, "Invalid origin");

        var result = await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.False(result.Success);
        Assert.Equal(4001, result.Code);
        Assert.Contains("Invalid origin", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_NoDiscordRunningReportsUnreachable()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.Accepting = false;

        var result = await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();

        Assert.False(result.Success);
        Assert.Null(result.Code);
        Assert.Contains("not reachable", result.Message, StringComparison.Ordinal);
        Assert.True(fixture.Client.Disconnected.IsCompleted);
    }

    [Fact]
    public async Task Handshake_WithoutReadyTimesOutAfterFiveSecondsAndClosesTheTransport()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Ignore;

        var pending = fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).AsTask();
        fixture.Time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(pending.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromSeconds(0.1));
        var result = await pending.Guard();

        Assert.False(result.Success);
        Assert.Contains("handshake", result.Message, StringComparison.Ordinal);
        Assert.True(fixture.Server.Latest.ClosedByClient);
    }

    [Fact]
    public async Task Handshake_CancellationTearsDownAndPropagates()
    {
        await using var fixture = new ClientFixture();
        fixture.Server.OnHandshake = FakeDiscordServer.Replies.Ignore;
        using var cancel = new CancellationTokenSource();

        var pending = fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, cancel.Token).AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Guard());
        Assert.True(fixture.Server.Latest.ClosedByClient);
    }

    [Fact]
    public async Task Client_CanConnectOnlyOnce()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None));
    }
}
