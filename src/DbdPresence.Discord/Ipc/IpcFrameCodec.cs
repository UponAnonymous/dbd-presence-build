using System.Buffers.Binary;

namespace DbdPresence.Discord.Ipc;

/// <summary>
/// Encodes and decodes IPC frames: <c>uint32 LE opcode</c>, <c>uint32 LE payload length</c>, then the payload
/// (FR-RPC-01). An encoded frame is one contiguous buffer so it can be written with a single call, as the
/// protocol requires.
/// </summary>
internal static class IpcFrameCodec
{
    /// <summary>Size of the frame header in bytes.</summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Largest payload accepted or produced: a 64 KiB frame including its 8-byte header, as in discord-rpc
    /// (65 528 bytes). Anything larger is a protocol error.
    /// </summary>
    public const int MaxPayloadBytes = (64 * 1024) - HeaderSize;

    /// <summary>Builds the single buffer for one frame.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The opcode is unknown.</exception>
    /// <exception cref="ArgumentException">The payload exceeds <see cref="MaxPayloadBytes"/>.</exception>
    public static byte[] Encode(IpcOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(opcode))
        {
            throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unknown IPC opcode.");
        }

        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"A frame payload is limited to {MaxPayloadBytes} bytes.", nameof(payload));
        }

        var buffer = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    /// <summary>
    /// Reads exactly one frame. Returns <see langword="null"/> when the peer closed the stream cleanly between
    /// frames. Partial reads are accumulated; the length is validated before any payload is read.
    /// </summary>
    /// <exception cref="IpcProtocolException">Unknown opcode, oversize payload, or the stream ended inside a frame.</exception>
    public static async ValueTask<IpcFrame?> ReadAsync(IDiscordTransport transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var header = new byte[HeaderSize];
        if (!await ReadExactlyAsync(transport, header, atFrameStart: true, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var rawOpcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (!Enum.IsDefined((IpcOpcode)rawOpcode))
        {
            throw new IpcProtocolException($"Unknown IPC opcode {rawOpcode}.");
        }

        if (length > MaxPayloadBytes)
        {
            throw new IpcProtocolException($"IPC frame of {length} bytes exceeds the {MaxPayloadBytes}-byte limit.");
        }

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(transport, payload, atFrameStart: false, cancellationToken).ConfigureAwait(false);
        }

        return new IpcFrame((IpcOpcode)rawOpcode, payload);
    }

    private static async ValueTask<bool> ReadExactlyAsync(
        IDiscordTransport transport, byte[] buffer, bool atFrameStart, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await transport.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (atFrameStart && filled == 0)
                {
                    return false;
                }

                throw new IpcProtocolException("The connection ended in the middle of an IPC frame.");
            }

            filled += read;
        }

        return true;
    }
}
