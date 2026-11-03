namespace DbdPresence.Discord.Ipc;

/// <summary>The peer violated the IPC framing rules; the connection must be dropped.</summary>
internal sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string message)
        : base(message)
    {
    }
}
