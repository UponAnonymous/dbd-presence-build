using System.Globalization;
using System.Text;

namespace DbdPresence.Core.Roster;

/// <summary>
/// The fold of a character name (PLAN_V2_S5B §2.2 check 4, owner decision D35): the form the app reports and the service compares.
/// It follows the OCR normaliser (<c>DbdPresence.Vision.Text.TextNormalizer.Normalize</c>): compatibility decomposition, combining
/// marks dropped, ø/ł/đ/ß folded, apostrophes removed, lower case, every other character one space. A name with a letter or digit
/// that does not fold to a–z or 0–9 has no fold. The service port (<c>service/src/roster/fold.ts</c>) is checked against the same
/// shared cases (<c>tests/fixtures/roster/fold-cases.json</c>).
/// </summary>
public static class RosterNameFold
{
    /// <summary>Shortest folded name that is reported.</summary>
    public const int MinLength = 2;

    /// <summary>Longest folded name that is reported (the service refuses longer ones).</summary>
    public const int MaxLength = 40;

    /// <summary>The fold of <paramref name="text"/>, or null when one of its letters or digits does not fold to a–z or 0–9.</summary>
    /// <param name="text">The name as read.</param>
    /// <returns>The folded name (possibly empty), or null.</returns>
    public static string? Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        // By code point, so a letter outside the Basic Multilingual Plane counts as a letter, exactly as in the service port.
        foreach (var raw in text.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            var c = Special(raw);
            if (c.Value is '\'' or '‘' or '’' or '`' or '´' or 'ʼ')
            {
                continue;
            }

            if (Rune.IsLetter(c) || Rune.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber)
            {
                var lower = Rune.ToLowerInvariant(c).Value;
                if (lower is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
                {
                    return null;
                }

                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append((char)lower);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>The fold, when it can be reported: 2 to 40 characters. Otherwise null.</summary>
    /// <param name="text">The name as read.</param>
    /// <returns>The folded name, or null.</returns>
    public static string? FoldForReport(string? text) =>
        Fold(text) is { Length: >= MinLength and <= MaxLength } folded ? folded : null;

    // Letters that do not decompose under NFKD (as TextNormalizer.Fold).
    private static Rune Special(Rune c) => c.Value switch
    {
        'ø' or 'Ø' => new Rune('o'),
        'ł' or 'Ł' => new Rune('l'),
        'đ' or 'Đ' => new Rune('d'),
        'ß' => new Rune('s'),
        _ => c,
    };
}
