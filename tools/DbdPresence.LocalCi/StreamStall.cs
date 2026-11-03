namespace DbdPresence.LocalCi;

/// <summary>A stream sent no data for longer than its limit (a connection that died without closing).</summary>
public sealed class StreamStalledException : IOException
{
    public StreamStalledException(TimeSpan limit)
        : base($"No data came for {limit.TotalSeconds:0} seconds.")
    {
    }
}

/// <summary>
/// Reads with a limit on the time one read may wait (local CI round 2, QA M5). A request's overall timeout does not cover a body that starts and then goes quiet: every read there waits for ever
/// unless it has a limit of its own, so a dead connection could stop the loop or a download without any error.
/// </summary>
public static class StreamStall
{
    /// <summary>Reads like <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> but throws <see cref="StreamStalledException"/> when nothing arrives within <paramref name="limit"/>.</summary>
    public static async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, TimeSpan limit, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(time);
        using var quiet = new CancellationTokenSource(limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quiet.Token);
        try
        {
            return await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && quiet.IsCancellationRequested)
        {
            throw new StreamStalledException(limit);
        }
    }
}
