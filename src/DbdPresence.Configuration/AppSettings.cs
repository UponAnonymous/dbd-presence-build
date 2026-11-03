using System.Text.Json.Serialization;
using DbdPresence.Core.Pictures;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration;

/// <summary>Theme preference (FR-APP-01).</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows app theme.</summary>
    System,

    /// <summary>Always dark.</summary>
    Dark,

    /// <summary>Always light.</summary>
    Light,
}

/// <summary>UI density (FR-APP-03).</summary>
public enum Density
{
    /// <summary>Roomy spacing: 24 DIP card padding (controls are 32 DIP tall in both densities).</summary>
    Comfortable,

    /// <summary>Compact spacing: 16 DIP card padding.</summary>
    Compact,
}

/// <summary>Which events raise tray notifications (FR-LIFE-04).</summary>
public enum NotificationLevel
{
    /// <summary>No notifications.</summary>
    Off,

    /// <summary>Only critical problems (default).</summary>
    CriticalOnly,

    /// <summary>All state changes worth knowing (connection lost/restored, detector degraded).</summary>
    All,
}

/// <summary>Discord connection settings (FR-RPC-07, FR-RPC-12, FR-RPC-13).</summary>
public sealed record DiscordSettings
{
    /// <summary>The catalog key selected on a fresh install.</summary>
    public const string DefaultApplicationKey = "dbd";

    /// <summary>Gets the text shown after "Playing" (1–128 characters); null shows the application's own name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the key of the official application chosen from the catalog (see <see cref="IApplicationCatalog"/>).</summary>
    public string SelectedApplication { get; init; } = DefaultApplicationKey;

    /// <summary>Gets a value indicating whether the user's own Discord application is used instead of the catalog one (Advanced, D11).</summary>
    public bool UseOwnApplication { get; init; }

    /// <summary>Gets the user's own Discord Application ID (public, not a secret). Empty = not entered. Used only when <see cref="UseOwnApplication"/> is true.</summary>
    public string OwnApplicationId { get; init; } = string.Empty;

    /// <summary>Gets the name of the user's own application, used only for the in-app preview when <see cref="UseOwnApplication"/> is true.</summary>
    public string ApplicationName { get; init; } = "DBD Presence";
}

/// <summary>Persistent privacy switches (FR-VIS-02..03). Quick pause is runtime-only and not stored.</summary>
public sealed record PrivacySettings
{
    /// <summary>Gets a value indicating whether streamer mode is on.</summary>
    public bool StreamerMode { get; init; }

    /// <summary>Gets a value indicating whether the whole activity is hidden.</summary>
    public bool HideFullActivity { get; init; }

    /// <summary>Gets a value indicating whether capture pauses while DBD is not focused (default on).</summary>
    public bool PauseCaptureWhenUnfocused { get; init; } = true;
}

/// <summary>Appearance (FR-APP-01..04).</summary>
public sealed record AppearanceSettings
{
    /// <summary>Gets the theme preference.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>Gets the accent colour as #RRGGBB.</summary>
    public string Accent { get; init; } = "#D4A649";

    /// <summary>Gets an optional custom surface tint as #RRGGBB (empty = theme default).</summary>
    public string SurfaceTint { get; init; } = string.Empty;

    /// <summary>Gets the density.</summary>
    public Density Density { get; init; } = Density.Comfortable;

    /// <summary>Gets the font scale percentage (90–150, step 10).</summary>
    public int FontScalePercent { get; init; } = 100;

    /// <summary>Gets the reduce-motion preference: null follows Windows.</summary>
    public bool? ReduceMotion { get; init; }
}

/// <summary>Startup, tray and process settings (FR-LIFE-*, FR-PROC-03).</summary>
public sealed record LifecycleSettings
{
    /// <summary>Gets a value indicating whether the app starts with Windows (opt-in).</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>Gets a value indicating whether the app starts minimised to the tray (opt-in).</summary>
    public bool StartMinimised { get; init; }

    /// <summary>Gets a value indicating whether closing the window hides it to the tray.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Gets the notification level.</summary>
    public NotificationLevel Notifications { get; init; } = NotificationLevel.CriticalOnly;

