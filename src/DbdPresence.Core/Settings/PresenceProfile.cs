using DbdPresence.Core.Game;
using DbdPresence.Core.Presence;

namespace DbdPresence.Core.Settings;

/// <summary>What to publish while the game is not running (FR-RPC-06).</summary>
public enum NotRunningBehavior
{
    /// <summary>Clear the activity (default).</summary>
    Clear,

    /// <summary>Show the NotRunning template.</summary>
    ShowTemplate,
}

/// <summary>What to publish while paused (FR-VIS-03).</summary>
public enum PauseBehavior
{
    /// <summary>Clear the activity (default).</summary>
    Clear,

    /// <summary>Show the generic Running template without match details.</summary>
    Generic,
}

/// <summary>
/// Whether the role comes from the user or from detection. Since D30 (everything is automatic) the role always comes from detection; the
/// enum stays only so that older settings files still load. It is ignored.
/// </summary>
public enum RoleSource
{
    /// <summary>Use the confirmed detected role. The only behaviour since D30.</summary>
    AutoWithManualFallback,

    /// <summary>Formerly "always use the manual role". Ignored since D30 and reset to <see cref="AutoWithManualFallback"/> when settings load.</summary>
    Manual,
}

/// <summary>Field visibility (FR-VIS-01, FR-VIS-04). Defaults follow SAFETY R-02.</summary>
public sealed record VisibilitySettings
{
    /// <summary>Gets a value indicating whether the map and realm may be shown.</summary>
    public bool ShowMap { get; init; } = true;

    /// <summary>Gets a value indicating whether the role (and role artwork) may be shown.</summary>
    public bool ShowRole { get; init; } = true;

    /// <summary>Gets a value indicating whether the character may be shown.</summary>
    public bool ShowCharacter { get; init; } = true;

    /// <summary>Gets a value indicating whether the game mode (1v4, 2v8, custom, event) may be shown.</summary>
    public bool ShowMode { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether where the user is (menu, lobby, searching, loading, in a match, results) may be shown. Off makes every moment
    /// that needs screen reading fall back to the plain "playing" text.
    /// </summary>
    public bool ShowScene { get; init; } = true;

    /// <summary>Gets a value indicating whether the match result ({result}) may be shown.</summary>
    public bool ShowResult { get; init; } = true;

    /// <summary>Gets a value indicating whether timers and {duration} may be shown.</summary>
    public bool ShowTimers { get; init; } = true;

    /// <summary>Gets a value indicating whether buttons are sent.</summary>
    public bool ShowButtons { get; init; } = true;

    /// <summary>Gets a value indicating whether the small image is sent.</summary>
    public bool ShowSmallImage { get; init; } = true;

    /// <summary>Gets a value indicating whether live match stats (generators, status) may be shown. Off by default.</summary>
    public bool ShowLiveMatchStats { get; init; }

    /// <summary>Gets a value indicating whether trial details wait until the trial ends.</summary>
    public bool PostMatchDetailsOnly { get; init; }

    /// <summary>Gets a value indicating whether approximate (joined-late) match timers may be shown.</summary>
    public bool AllowApproximateDurations { get; init; }
}

/// <summary>Artwork references (FR-ART-01..05). Values are uploaded asset keys or https URLs.</summary>
public sealed record ArtworkSettings
{
    /// <summary>Gets the app logo reference.</summary>
    public string AppLogo { get; init; } = "app_logo";

    /// <summary>Gets the Survivor artwork reference.</summary>
    public string RoleSurvivor { get; init; } = "role_survivor";

    /// <summary>Gets the Killer artwork reference.</summary>
    public string RoleKiller { get; init; } = "role_killer";

