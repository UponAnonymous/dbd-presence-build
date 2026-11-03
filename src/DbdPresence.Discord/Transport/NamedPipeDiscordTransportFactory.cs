namespace DbdPresence.Discord;

/// <summary>Creates <see cref="IDiscordTransport"/> instances that talk to the local Discord client through named pipes.</summary>
public sealed class NamedPipeDiscordTransportFactory : IDiscordTransportFactory
{
    private const string DiscordPipePrefix = "discord-ipc-";
    private const int DiscordPipeCount = 10;
    private static readonly TimeSpan s_defaultConnectTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string _pipeNamePrefix;
    private readonly int _pipeCount;
    private readonly TimeSpan _connectTimeout;

    /// <summary>Initializes a factory for Discord's well-known pipes <c>discord-ipc-0</c> to <c>discord-ipc-9</c>.</summary>
    public NamedPipeDiscordTransportFactory()
        : this(DiscordPipePrefix, DiscordPipeCount, s_defaultConnectTimeout)
    {
    }

    internal NamedPipeDiscordTransportFactory(string pipeNamePrefix, int pipeCount, TimeSpan connectTimeout)
    {
        _pipeNamePrefix = pipeNamePrefix;
        _pipeCount = pipeCount;
        _connectTimeout = connectTimeout;
    }

    /// <inheritdoc />
    public IDiscordTransport Create() => Create(0);

    /// <inheritdoc />
    public IDiscordTransport Create(int firstEndpoint) =>
        new NamedPipeDiscordTransport(_pipeNamePrefix, _pipeCount, _connectTimeout, firstEndpoint);
}
