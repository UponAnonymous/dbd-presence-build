using System.Globalization;
using System.Text;

namespace DbdPresence.Core.Presence;

/// <summary>Grapheme-safe length checks and truncation for Discord text fields.</summary>
public static class TextFit
{
    /// <summary>The ellipsis appended to truncated text.</summary>
    public const string Ellipsis = "…";

    /// <summary>Returns the UTF-8 byte count of <paramref name="text"/>.</summary>
    public static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>True when the text fits both the character and the byte limit.</summary>
    public static bool Fits(string text, int maxChars, int maxBytes) =>
        text.Length <= maxChars && Utf8Length(text) <= maxBytes;

    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it fits; otherwise the longest prefix of whole
    /// grapheme clusters that, followed by an ellipsis, fits both limits. Never splits a surrogate pair,
    /// emoji sequence or combining mark from its base.
    /// </summary>
    public static string Fit(string text, int maxChars, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Fits(text, maxChars, maxBytes))
        {
            return text;
        }

        var budgetChars = maxChars - Ellipsis.Length;
        var budgetBytes = maxBytes - Utf8Length(Ellipsis);
        var builder = new StringBuilder();
        var bytes = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var elementBytes = Utf8Length(element);
            if (builder.Length + element.Length > budgetChars || bytes + elementBytes > budgetBytes)
            {
                break;
            }

            builder.Append(element);
            bytes += elementBytes;
        }

        return builder.ToString().TrimEnd() + Ellipsis;
    }
}
