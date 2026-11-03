using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Packs.Pictures;

/// <summary>What the import review does with one file.</summary>
public enum MatchOutcome
{
    /// <summary>The file name's code (<c>K27_</c>, after <c>T_UI_</c> and before <c>_Portrait</c> are removed) names the character.</summary>
    ByCode,

    /// <summary>The code is missing but the name matches one character exactly. Shown with the suggestion chosen; the user can change it.</summary>
    ByName,

    /// <summary>The code and the name name different characters. Nothing is chosen.</summary>
    Conflict,

    /// <summary>Another file of this import already took the character, so the user decides.</summary>
    SameCharacter,

    /// <summary>The character already has three own pictures; the user may skip the file or replace one.</summary>
    NoFreeSlot,

    /// <summary>Nothing in the name matches. The user may pick a character or leave the file out.</summary>
    Unmatched,

    /// <summary>The file is not used: a map, a role icon, or a duplicate. See <see cref="PictureProposal.Reason"/>.</summary>
    NotImported,
}

/// <summary>The review's view of one file.</summary>
/// <param name="Item">The file, already made clean by the image helper.</param>
/// <param name="Outcome">What to do with it.</param>
/// <param name="Character">The character chosen (<see cref="MatchOutcome.ByCode"/>, <see cref="MatchOutcome.ByName"/>) or suggested (full, same character); else null.</param>
/// <param name="Other">For <see cref="MatchOutcome.Conflict"/>, the character the name points at (the code's is in <paramref name="Character"/>).</param>
/// <param name="Reason">The sentence for <see cref="MatchOutcome.NotImported"/> and <see cref="MatchOutcome.Conflict"/>; otherwise null.</param>
public sealed record PictureProposal(ImportItem Item, MatchOutcome Outcome, RosterCharacter? Character, RosterCharacter? Other, string? Reason);

/// <summary>
/// Proposes a character for every file of an import (FR-PACK-07, PLAN_V2_S5B §2.7). Matching is by code first, then by name, through
/// <see cref="MappingProposer"/>; a code that disagrees with the name is reported as a conflict and never guessed. The matcher assigns nothing:
/// the review applies the choices when the user presses Import.
/// </summary>
public static class PictureMatcher
{
    /// <summary>Matches the files of one import.</summary>
    /// <param name="items">The files, in archive order.</param>
    /// <param name="roster">The characters the app knows (shipped list and synced additions).</param>
    /// <param name="existing">The characters' own pictures now, by key, so full characters and repeats are found; null for none.</param>
    public static IReadOnlyList<PictureProposal> Match(
        IReadOnlyList<ImportItem> items, CharacterRoster roster, IReadOnlyDictionary<string, CharacterPictures>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(roster);
        var proposals = new List<PictureProposal>(items.Count);
        var firstWithHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (firstWithHash.TryGetValue(item.Sha256, out var original))
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.NotImported, null, null, $"Same picture as {original}."));
                continue;
            }

            firstWithHash[item.Sha256] = item.DisplayName;
            var analysis = MappingProposer.Analyse(item.DisplayName, roster);
            if (analysis.Conflict)
            {
                proposals.Add(new PictureProposal(
                    item,
                    MatchOutcome.Conflict,
                    analysis.ConflictCode,
                    analysis.ConflictName,
                    $"The code says {analysis.ConflictCode!.Name} but the name says {analysis.ConflictName!.Name}."));
                continue;
            }

            if (analysis.Proposal is not { } proposal)
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.Unmatched, null, null, null));
                continue;
            }

            if (proposal.Kind == ProposalKind.Map)
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.NotImported, null, null, "Map pictures aren't used yet."));
                continue;
            }

            if (proposal.Kind == ProposalKind.Role)
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.NotImported, null, null, "Role icons come only from the owner's packs."));
                continue;
            }

            var character = roster.Characters.First(c => string.Equals(c.Id, proposal.Slug, StringComparison.Ordinal));
            var key = PictureKeys.Of(character.Id);
            var current = existing is not null && existing.TryGetValue(key, out var pictures) ? pictures : CharacterPictures.Empty;
            if (current.Items.Any(i => i.Sha256 == item.Sha256))
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.NotImported, character, null, $"{character.Name} already has this picture."));
            }
            else if (current.Items.Count >= PictureLimits.MaxOwnPerCharacter)
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.NoFreeSlot, character, null, null));
            }
            else if (!taken.Add(character.Id))
            {
                proposals.Add(new PictureProposal(item, MatchOutcome.SameCharacter, null, null, null));
            }
            else
            {
                proposals.Add(new PictureProposal(
                    item, proposal.Basis == ProposalBasis.Code ? MatchOutcome.ByCode : MatchOutcome.ByName, character, null, null));
            }
        }

        return proposals;
    }
}

/// <summary>One picture the save will use online, for <see cref="StorageEstimator"/>.</summary>
/// <param name="Bytes">The file size.</param>
/// <param name="InUse">True when the character will use it, so it is saved first.</param>
/// <param name="UsesQuota">False for a link and for a picture that matches an owner copy: nothing is saved, so no space is used.</param>
public sealed record EstimateItem(long Bytes, bool InUse, bool UsesQuota = true);

/// <summary>What saving a set of pictures online would use (FR-PACK-17).</summary>
/// <param name="SaveBytes">The bytes that would be saved online.</param>
/// <param name="FitCount">How many pictures fit and will be saved.</param>
/// <param name="PreviewOnlyCount">How many stay in the preview only because the space runs out.</param>
/// <param name="FreeBytes">The space free before saving.</param>
/// <param name="Fits">True when everything fits.</param>
public sealed record StorageEstimate(long SaveBytes, int FitCount, int PreviewOnlyCount, long FreeBytes, bool Fits);

/// <summary>
/// Works out how much of the online storage a save uses and how many pictures would stay in the preview only. Pictures are taken in the order the
/// queue saves them (In use first, then the order added) and saving stops at the first one that does not fit, as the service does with
/// <c>409 upload_limit</c>. The sizes are the helper's output files, so a figure computed before the pictures are made smaller is an upper bound.
/// </summary>
public static class StorageEstimator
{
    /// <summary>Estimates a save.</summary>
    /// <param name="items">The pictures, in the order they were added.</param>
    /// <param name="usedBytes">The bytes already used online (from the service).</param>
    /// <param name="limitBytes">The most bytes allowed (from the service).</param>
    public static StorageEstimate Estimate(IReadOnlyList<EstimateItem> items, long usedBytes, long limitBytes)
    {
        ArgumentNullException.ThrowIfNull(items);
        var free = Math.Max(0, limitBytes - usedBytes);
        var ordered = items.Where(i => i.InUse).Concat(items.Where(i => !i.InUse));
        long saved = 0;
        var fit = 0;
        var stopped = false;
        var previewOnly = 0;
        foreach (var item in ordered)
        {
            if (!item.UsesQuota)
            {
                fit++;
                continue;
            }

            if (!stopped && saved + item.Bytes <= free)
            {
                saved += item.Bytes;
                fit++;
            }
            else
            {
                stopped = true;
                previewOnly++;
            }
        }

        return new StorageEstimate(saved, fit, previewOnly, free, previewOnly == 0);
    }
}
