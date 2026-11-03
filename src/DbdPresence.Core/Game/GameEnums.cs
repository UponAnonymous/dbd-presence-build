namespace DbdPresence.Core.Game;

/// <summary>Process-level lifecycle of the game (ARCHITECTURE §4.1).</summary>
public enum GameState
{
    /// <summary>No DBD process.</summary>
    NotRunning,

    /// <summary>Only the launcher/bootstrapper is running.</summary>
    Launching,

    /// <summary>The game executable is running.</summary>
    Running,
}

/// <summary>Player role.</summary>
public enum Role
{
    /// <summary>Not known or not set.</summary>
    None,

    /// <summary>Survivor.</summary>
    Survivor,

    /// <summary>Killer.</summary>
    Killer,
}

/// <summary>User-declared game mode (FR-DET-12).</summary>
public enum GameMode
{
    /// <summary>Not declared; mode-dependent detectors stay off.</summary>
    Unknown,

    /// <summary>Standard 1v4.</summary>
    Standard,

    /// <summary>2v8.</summary>
    TwoVersusEight,

    /// <summary>Custom game played as 1v4.</summary>
    Custom,

    /// <summary>Any other mode.</summary>
    Other,
}

/// <summary>The 13 presence states that each have a template (FR-TPL-01).</summary>
public enum PresenceState
{
    /// <summary>Game not running.</summary>
    NotRunning,

    /// <summary>Launcher only.</summary>
    Launching,

    /// <summary>Game running, scene unknown or detection off.</summary>
    Running,

    /// <summary>Main menu.</summary>
    MainMenu,

    /// <summary>Searching for a match.</summary>
    Matchmaking,

    /// <summary>Lobby, role unknown.</summary>
    Lobby,

    /// <summary>Survivor lobby.</summary>
    LobbySurvivor,

    /// <summary>Killer lobby.</summary>
    LobbyKiller,

    /// <summary>Loading into a trial.</summary>
    Loading,

    /// <summary>In a trial, role unknown.</summary>
    InTrial,

    /// <summary>In a trial as Survivor.</summary>
    InTrialSurvivor,

    /// <summary>In a trial as Killer.</summary>
    InTrialKiller,

    /// <summary>Results (tally) screen.</summary>
    Results,
}

/// <summary>Fallback chains between presence states (ARCHITECTURE §5).</summary>
public static class PresenceStateChain
{
    /// <summary>Returns the state followed by its fallbacks, most specific first.</summary>
    public static IReadOnlyList<PresenceState> For(PresenceState state) => state switch
    {
        PresenceState.LobbySurvivor or PresenceState.LobbyKiller => [state, PresenceState.Lobby, PresenceState.Running],
        PresenceState.InTrialSurvivor or PresenceState.InTrialKiller => [state, PresenceState.InTrial, PresenceState.Running],
        PresenceState.NotRunning => [PresenceState.NotRunning],
        PresenceState.Running => [PresenceState.Running],
        _ => [state, PresenceState.Running],
    };
}
