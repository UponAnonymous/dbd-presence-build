namespace DbdPresence.Core.Presence;

/// <summary>A validated button.</summary>
/// <param name="Label">Label within Discord limits.</param>
/// <param name="Url">Absolute https URL within limits.</param>
public sealed record ActivityButton(string Label, string Url);

/// <summary>
/// An activity that passed <see cref="ActivityValidator"/>. Only the validator can create one, so the
/// publisher can never send an unchecked payload (FR-RPC-04). Equality is structural (used to skip
/// unchanged updates; FR-RPC-03).
/// </summary>
public sealed class ValidatedActivity : IEquatable<ValidatedActivity>
{
    internal ValidatedActivity(
        ActivityKind kind,
        string? details,
        string? state,
        DateTimeOffset? startTimestamp,
        string? largeImage,
        string? largeText,
        string? smallImage,
        string? smallText,
        IReadOnlyList<ActivityButton> buttons,
        StatusDisplay? statusDisplay,
        string? nameOverride)
    {
        Kind = kind;
        Details = details;
        State = state;
        StartTimestamp = startTimestamp;
        LargeImage = largeImage;
        LargeText = largeText;
        SmallImage = smallImage;
        SmallText = smallText;
        Buttons = buttons;
        StatusDisplay = statusDisplay;
        NameOverride = nameOverride;
    }

    /// <summary>Gets the activity type.</summary>
    public ActivityKind Kind { get; }

    /// <summary>Gets the details line.</summary>
    public string? Details { get; }

    /// <summary>Gets the state line.</summary>
    public string? State { get; }

    /// <summary>Gets the timer start (sent as Unix milliseconds).</summary>
    public DateTimeOffset? StartTimestamp { get; }

    /// <summary>Gets the large image key or URL.</summary>
    public string? LargeImage { get; }

    /// <summary>Gets the large image text.</summary>
    public string? LargeText { get; }

    /// <summary>Gets the small image key or URL.</summary>
    public string? SmallImage { get; }

    /// <summary>Gets the small image text.</summary>
    public string? SmallText { get; }

    /// <summary>Gets the buttons (0–2).</summary>
    public IReadOnlyList<ActivityButton> Buttons { get; }

    /// <summary>Gets the status display preference.</summary>
    public StatusDisplay? StatusDisplay { get; }

    /// <summary>Gets the name override.</summary>
    public string? NameOverride { get; }

    /// <inheritdoc />
    public bool Equals(ValidatedActivity? other) =>
        other is not null
        && Kind == other.Kind
        && Details == other.Details
        && State == other.State
        && StartTimestamp == other.StartTimestamp
        && LargeImage == other.LargeImage
        && LargeText == other.LargeText
        && SmallImage == other.SmallImage
        && SmallText == other.SmallText
        && StatusDisplay == other.StatusDisplay
        && NameOverride == other.NameOverride
        && Buttons.SequenceEqual(other.Buttons);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValidatedActivity);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Details);
        hash.Add(State);
        hash.Add(StartTimestamp);
        hash.Add(LargeImage);
        hash.Add(LargeText);
        hash.Add(SmallImage);
        hash.Add(SmallText);
        hash.Add(StatusDisplay);
        hash.Add(NameOverride);
        foreach (var button in Buttons)
        {
            hash.Add(button);
        }

        return hash.ToHashCode();
    }
}