    /// <summary>Gets extra game executable names (e.g. other store builds), max 10.</summary>
    public IReadOnlyList<string> ExtraProcessNames { get; init; } = [];

    /// <summary>Gets a value indicating whether the user was told that closing the window keeps the app running.</summary>
    public bool CloseToTrayExplained { get; init; }

    /// <summary>Gets a value indicating whether the user dismissed the Home suggestion to turn on screen reading ("Not now"). It never turns anything on by itself.</summary>
    public bool ScreenReadingTipDismissed { get; init; }

    /// <summary>Gets the last window size and position (null = default placement).</summary>
    public WindowPlacement? Window { get; init; }

    /// <summary>
    /// Gets the id (16 lower-case hex characters, a hash of the text) of the last announcement banner the user dismissed (FR-ONL-08). A banner with a
    /// different text has a different id and shows again. Empty when none was dismissed.
    /// </summary>
    public string DismissedAnnouncement { get; init; } = string.Empty;
}

/// <summary>Main window bounds in device-independent pixels (UI_SPEC §2). Checked against the monitors on restore.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Width">Width (at least 880).</param>
/// <param name="Height">Height (at least 600).</param>
/// <param name="Maximized">True when the window was maximised.</param>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>Minimum window width (FR-APP-04).</summary>
    public const double MinWidth = 880;

    /// <summary>Minimum window height (FR-APP-04).</summary>
    public const double MinHeight = 600;
}

/// <summary>Screen-detection settings (FR-CAP-*). Detection is off by default (NFR-PRIV-01).</summary>
public sealed record DetectionSettings
{
    /// <summary>Gets a value indicating whether screen detection is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets a value indicating whether the user acknowledged the anti-cheat/privacy notice.</summary>
    public bool RiskAcknowledged { get; init; }

    /// <summary>Gets the menu scan interval in milliseconds (1000–2000).</summary>
    public int MenuScanIntervalMs { get; init; } = 1000;

    /// <summary>Gets the in-trial scan interval in milliseconds (500–1000).</summary>
    public int TrialScanIntervalMs { get; init; } = 1000;

    /// <summary>Gets a value indicating whether low-impact mode halves sampling.</summary>
    public bool LowImpact { get; init; }

    /// <summary>Gets the OCR locale.</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>Gets a value indicating whether the game version is used to validate detector contexts.</summary>
    public bool UseGameVersion { get; init; } = true;
}

/// <summary>
/// Image pack choices (FR-PACK-06, PLAN_V2 §2.3). Pack ids are slugs; the empty string means no pack is chosen for the category (nothing is bundled, D16). The image files of
/// imported packs and overrides stay on this PC under the app data folder and are never uploaded.
/// </summary>
public sealed record PackSettings
{
    /// <summary>The value meaning "no pack chosen" for a category: the owner fallback pack, if any, then no image.</summary>
    public const string NoPack = "";

    /// <summary>Gets the pack used for the Killer role icon.</summary>
    public string KillerPack { get; init; } = NoPack;

    /// <summary>Gets the pack used for the Survivor role icon.</summary>
    public string SurvivorPack { get; init; } = NoPack;

    /// <summary>Gets the pack used for character images.</summary>
    public string CharactersPack { get; init; } = NoPack;

    /// <summary>Gets the pack used for map images.</summary>
    public string MapsPack { get; init; } = NoPack;

    /// <summary>
    /// Gets the per-image overrides: mapping key such as <c>role:killer</c> to the file name (<c>&lt;sha256&gt;.png</c>) of the
    /// normalised copy under <c>packs/overrides/</c>. Overrides are shown only in the preview; Discord gets the synced pack image, or none.
    /// </summary>
    public IReadOnlyDictionary<string, string> Overrides { get; init; } = new Dictionary<string, string>();
}

/// <summary>What the settings migration to v3 could not carry over, told to the user once (PLAN_V2_S5B §2.6).</summary>
public sealed record PictureMigrationNotice
{
    /// <summary>Gets the generic keys (role icons, generic tiles, map) whose old overrides were not migrated: only owner packs fill them now.</summary>
    public IReadOnlyList<string> GenericOverrides { get; init; } = [];

