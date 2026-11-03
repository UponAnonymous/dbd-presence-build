using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Presence;

namespace DbdPresence.Discord.Ipc;

/// <summary>
/// One connection to the local Discord client (FR-RPC-01, FR-RPC-08). It performs the handshake,
/// sends <c>SET_ACTIVITY</c> commands and runs a background reader that correlates responses by nonce, answers
/// <c>PING</c> with <c>PONG</c> and notices <c>CLOSE</c> or end of stream. A connection is used once: after it
/// ended, create a new client with a fresh transport.
/// </summary>
internal sealed class DiscordIpcClient : IAsyncDisposable
{
    private readonly IDiscordTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ackTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<SetActivityResult>> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<DisconnectInfo> _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<HandshakeResult>? _handshake;
    private Task? _reader;
    private bool _ready;
    private bool _closed;
    private int _disposed;
    private Task[]? _awaiting;

    /// <summary>Initializes a client over an unconnected transport.</summary>
    /// <param name="transport">The transport; the client takes ownership and disposes it.</param>
    /// <param name="timeProvider">Source of time for the ack timeout.</param>
    /// <param name="ackTimeout">How long to wait for READY and for each SET_ACTIVITY response (5 s by default in the publisher).</param>
    public DiscordIpcClient(IDiscordTransport transport, TimeProvider timeProvider, TimeSpan ackTimeout)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _transport = transport;
        _timeProvider = timeProvider;
        _ackTimeout = ackTimeout;
    }

    /// <summary>Gets the endpoint (pipe name) the transport connected to.</summary>
    public string? Endpoint => _transport.Endpoint;

    /// <summary>Completes when the connection ended for any reason: CLOSE, end of stream, protocol error, timeout or local disposal.</summary>
    public Task<DisconnectInfo> Disconnected => _disconnected.Task;

    /// <summary>
    /// True while a handshake or <c>SET_ACTIVITY</c> request is waiting for its answer and neither the answer nor
    /// the timeout has arrived. Lets tests tell "waiting for Discord" from "has work to do".
    /// </summary>
    internal bool IsAwaitingResponse => Volatile.Read(ref _awaiting) is { } waits && waits.All(wait => !wait.IsCompleted);

    /// <summary>Connects the transport, sends the handshake and waits for <c>READY</c>.</summary>
    /// <param name="applicationId">The Discord application (client) id.</param>
    /// <param name="cancellationToken">Cancels the attempt; the connection is torn down and the cancellation propagates.</param>
    /// <returns>Success, or the failure reason (a CLOSE/ERROR code and message surface here).</returns>
    public async ValueTask<HandshakeResult> ConnectAndHandshakeAsync(string applicationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        if (_reader is not null)
        {
            throw new InvalidOperationException("A client connects only once.");
        }

        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // Fixed text only: an exception message can carry paths or user names (security review S-L1).
            var info = new DisconnectInfo(
                null,
                _transport.Notice is { } notice ? $"Discord is not reachable ({ex.GetType().Name}). {notice}" : $"Discord is not reachable ({ex.GetType().Name}).");
            Fail(info);
            return HandshakeResult.Failure(null, info.Message);
        }

        var handshake = new TaskCompletionSource<HandshakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _handshake = handshake;
        }

        _reader = ReadLoopAsync(_lifetime.Token);
        try
        {
            return await RequestAsync(IpcFrameCodec.Encode(IpcOpcode.Handshake, IpcMessageWriter.Handshake(applicationId)), handshake, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            var info = new DisconnectInfo(null, $"Sending the handshake failed ({ex.GetType().Name}).");
            Fail(info);
            return HandshakeResult.Failure(null, info.Message);
        }
        catch (TimeoutException)
        {
            var info = new DisconnectInfo(null, $"Discord did not answer the handshake within {_ackTimeout.TotalSeconds:0.#} s.");
            Fail(info);
            await DisposeAsync().ConfigureAwait(false);
            return HandshakeResult.Failure(null, info.Message);
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends <c>SET_ACTIVITY</c> and waits for the response with the matching nonce. A <see langword="null"/>
    /// activity clears the presence. After <see cref="SetActivityOutcome.Timeout"/> the connection is closed.
    /// </summary>
    /// <param name="activity">The validated activity, or <see langword="null"/> to clear.</param>
    /// <param name="pid">The process id Discord associates with the activity.</param>
    /// <param name="cancellationToken">Stops waiting; the request may still be in flight.</param>
    /// <returns>The typed outcome.</returns>
    public async ValueTask<SetActivityResult> SetActivityAsync(ValidatedActivity? activity, int pid, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("D");
        var response = new TaskCompletionSource<SetActivityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed || !_ready)
            {
                return SetActivityResult.Disconnected("Not connected to Discord.");
            }

            _pending.Add(nonce, response);
        }

        try
        {
            return await RequestAsync(IpcFrameCodec.Encode(IpcOpcode.Frame, IpcMessageWriter.SetActivity(pid, activity, nonce)), response, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            RemovePending(nonce);
            var info = new DisconnectInfo(null, $"Sending the activity failed ({ex.GetType().Name}).");
            Fail(info);
            return SetActivityResult.Disconnected(info.Message);
        }
        catch (TimeoutException)
        {
            RemovePending(nonce);
            var info = new DisconnectInfo(null, $"Discord did not acknowledge the update within {_ackTimeout.TotalSeconds:0.#} s.");
            Fail(info);
            await DisposeAsync().ConfigureAwait(false);
            return SetActivityResult.Timeout(info.Message);
        }
        catch (OperationCanceledException)
        {
            RemovePending(nonce);
            throw;
        }
    }

    /// <summary>
    /// Writes one request frame and waits for its answer. The ack timeout starts <em>before</em> the write, so a
    /// write that never completes (a stalled pipe) is bounded by the same <see cref="_ackTimeout"/> as a missing
    /// response (QA review Q-L2). When the timeout wins, the stuck write is cancelled and observed.
    /// </summary>
    private async Task<T> RequestAsync<T>(byte[] frame, TaskCompletionSource<T> response, CancellationToken cancellationToken)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var answer = response.Task.WaitAsync(_ackTimeout, _timeProvider, guard.Token);
        var write = WriteAsync(frame, guard.Token).AsTask();
        Volatile.Write(ref _awaiting, [response.Task, answer]);
        try
        {
            if (await Task.WhenAny(write, answer).ConfigureAwait(false) == write)
            {
                await write.ConfigureAwait(false);
            }

            return await answer.ConfigureAwait(false);
        }
        finally
        {
            await guard.CancelAsync().ConfigureAwait(false);
            if (!write.IsCompleted)
            {
                _ = ObserveAbandonedWriteAsync(write);
            }
        }
    }

    private static async Task ObserveAbandonedWriteAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // hygiene: allow-swallow The request that owned this write already ended (timeout, cancel or disconnect) and reported its own outcome.
        }
    }

    /// <summary>Closes the transport and stops the reader. Safe to call repeatedly.</summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Fail(new DisconnectInfo(null, "The connection was closed locally."));
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_reader is { } reader)
        {
            await reader.ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await IpcFrameCodec.ReadAsync(_transport, cancellationToken).ConfigureAwait(false);
                if (frame is not { } received)
                {
                    Fail(new DisconnectInfo(null, "Discord closed the connection."));
                    return;
                }

                if (!await HandleAsync(received, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Fail(new DisconnectInfo(null, "The connection was closed locally."));
        }
#pragma warning disable CA1031 // Boundary of the reader task: no exception may escape unobserved. It becomes the recorded disconnect reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Fixed text only (S-L1): our own protocol errors carry just opcode and size; anything else is named by type.
            Fail(new DisconnectInfo(null, ex switch
            {
                IpcProtocolException => $"Protocol error: {ex.Message}",
                JsonException => "Protocol error: Discord sent malformed JSON.",
                _ => $"The connection failed ({ex.GetType().Name}).",
            }));
        }
    }

    /// <summary>Handles one frame; returns false when the connection ended.</summary>
    private async ValueTask<bool> HandleAsync(IpcFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Opcode)
        {
            case IpcOpcode.Frame:
                HandleFrame(IpcMessage.ParseFrame(frame.Payload));
                return !IsClosed;

            case IpcOpcode.Ping:
                await WriteAsync(IpcFrameCodec.Encode(IpcOpcode.Pong, frame.Payload.Span), cancellationToken).ConfigureAwait(false);
                return true;

            case IpcOpcode.Pong:
                return true;

            case IpcOpcode.Close:
                var close = IpcMessage.ParseClose(frame.Payload);
                Fail(new DisconnectInfo(close.Code, close.Message is { } closeText ? DiscordText.Quote(closeText) : "Discord closed the connection."));
                return false;

            default:
                throw new IpcProtocolException($"Unexpected {frame.Opcode} frame from Discord.");
        }
    }

    private void HandleFrame(IpcMessage message)
    {
        if (message.Event == "READY" && message.Command == "DISPATCH")
        {
            lock (_gate)
            {
                _ready = true;
            }

            _handshake?.TrySetResult(HandshakeResult.Ready);
            return;
        }

        if (message.Event == "ERROR")
        {
            HandleError(message);
            return;
        }

        if (message.Nonce is { } nonce && TakePending(nonce) is { } response)
        {
            response.TrySetResult(SetActivityResult.Success);
        }
    }

    private void HandleError(IpcMessage message)
    {
        if (message.Nonce is { } nonce && TakePending(nonce) is { } response)
        {
            response.TrySetResult(SetActivityResult.DiscordError(message.Code, message.Message));
            return;
        }

        var text = message.Message is { } said ? DiscordText.Quote(said) : "Discord reported an error.";
        var hasNonce = message.Nonce is not null;

        TaskCompletionSource<SetActivityResult>? onlyPending = null;
        var handshaking = false;
        lock (_gate)
        {
            handshaking = !_ready;
            if (!hasNonce && _pending.Count == 1)
            {
                var key = _pending.Keys.First();
                onlyPending = _pending[key];
                _pending.Remove(key);
            }
        }

        if (handshaking)
        {
            Fail(new DisconnectInfo(message.Code, text));
        }
        else if (onlyPending is not null)
        {
            // Requests are sent one at a time, so an error without a nonce can only belong to the one in flight.
            onlyPending.TrySetResult(SetActivityResult.DiscordError(message.Code, message.Message));
        }
        else if (hasNonce)
        {
            // A nonce that matches no pending request is a stale or foreign answer (for example for a request that
            // was cancelled). It must not be attributed to the request that is in flight (QA review Q-L3).
            return;
        }
        else
        {
            Fail(new DisconnectInfo(message.Code, $"Discord sent an error that matches no request. {text}"));
        }
    }

    private bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    private TaskCompletionSource<SetActivityResult>? TakePending(string nonce)
    {
        lock (_gate)
        {
            return _pending.Remove(nonce, out var response) ? response : null;
        }
    }

    private void RemovePending(string nonce) => _ = TakePending(nonce);

    private async ValueTask WriteAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Marks the connection ended (first reason wins) and wakes everything that waits on it.</summary>
    private void Fail(DisconnectInfo info)
    {
        List<TaskCompletionSource<SetActivityResult>> pending;
        TaskCompletionSource<HandshakeResult>? handshake;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = [.. _pending.Values];
            _pending.Clear();
            handshake = _handshake;
        }

        _disconnected.TrySetResult(info);
        handshake?.TrySetResult(HandshakeResult.Failure(info.Code, DescribeHandshakeFailure(info)));
        foreach (var response in pending)
        {
            response.TrySetResult(SetActivityResult.Disconnected(info.Message));
        }
    }

    private static string DescribeHandshakeFailure(DisconnectInfo info) =>
        info.Code is { } code
            ? $"Discord rejected the connection (code {code.ToString(CultureInfo.InvariantCulture)}). {info.Message}"
            : info.Message;
}
