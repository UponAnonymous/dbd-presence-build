using System.Text;

namespace DbdPresence.Vision.Prompts;

/// <summary>
/// Shapes recognised prompt text. A power prompt is a short upper-case phrase at the bottom centre of the killer HUD, sometimes with a key glyph
/// (<c>[RMB]</c>, a single key letter, a controller button) next to it. Nothing here interprets the words; it only removes glyphs and decides
/// whether a line has the shape of a prompt at all (a short phrase of letters), so that a line of other text is never kept as a candidate.
/// </summary>
internal static class PromptText
{
    /// <summary>The fewest letters a candidate prompt has (a shorter line is not a phrase).</summary>
    public const int MinLetters = 6;

    /// <summary>The most characters a candidate prompt has (longer lines are sentences, not prompts).</summary>
    public const int MaxLength = 40;

    /// <summary>The most words a candidate prompt has.</summary>
    public const int MaxWords = 5;

    private static readonly System.Buffers.SearchValues<char> s_glyphMarks = System.Buffers.SearchValues.Create("[](){}<>‹›«»|");

    private static readonly HashSet<string> s_controllerKeys = new(StringComparer.OrdinalIgnoreCase) { "LB", "RB", "LT", "RT", "L1", "R1", "L2", "R2", "ZL", "ZR", "LMB", "RMB", "MMB" };

    /// <summary>Splits recognised text into non-empty lines.</summary>
    public static IReadOnlyList<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Removes key glyph tokens from one line and joins the remaining words with single spaces.</summary>
    public static string WithoutKeyGlyphs(string line)
    {
        var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => !IsKeyGlyph(w));
        return string.Join(' ', words);
    }

    /// <summary>
    /// The candidate form of a line: upper-case letters and single spaces, or null when the line does not have the shape of a prompt (too short,
    /// too long, too many words, or no word of four letters).
    /// </summary>
    public static string? Candidate(string line)
    {
        var text = WithoutKeyGlyphs(line);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ' ');
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = string.Join(' ', words);
        if (candidate.Length > MaxLength || words.Length is < 1 or > MaxWords || candidate.Count(char.IsLetter) < MinLetters || !words.Any(w => w.Length >= 4))
        {
            return null;
        }

        return candidate;
    }

    private static bool IsKeyGlyph(string token)
    {
        if (token.AsSpan().IndexOfAny(s_glyphMarks) >= 0 || s_controllerKeys.Contains(token))
        {
            return true;
        }

        return token.Length == 1 || token.All(c => !char.IsLetter(c));
    }
}
