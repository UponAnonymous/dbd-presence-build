namespace DbdPresence.Discord.Tests.Support;

/// <summary>
/// A one-directional in-memory byte pipe. Writes never block; a read waits for data, end of stream or
/// cancellation. Continuations run synchronously on the writer so tests stay deterministic.
/// </summary>
internal sealed class ByteChannel
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private int _offset;
    private bool _completed;
    private TaskCompletionSource? _waiter;

    /// <summary>Appends bytes for the reader.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _chunks.Enqueue(data.ToArray());
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>Ends the stream: after the buffered bytes, reads return 0.</summary>
    public void Complete()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _completed = true;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes; 0 at end of stream.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource waiter;
            lock (_gate)
            {
                if (_chunks.Count > 0)
                {
                    var chunk = _chunks.Peek();
                    var count = Math.Min(buffer.Length, chunk.Length - _offset);
                    chunk.AsMemory(_offset, count).CopyTo(buffer);
                    _offset += count;
                    if (_offset == chunk.Length)
                    {
                        _chunks.Dequeue();
                        _offset = 0;
                    }

                    return count;
                }

                if (_completed)
                {
                    return 0;
                }

                _waiter = waiter = new TaskCompletionSource();
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            await waiter.Task.ConfigureAwait(false);
        }
    }
}
