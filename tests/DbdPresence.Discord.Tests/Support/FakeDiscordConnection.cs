using System.Buffers.Binary;
using System.Text;
using DbdPresence.Discord.Ipc;

namespace DbdPresence.Discord.Tests.Support;

/// <summary>
/// The in-memory duplex transport for one connection, with the server side attached: it decodes what the
/// client writes, records it, and answers according to the owning <see cref="FakeDiscordServer"/>.
/// </summary>
internal sealed class FakeDiscordConnection : IDiscordTransport
{
    private readonly FakeDiscordServer _server;
    private readonly ByteChannel _toClient = new();
    private readonly object _gate = new();
    private readonly List<ReceivedFrame> _received = [];
    private readonly int _firstEndpoint;
    private bool _serverClosed;

    public FakeDiscordConnection(FakeDiscordServer server, int firstEndpoint = 0)
    {
        _server = server;
        _firstEndpoint = firstEndpoint;
    }

    /// <inheritdoc />
    public string? Endpoint { get; private set; }

    /// <inheritdoc />
    public int EndpointCount => _server.EndpointCount;

    /// <inheritdoc />
    public int EndpointIndex => _firstEndpoint;

    /// <summary>Gets a value indicating whether the client disposed the transport.</summary>
    public bool ClosedByClient { get; private set; }

    /// <summary>Gets everything the client has sent on this connection.</summary>
    public IReadOnlyList<ReceivedFrame> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    /// <summary>Gets the SET_ACTIVITY commands received on this connection.</summary>
    public IReadOnlyList<ReceivedFrame> SetActivityFrames => [.. Received.Where(f => f.IsSetActivity)];

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        _server.RecordConnectAttempt();
        if (!_server.Accepting)
        {
            throw new IOException("No Discord IPC pipe accepted a connection.");
        }

        Endpoint = $"discord-ipc-{_firstEndpoint}";
        _server.Register(this);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (ClosedByClient || _serverClosed)
        {
            throw new IOException("Broken pipe.");
        }

        if (_server.FailWrites)
        {
            throw new IOException(@"The pipe \\.\pipe\C:\Users\private-name\secret is broken.");
        }

        if (_server.StallWrites)
        {
            return StallAsync(cancellationToken);
        }

        var bytes = frame.ToArray();
        if (bytes.Length < IpcFrameCodec.HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - IpcFrameCodec.HeaderSize)
        {
            _server.RecordViolation($"A write of {bytes.Length} bytes was not exactly one frame.");
            return ValueTask.CompletedTask;
        }

        var received = new ReceivedFrame(
            (IpcOpcode)BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            bytes[IpcFrameCodec.HeaderSize..],
            _server.Time.GetUtcNow());
        lock (_gate)
        {
            _received.Add(received);
        }

        _server.Dispatch(this, received);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask StallAsync(CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _toClient.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ClosedByClient = true;
        _toClient.Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends one frame to the client.</summary>
    public void Send(IpcOpcode opcode, string json) => SendRaw(IpcFrameCodec.Encode(opcode, Encoding.UTF8.GetBytes(json)));

    /// <summary>Sends raw bytes to the client (for malformed or fragmented input).</summary>
    public void SendRaw(byte[] bytes) => _toClient.Write(bytes);

    /// <summary>Sends a READY dispatch that includes a user object the client must never expose.</summary>
    public void SendReady() =>
        Send(IpcOpcode.Frame, """{"cmd":"DISPATCH","data":{"v":1,"config":{"cdn_host":"cdn.discordapp.com"},"user":{"id":"1","username":"private-user"}},"evt":"READY","nonce":null}""");

    /// <summary>Sends the success response for a SET_ACTIVITY request.</summary>
    public void SendActivityAccepted(string? nonce) =>
        Send(IpcOpcode.Frame, $$"""{"cmd":"SET_ACTIVITY","data":{"name":"app"},"evt":null,"nonce":"{{nonce}}"}""");

    /// <summary>Sends an ERROR response for a request.</summary>
    public void SendError(string? nonce, int code, string message) =>
        Send(
            IpcOpcode.Frame,
            nonce is null
                ? $$"""{"cmd":"SET_ACTIVITY","data":{"code":{{code}},"message":"{{message}}"},"evt":"ERROR"}"""
                : $$"""{"cmd":"SET_ACTIVITY","data":{"code":{{code}},"message":"{{message}}"},"evt":"ERROR","nonce":"{{nonce}}"}""");

    /// <summary>Sends a CLOSE frame.</summary>
    public void SendClose(int code, string message) =>
        Send(IpcOpcode.Close, $$"""{"code":{{code}},"message":"{{message}}"}""");

    /// <summary>Sends a PING frame with the given payload.</summary>
    public void SendPing(string payload) => Send(IpcOpcode.Ping, payload);

    /// <summary>Ends the connection from the server side: the client reads end of stream and writes fail.</summary>
    public void Drop()
    {
        _serverClosed = true;
        _toClient.Complete();
    }
}
