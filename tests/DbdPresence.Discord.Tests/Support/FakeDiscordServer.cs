using DbdPresence.Discord.Ipc;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Discord.Tests.Support;

/// <summary>
/// A scriptable in-memory Discord: a transport factory whose connections answer the handshake and
/// <c>SET_ACTIVITY</c> the way the real client does, or deliberately misbehave (error, close, silence,
/// drop, restart). Every frame the client sends is recorded.
/// </summary>
internal sealed class FakeDiscordServer : IDiscordTransportFactory
{
    private readonly object _gate = new();
    private readonly List<FakeDiscordConnection> _connections = [];
    private readonly List<DateTimeOffset> _connectAttempts = [];
    private readonly List<string> _violations = [];

    public FakeDiscordServer(FakeTimeProvider time)
    {
        Time = time;
    }

    /// <summary>Gets the fake clock used to timestamp frames.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Gets or sets a value indicating whether connection attempts succeed (false: Discord is not running).</summary>
    public bool Accepting { get; set; } = true;

    /// <summary>Gets or sets how many pipes the fake offers (Discord has 10); connection attempts can start at any of them.</summary>
    public int EndpointCount { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether writes from the client never complete (a stalled pipe) until cancelled.</summary>
    public bool StallWrites { get; set; }

    /// <summary>Gets or sets a value indicating whether writes from the client fail with an IOException whose message names a private path.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Gets or sets how the server answers the handshake. The default sends READY.</summary>
    public Action<FakeDiscordConnection, ReceivedFrame> OnHandshake { get; set; } = Replies.Ready;

    /// <summary>Gets or sets how the server answers SET_ACTIVITY. The default acknowledges it.</summary>
    public Action<FakeDiscordConnection, ReceivedFrame> OnSetActivity { get; set; } = Replies.Accept;

    /// <summary>Gets every connection that completed ConnectAsync, oldest first.</summary>
    public IReadOnlyList<FakeDiscordConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections];
            }
        }
    }

    /// <summary>Gets the time of every connection attempt, successful or not.</summary>
    public IReadOnlyList<DateTimeOffset> ConnectAttempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _connectAttempts];
            }
        }
    }

    /// <summary>Gets framing violations by the client (a write that was not exactly one frame).</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (_gate)
            {
                return [.. _violations];
            }
        }
    }

    /// <summary>Gets the total number of frames received over all connections.</summary>
    public int FrameCount => Connections.Sum(c => c.Received.Count);

    /// <summary>Gets all SET_ACTIVITY commands over all connections, in arrival order.</summary>
    public IReadOnlyList<ReceivedFrame> SetActivityFrames =>
        [.. Connections.SelectMany(c => c.SetActivityFrames).OrderBy(f => f.At)];

    /// <summary>Gets the most recent connection.</summary>
    public FakeDiscordConnection Latest => Connections[^1];

    /// <inheritdoc />
    public IDiscordTransport Create() => Create(0);

    /// <inheritdoc />
    public IDiscordTransport Create(int firstEndpoint) => new FakeDiscordConnection(this, firstEndpoint);

    /// <summary>Simulates Discord quitting: every connection ends and new attempts fail until <see cref="Accepting"/> is set.</summary>
    public void Stop()
    {
        Accepting = false;
        foreach (var connection in Connections)
        {
            connection.Drop();
        }
    }

    internal void RecordConnectAttempt()
    {
        lock (_gate)
        {
            _connectAttempts.Add(Time.GetUtcNow());
        }
    }

    internal void Register(FakeDiscordConnection connection)
    {
        lock (_gate)
        {
            _connections.Add(connection);
        }
    }

    internal void RecordViolation(string description)
    {
        lock (_gate)
        {
            _violations.Add(description);
        }
    }

    internal void Dispatch(FakeDiscordConnection connection, ReceivedFrame frame)
    {
        switch (frame.Opcode)
        {
            case IpcOpcode.Handshake:
                OnHandshake(connection, frame);
                break;
            case IpcOpcode.Frame when frame.IsSetActivity:
                OnSetActivity(connection, frame);
                break;
            default:
                break;
        }
    }

    /// <summary>Ready-made server behaviours.</summary>
    public static class Replies
    {
        /// <summary>Answers the handshake with READY.</summary>
        public static Action<FakeDiscordConnection, ReceivedFrame> Ready { get; } = (connection, _) => connection.SendReady();

        /// <summary>Acknowledges SET_ACTIVITY, echoing the nonce.</summary>
        public static void Accept(FakeDiscordConnection connection, ReceivedFrame frame) => connection.SendActivityAccepted(frame.Nonce);

        /// <summary>Does not answer at all.</summary>
        public static Action<FakeDiscordConnection, ReceivedFrame> Ignore { get; } = (_, _) => { };

        /// <summary>Creates a behaviour that answers with a Discord ERROR carrying the request's nonce.</summary>
        public static Action<FakeDiscordConnection, ReceivedFrame> Error(int code, string message) =>
            (connection, frame) => connection.SendError(frame.Nonce, code, message);

        /// <summary>Creates a behaviour that answers with a CLOSE frame.</summary>
        public static Action<FakeDiscordConnection, ReceivedFrame> Close(int code, string message) =>
            (connection, _) => connection.SendClose(code, message);
    }
}
