using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.Core.Templates;

/// <summary>
/// Renders parsed templates against the currently <em>publishable</em> token values (FR-TPL-03).
/// A token only has a value when the underlying field is confirmed, fresh and visible; callers build
/// that map. A token outside an optional segment without a value makes the whole field unrenderable,
/// so the composer falls back to the next template instead of publishing a partial sentence.
/// </summary>
public static partial class TemplateRenderer
{
    /// <summary>Renders the template; returns null if it is invalid or a mandatory token has no value.</summary>
    /// <param name="template">A parsed template.</param>
    /// <param name="values">Publishable token values keyed by canonical token name.</param>
    public static string? Render(ParsedTemplate template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (!template.IsValid)
        {
            return null;
        }

        var output = new StringBuilder();
        foreach (var part in template.Parts)
        {
            switch (part)
            {
                case LiteralPart literal:
                    output.Append(literal.Text);
                    break;
                case TokenPart token:
                    if (!TryValue(values, token.Name, out var value))
                    {
                        return null;
                    }

                    output.Append(value);
                    break;
                case OptionalPart optional:
                    output.Append(RenderOptional(optional, values));
                    break;
            }
        }

        return Normalise(output.ToString());
    }

    private static string RenderOptional(OptionalPart optional, IReadOnlyDictionary<string, string> values)
    {
        var segment = new StringBuilder();
        foreach (var part in optional.Parts)
        {
            if (part is LiteralPart literal)
            {
                segment.Append(literal.Text);
            }
            else if (part is TokenPart token)
            {
                if (!TryValue(values, token.Name, out var value))
                {
                    return string.Empty;
                }

                segment.Append(value);
            }
        }

        return segment.ToString();
    }

    private static bool TryValue(IReadOnlyDictionary<string, string> values, string name, out string value)
    {
        if (values.TryGetValue(name, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    // Dropped segments can leave doubled spaces or dangling separators at the edges; tidy them.
    private static string Normalise(string text)
    {
        var collapsed = Spaces().Replace(text, " ").Trim();
        return EdgeSeparators().Replace(collapsed, string.Empty).Trim();
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[\s·•|,\-–—:]+|[\s·•|,\-–—:]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EdgeSeparators();
}
