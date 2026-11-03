using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DbdPresence.UiTests;

/// <summary>
/// A minimal stand-in for the Discord desktop app on <c>\\.\pipe\discord-ipc-0</c> (documented local RPC framing:
/// uint32 LE opcode + uint32 LE length + UTF-8 JSON). It answers the handshake with READY and acknowledges every
/// SET_ACTIVITY, recording each activity (null = cleared). Used to verify the real app's end-to-end behaviour on
/// Windows CI where no Discord is installed.
/// </summary>
internal sealed class FakeDiscordPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe = new("discord-ipc-0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<JsonElement?> _activities = [];
    private readonly Task _loop;

    public FakeDiscordPipeServer() => _loop = Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Gets the client id sent in the handshake, once connected.</summary>
    public string? ClientId { get; private set; }

    /// <summary>Gets a value indicating whether the client closed the pipe.</summary>
    public bool ClientDisconnected { get; private set; }

    /// <summary>Gets the activities received so far (null entries are clears).</summary>
    public IReadOnlyList<JsonElement?> Activities
    {
        get
        {
            lock (_gate)
            {
                return [.. _activities];
            }
        }
    }

    /// <summary>Waits (real time) until <paramref name="condition"/> holds.</summary>
    public bool WaitFor(Func<FakeDiscordPipeServer, bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(this))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // hygiene: allow-swallow the loop ends by cancellation when the test finishes.
        }

        await _pipe.DisposeAsync();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        await _pipe.WaitForConnectionAsync(cancellation);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var header = new byte[8];
                if (!await ReadExactlyOrEndAsync(header, cancellation))
                {
                    ClientDisconnected = true;
                    return;
                }

                var opcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                var body = new byte[length];
                if (!await ReadExactlyOrEndAsync(body, cancellation))
                {
                    ClientDisconnected = true;
                    return;
                }

                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (opcode == 0)
                {
                    ClientId = root.GetProperty("client_id").GetString();
                    await SendAsync("""{"cmd":"DISPATCH","data":{"v":1},"evt":"READY","nonce":null}""", cancellation);
                }
                else if (opcode == 1 && root.GetProperty("cmd").GetString() == "SET_ACTIVITY")
                {
                    var activity = root.GetProperty("args").GetProperty("activity");
                    lock (_gate)
                    {
                        _activities.Add(activity.ValueKind == JsonValueKind.Null ? null : activity.Clone());
                    }

                    var nonce = root.GetProperty("nonce").GetString();
                    await SendAsync($$"""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"{{nonce}}"}""", cancellation);
                }
                else if (opcode == 2)
                {
                    ClientDisconnected = true;
                    return;
                }
            }
        }
        catch (IOException)
        {
            ClientDisconnected = true; // hygiene: allow-swallow the client closing the pipe ends the conversation.
        }
    }

    private async Task<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken cancellation)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _pipe.ReadAsync(buffer.AsMemory(read), cancellation);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private async Task SendAsync(string json, CancellationToken cancellation)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 1);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        await _pipe.WriteAsync(frame, cancellation);
        await _pipe.FlushAsync(cancellation);
    }
}
