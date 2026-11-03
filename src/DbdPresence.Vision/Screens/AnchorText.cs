using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// Reads recognised text against short fixed phrases (<c>QUIT</c>, <c>CANCEL</c>, <c>MATCH RESULT</c>). Nothing here accepts free text: a phrase
/// either appears in the lines, within the edit tolerance after folding OCR confusions, or it does not. The result is a score in [0, 1]:
/// 1 for an exact reading, less for each edit, 0 for no reading.
/// </summary>
internal static class AnchorText
{
    /// <summary>Splits recognised text into trimmed, non-empty lines.</summary>
    public static IReadOnlyList<string> Lines(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The number of letters in the text (digits and punctuation do not count).</summary>
    public static int Letters(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(char.IsLetter);

    /// <summary>Folds a line for comparison: the OCR skeleton, with the key hint <c>ESC</c> removed (it is a key cap, not part of the label).</summary>
    public static string Key(string? text)
    {
        var skeleton = TextNormalizer.Skeleton(text);
        if (skeleton.Length == 0)
        {
            return skeleton;
        }

        return string.Join(' ', skeleton.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "esc"));
    }

    /// <summary>
    /// The best score of a whole line against a phrase. Use this for buttons and labels that stand on their own line, so that <c>PLAY</c> is
    /// not found inside <c>PLAY EVENT</c>.
    /// </summary>
    public static double ExactLine(IReadOnlyList<string>? lines, string phrase, double maxEditFraction)
    {
        if (lines is null)
        {
            return 0;
        }

        var target = Key(phrase);
        var best = 0.0;
        foreach (var line in lines)
        {
            best = Math.Max(best, Score(Key(line), target, maxEditFraction));
        }

        return best;
    }

    /// <summary>The best score of a run of words inside any line (for a tab row, where several labels share one line).</summary>
    public static double Contains(IReadOnlyList<string>? lines, string phrase, double maxEditFraction)
    {
        if (lines is null)
        {
            return 0;
        }

        var target = Key(phrase);
        var width = target.Split(' ').Length;
        var best = 0.0;
        foreach (var line in lines)
        {
            var tokens = Key(line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + width <= tokens.Length; i++)
            {
                best = Math.Max(best, Score(string.Join(' ', tokens.AsSpan(i, width).ToArray()), target, maxEditFraction));
            }
        }

        return best;
    }

    /// <summary>The score of one folded text against one folded phrase: 1 minus the edits over the phrase length, or 0 beyond the tolerance.</summary>
    public static double Score(string candidate, string target, double maxEditFraction)
    {
        if (candidate.Length == 0 || target.Length == 0)
        {
            return 0;
        }

        var allowed = (int)Math.Floor((target.Length * maxEditFraction) + 1e-9);
        var edits = TextNormalizer.Levenshtein(candidate, target);
        return edits <= allowed ? 1.0 - ((double)edits / target.Length) : 0.0;
    }

    private const string BracketsAndArrows = "<>‹›«»[](){}|";

    private static readonly HashSet<string> s_controllerKeys = new(StringComparer.Ordinal) { "LB", "RB", "LT", "RT", "L1", "R1", "L2", "R2", "ZL", "ZR" };

    /// <summary>
    /// Whether a line of the spectator bar has the shape <c>‹ key  name  key ›</c>: a name of at least <paramref name="minNameLetters"/> letters
    /// (up to four words) between two key glyphs. A key glyph is a single character (a keyboard key), a bare arrow or bracket, or a controller
    /// shoulder key; the glyphs follow the input device, so none of them is required to be a particular one. Any other text in the bar (a banner,
    /// a notice) does not have the shape.
    /// </summary>
    public static bool HasSpectatorBarShape(string? text, int minNameLetters)
    {
        foreach (var line in Lines(text))
        {
            var tokens = Words(line);
            if (tokens.Length < 3 || tokens.Length > 6 || !IsKeyGlyph(tokens[0]) || !IsKeyGlyph(tokens[^1]))
            {
                continue;
            }

            var middle = tokens[1..^1];
            if (!middle.Any(IsNameToken) || middle.Sum(Letters) < minNameLetters)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsNameToken(string token) => Letters(token) >= 1;

    private static bool IsKeyGlyph(string token)
    {
        var core = token.Trim(BracketsAndArrows.ToCharArray());
        return core.Length == 0 ? token.Length > 0 : (core.Length == 1 && char.IsLetterOrDigit(core[0])) || s_controllerKeys.Contains(core.ToUpperInvariant());
    }

    /// <summary>Splits a line into its words.</summary>
    public static string[] Words(string line) => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
