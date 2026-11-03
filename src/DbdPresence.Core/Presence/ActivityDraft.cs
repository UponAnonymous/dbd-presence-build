namespace DbdPresence.Core.Presence;

/// <summary>A button as composed from settings; validated before it can be sent.</summary>
/// <param name="Label">Button text.</param>
/// <param name="Url">Absolute https URL.</param>
public sealed record ButtonDraft(string Label, string Url);

/// <summary>
/// An activity as composed from templates and settings. It is <em>not</em> sendable: only
/// <see cref="ActivityValidator"/> turns it into a <see cref="ValidatedActivity"/>.
/// </summary>
public sealed record ActivityDraft
{
    /// <summary>Gets the activity type.</summary>
    public ActivityKind Kind { get; init; } = ActivityKind.Playing;

    /// <summary>Gets the first line under the application name.</summary>
    public string? Details { get; init; }

    /// <summary>Gets the second line.</summary>
    public string? State { get; init; }

    /// <summary>Gets the elapsed-timer start, if any.</summary>
    public DateTimeOffset? StartTimestamp { get; init; }

    /// <summary>Gets the large image: an uploaded asset key or an https URL.</summary>
    public string? LargeImage { get; init; }

    /// <summary>Gets the hover text of the large image.</summary>
    public string? LargeText { get; init; }

    /// <summary>Gets the small image: an uploaded asset key or an https URL.</summary>
    public string? SmallImage { get; init; }

    /// <summary>Gets the hover text of the small image.</summary>
    public string? SmallText { get; init; }

    /// <summary>Gets up to two buttons.</summary>
    public IReadOnlyList<ButtonDraft> Buttons { get; init; } = [];

    /// <summary>Gets the optional status display preference (omitted when null).</summary>
    public StatusDisplay? StatusDisplay { get; init; }

    /// <summary>Gets the optional display-name override (experimental; omitted when null).</summary>
    public string? NameOverride { get; init; }
}
