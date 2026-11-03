namespace DbdPresence.Core.Game;

/// <summary>A running process that matched a configured game executable name.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="ExecutableName">Matched executable name.</param>
/// <param name="StartedUtc">Process start time, if the allow-listed query succeeded.</param>
public sealed record GameProcessInfo(int ProcessId, string ExecutableName, DateTimeOffset? StartedUtc);

/// <summary>One process poll (FR-PROC-01/02).</summary>
/// <param name="ObservedUtc">When the poll ran.</param>
/// <param name="Game">The game process, if present.</param>
/// <param name="LauncherPresent">Whether a launcher-only executable is present.</param>
public sealed record ProcessScan(DateTimeOffset ObservedUtc, GameProcessInfo? Game, bool LauncherPresent);

/// <summary>Immutable process-level game state.</summary>
public sealed record GameLifecycle
{
    /// <summary>The initial state.</summary>
    public static readonly GameLifecycle Initial = new();

    /// <summary>Gets the confirmed state.</summary>
    public GameState State { get; init; } = GameState.NotRunning;

    /// <summary>Gets the confirmed game process (when <see cref="GameState.Running"/>).</summary>
    public GameProcessInfo? Process { get; init; }

    /// <summary>Gets when the game session started (process start, or first detection if unknown).</summary>
    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>Gets a value indicating whether <see cref="StartedUtc"/> is the first-detection fallback.</summary>
    public bool StartIsApproximate { get; init; }

    /// <summary>Gets consecutive polls that observed <see cref="PendingKey"/> (debounce).</summary>
    internal int PendingCount { get; init; }

    /// <summary>Gets the (state, PID) the pending polls observed.</summary>
    internal (GameState State, int? ProcessId) PendingKey { get; init; }

    /// <summary>Gets the confirmed (state, PID) key.</summary>
    internal (GameState State, int? ProcessId) Key => (State, Process?.ProcessId);
}

/// <summary>
/// Pure transition function for the game lifecycle (ARCHITECTURE §4.1). A change needs
/// <see cref="ConfirmationPolls"/> consecutive agreeing polls, so a single missed or spurious poll never
/// flips the state.
/// </summary>
public static class GameLifecycleTracker
{
    /// <summary>Consecutive polls required to change state.</summary>
    public const int ConfirmationPolls = 2;

    /// <summary>Computes the next lifecycle from a poll.</summary>
    public static GameLifecycle Next(GameLifecycle current, ProcessScan scan)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(scan);

        // Launching is only entered from NotRunning: a launcher left behind after the game exits is ignored.
        var observedState = scan.Game is not null
            ? GameState.Running
            : scan.LauncherPresent && current.State != GameState.Running ? GameState.Launching : GameState.NotRunning;

        // The PID is part of the key, so a game restarted between polls is confirmed as a new session.
        var observed = (observedState, scan.Game?.ProcessId);
        if (observed == current.Key)
        {
            return current with { PendingCount = 0, PendingKey = default };
        }

        var pending = current.PendingCount > 0 && current.PendingKey == observed ? current.PendingCount + 1 : 1;
        if (pending < ConfirmationPolls)
        {
            return current with { PendingCount = pending, PendingKey = observed };
        }

        return observedState switch
        {
            GameState.Running => new GameLifecycle
            {
                State = GameState.Running,
                Process = scan.Game,
                StartedUtc = scan.Game!.StartedUtc ?? scan.ObservedUtc,
                StartIsApproximate = scan.Game.StartedUtc is null,
            },
            GameState.Launching => new GameLifecycle { State = GameState.Launching },
            _ => GameLifecycle.Initial,
        };
    }
}
