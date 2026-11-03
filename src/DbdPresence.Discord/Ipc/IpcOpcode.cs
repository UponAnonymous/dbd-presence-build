namespace DbdPresence.Discord.Ipc;

/// <summary>Frame opcodes of Discord's local IPC protocol (rpc.md, "Connections").</summary>
internal enum IpcOpcode : uint
{
    /// <summary>Client to server: protocol version and client id.</summary>
    Handshake = 0,

    /// <summary>JSON command, response or event.</summary>
    Frame = 1,

    /// <summary>Connection termination, with an optional code and message.</summary>
    Close = 2,

    /// <summary>Keep-alive request; must be answered with <see cref="Pong"/> and the same payload.</summary>
    Ping = 3,

    /// <summary>Keep-alive answer.</summary>
    Pong = 4,
}
