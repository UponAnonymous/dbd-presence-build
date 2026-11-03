using System.Text;

namespace DbdPresence.Study;

/// <summary>
/// Makes text safe to show on the Study page and to keep in <c>study-runs.json</c>: no control characters, one line, at most
/// <see cref="MaxLength"/> characters. Failure sentences are written by this code, but a sentence must never carry text a model
/// chose or a raw exception message (local paths), so everything that is kept or shown passes through here.
/// </summary>
internal static class StudyText
{
    /// <summary>The longest failure text kept or shown.</summary>
    public const int MaxLength = 200;

    /// <summary>Cleans a line for the log; empty text for null or empty text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The cleaned text.</returns>
    public static string Line(string? text) => Plain(text) ?? string.Empty;

    /// <summary>Cleans a sentence; null for null or empty text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The cleaned text, or null.</returns>
    public static string? Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxLength + 1));
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var space = char.IsControl(c) || char.IsWhiteSpace(c);
            if (space && lastWasSpace)
            {
                continue;
            }

            builder.Append(space ? ' ' : c);
            lastWasSpace = space;
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length <= MaxLength)
        {
            return cleaned.Length == 0 ? null : cleaned;
        }

        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(cleaned[cut - 1]))
        {
            cut--;
        }

        return cleaned[..cut] + "…";
    }
}
