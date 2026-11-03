namespace DbdPresence.Discord;

/// <summary>Connection state of the Discord presence publisher.</summary>
public enum RpcStatus
{
    /// <summary>No valid Discord application id is set; the publisher is idle and makes no connection attempts.</summary>
    NotConfigured,

    /// <summary>Not connected to Discord (not running, restarting, or backing off before the next attempt).</summary>
    Disconnected,

    /// <summary>Connecting to Discord and performing the handshake.</summary>
    Connecting,

    /// <summary>Connected; presence updates are sent.</summary>
    Connected,

    /// <summary>An unexpected internal error occurred in the publisher loop. It is recorded and retried with backoff.</summary>
    Faulted,
}
