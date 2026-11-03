using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Templates;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>How serious a field message is (shown with text and an icon, never colour alone).</summary>
public enum FieldSeverity
{
    /// <summary>No message.</summary>
    None,

    /// <summary>Close to a limit, or a non-blocking note.</summary>
    Warning,

    /// <summary>The field cannot be used as written.</summary>
    Error,
}

/// <summary>
/// One template text field in the Studio (UI_SPEC §3): the source text, a live counter of the rendered text
/// against Discord's limits, and syntax errors with their column.
/// </summary>
public sealed partial class TemplateFieldViewModel : ObservableObject
{
    private const int WarnAt = DiscordLimits.TextMax * 9 / 10;
    private readonly Action<string> _commit;
    private bool _loading;

    /// <summary>Initializes a new instance of the <see cref="TemplateFieldViewModel"/> class.</summary>
    /// <param name="label">The field label.</param>
    /// <param name="isRequired">True for the details line (an empty value means the template is unused).</param>
    /// <param name="commit">Called with the new source text after each user edit.</param>
    public TemplateFieldViewModel(string label, bool isRequired, Action<string> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Label = label;
        IsRequired = isRequired;
        _commit = commit;
        Counter = Count(string.Empty);
    }

    /// <summary>Gets the field label.</summary>
    public string Label { get; }

    /// <summary>Gets a value indicating whether the field is the required details line.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets or sets the template source text.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Gets the counter for the rendered preview text, for example "38 of 128". It is the stricter of characters and bytes, shown as one number (bytes are never mentioned).</summary>
    [ObservableProperty]
    public partial string Counter { get; private set; }

    /// <summary>Gets the message severity.</summary>
    [ObservableProperty]
    public partial FieldSeverity Severity { get; private set; }

    /// <summary>Gets the message, if any.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Sets the text from settings without committing it back. Skipped while it already matches.</summary>
    public void Load(string text)
    {
        if (string.Equals(Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _loading = true;
        try
        {
            Text = text;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Updates the counter and messages for the text the preview rendered from this field.</summary>
    /// <param name="rendered">The text Discord would receive, or null when the field rendered nothing.</param>
    /// <param name="wasShortened">True when the validator shortened the rendered text to fit Discord's limits.</param>
    public void Evaluate(string? rendered, bool wasShortened)
    {
        var shown = rendered ?? string.Empty;
        Counter = Count(shown);

        var parsed = TemplateParser.Parse(Text);
        if (!parsed.IsValid)
        {
            var error = parsed.Errors[0];
            Severity = FieldSeverity.Error;
            Message = string.Create(CultureInfo.InvariantCulture, $"{error.Message.TrimEnd('.')} (column {error.Column}).");
            return;
        }

        if (wasShortened)
        {
            Severity = FieldSeverity.Error;
            Message = $"Too long for Discord by {Over(shown)}: the text is shortened and ends with “…”.";
        }
        else if (shown.Length >= WarnAt || TextFit.Utf8Length(shown) >= WarnAt)
        {
            Severity = FieldSeverity.Warning;
            Message = "Almost full.";
        }
        else if (IsRequired && string.IsNullOrWhiteSpace(Text))
        {
            Severity = FieldSeverity.Warning;
            Message = "Empty, so Discord uses your next text instead.";
        }
        else if (shown.Length is > 0 and < DiscordLimits.TextMin)
        {
            Severity = FieldSeverity.Warning;
            Message = $"Discord needs at least {DiscordLimits.TextMin} characters, so this isn't shown.";
        }
        else
        {
            Severity = FieldSeverity.None;
            Message = null;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading)
        {
            _commit(value);
        }
    }

    private static string Count(string text) => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(text.Length, TextFit.Utf8Length(text))} of {DiscordLimits.TextMax}");

    private static int Over(string text) => Math.Max(1, Math.Max(text.Length, TextFit.Utf8Length(text)) - DiscordLimits.TextMax);
}
