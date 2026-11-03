namespace DbdPresence.Discord;

/// <summary>
/// A byte-oriented duplex connection to the local Discord client. The production implementation is a named
/// pipe; tests substitute an in-memory fake. One instance serves exactly one connection attempt.
/// </summary>
public interface IDiscordTransport : IAsyncDisposable
{
    /// <summary>
    /// Gets the name of the endpoint that accepted the connection (for example <c>discord-ipc-0</c>), or
    /// <see langword="null"/> before <see cref="ConnectAsync"/> succeeded.
    /// </summary>
    string? Endpoint { get; }

    /// <summary>Gets how many endpoints the transport can try (Discord's pipes 0 to 9). Defaults to 1.</summary>
    int EndpointCount => 1;

    /// <summary>Gets the zero-based index of the endpoint that accepted the connection. Defaults to 0.</summary>
    int EndpointIndex => 0;

    /// <summary>
    /// Gets a fixed, user-presentable remark about endpoints that were skipped (for example one that another
    /// account owns), or <see langword="null"/>. It never contains data received from the endpoint.
    /// </summary>
    string? Notice => null;

    /// <summary>Connects to the first available endpoint.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="IOException">No endpoint accepted the connection (Discord is not running).</exception>
    /// <returns>A task that completes when the connection is established.</returns>
    ValueTask ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Writes one complete IPC frame with a single call.</summary>
    /// <param name="frame">The encoded frame (header and payload).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame has been handed to the operating system.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>Reads available bytes.</summary>
    /// <param name="buffer">Receives the bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; <c>0</c> means the peer closed the connection.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
