using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Discord.Tests.Ipc;

/// <summary>A single <see cref="DiscordIpcClient"/> over a fake Discord, on a fake clock.</summary>
internal sealed class ClientFixture : IAsyncDisposable
{
    public ClientFixture()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        Server = new FakeDiscordServer(Time);
        Client = new DiscordIpcClient(Server.Create(), Time, TimeSpan.FromSeconds(5));
    }

    public FakeTimeProvider Time { get; }

    public FakeDiscordServer Server { get; }

    public DiscordIpcClient Client { get; }

    /// <summary>Connects and completes the handshake successfully.</summary>
    public async Task<HandshakeResult> ConnectAsync()
    {
        var result = await Client.ConnectAndHandshakeAsync(TestActivities.ApplicationId, CancellationToken.None).Guard();
        Assert.True(result.Success, result.Message);
        return result;
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
