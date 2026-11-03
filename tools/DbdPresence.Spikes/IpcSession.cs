using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using DbdPresence.Discord;

namespace DbdPresence.Spikes;

/// <summary>
/// Minimal Discord IPC session over the shipped named-pipe transport. The shipped client and codec are internal and
/// typed to the shipped activity model, which lacks the fields under test, so this class frames raw JSON itself
/// (8-byte header: opcode, length; both little-endian).
/// </summary>
internal sealed class IpcSession : IAsyncDisposable
{
    private const uint OpHandshake = 0;
    private const uint OpFrame = 1;
    private const uint OpClose = 2;
    private const uint OpPing = 3;
    private const uint OpPong = 4;
    private const int MaxPayload = 64 * 1024;
    private static readonly TimeSpan s_replyTimeout = TimeSpan.FromSeconds(15);

    private readonly IDiscordTransport _transport = new NamedPipeDiscordTransportFactory().Create();

    public async Task<(bool Ok, string Sent, string Response)> ConnectAsync(string appId, CancellationToken cancellationToken)
    {
        var sent = new JsonObject { ["v"] = 1, ["client_id"] = appId }.ToJsonString();
        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return (false, sent, $"Could not connect to Discord's pipe ({ex.GetType().Name}: {ex.Message}). Is the Discord desktop app running?");
        }

        await WriteAsync(OpHandshake, sent, cancellationToken).ConfigureAwait(false);
        var reply = await ReadMatchingAsync(json => json?["evt"]?.GetValue<string>() is "READY" or "ERROR", cancellationToken).ConfigureAwait(false);
        if (reply.Json?["evt"]?.GetValue<string>() == "READY")
        {
            // The READY payload carries the signed-in user's name and ID; it is deliberately not recorded.
            return (true, sent, "READY received (payload not recorded because it contains the signed-in user).");
        }

        return (false, sent, reply.Text);
    }

    /// <summary>Sends SET_ACTIVITY. A null activity clears it. Returns the exact JSON sent and Discord's full reply.</summary>
    public async Task<(string Sent, string Response)> SetActivityAsync(JsonObject? activity, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("D");
        var args = new JsonObject { ["pid"] = Environment.ProcessId };
        if (activity is not null)
        {
            args["activity"] = activity;
        }

        var sent = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = nonce }.ToJsonString();
        await WriteAsync(OpFrame, sent, cancellationToken).ConfigureAwait(false);
        var reply = await ReadMatchingAsync(json => json?["nonce"]?.GetValue<string>() == nonce, cancellationToken).ConfigureAwait(false);
        return (sent, reply.Text);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private async Task WriteAsync(uint opcode, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxPayload - 8)
        {
            throw new InvalidOperationException("Payload too large for one IPC frame.");
        }

        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        await _transport.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Text, JsonNode? Json)> ReadMatchingAsync(Func<JsonNode?, bool> match, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_replyTimeout);
        try
        {
            while (true)
            {
                var header = new byte[8];
                if (!await ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false))
                {
                    return ("Discord closed the connection without an answer.", null);
                }

                var opcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                if (length > MaxPayload)
                {
                    return ($"Discord sent an oversized frame ({length} bytes).", null);
                }

                var payload = new byte[length];
                if (length > 0 && !await ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false))
                {
                    return ("Discord closed the connection in the middle of a frame.", null);
                }

                var text = Encoding.UTF8.GetString(payload);
                switch (opcode)
                {
                    case OpPing:
                        await WriteAsync(OpPong, text, timeout.Token).ConfigureAwait(false);
                        break;
                    case OpClose:
                        return ($"Discord closed the connection: {text}", null);
                    case OpFrame:
                        var json = JsonNode.Parse(text);
                        if (match(json))
                        {
                            return (text, json);
                        }

                        break;
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ($"No answer from Discord within {s_replyTimeout.TotalSeconds:0} s.", null);
        }
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await _transport.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }
}
