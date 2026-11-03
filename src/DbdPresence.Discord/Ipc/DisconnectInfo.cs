namespace DbdPresence.Discord.Ipc;

/// <summary>Why a connection ended.</summary>
/// <param name="Code">The Discord close or error code, if Discord supplied one.</param>
/// <param name="Message">A human-readable reason.</param>
internal sealed record DisconnectInfo(int? Code, string Message);
