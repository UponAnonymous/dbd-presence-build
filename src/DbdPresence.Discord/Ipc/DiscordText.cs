using System.Globalization;
using System.Text;

namespace DbdPresence.Discord.Ipc;

/// <summary>
/// Makes free text that Discord sent safe to show (FR-RPC-08; security review S-M2). A peer on the pipe is
/// not trusted, so its text is cleaned at the boundary, before it can reach <see cref="RpcHealth.LastError"/>
/// and the UI: control characters, format characters (which include the bidirectional overrides) and line or
/// paragraph separators are dropped, runs of whitespace collapse to one space, and the result is at most
/// <see cref="MaxLength"/> characters, ending in an ellipsis when it was cut.
/// </summary>
internal static class DiscordText
{
    /// <summary>The longest sanitised text, including a trailing ellipsis.</summary>
    public const int MaxLength = 200;

    private const char Ellipsis = '…';

    /// <summary>Cleans <paramref name="raw"/>; returns <see langword="null"/> when nothing printable remains.</summary>
    /// <param name="raw">Text received from Discord.</param>
    /// <returns>The sanitised text, or <see langword="null"/>.</returns>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var text = new StringBuilder(MaxLength + 2);
        var pendingSpace = false;
        var truncated = false;
        foreach (var rune in raw.EnumerateRunes())
        {
            if (IsWhitespace(rune))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (IsDropped(rune))
            {
                continue;
            }

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            _ = text.Append(rune.ToString());
            if (text.Length > MaxLength)
            {
                truncated = true;
                break;
            }
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (!truncated)
        {
            return text.ToString();
        }

        text.Length = MaxLength - 1;
        if (char.IsHighSurrogate(text[^1]))
        {
            text.Length--;
        }

        while (text.Length > 0 && text[^1] == ' ')
        {
            text.Length--;
        }

        return text.Append(Ellipsis).ToString();
    }

    /// <summary>Presents Discord's text as quoted speech so it cannot be mistaken for the application's own words.</summary>
    /// <param name="sanitized">Text already returned by <see cref="Sanitize"/>.</param>
    /// <returns>For example <c>Discord said: "Invalid Client ID"</c>.</returns>
    public static string Quote(string sanitized) => $"Discord said: \"{sanitized}\"";

    private static bool IsWhitespace(Rune rune) =>
        rune.Value is '\t' or '\n' or '\r' or '\v' or '\f' or 0x85
        || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;

    private static bool IsDropped(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}
