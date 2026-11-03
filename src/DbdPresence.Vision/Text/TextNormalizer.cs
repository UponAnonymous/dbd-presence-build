using System.Globalization;
using System.Text;

namespace DbdPresence.Vision.Text;

/// <summary>
/// Normalises OCR output and vocabulary names so they can be compared (ADR-0004): case, diacritics, quotes and
/// punctuation are removed and whitespace is collapsed. <see cref="Skeleton"/> additionally folds the characters OCR
/// engines confuse with each other (l/1/I/|, O/0, S/5, B/8, Z/2, and the pairs rn/m and vv/w) onto one letter. The fold is applied to
/// both sides of every comparison, so it only forgives those confusions and never changes which names are distinct
/// (a test asserts that all vocabulary skeletons are unique).
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Lower-cases, strips diacritics, drops apostrophes and turns other punctuation into single spaces. A vertical bar or exclamation
    /// mark that sits inside a word is OCR's misreading of a capital I or an l and becomes "l" ("Pa!e" -> "pale", "|ronworks" -> "lronworks");
    /// the same characters standing alone, as a border or punctuation, are dropped.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var chars = new List<char>(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            var c = Fold(raw);
            if (c is not ('\'' or '\u2018' or '\u2019' or '`' or '\u00B4' or '\u02BC'))
            {
                chars.Add(c); // Apostrophes join words ("Lery's" -> "lerys"), so they are not kept.
            }
        }

        var builder = new StringBuilder(chars.Count);
        var pendingSpace = false;
        for (var i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c is '|' or '!' && IsBarMisreading(chars, i))
            {
                c = 'l';
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static bool IsBarMisreading(List<char> chars, int index)
    {
        var before = index > 0 && char.IsLetterOrDigit(chars[index - 1]);
        var after = index + 1 < chars.Count && char.IsLetterOrDigit(chars[index + 1]);
        return chars[index] == '|' ? before || after : before && after;
    }

    /// <summary>The normalised text with OCR-confusable characters folded together.</summary>
    public static string Skeleton(string? text)
    {
        var normalized = Normalize(text);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            builder.Append(c switch
            {
                '1' or 'i' or 'l' => 'l',
                '0' => 'o',
                '5' => 's',
                '8' => 'b',
                '2' => 'z',
                _ => c,
            });
        }

        return builder.ToString().Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal);
    }

    /// <summary>Normalised Levenshtein distance in [0, 1]: edits divided by the longer length. Two empty strings are 0 apart.</summary>
    public static double NormalizedDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 0 : (double)Levenshtein(a, b) / longest;
    }

    /// <summary>The Levenshtein edit distance (insert, delete, substitute; each costs 1).</summary>
    public static int Levenshtein(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 || b.Length == 0)
        {
            return a.Length + b.Length;
        }

        // Two rows of the dynamic-programming table; short texts stay on the stack.
        Span<int> rows = b.Length + 1 <= 128 ? stackalloc int[(b.Length + 1) * 2] : new int[(b.Length + 1) * 2];
        var previous = rows[..(b.Length + 1)];
        var current = rows[(b.Length + 1)..];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var ai = a[i - 1];
            for (var j = 1; j <= b.Length; j++)
            {
                var best = previous[j - 1] + (ai == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                if (deletion < best)
                {
                    best = deletion;
                }

                if (insertion < best)
                {
                    best = insertion;
                }

                current[j] = best;
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[b.Length];
    }

    // Letters that do not decompose under NFKD.
    private static char Fold(char c) => c switch
    {
        'ø' or 'Ø' => 'o',
        'ł' or 'Ł' => 'l',
        'đ' or 'Đ' => 'd',
        'ß' => 's',
        _ => c,
    };
}
