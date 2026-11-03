using System.Globalization;
using System.Text;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Packs.Import;

/// <summary>
/// Suggests a role, character or map from an imported file's name (FR-PACK-07, PLAN_V2_S5B §2.7). In order:
/// <list type="number">
/// <item>the base name loses a leading <c>T_UI_</c> and a trailing <c>_Portrait</c> (any case);</item>
/// <item>a leading character code of the roster (<c>K27_</c>) gives that character (by code), unless the rest of the name is exactly another
/// character's name, in which case nothing is proposed (code and name disagree: never guessed);</item>
/// <item>otherwise the rest, folded (case, separators and diacritics removed), equal to exactly one character's name, other name, name before a
/// dash or id gives that character (by name); a leading "the" may be left out;</item>
/// <item>otherwise a character id, map or role slug contained in the name, as before; a name containing two character ids proposes nothing.</item>
/// </list>
/// </summary>
public static class MappingProposer
{
    private const string UiPrefix = "T_UI_";
    private const string PortraitSuffix = "_Portrait";
    private static readonly string[] s_dashes = [" – ", " - "];

    /// <summary>Returns a proposal that needs user confirmation, or null when the name matches nothing known.</summary>
    /// <param name="displayName">The sanitised file name.</param>
    /// <param name="roster">The characters the app knows.</param>
    public static MappingProposal? Propose(string displayName, CharacterRoster roster) => Analyse(displayName, roster).Proposal;

    /// <summary>
    /// Like <see cref="Propose"/>, and also says when nothing was proposed because the code and the name point at different characters
    /// (<see cref="ProposalAnalysis.Conflict"/>), so the review can report it instead of guessing.
    /// </summary>
    /// <param name="displayName">The sanitised file name.</param>
    /// <param name="roster">The characters the app knows.</param>
    public static ProposalAnalysis Analyse(string displayName, CharacterRoster roster)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(roster);
        var stem = StripAffixes(Path.GetFileNameWithoutExtension(displayName));
        if (stem.Length == 0)
        {
            return new ProposalAnalysis(null, null, null);
        }

        if (stem.Length == 3 && CharacterRoster.IsCode(stem.ToUpperInvariant()) && roster.FindByCode(stem.ToUpperInvariant()) is { } codeOnly)
        {
            // "K27.png", "K27_Portrait.png", "T_UI_K27_Portrait.png": the code alone names the character.
            return new ProposalAnalysis(new MappingProposal(ProposalKind.Character, codeOnly.Id, ProposalBasis.Code), null, null);
        }

        var hasCode = stem.Length > 4 && CharacterRoster.IsCode(stem[..3]) && stem[3] == '_';
        var rest = hasCode ? stem[4..] : stem;
        var byName = MatchByName(rest, roster);
        if (hasCode && roster.FindByCode(stem[..3]) is { } byCode)
        {
            return byName is not null && !ReferenceEquals(byName, byCode)
                ? new ProposalAnalysis(null, byCode, byName)
                : new ProposalAnalysis(new MappingProposal(ProposalKind.Character, byCode.Id, ProposalBasis.Code), null, null);
        }

        if (byName is not null)
        {
            return new ProposalAnalysis(new MappingProposal(ProposalKind.Character, byName.Id), null, null);
        }

        return new ProposalAnalysis(MatchBySlug(Slugify(rest), roster), null, null);
    }

    /// <summary>Lower-cases and replaces every run of non-alphanumeric characters with one hyphen.</summary>
    public static string Slugify(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        var pendingHyphen = false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(c));
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return builder.ToString();
    }

    private static string StripAffixes(string stem)
    {
        if (stem.StartsWith(UiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[UiPrefix.Length..];
        }

        if (stem.EndsWith(PortraitSuffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^PortraitSuffix.Length];
        }

        return stem;
    }

    private static RosterCharacter? MatchByName(string text, CharacterRoster roster)
    {
        var folded = Fold(text);
        if (folded.Length == 0)
        {
            return null;
        }

        RosterCharacter? found = null;
        foreach (var character in roster.Characters)
        {
            if (Spellings(character).Any(s => s == folded || (s.StartsWith("the", StringComparison.Ordinal) && s[3..] == folded)))
            {
                if (found is not null)
                {
                    return null;
                }

                found = character;
            }
        }

        return found;
    }

    private static IEnumerable<string> Spellings(RosterCharacter character)
    {
        foreach (var name in character.OtherNames.Prepend(character.Name))
        {
            yield return Fold(name);
            foreach (var dash in s_dashes)
            {
                var at = name.IndexOf(dash, StringComparison.Ordinal);
                if (at > 0)
                {
                    yield return Fold(name[..at]);
                }
            }
        }

        yield return Fold(character.Id);
    }

    private static MappingProposal? MatchBySlug(string slug, CharacterRoster roster)
    {
        if (slug.Length == 0)
        {
            return null;
        }

        var padded = "-" + slug + "-";
        var characters = roster.Characters.Where(c => Contains(padded, c.Id)).Take(2).ToList();
        if (characters.Count > 1)
        {
            return null;
        }

        if (characters.Count == 1)
        {
            return new MappingProposal(ProposalKind.Character, characters[0].Id);
        }

        // Maps are tried before roles so "survivor-hawkins" proposes the more specific map.
        var map = KnownSlugs.Maps.FirstOrDefault(m => Contains(padded, m));
        if (map is not null)
        {
            return new MappingProposal(ProposalKind.Map, map);
        }

        var role = KnownSlugs.Roles.FirstOrDefault(r => Contains(padded, r));
        return role is null ? null : new MappingProposal(ProposalKind.Role, role);
    }

    private static bool Contains(string padded, string slug) =>
        padded.Contains("-" + slug + "-", StringComparison.Ordinal)
        || (slug.StartsWith("the-", StringComparison.Ordinal) && padded.Contains("-" + slug[4..] + "-", StringComparison.Ordinal));

    /// <summary>Letters and digits only, lower case, without diacritics ("The Onryō" and "TheOnryo" both give "theonryo").</summary>
    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}

/// <summary>The result of reading a file name against the roster.</summary>
/// <param name="Proposal">The proposal, or null when there is none.</param>
/// <param name="ConflictCode">The character the file name's code points at, when the name points at another one.</param>
/// <param name="ConflictName">The character the rest of the file name points at, when the code points at another one.</param>
public sealed record ProposalAnalysis(MappingProposal? Proposal, RosterCharacter? ConflictCode, RosterCharacter? ConflictName)
{
    /// <summary>True when the code and the name disagree: nothing is proposed.</summary>
    public bool Conflict => ConflictCode is not null;
}
