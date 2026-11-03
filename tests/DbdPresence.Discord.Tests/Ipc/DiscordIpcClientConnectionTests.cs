// Covers: FR-RPC-01 (PING/PONG, CLOSE, frame cap, 5 s ack timeout tears the connection down), FR-RPC-02 (disconnect detection for reconnect), FR-RPC-08 (sanitised Discord text).
using System.Text;
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Ipc;

public sealed class DiscordIpcClientConnectionTests
{
    private const int Pid = 4242;

    [Fact]
    public async Task Ping_IsAnsweredWithPongCarryingTheSamePayload()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.SendPing("""{"nonce":"abc-123"}""");

        var pong = Assert.Single(fixture.Server.Latest.Received, f => f.Opcode == IpcOpcode.Pong);
        Assert.Equal("""{"nonce":"abc-123"}""", pong.Text);
        Assert.Empty(fixture.Server.Violations);
        Assert.False(fixture.Client.Disconnected.IsCompleted);
    }

    [Fact]
    public async Task Ping_WithEmptyPayloadIsAnsweredWithEmptyPong()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.SendRaw(IpcFrameCodec.Encode(IpcOpcode.Ping, []));

        var pong = Assert.Single(fixture.Server.Latest.Received, f => f.Opcode == IpcOpcode.Pong);
        Assert.Empty(pong.Payload);
    }

    [Fact]
    public async Task Pong_FromTheServerIsIgnored()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.Send(IpcOpcode.Pong, "{}");
        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Still fine"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Close_EndsTheConnectionWithCodeAndMessage_AndLaterRequestsReportDisconnected()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.SendClose(1000, "Discord is shutting down");
        var info = await fixture.Client.Disconnected.Guard();
        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Too late"), Pid, CancellationToken.None).Guard();

        Assert.Equal(1000, info.Code);
        Assert.Equal("Discord said: \"Discord is shutting down\"", info.Message);
        Assert.Equal(SetActivityOutcome.Disconnected, result.Outcome);
    }

    [Fact]
    public async Task EndOfStream_EndsTheConnection()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.Drop();
        var info = await fixture.Client.Disconnected.Guard();

        Assert.Null(info.Code);
        Assert.Contains("closed", info.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectionLostWhileWaiting_CompletesTheRequestAsDisconnected()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Ignore;

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("In flight"), Pid, CancellationToken.None).AsTask();
        fixture.Server.Latest.Drop();
        var result = await pending.Guard();

        Assert.Equal(SetActivityOutcome.Disconnected, result.Outcome);
    }

    [Fact]
    public async Task WriteFailure_ReportsDisconnected()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.Latest.Drop();
        await fixture.Client.Disconnected.Guard();

        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Nobody home"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.Disconnected, result.Outcome);
    }

    [Fact]
    public async Task AckTimeout_IsFiveSeconds_ReturnsTimeoutAndTearsTheConnectionDown()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = FakeDiscordServer.Replies.Ignore;

        var pending = fixture.Client.SetActivityAsync(TestActivities.Simple("No answer"), Pid, CancellationToken.None).AsTask();
        fixture.Time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(pending.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await pending.Guard();

        Assert.Equal(SetActivityOutcome.Timeout, result.Outcome);
        Assert.Contains("acknowledge", result.Message, StringComparison.Ordinal);
        Assert.True(fixture.Server.Latest.ClosedByClient);
        Assert.True(fixture.Client.Disconnected.IsCompleted);
        Assert.Equal(SetActivityOutcome.Disconnected, (await fixture.Client.SetActivityAsync(null, Pid, CancellationToken.None).Guard()).Outcome);
    }

    [Fact]
    public async Task OversizeFrame_IsAProtocolErrorThatDisconnects()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.SendRaw([.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(65529u)]);
        var info = await fixture.Client.Disconnected.Guard();

        Assert.Contains("Protocol error", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOpcode_IsAProtocolErrorThatDisconnects()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.SendRaw([.. BitConverter.GetBytes(9u), .. BitConverter.GetBytes(0u)]);
        var info = await fixture.Client.Disconnected.Guard();

        Assert.Contains("Protocol error", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedJson_IsAProtocolErrorThatDisconnects()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        fixture.Server.Latest.Send(IpcOpcode.Frame, "{not json");
        var info = await fixture.Client.Disconnected.Guard();

        Assert.Contains("Protocol error", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentedServerFrames_AreReassembled()
    {
        await using var fixture = new ClientFixture();
        await fixture.ConnectAsync();
        fixture.Server.OnSetActivity = (connection, frame) =>
        {
            var response = IpcFrameCodec.Encode(
                IpcOpcode.Frame,
                Encoding.UTF8.GetBytes($$"""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"{{frame.Nonce}}"}"""));
            foreach (var piece in response.Chunk(3))
            {
                connection.SendRaw(piece);
            }
        };

        var result = await fixture.Client.SetActivityAsync(TestActivities.Simple("Chunked"), Pid, CancellationToken.None).Guard();

        Assert.Equal(SetActivityOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Dispose_ClosesTheTransportAndIsIdempotent()
    {
        var fixture = new ClientFixture();
        await fixture.ConnectAsync();

        await fixture.DisposeAsync();
        await fixture.DisposeAsync();

        Assert.True(fixture.Server.Latest.ClosedByClient);
        Assert.True(fixture.Client.Disconnected.IsCompleted);
    }
}
