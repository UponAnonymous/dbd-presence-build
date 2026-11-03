namespace DbdPresence.Core.Presence;

/// <summary>
/// Field limits applied to every outgoing activity. Where sources disagree, the stricter value is used
/// (FEASIBILITY D5, D6): lengths are checked both in UTF-16 characters and in UTF-8 bytes.
/// </summary>
public static class DiscordLimits
{
    /// <summary>Minimum length of details, state and image texts.</summary>
    public const int TextMin = 2;

    /// <summary>Maximum characters (and UTF-8 bytes) of details, state, image texts and the name override.</summary>
    public const int TextMax = 128;

    /// <summary>Maximum length of an asset key or image URL (library limit 256 is stricter than Discord's 300).</summary>
    public const int ImageMax = 256;

    /// <summary>Maximum number of buttons.</summary>
    public const int ButtonCount = 2;

    /// <summary>Maximum button label characters.</summary>
    public const int ButtonLabelMaxChars = 32;

    /// <summary>Maximum button label UTF-8 bytes (stricter widely-used library limit).</summary>
    public const int ButtonLabelMaxBytes = 31;

    /// <summary>Maximum button URL length.</summary>
    public const int ButtonUrlMax = 512;

    /// <summary>Documented Discord rate limit: updates per window.</summary>
    public const int DocumentedUpdatesPerWindow = 5;

    /// <summary>Updates the app sends per window (safety margin under the documented limit).</summary>
    public const int AppUpdatesPerWindow = 4;

    /// <summary>Rate-limit window.</summary>
    public static readonly TimeSpan UpdateWindow = TimeSpan.FromSeconds(20);
}
