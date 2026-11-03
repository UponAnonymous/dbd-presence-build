using DbdPresence.Core.Presence;

namespace DbdPresence.Discord;

/// <summary>
/// Timing and limits of the <see cref="PresencePublisher"/>. The defaults are the values in ARCHITECTURE §5
/// and §6; they are options so tests can use the same numbers deliberately and so a change is a single place.
/// </summary>
public sealed record PresencePublisherOptions
{
    /// <summary>Gets how long to wait for READY and for each update's response (ack timeout, default 5 s).</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long an update waits for further changes before it is sent (default 1 s).</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the longest an update may be held back by continuous changes (default 5 s), so a steady stream
    /// of changes cannot starve the presence.
    /// </summary>
    public TimeSpan MaxCoalesceDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the time budget for clearing the activity on shutdown (default 500 ms).</summary>
    public TimeSpan ClearBudget { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets the first reconnect delay (default 1 s); it doubles after each failed attempt.</summary>
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets how long a session must last to count as healthy (default 30 s). A session is also healthy as soon as
    /// Discord acknowledges an update. Only a healthy session resets the reconnect backoff, so a Discord that
    /// sends READY and then closes is not retried every second (QA review Q-M1).
    /// </summary>
    public TimeSpan HealthySessionDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the reconnect delay cap (default 60 s).</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the random spread applied to each reconnect delay: delay × (1 ± this fraction), capped at <see cref="MaxBackoff"/> (default 0.2).</summary>
    public double BackoffJitter { get; init; } = 0.2;

    /// <summary>Gets the number of updates allowed per <see cref="UpdateWindow"/> (default <see cref="DiscordLimits.AppUpdatesPerWindow"/>).</summary>
    public int UpdatesPerWindow { get; init; } = DiscordLimits.AppUpdatesPerWindow;

    /// <summary>Gets the rate-limit window (default <see cref="DiscordLimits.UpdateWindow"/>).</summary>
    public TimeSpan UpdateWindow { get; init; } = DiscordLimits.UpdateWindow;

    /// <summary>Gets the process id sent with every update (default: the current process).</summary>
    public int ProcessId { get; init; } = Environment.ProcessId;
}
