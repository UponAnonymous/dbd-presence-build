namespace DbdPresence.Discord;

/// <summary>
/// An immutable snapshot of the Discord connection for diagnostics and the UI (FR-RPC-02, FR-RPC-08). It
/// never contains Discord user data.
/// </summary>
/// <param name="Status">The connection state.</param>
/// <param name="Endpoint">The pipe the connection uses (for example <c>discord-ipc-0</c>); <see langword="null"/> when not connected.</param>
/// <param name="LastAckUtc">When Discord last acknowledged an update, if ever.</param>
/// <param name="LastError">The most recent connection or Discord error; cleared by a new connection or a successful update.</param>
/// <param name="UpdatesInWindow">Updates sent in the rate-limit window ending when this snapshot was taken.</param>
/// <param name="NextRetryUtc">When the next connection attempt is due while backing off; otherwise <see langword="null"/>.</param>
public sealed record RpcHealth(
    RpcStatus Status,
    string? Endpoint,
    DateTimeOffset? LastAckUtc,
    string? LastError,
    int UpdatesInWindow,
    DateTimeOffset? NextRetryUtc)
{
    /// <summary>Gets how many times a <see cref="PresencePublisher.HealthChanged"/> subscriber threw. A failing subscriber never affects publishing.</summary>
    public int HandlerFailures { get; init; }

    /// <summary>Gets the exception type name of the most recent subscriber failure, or <see langword="null"/> if none occurred.</summary>
    public string? LastHandlerFailureType { get; init; }
}