    /// <summary>Gets map artwork references keyed by map id.</summary>
    public IReadOnlyDictionary<string, string> Maps { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the fallback large image used when a mapped image is unavailable.</summary>
    public string Fallback { get; init; } = "app_logo";

    /// <summary>Gets the asset keys the user confirmed are uploaded to their Discord application (FR-ART-05).</summary>
    public IReadOnlyCollection<string> ConfirmedUploadedKeys { get; init; } = [];

    /// <summary>Gets a value indicating whether unconfirmed keys are replaced by the fallback.</summary>
    public bool ReplaceUnconfirmedKeys { get; init; }

    /// <summary>
    /// Gets the reference (asset key or https URL) for the character the user picked. Set by the pack wiring at run time
    /// from the active image packs; never read from or written to settings files (FR-PACK-06).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CharacterImage { get; init; }

    /// <summary>
    /// Gets the references (https URL of a synced pack, or a confirmed asset key) of the status and result pictures by pack slug
    /// (<see cref="Composition.AutomaticIcons"/>). Set by the pack wiring at run time from the active packs; never read from or written to settings files. A slug
    /// without an entry has no picture, and the automatic small image then shows the role icon.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> StatusImages { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets a value indicating whether the images come from Behaviour Interactive's artwork (an image pack with that origin).
    /// Set by the pack wiring at run time, never read from or written to settings files, so a profile cannot switch the
    /// labelling off (FR-RPC-12). The composer then adds <see cref="Composition.PresenceComposer.BehaviourLabel"/> to the image hover text.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBehaviourArtwork { get; init; }
}

/// <summary>
/// Values the user used to declare by hand. Since D30 (everything is automatic) nothing reads them: role, character and mode come only from
/// confirmed detection. The type stays so that older settings files and profile exports still load; settings validation resets it to empty.
/// </summary>
[Obsolete("Declared details were removed by D30; role, character and mode come only from detection. Kept so older settings files still load.")]
public sealed record ManualDetails
{
    /// <summary>Gets the declared role.</summary>
    public Role Role { get; init; } = Role.None;

    /// <summary>Gets how the role is chosen.</summary>
    public RoleSource RoleSource { get; init; } = RoleSource.AutoWithManualFallback;

    /// <summary>Gets the declared character (0–64 characters).</summary>
    public string Character { get; init; } = string.Empty;

    /// <summary>Gets the declared game mode.</summary>
    public GameMode Mode { get; init; } = GameMode.Unknown;
}

#pragma warning disable CS0618 // The profile still carries the obsolete ManualDetails so that older files round-trip.
/// <summary>A named presence profile (FR-PROF-01): everything that shapes the published activity.</summary>
public sealed record PresenceProfile
{
    /// <summary>Gets the stable id.</summary>
    public string Id { get; init; } = "default";

    /// <summary>Gets the display name.</summary>
    public string Name { get; init; } = "Default";

    /// <summary>Gets the activity type.</summary>
    public ActivityKind ActivityKind { get; init; } = ActivityKind.Playing;

    /// <summary>Gets the optional status display preference (advanced).</summary>
    public StatusDisplay? StatusDisplay { get; init; }

    /// <summary>Gets the optional name override (experimental, off when empty).</summary>
    public string NameOverride { get; init; } = string.Empty;

    /// <summary>Gets the behaviour while the game is not running.</summary>
    public NotRunningBehavior WhenNotRunning { get; init; } = NotRunningBehavior.Clear;

    /// <summary>Gets the behaviour while paused.</summary>
    public PauseBehavior WhenPaused { get; init; } = PauseBehavior.Clear;

    /// <summary>Gets the normal templates.</summary>
    public TemplateSet Templates { get; init; } = TemplatePresets.Detailed;

    /// <summary>Gets the templates used in streamer mode.</summary>
    public TemplateSet StreamerTemplates { get; init; } = TemplatePresets.StreamerSafe;

    /// <summary>Gets field visibility.</summary>
    public VisibilitySettings Visibility { get; init; } = new();

    /// <summary>Gets artwork references.</summary>
    public ArtworkSettings Artwork { get; init; } = new();

    /// <summary>Gets the values older versions let the user declare. Ignored since D30 and cleared when settings load.</summary>
    [Obsolete("Declared details were removed by D30; role, character and mode come only from detection. Kept so older settings files still load.")]
    public ManualDetails Manual { get; init; } = new();
}
#pragma warning restore CS0618
