using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DbdPresence.Online.SignIn;

/// <summary>What the loopback listener ended up with.</summary>
public enum CallbackKind
{
    /// <summary>A well-formed callback with a code and a state.</summary>
    Code,

    /// <summary>Discord reported an error (for example the user pressed Cancel).</summary>
    Denied,

    /// <summary>The request was not the expected callback (answered, but the wait continues).</summary>
    Invalid,

    /// <summary>Nothing arrived in time.</summary>
    TimedOut,
}

/// <summary>The outcome of waiting for the callback.</summary>
/// <param name="Kind">What arrived.</param>
/// <param name="Code">The authorization code (only for <see cref="CallbackKind.Code"/>). Never logged.</param>
/// <param name="State">The state echoed by Discord (only for <see cref="CallbackKind.Code"/>).</param>
public sealed record CallbackResult(CallbackKind Kind, string? Code, string? State);

/// <summary>A started listener, for one callback.</summary>
public interface ICallbackListener : IDisposable
{
    /// <summary>Gets the port the listener is bound to on 127.0.0.1.</summary>
    int Port { get; }

    /// <summary>Waits for the callback that carries the expected state, or for the timeout; other requests are answered and ignored.</summary>
    /// <param name="expectedState">The random state of this sign-in. A callback with any other state is answered with 400 and does not end the wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What arrived.</returns>
    Task<CallbackResult> WaitAsync(string expectedState, CancellationToken cancellationToken);
}

/// <summary>Starts callback listeners.</summary>
public interface ICallbackListenerFactory
{
    /// <summary>Binds a listener on a random free loopback port.</summary>
    /// <returns>The listener.</returns>
    ICallbackListener Start();
}

/// <summary>
/// The loopback sign-in listener (FR-ONL-01, ADR-0011): bound to <see cref="IPAddress.Loopback"/> only, random free port, path
/// <c>/callback</c>, accepts connections until one valid callback arrives (junk answered with 400/404, idle ones dropped after 5 s), 120 s timeout counted from <see cref="Start"/>. It speaks just enough HTTP for one GET.
/// </summary>
public sealed class LoopbackListenerFactory : ICallbackListenerFactory
{
    /// <summary>How long the browser has to come back.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="LoopbackListenerFactory"/> class.</summary>
    /// <param name="time">The clock for the timeout.</param>
    public LoopbackListenerFactory(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <inheritdoc />
    public ICallbackListener Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start(16);
            return new Listener(listener, ((IPEndPoint)listener.LocalEndpoint).Port, _time);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    private sealed class Listener : ICallbackListener
    {
        private const int MaxRequestBytes = 8192;
        private const int MaxConcurrentConnections = 8;
        private static readonly TimeSpan s_readTimeout = TimeSpan.FromSeconds(5);
        private const string ClosePage = "You can close this tab and return to DBD Presence.";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _deadline;
        private readonly TimeProvider _time;
        private int _open;
        private byte[] _expectedState = [];

        public Listener(TcpListener listener, int port, TimeProvider time)
        {
            _listener = listener;
            _time = time;
            Port = port;
            _deadline = new CancellationTokenSource(Timeout, time);
        }

        public int Port { get; }

        public async Task<CallbackResult> WaitAsync(string expectedState, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(expectedState);
            _expectedState = Encoding.UTF8.GetBytes(expectedState);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
            var final = new TaskCompletionSource<CallbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = AcceptLoopAsync(final, linked.Token);
            try
            {
                return await final.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new CallbackResult(CallbackKind.TimedOut, null, null);
            }
            finally
            {
                _listener.Stop();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _deadline.Dispose();
        }

        // Keeps accepting until a valid callback (or Discord's error redirect) has been seen or the window closes (TM-08).
        private async Task AcceptLoopAsync(TaskCompletionSource<CallbackResult> final, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    if (Interlocked.Increment(ref _open) > MaxConcurrentConnections)
                    {
                        Interlocked.Decrement(ref _open);
                        client.Dispose();
                        continue;
                    }

                    _ = ServeAsync(client, final, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // hygiene: allow-swallow The wait ended (deadline or caller); WaitAsync reports it.
            }
            catch (ObjectDisposedException)
            {
                // hygiene: allow-swallow The listener was stopped because the wait ended.
            }
            catch (SocketException)
            {
                // hygiene: allow-swallow The listener was stopped while accepting.
            }
        }

        private async Task ServeAsync(TcpClient client, TaskCompletionSource<CallbackResult> final, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                {
                    using var timeout = new CancellationTokenSource(s_readTimeout, _time);
                    using var perConnection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                    var stream = client.GetStream();
                    var (result, status) = await ReadRequestAsync(stream, perConnection.Token).ConfigureAwait(false);
                    await RespondAsync(stream, status, perConnection.Token).ConfigureAwait(false);
                    if (result.Kind is CallbackKind.Code or CallbackKind.Denied)
                    {
                        final.TrySetResult(result);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // hygiene: allow-swallow Slow or idle connection, or the wait ended: dropped. The sign-in carries on.
            }
            catch (IOException)
            {
                // hygiene: allow-swallow The peer closed or reset the connection: not a callback.
            }
            catch (SocketException)
            {
                // hygiene: allow-swallow The peer reset the connection: not a callback.
            }
            catch (ObjectDisposedException)
            {
                // hygiene: allow-swallow The listener was torn down while this connection was open.
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }

        private async Task<(CallbackResult Result, int Status)> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[MaxRequestBytes];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    break;
                }
            }

            return Parse(Encoding.ASCII.GetString(buffer, 0, length));
        }

        private (CallbackResult Result, int Status) Parse(string request)
        {
            var invalid = (new CallbackResult(CallbackKind.Invalid, null, null), 400);
            var lines = request.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return invalid;
            }

            // DNS-rebinding guard: the Host header must be exactly the loopback address and port we listen on.
            var host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
            if (host is null || host["Host:".Length..].Trim() != $"127.0.0.1:{Port}")
            {
                return invalid;
            }

            var target = parts[1];
            var question = target.IndexOf('?', StringComparison.Ordinal);
            var path = question < 0 ? target : target[..question];
            if (path != "/callback")
            {
                return (new CallbackResult(CallbackKind.Invalid, null, null), 404);
            }

            var query = ParseQuery(question < 0 ? string.Empty : target[(question + 1)..]);

            // Only the sign-in this app started may end the wait: the state must match (fixed-time compare). AccountManager checks it again.
            if (!query.TryGetValue("state", out var received)
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), _expectedState))
            {
                return invalid;
            }

            if (query.ContainsKey("error"))
            {
                return (new CallbackResult(CallbackKind.Denied, null, null), 200);
            }

            return query.TryGetValue("code", out var code) && query.TryGetValue("state", out var state) && code.Length is > 0 and <= 256 && state.Length is > 0 and <= 128
                ? (new CallbackResult(CallbackKind.Code, code, state), 200)
                : invalid;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
                var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
                values.TryAdd(name, value);
            }

            return values;
        }

        private static async Task RespondAsync(NetworkStream stream, int status, CancellationToken cancellationToken)
        {
            var body = status == 200 ? ClosePage : "Not found.";
            var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
            var bytes = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
                + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\n"
                + $"Connection: close\r\n\r\n{body}");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
