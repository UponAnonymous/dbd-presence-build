using DbdPresence.Core.Game;

namespace DbdPresence.Core.Settings;

/// <summary>Built-in template presets (FR-TPL-05; defaults from UI_SPEC §3).</summary>
public static class TemplatePresets
{
    /// <summary>Detailed: uses every confirmed value that visibility allows.</summary>
    public static TemplateSet Detailed { get; } = new()
    {
        ByState = new Dictionary<PresenceState, PresenceTemplate>
        {
            [PresenceState.NotRunning] = new() { Details = "Taking a break from the Fog", Timer = TimerSource.None },
            [PresenceState.Launching] = new() { Details = "Launching Dead by Daylight", Timer = TimerSource.None },
            [PresenceState.Running] = new()
            {
                Details = "Playing Dead by Daylight",
                State = "[As {role}][ · {character}]",
                LargeImage = ImageSlot.Role,
                LargeText = "[{role}][ · {mode}]",
            },
            [PresenceState.MainMenu] = new() { Details = "In the main menu" },
            [PresenceState.Matchmaking] = new() { Details = "Searching for a match", State = "[As {role}]", SmallImage = ImageSlot.Role },
            [PresenceState.Lobby] = new() { Details = "In a lobby" },
            [PresenceState.LobbySurvivor] = new() { Details = "In a lobby", State = "Survivor[ · {character}]", LargeImage = ImageSlot.Role },
            [PresenceState.LobbyKiller] = new() { Details = "In a lobby", State = "Killer[ · {character}]", LargeImage = ImageSlot.Role },
            [PresenceState.Loading] = new() { Details = "Entering the Fog" },
            [PresenceState.InTrial] = new() { Details = "In a trial[ · {map}]", State = "[{generators} gens left]", Timer = TimerSource.Match },
            [PresenceState.InTrialSurvivor] = new()
            {
                Details = "In a trial[ · {map}]",
                State = "[{generators} gens left]",
                LargeImage = ImageSlot.Map,
                SmallImage = ImageSlot.Automatic,
                Timer = TimerSource.Match,
            },
            [PresenceState.InTrialKiller] = new()
            {
                Details = "Hunting[ on {map}]",
                State = "[{generators} gens left]",
                LargeImage = ImageSlot.Role,
                SmallImage = ImageSlot.AppLogo,
                Timer = TimerSource.Match,
            },
            [PresenceState.Results] = new() { Details = "Viewing results", State = "[{result}]", SmallImage = ImageSlot.Automatic, Timer = TimerSource.None },
        },
    };

    /// <summary>Minimal: activity only, no match details.</summary>
    public static TemplateSet Minimal { get; } = new()
    {
        ByState = new Dictionary<PresenceState, PresenceTemplate>
        {
            [PresenceState.Launching] = new() { Details = "Launching Dead by Daylight", Timer = TimerSource.None },
            [PresenceState.Running] = new() { Details = "Playing Dead by Daylight" },
        },
    };

    /// <summary>Streamer-safe: no map, timers or live stats; role only.</summary>
    public static TemplateSet StreamerSafe { get; } = new()
    {
        ByState = new Dictionary<PresenceState, PresenceTemplate>
        {
            [PresenceState.Launching] = new() { Details = "Launching Dead by Daylight", Timer = TimerSource.None },
            [PresenceState.Running] = new() { Details = "Playing Dead by Daylight", State = "[As {role}]", Timer = TimerSource.None },
        },
    };
}
