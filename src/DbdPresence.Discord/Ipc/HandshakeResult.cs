namespace DbdPresence.Discord.Ipc;

/// <summary>Outcome of connecting and performing the IPC handshake.</summary>
/// <param name="Success"><see langword="true"/> when Discord answered with <c>READY</c>.</param>
/// <param name="Code">The Discord close or error code (for example 4000), if any.</param>
/// <param name="Message">The reason for a failure; <see langword="null"/> on success.</param>
internal readonly record struct HandshakeResult(bool Success, int? Code, string? Message)
{
    /// <summary>The handshake succeeded.</summary>
    public static HandshakeResult Ready { get; } = new(true, null, null);

    /// <summary>Creates a failed result.</summary>
    public static HandshakeResult Failure(int? code, string message) => new(false, code, message);
}
