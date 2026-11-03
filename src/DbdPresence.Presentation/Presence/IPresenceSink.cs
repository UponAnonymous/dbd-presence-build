using DbdPresence.Core.Presence;
using DbdPresence.Discord;

namespace DbdPresence.Presentation.Presence;

/// <summary>Where composed activities go: the Discord publisher in the app, a fake in tests.</summary>
public interface IPresenceSink
{
    /// <summary>Raised (on any thread) when connection health changes.</summary>
    event EventHandler<RpcHealth>? HealthChanged;

    /// <summary>Gets the latest connection health.</summary>
    RpcHealth Health { get; }

    /// <summary>Requests that Discord shows <paramref name="activity"/> (null clears). Latest wins; never blocks.</summary>
    void Publish(ValidatedActivity? activity);

    /// <summary>Sets the Discord application id (null or invalid = idle).</summary>
    void SetApplicationId(string? applicationId);
}

/// <summary>Adapts <see cref="PresencePublisher"/> to <see cref="IPresenceSink"/>.</summary>
/// <param name="publisher">The publisher.</param>
public sealed class DiscordPresenceSink(PresencePublisher publisher) : IPresenceSink
{
    /// <inheritdoc />
    public event EventHandler<RpcHealth>? HealthChanged
    {
        add => publisher.HealthChanged += value;
        remove => publisher.HealthChanged -= value;
    }

    /// <inheritdoc />
    public RpcHealth Health => publisher.Health;

    /// <inheritdoc />
    public void Publish(ValidatedActivity? activity) => publisher.Publish(activity);

    /// <inheritdoc />
    public void SetApplicationId(string? applicationId) => publisher.SetApplicationId(applicationId);
}
