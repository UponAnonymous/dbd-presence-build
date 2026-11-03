namespace DbdPresence.Discord;

/// <summary>Creates a fresh, unconnected <see cref="IDiscordTransport"/> for each connection attempt.</summary>
public interface IDiscordTransportFactory
{
    /// <summary>Creates a new transport.</summary>
    /// <returns>An unconnected transport the caller owns and must dispose.</returns>
    IDiscordTransport Create();

    /// <summary>
    /// Creates a transport that starts with endpoint <paramref name="firstEndpoint"/> and skips the earlier ones.
    /// The publisher uses it to continue with the next endpoint after one accepted but did not complete the
    /// handshake. The default ignores the index, which is right for a factory with a single endpoint.
    /// </summary>
    /// <param name="firstEndpoint">The zero-based index of the first endpoint to try.</param>
    /// <returns>An unconnected transport the caller owns and must dispose.</returns>
    IDiscordTransport Create(int firstEndpoint) => Create();
}
