namespace DbdPresence.Discord.Tests.Support;

/// <summary>A read-only transport that serves a fixed byte sequence in chunks of a chosen size.</summary>
internal sealed class BufferTransport : IDiscordTransport
{
    private readonly byte[] _data;
    private readonly int _chunkSize;
    private int _position;

    public BufferTransport(byte[] data, int chunkSize = int.MaxValue)
    {
        _data = data;
        _chunkSize = chunkSize;
    }

    /// <inheritdoc />
    public string? Endpoint => "buffer";

    /// <summary>Gets the number of bytes consumed so far.</summary>
    public int Position => _position;

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The buffer transport is read-only.");

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var count = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
        _data.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
