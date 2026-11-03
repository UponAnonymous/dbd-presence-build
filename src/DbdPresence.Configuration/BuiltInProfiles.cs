using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration;

/// <summary>Built-in profiles (FR-PROF-01). Users duplicate and edit them; built-ins are restored from here.</summary>
public static class BuiltInProfiles
{
    /// <summary>The default profile.</summary>
    public static PresenceProfile Default { get; } = new() { Id = "default", Name = "Default" };

    /// <summary>Survivor main. Since D30 the role comes from detection, so this differs from Default only by name.</summary>
    public static PresenceProfile SurvivorMain { get; } = new()
    {
        Id = "survivor-main",
        Name = "Survivor main",
    };

    /// <summary>Killer main. Since D30 the role comes from detection, so this differs from Default only by name.</summary>
    public static PresenceProfile KillerMain { get; } = new()
    {
        Id = "killer-main",
        Name = "Killer main",
    };

    /// <summary>Streamer: minimal details, no timers, no live stats.</summary>
    public static PresenceProfile Streamer { get; } = new()
    {
        Id = "streamer",
        Name = "Streamer",
        Templates = TemplatePresets.StreamerSafe,
        Visibility = new VisibilitySettings { ShowMap = false, ShowTimers = false, ShowLiveMatchStats = false },
    };

    /// <summary>Special modes: mode-dependent fields off, generic presence.</summary>
    public static PresenceProfile SpecialMode { get; } = new()
    {
        Id = "special-mode",
        Name = "Special mode",
        Templates = TemplatePresets.Minimal,
    };

    /// <summary>All built-in profiles.</summary>
    public static IReadOnlyList<PresenceProfile> All { get; } = [Default, SurvivorMain, KillerMain, Streamer, SpecialMode];
}
