using System.Globalization;
using System.Text;

namespace DbdPresence.Core.Presence;

/// <summary>
/// Cleans free text before it can reach Discord (FR-RPC-04, FR-RPC-08): control, format (including bidirectional
/// overrides) and line or paragraph separators are dropped, whitespace runs collapse to one space, and the ends are trimmed.
/// </summary>
public static class ActivityText
{
    /// <summary>Cleans <paramref name="value"/>; returns an empty string when nothing printable remains.</summary>
    /// <param name="value">The raw text.</param>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\t' or '\n' or '\r' or '\v' or '\f' or 0x85 || category == UnicodeCategory.SpaceSeparator)
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            text.Append(rune.ToString());
        }

        return text.ToString();
    }
}
