using DbdPresence.Core.Game;

namespace DbdPresence.Presentation.Presence;

/// <summary>When a presence state can actually be reached (FR-TPL-07).</summary>
public enum Reachability
{
    /// <summary>Reachable from process detection alone.</summary>
    Always,

    /// <summary>Needs screen reading.</summary>
    NeedsDetection,

    /// <summary>Needs screen reading and a known role.</summary>
    NeedsDetectionAndRole,
}

/// <summary>Display names and reachability for presence states.</summary>
public static class StudioText
{
    /// <summary>Returns the user-facing state name.</summary>
    public static string StateName(PresenceState state) => state switch
    {
        PresenceState.NotRunning => "Game closed",
        PresenceState.Launching => "Game starting",
        PresenceState.Running => "Playing (no screen reading)",
        PresenceState.MainMenu => "Main menu",
        PresenceState.Matchmaking => "Looking for a match",
        PresenceState.Lobby => "In a lobby",
        PresenceState.LobbySurvivor => "In a lobby as Survivor",
        PresenceState.LobbyKiller => "In a lobby as Killer",
        PresenceState.Loading => "Loading",
        PresenceState.InTrial => "In a match",
        PresenceState.InTrialSurvivor => "In a match as Survivor",
        PresenceState.InTrialKiller => "In a match as Killer",
        PresenceState.Results => "After the match",
        _ => state.ToString(),
    };

    /// <summary>Returns when the state can be reached.</summary>
    public static Reachability ReachabilityOf(PresenceState state) => state switch
    {
        PresenceState.NotRunning or PresenceState.Launching or PresenceState.Running => Reachability.Always,
        PresenceState.LobbySurvivor or PresenceState.LobbyKiller or PresenceState.InTrialSurvivor or PresenceState.InTrialKiller => Reachability.NeedsDetectionAndRole,
        _ => Reachability.NeedsDetection,
    };

    /// <summary>Returns the badge text for a reachability.</summary>
    public static string Badge(Reachability reachability) => reachability switch
    {
        Reachability.Always => "Always available",
        _ => "Needs screen reading",
    };
}
