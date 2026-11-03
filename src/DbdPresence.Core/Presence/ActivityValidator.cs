using System.Text.RegularExpressions;

namespace DbdPresence.Core.Presence;

/// <summary>The field a validation issue refers to.</summary>
public enum ActivityField
{
    /// <summary>Details line.</summary>
    Details,

    /// <summary>State line.</summary>
    State,

    /// <summary>Large image key or URL.</summary>
    LargeImage,

    /// <summary>Large image text.</summary>
    LargeText,

    /// <summary>Small image key or URL.</summary>
    SmallImage,

    /// <summary>Small image text.</summary>
    SmallText,

    /// <summary>A button.</summary>
    Button,

    /// <summary>Name override.</summary>
    NameOverride,

    /// <summary>Timer start.</summary>
    StartTimestamp,
}

/// <summary>What the validator did to a field.</summary>
public enum ValidationAction
{
    /// <summary>The field was shortened (grapheme-safe, with an ellipsis) to fit the limits.</summary>
    Truncated,

    /// <summary>The field was invalid and has been omitted from the payload.</summary>
    Omitted,
}

/// <summary>One validation finding, shown in the Studio and in diagnostics.</summary>
/// <param name="Field">The affected field.</param>
/// <param name="Action">What was done.</param>
/// <param name="Reason">Human-readable reason.</param>
public sealed record ValidationIssue(ActivityField Field, ValidationAction Action, string Reason);

/// <summary>The outcome of validating a draft.</summary>
/// <param name="Activity">The sendable activity.</param>
/// <param name="Issues">Every truncation or omission made.</param>
public sealed record ActivityValidationResult(ValidatedActivity Activity, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Enforces Discord's activity limits (FR-RPC-04, FR-RPC-09, FR-ART-01). It never sends anything; it only
/// produces <see cref="ValidatedActivity"/> instances. Over-long text is truncated grapheme-safely; text
/// below the minimum, malformed images and invalid buttons are omitted with a recorded issue.
/// </summary>
public static partial class ActivityValidator
{
    /// <summary>Validates a draft.</summary>
    public static ActivityValidationResult Validate(ActivityDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<ValidationIssue>();

        var details = Text(draft.Details, ActivityField.Details, issues);
        var state = Text(draft.State, ActivityField.State, issues);
        var largeText = Text(draft.LargeText, ActivityField.LargeText, issues);
        var smallText = Text(draft.SmallText, ActivityField.SmallText, issues);
        var nameOverride = Text(draft.NameOverride, ActivityField.NameOverride, issues);
        var largeImage = Image(draft.LargeImage, ActivityField.LargeImage, issues);
        var smallImage = Image(draft.SmallImage, ActivityField.SmallImage, issues);

        // Image texts are only meaningful with their image.
        if (largeImage is null && largeText is not null)
        {
            issues.Add(new(ActivityField.LargeText, ValidationAction.Omitted, "No large image to attach the text to."));
            largeText = null;
        }

        if (smallImage is null && smallText is not null)
        {
            issues.Add(new(ActivityField.SmallText, ValidationAction.Omitted, "No small image to attach the text to."));
            smallText = null;
        }

        var start = draft.StartTimestamp;
        if (start is { } s && s.ToUnixTimeMilliseconds() <= 0)
        {
            issues.Add(new(ActivityField.StartTimestamp, ValidationAction.Omitted, "Timer start is before 1970."));
            start = null;
        }

        var buttons = new List<ActivityButton>();
        foreach (var button in draft.Buttons)
        {
            if (buttons.Count == DiscordLimits.ButtonCount)
            {
                issues.Add(new(ActivityField.Button, ValidationAction.Omitted, $"Discord shows at most {DiscordLimits.ButtonCount} buttons."));
                break;
            }

            if (ValidButton(button, issues) is { } valid)
            {
                buttons.Add(valid);
            }
        }

        var activity = new ValidatedActivity(
            Enum.IsDefined(draft.Kind) ? draft.Kind : ActivityKind.Playing,
            details,
            state,
            start,
            largeImage,
            largeText,
            smallImage,
            smallText,
            buttons,
            draft.StatusDisplay is { } display && Enum.IsDefined(display) ? display : null,
            nameOverride);
        return new ActivityValidationResult(activity, issues);
    }

    /// <summary>True when <paramref name="value"/> is a well-formed asset key or https image URL.</summary>
    public static bool IsValidImageReference(string value) =>
        value.Length <= DiscordLimits.ImageMax && (AssetKey().IsMatch(value) || IsHttpsUrl(value, DiscordLimits.ImageMax));

    /// <summary>True when <paramref name="value"/> is an absolute https URL with a host and no whitespace.</summary>
    public static bool IsHttpsUrl(string value, int maxLength) =>
        value.Length <= maxLength
        && !value.Any(char.IsWhiteSpace)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);

    private static string? Text(string? value, ActivityField field, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = CollapseWhitespace(value);
        if (text.Length < DiscordLimits.TextMin)
        {
            issues.Add(new(field, ValidationAction.Omitted, $"Shorter than Discord's minimum of {DiscordLimits.TextMin} characters."));
            return null;
        }

        var fitted = TextFit.Fit(text, DiscordLimits.TextMax, DiscordLimits.TextMax);
        if (!ReferenceEquals(fitted, text) && fitted != text)
        {
            issues.Add(new(field, ValidationAction.Truncated, $"Longer than {DiscordLimits.TextMax} characters or bytes."));
        }

        return fitted;
    }

    private static string? Image(string? value, ActivityField field, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!IsValidImageReference(trimmed))
        {
            issues.Add(new(field, ValidationAction.Omitted, "Not a valid asset key (a–z, 0–9, _ - .) or https URL within 256 characters."));
            return null;
        }

        return trimmed;
    }

    private static ActivityButton? ValidButton(ButtonDraft button, List<ValidationIssue> issues)
    {
        var label = CollapseWhitespace(button.Label ?? string.Empty);
        if (label.Length == 0 || !TextFit.Fits(label, DiscordLimits.ButtonLabelMaxChars, DiscordLimits.ButtonLabelMaxBytes))
        {
            issues.Add(new(ActivityField.Button, ValidationAction.Omitted, $"Button label must be 1–{DiscordLimits.ButtonLabelMaxChars} characters and at most {DiscordLimits.ButtonLabelMaxBytes} bytes."));
            return null;
        }

        var url = (button.Url ?? string.Empty).Trim();
        if (!IsHttpsUrl(url, DiscordLimits.ButtonUrlMax))
        {
            issues.Add(new(ActivityField.Button, ValidationAction.Omitted, $"Button \"{label}\" needs an https URL of at most {DiscordLimits.ButtonUrlMax} characters."));
            return null;
        }

        return new ActivityButton(label, url);
    }

    private static string CollapseWhitespace(string value) => ActivityText.Clean(value);

    // Uploaded asset keys are lower-cased by Discord (FEASIBILITY D10); we require the canonical form.
    [GeneratedRegex("^[a-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetKey();
}
