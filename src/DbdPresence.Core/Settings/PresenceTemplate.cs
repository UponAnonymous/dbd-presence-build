using System.Text.Json.Serialization;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Settings;

/// <summary>Where an image comes from (FR-ART-01, FR-ART-04). Images are never built by interpolating tokens into keys.</summary>
public enum ImageSource
{
    /// <summary>No image.</summary>
    None,

    /// <summary>A fixed asset key or https URL given in <see cref="ImageSlot.Value"/>.</summary>
    Fixed,

    /// <summary>The app logo from the artwork settings.</summary>
    AppLogo,

    /// <summary>The artwork mapped to the current role (hidden when role is hidden or unknown).</summary>
    RoleArtwork,

    /// <summary>The artwork mapped to the current map (hidden when the map is hidden or unknown).</summary>
    MapArtwork,

    /// <summary>The artwork for the character the user picked (hidden when no character is set or characters are hidden).</summary>
    CharacterArtwork,

    /// <summary>
    /// The small image only (FR-AUTO-01): the role icon, switching to a hook, dying or match-result icon while the app has confirmed that state from a
    /// validated detector and the pack has the picture. Rejected on the large image.
    /// </summary>
    Automatic,
}

/// <summary>An image choice for a template field.</summary>
/// <param name="Source">Where the image comes from.</param>
/// <param name="Value">Asset key or https URL when <paramref name="Source"/> is <see cref="ImageSource.Fixed"/>.</param>
public sealed record ImageSlot(ImageSource Source, string? Value = null)
{
    /// <summary>No image.</summary>
    public static readonly ImageSlot None = new(ImageSource.None);

    /// <summary>The app logo.</summary>
    public static readonly ImageSlot AppLogo = new(ImageSource.AppLogo);

    /// <summary>Role artwork.</summary>
    public static readonly ImageSlot Role = new(ImageSource.RoleArtwork);

    /// <summary>Map artwork.</summary>
    public static readonly ImageSlot Map = new(ImageSource.MapArtwork);

    /// <summary>Character artwork.</summary>
    public static readonly ImageSlot Character = new(ImageSource.CharacterArtwork);

    /// <summary>The role icon that changes by itself to a status or result icon (small image only).</summary>
    public static readonly ImageSlot Automatic = new(ImageSource.Automatic);
}

/// <summary>Which start time drives Discord's elapsed timer.</summary>
public enum TimerSource
{
    /// <summary>No timer.</summary>
    None,

    /// <summary>Time since the game was started.</summary>
    GameSession,

    /// <summary>Time since the current trial started (needs confirmed in-trial detection).</summary>
    Match,
}

/// <summary>A button template.</summary>
/// <param name="Label">Button label.</param>
/// <param name="Url">https URL.</param>
public sealed record ButtonTemplate(string Label, string Url);

/// <summary>The editable template for one presence state (FR-TPL-01). Text fields use the template syntax.</summary>
public sealed record PresenceTemplate
{
    /// <summary>Gets the details line template. An empty details template means "fall back".</summary>
    public string Details { get; init; } = string.Empty;

    /// <summary>Gets the state line template.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>Gets the large image choice.</summary>
    public ImageSlot LargeImage { get; init; } = ImageSlot.AppLogo;

    /// <summary>Gets the large image hover text template.</summary>
    public string LargeText { get; init; } = string.Empty;

    /// <summary>Gets the small image choice.</summary>
    public ImageSlot SmallImage { get; init; } = ImageSlot.None;

    /// <summary>Gets the small image hover text template.</summary>
    public string SmallText { get; init; } = string.Empty;

    /// <summary>Gets the buttons (at most two are sent).</summary>
    public IReadOnlyList<ButtonTemplate> Buttons { get; init; } = [];

    /// <summary>Gets the timer source.</summary>
    public TimerSource Timer { get; init; } = TimerSource.GameSession;

    /// <summary>Gets a value indicating whether this template is empty (falls back to the next state). Not persisted.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Details);
}

/// <summary>A complete set of templates keyed by presence state.</summary>
public sealed record TemplateSet
{
    /// <summary>Gets the templates; missing states fall back along the chain.</summary>
    public IReadOnlyDictionary<PresenceState, PresenceTemplate> ByState { get; init; } = new Dictionary<PresenceState, PresenceTemplate>();

    /// <summary>Returns the template for a state, or null when absent or empty.</summary>
    public PresenceTemplate? Get(PresenceState state) =>
        ByState.TryGetValue(state, out var template) && !template.IsEmpty ? template : null;
}
