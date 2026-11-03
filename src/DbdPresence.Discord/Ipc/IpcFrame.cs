namespace DbdPresence.Discord.Ipc;

/// <summary>One decoded IPC frame.</summary>
/// <param name="Opcode">The frame opcode.</param>
/// <param name="Payload">The UTF-8 payload (JSON for most opcodes; may be empty).</param>
internal readonly record struct IpcFrame(IpcOpcode Opcode, ReadOnlyMemory<byte> Payload);