    /// <summary>Gets how many pictures of imported packs were over the limit of three per character and were left out.</summary>
    public int OverLimit { get; init; }

    /// <summary>Gets a value indicating whether there is anything to tell.</summary>
    [JsonIgnore]
    public bool HasContent => GenericOverrides.Count > 0 || OverLimit > 0;
}

/// <summary>
/// Settings of the owner-only Study page (PLAN_AUTO_LABELLING, FR-LAB-03). The page exists only for the owner's own account, so for everyone else this is never read.
/// A file without the member (written before it existed) reads as the default, so no schema version change is needed.
/// </summary>
public sealed record StudySettings
{
    /// <summary>Gets a value indicating whether new recordings are labelled automatically after the game closes. On by default; it matters only once the page is shown.</summary>
    public bool LabelAfterPlay { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the owner confirmed, once, that the review pictures are sent through the owner's own service to the owner's labelling account (they can show
    /// other players' names). Off by default: nothing is uploaded, automatically or by hand, until the owner has confirmed on the Study page (ADR-0018, SAFETY_AND_PRIVACY section 3.3).
    /// </summary>
    public bool LabellingConfirmed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the owner confirmed, once, what recording from the Study page saves (pictures of the game window on this PC, which can show other players'
    /// names; nothing is uploaded). Off by default: the Start recording button does nothing until the owner has confirmed (FR-LAB-06, SAFETY_AND_PRIVACY section 3).
    /// </summary>
    public bool RecordingConfirmed { get; init; }
}

/// <summary>The persisted settings document (ADR-0007). <see cref="SchemaVersion"/> drives migrations.</summary>
public sealed record AppSettings
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Gets the schema version of this document.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets a value indicating whether first-run setup was completed.</summary>
    public bool FirstRunCompleted { get; init; }

    /// <summary>Gets Discord settings.</summary>
    public DiscordSettings Discord { get; init; } = new();

    /// <summary>Gets the active profile id.</summary>
    public string ActiveProfileId { get; init; } = "default";

    /// <summary>Gets the presence profiles (at least one).</summary>
    public IReadOnlyList<PresenceProfile> Profiles { get; init; } = [BuiltInProfiles.Default];

    /// <summary>Gets persistent privacy settings.</summary>
    public PrivacySettings Privacy { get; init; } = new();

    /// <summary>Gets appearance settings.</summary>
    public AppearanceSettings Appearance { get; init; } = new();

    /// <summary>Gets lifecycle settings.</summary>
    public LifecycleSettings Lifecycle { get; init; } = new();

    /// <summary>Gets detection settings.</summary>
    public DetectionSettings Detection { get; init; } = new();

    /// <summary>Gets the image pack choices.</summary>
    public PackSettings Packs { get; init; } = new();

    /// <summary>Gets the owner-only Study page settings.</summary>
    public StudySettings Study { get; init; } = new();

    /// <summary>
    /// Gets the own pictures per mapping key <c>character:&lt;id&gt;</c> (schema v3, PLAN_V2_S5B §2.6): at most three each and the one in use. The
    /// default portrait is not stored here; it comes from the default pack.
    /// </summary>
    public IReadOnlyDictionary<string, CharacterPictures> Pictures { get; init; } = new Dictionary<string, CharacterPictures>();

    /// <summary>Gets the longest side of pictures saved from now on: 512 (default) or 1024.</summary>
    public int PictureSize { get; init; } = PictureLimits.DefaultSize;

    /// <summary>Gets whether the user accepted the picture rules. Declined keeps own pictures in the preview only.</summary>
    public PictureRules PictureRules { get; init; } = PictureRules.Unset;

    /// <summary>Gets the one-time notice about what the v2 to v3 migration left out, or null when there is nothing to tell (or it was dismissed).</summary>
    public PictureMigrationNotice? PictureNotice { get; init; }

    /// <summary>Gets the active profile (falls back to the first profile). Computed; not persisted.</summary>
    [JsonIgnore]
    public PresenceProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];
}
