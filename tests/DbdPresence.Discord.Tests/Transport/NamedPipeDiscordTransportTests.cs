// Covers: FR-RPC-01 (pipe scan discord-ipc-0..9, framed duplex I/O against a real pipe), NFR-SEC-01 (local named pipe only, current-user servers only).
using System.IO.Pipes;
using System.Text;
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Discord.Tests.Transport;

public sealed class NamedPipeDiscordTransportTests
{
    // A missing pipe fails at once; this only bounds how long a connect attempt may take if it did not.
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromMilliseconds(30);

    private static string UniquePrefix() => $"dbdp-test-{Guid.NewGuid():N}-";

    private static NamedPipeServerStream StartServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    [Fact]
    public async Task Connect_UsesTheFirstPipeThatAccepts()
    {
        var prefix = UniquePrefix();
        await using var server = StartServer(prefix + "0");
        var accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var transport = new NamedPipeDiscordTransportFactory(prefix, 3, s_connectTimeout).Create();

        await transport.ConnectAsync(CancellationToken.None).Guard();
        await accept.Guard();

        Assert.Equal(prefix + "0", transport.Endpoint);
    }

    [Fact]
    public async Task Connect_SkipsMissingPipesAndFindsALaterOne()
    {
        var prefix = UniquePrefix();
        await using var server = StartServer(prefix + "2");
        var accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var transport = new NamedPipeDiscordTransportFactory(prefix, 4, s_connectTimeout).Create();

        await transport.ConnectAsync(CancellationToken.None).Guard();
        await accept.Guard();

        Assert.Equal(prefix + "2", transport.Endpoint);
    }

    [Fact]
    public async Task Connect_StartingAtALaterEndpoint_SkipsTheEarlierPipes()
    {
        var prefix = UniquePrefix();
        await using var first = StartServer(prefix + "0");
        await using var second = StartServer(prefix + "1");
        var accept = second.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var transport = new NamedPipeDiscordTransportFactory(prefix, 3, s_connectTimeout).Create(1);

        await transport.ConnectAsync(CancellationToken.None).Guard();
        await accept.Guard();

        Assert.Equal(prefix + "1", transport.Endpoint);
        Assert.Equal(1, transport.EndpointIndex);
        Assert.Equal(3, transport.EndpointCount);
        Assert.Null(transport.Notice);
    }

    [Fact]
    public async Task Connect_WithNoPipe_ThrowsIOException()
    {
        await using var transport = new NamedPipeDiscordTransportFactory(UniquePrefix(), 2, s_connectTimeout).Create();

        var error = await Assert.ThrowsAsync<IOException>(async () => await transport.ConnectAsync(CancellationToken.None));

        Assert.Null(transport.Endpoint);
        Assert.Contains("No Discord IPC pipe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_TwiceOnTheSameTransport_Throws()
    {
        var prefix = UniquePrefix();
        await using var server = StartServer(prefix + "0");
        var accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var transport = new NamedPipeDiscordTransportFactory(prefix, 1, s_connectTimeout).Create();
        await transport.ConnectAsync(CancellationToken.None).Guard();
        await accept.Guard();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Frames_RoundTripOverARealPipe_AndTheClientWritesOneBufferPerFrame()
    {
        var prefix = UniquePrefix();
        await using var server = StartServer(prefix + "0");
        var accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        await using var client = new DiscordIpcClient(
            new NamedPipeDiscordTransportFactory(prefix, 1, s_connectTimeout).Create(), time, TimeSpan.FromSeconds(5));

        var handshake = client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).AsTask();
        await accept.Guard();
        var request = await ReadFrameAsync(server);
        Assert.Equal(IpcOpcode.Handshake, request.Opcode);
        Assert.Equal("""{"v":1,"client_id":"123456789012345678"}""", Encoding.UTF8.GetString(request.Payload.Span));
        await WriteFrameAsync(server, IpcOpcode.Frame, """{"cmd":"DISPATCH","data":{"v":1},"evt":"READY","nonce":null}""");
        Assert.True((await handshake.Guard()).Success);
        Assert.Equal(prefix + "0", client.Endpoint);

        var set = client.SetActivityAsync(TestActivities.Simple("Over the pipe"), 7, CancellationToken.None).AsTask();
        var command = await ReadFrameAsync(server);
        var json = Encoding.UTF8.GetString(command.Payload.Span);
        Assert.Contains("\"details\":\"Over the pipe\"", json, StringComparison.Ordinal);
        var nonce = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("nonce").GetString();
        await WriteFrameAsync(server, IpcOpcode.Frame, $$"""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"{{nonce}}"}""");

        Assert.Equal(SetActivityOutcome.Success, (await set.Guard()).Outcome);
    }

    [Fact]
    public async Task ServerClosingThePipe_IsDetectedAsDisconnect()
    {
        var prefix = UniquePrefix();
        var server = StartServer(prefix + "0");
        var accept = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        await using var client = new DiscordIpcClient(
            new NamedPipeDiscordTransportFactory(prefix, 1, s_connectTimeout).Create(), time, TimeSpan.FromSeconds(5));
        var handshake = client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).AsTask();
        await accept.Guard();
        _ = await ReadFrameAsync(server);
        await WriteFrameAsync(server, IpcOpcode.Frame, """{"cmd":"DISPATCH","evt":"READY"}""");
        await handshake.Guard();

        await server.DisposeAsync();
        var info = await client.Disconnected.Guard();

        Assert.False(string.IsNullOrEmpty(info.Message));
    }

    private static async Task<IpcFrame> ReadFrameAsync(NamedPipeServerStream server)
    {
        var header = new byte[IpcFrameCodec.HeaderSize];
        await server.ReadExactlyAsync(header).AsTask().Guard();
        var length = BitConverter.ToInt32(header, 4);
        var payload = new byte[length];
        await server.ReadExactlyAsync(payload).AsTask().Guard();
        return new IpcFrame((IpcOpcode)BitConverter.ToUInt32(header, 0), payload);
    }

    private static async Task WriteFrameAsync(NamedPipeServerStream server, IpcOpcode opcode, string json)
    {
        await server.WriteAsync(IpcFrameCodec.Encode(opcode, Encoding.UTF8.GetBytes(json))).AsTask().Guard();
        await server.FlushAsync();
    }
}
