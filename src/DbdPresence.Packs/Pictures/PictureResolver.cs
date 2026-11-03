using DbdPresence.Core.Game;
using DbdPresence.Core.Pictures;

namespace DbdPresence.Packs.Pictures;

/// <summary>One pack the resolver may take pictures from.</summary>
/// <param name="Id">The pack id.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="IsDefault">True for the owner's default pack (D31): the base layer of character pictures.</param>
/// <param name="IsFallback">True for the owner's fallback pack (D16).</param>
/// <param name="BehaviourArt">True when the signed index flags the pack as Behaviour artwork (FR-RPC-12).</param>
/// <param name="IsImported">True for a pack the user imported: its pictures are shown on this PC only and never reach Discord.</param>
public sealed record PicturePack(string Id, string Name, bool IsDefault, bool IsFallback, bool BehaviourArt, bool IsImported);

/// <summary>
/// A picture of a pack that is usable now: not withdrawn and present in the cache. The adapter leaves out withdrawn and unsynced entries
/// (FR-PACK-13), so the resolver never has to ask.
/// </summary>
/// <param name="PackId">The pack it belongs to.</param>
/// <param name="EntryId">The entry id.</param>
/// <param name="Key">The mapping key (<c>character:the-trapper</c>, <c>character:generic-killer</c>, <c>role:killer</c>).</param>
/// <param name="Sha256">The file's SHA-256.</param>
/// <param name="SourceSha256">The SHA-256 of the file before the owner's clean-up, or null.</param>
/// <param name="Url">The https address on the owner's CDN, or null for an imported pack.</param>
/// <param name="LocalPath">The cached file the preview shows, or null when none is available.</param>
public sealed record PicturePiece(string PackId, string EntryId, string Key, string Sha256, string? SourceSha256, string? Url, string? LocalPath);

/// <summary>Why an own picture is not on Discord now (PLAN_V2_S5B §6.2). The first one that applies wins.</summary>
public enum PictureOnlineStatus
{
    /// <summary>Saved online: Discord can show it.</summary>
    Saved,

    /// <summary>Not saved yet: queued or being saved.</summary>
    Saving,

    /// <summary>Signed out.</summary>
    SignedOut,

    /// <summary>The picture rules are not accepted.</summary>
    RulesNotAccepted,

    /// <summary>The owner blocked online pictures for this account.</summary>
    Blocked,

    /// <summary>Saving online is paused for everyone.</summary>
    Paused,

    /// <summary>The 60 MB would be passed.</summary>
    StorageFull,

    /// <summary>The service cannot be reached.</summary>
    ServiceUnreachable,

    /// <summary>The last attempt to save it failed.</summary>
    Failed,

    /// <summary>Today's saving limit is reached.</summary>
    DailyLimit,

    /// <summary>The picture's file is missing from this PC, or its link is no longer acceptable.</summary>
    Unusable,
}

/// <summary>The online state of one own picture.</summary>
/// <param name="Status">The state.</param>
/// <param name="Url">The unlisted https address when <paramref name="Status"/> is <see cref="PictureOnlineStatus.Saved"/>.</param>
public sealed record PictureOnline(PictureOnlineStatus Status, string? Url = null)
{
    /// <summary>
    /// The state when only the account facts are known: saved when the service lists the picture, otherwise by the first rule that fails
    /// (signed out, rules not accepted, blocked), otherwise still saving.
    /// </summary>
    public static PictureOnline Basic(PictureItem item, IReadOnlyDictionary<string, string> savedUrlBySha256, bool signedIn, PictureRules rules, bool blocked)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(savedUrlBySha256);
        if (savedUrlBySha256.TryGetValue(item.Sha256, out var url))
        {
            return new PictureOnline(PictureOnlineStatus.Saved, url);
        }

        if (!signedIn)
        {
            return new PictureOnline(PictureOnlineStatus.SignedOut);
        }

        if (rules != PictureRules.Accepted)
        {
            return new PictureOnline(PictureOnlineStatus.RulesNotAccepted);
        }

        return new PictureOnline(blocked ? PictureOnlineStatus.Blocked : PictureOnlineStatus.Saving);
    }
}

/// <summary>Everything the resolver needs. No I/O happens in the resolver (FR-PACK-13).</summary>
/// <param name="Packs">The packs, owner and imported.</param>
/// <param name="Pieces">The usable pictures of those packs.</param>
/// <param name="CharactersPackId">The pack the user chose for characters, or null.</param>
/// <param name="LocalPathOf">Where an own picture's file is on this PC, or null when the file is missing. Never called for a link.</param>
/// <param name="OnlineOf">The online state of an own picture.</param>
/// <param name="Rules">
/// Whether the picture rules were accepted. <see cref="PictureRules.Declined"/> keeps every own picture of the user preview-only (PLAN_V2_S5B §2.6):
/// Discord then gets nothing of theirs, not a link and not an owner copy.
/// </param>
public sealed record PictureContext(
    IReadOnlyList<PicturePack> Packs,
    IReadOnlyList<PicturePiece> Pieces,
    string? CharactersPackId,
    Func<PictureItem, string?> LocalPathOf,
    Func<PictureItem, PictureOnline> OnlineOf,
    PictureRules Rules = PictureRules.Unset);

/// <summary>Where the preview's picture comes from.</summary>
public enum PictureSource
{
    /// <summary>No picture.</summary>
    None,

    /// <summary>The user's own file (from the PC or an import).</summary>
    OwnFile,

    /// <summary>The user's own link; shown as a labelled tile, never fetched.</summary>
    OwnLink,

    /// <summary>The own picture's file is missing from this PC.</summary>
    Missing,

    /// <summary>An owner pack picture the user chose for this character.</summary>
    ChosenOwnerPack,

    /// <summary>A picture of the pack chosen for characters.</summary>
    CharactersPack,

    /// <summary>The default portrait of the owner's default pack.</summary>
    DefaultPack,

    /// <summary>A picture of the owner's fallback pack.</summary>
    FallbackPack,

    /// <summary>The generic killer or survivor tile of a pack.</summary>
    GenericTile,

    /// <summary>The role icon.</summary>
    RoleIcon,
}

/// <summary>Where Discord's picture comes from.</summary>
public enum DiscordSource
{
    /// <summary>Discord gets no character picture (the status keeps its role icon and app logo).</summary>
    None,

    /// <summary>An owner pack picture the user chose.</summary>
    ChosenOwnerPack,

    /// <summary>An owner copy of the user's own picture (same hash).</summary>
    OwnerCopy,

    /// <summary>The user's link.</summary>
    Link,

    /// <summary>The user's picture, saved online.</summary>
    SavedOnline,

    /// <summary>The pack chosen for characters.</summary>
    CharactersPack,

    /// <summary>The default portrait.</summary>
    DefaultPack,

    /// <summary>The fallback pack.</summary>
    FallbackPack,

    /// <summary>A generic killer or survivor tile.</summary>
    GenericTile,
}

/// <summary>What the preview shows for a character.</summary>
/// <param name="Source">Where it comes from.</param>
/// <param name="LocalPath">The file to show, or null (a link, a missing file, no picture).</param>
/// <param name="Link">The link for <see cref="PictureSource.OwnLink"/>.</param>
/// <param name="PackName">The pack it comes from, when it comes from a pack.</param>
/// <param name="PackId">The pack id.</param>
/// <param name="EntryId">The entry id.</param>
/// <param name="LocalOnly">True when the picture is from an imported pack: Discord never gets it.</param>
public sealed record PreviewPicture(PictureSource Source, string? LocalPath, string? Link, string? PackName, string? PackId, string? EntryId, bool LocalOnly = false);

/// <summary>What Discord is given for a character.</summary>
/// <param name="Reference">An https address, or null for no character picture.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="BehaviourArt">True when the source pack is Behaviour artwork, so the hover text carries the label.</param>
/// <param name="PackName">The pack it comes from, when it comes from a pack.</param>
/// <param name="WhyNotChosen">When the picture in use is the user's own and Discord shows something else, why; otherwise <see langword="null"/>.</param>
public sealed record DiscordPicture(string? Reference, DiscordSource Source, bool BehaviourArt, string? PackName, PictureOnlineStatus? WhyNotChosen);

/// <summary>The preview and Discord pictures of one character.</summary>
/// <param name="Preview">What the user sees here.</param>
/// <param name="Discord">What Discord is given.</param>
/// <param name="SourcesDiffer">True when the two sides come from different pictures even though no reason applies (a missing file with an owner copy, an entry with no cached file).</param>
public sealed record PictureResolution(PreviewPicture Preview, DiscordPicture Discord, bool SourcesDiffer = false)
{
    /// <summary>True when Discord shows something other than the preview (the page then shows both and says why).</summary>
    public bool DiffersFromPreview => SourcesDiffer || Discord.WhyNotChosen is not null || Preview.LocalOnly;
}

/// <summary>
/// Chooses the picture of a character for the preview and for Discord (FR-PACK-11, FR-PACK-13, PLAN_V2_S5B §2.6). One class for both sides.
/// <para>Preview, in order: the picture in use (own, or an owner entry) → the pack chosen for characters → the default pack → the fallback pack →
/// the generic tile → the role icon → none.</para>
/// <para>Discord, in order: an owner entry in use → (own picture in use) an owner copy with the same hash, the link, the copy saved online → the
/// pack chosen for characters (owner packs only; imported packs never reach Discord) → the default pack → the fallback pack → the generic tiles →
/// none. Discord needs an https address, so only entries with one count. A publish never starts an upload.</para>
/// <para>When Discord gets none, the status keeps its role icon and app logo exactly as <c>PackArtworkProvider</c> sets them (D16, D29).</para>
/// </summary>
public static class PictureResolver
{
    /// <summary>Resolves one character.</summary>
    /// <param name="key">The mapping key, <c>character:&lt;id&gt;</c>.</param>
    /// <param name="role">The character's role, for the generic tile and the role icon.</param>
    /// <param name="pictures">The character's own pictures, or null.</param>
    /// <param name="context">The packs and online state.</param>
    public static PictureResolution Resolve(string key, Role role, CharacterPictures? pictures, PictureContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(context);
        var preview = ResolvePreview(key, role, pictures, context);
        var discord = ResolveDiscord(key, role, pictures, context);
        return new PictureResolution(preview, discord, !SameThing(preview, discord, context));
    }

    // Whether the preview and Discord show the same picture. The two sides are resolved separately (the preview needs a file on this PC, Discord
    // an https address), so they can part ways without any reason being known: this compares what they ended up with (review F6).
    private static bool SameThing(PreviewPicture preview, DiscordPicture discord, PictureContext context)
    {
        switch (preview.Source)
        {
            case PictureSource.None:
            case PictureSource.RoleIcon:
                return discord.Reference is null; // Discord keeps its role icon and app logo
            case PictureSource.OwnLink:
                return discord.Source == DiscordSource.Link && string.Equals(discord.Reference, preview.Link, StringComparison.Ordinal);
            case PictureSource.OwnFile:
                return discord.Source is DiscordSource.SavedOnline or DiscordSource.OwnerCopy;
            case PictureSource.Missing:
                return false;
            default:
                return !preview.LocalOnly
                    && discord.Reference is { } reference
                    && FindEntry(context, preview.PackId, preview.EntryId) is { Url: { } url }
                    && string.Equals(url, reference, StringComparison.Ordinal);
        }
    }

    private static PreviewPicture ResolvePreview(string key, Role role, CharacterPictures? pictures, PictureContext context)
    {
        if (pictures?.Use is { } use)
        {
            if (use.Kind == PictureUseKind.Personal && pictures.Items.FirstOrDefault(i => i.Sha256 == use.Sha256) is { } item)
            {
                if (item.Link is { } link)
                {
                    return new PreviewPicture(PictureSource.OwnLink, null, link, null, null, null);
                }

                return context.LocalPathOf(item) is { } path
                    ? new PreviewPicture(PictureSource.OwnFile, path, null, null, null, null)
                    : new PreviewPicture(PictureSource.Missing, null, null, null, null, null);
            }

            if (use.Kind == PictureUseKind.Owner && FindEntry(context, use.Pack, use.Entry) is { LocalPath: not null } chosen)
            {
                return FromPiece(PictureSource.ChosenOwnerPack, chosen, context);
            }
        }

        foreach (var (source, pack) in Layers(context))
        {
            if (Find(context, pack.Id, key) is { LocalPath: not null } piece)
            {
                return FromPiece(source, piece, context);
            }
        }

        if (GenericKey(role) is { } generic)
        {
            foreach (var (_, pack) in Layers(context))
            {
                if (Find(context, pack.Id, generic) is { LocalPath: not null } piece)
                {
                    return FromPiece(PictureSource.GenericTile, piece, context);
                }
            }
        }

        if (RoleKey(role) is { } roleKey)
        {
            foreach (var (_, pack) in Layers(context))
            {
                if (Find(context, pack.Id, roleKey) is { LocalPath: not null } piece)
                {
                    return FromPiece(PictureSource.RoleIcon, piece, context);
                }
            }
        }

        return new PreviewPicture(PictureSource.None, null, null, null, null, null);
    }

    private static DiscordPicture ResolveDiscord(string key, Role role, CharacterPictures? pictures, PictureContext context)
    {
        PictureOnlineStatus? why = null;
        if (pictures?.Use is { } use)
        {
            if (use.Kind == PictureUseKind.Owner)
            {
                if (FindEntry(context, use.Pack, use.Entry) is { Url: { } url } chosen && IsHttps(url) && PackOf(context, chosen.PackId) is { IsImported: false } pack)
                {
                    return new DiscordPicture(url, DiscordSource.ChosenOwnerPack, pack.BehaviourArt, pack.Name, null);
                }
            }
            else if (context.Rules == PictureRules.Declined)
            {
                // Declining the picture rules keeps every own picture preview-only: nothing of the user's own goes to Discord (PLAN_V2_S5B §2.6).
                if (pictures.Items.Any(i => i.Sha256 == use.Sha256))
                {
                    why = PictureOnlineStatus.RulesNotAccepted;
                }
            }
            else if (pictures.Items.FirstOrDefault(i => i.Sha256 == use.Sha256) is { } item)
            {
                if (OwnerCopy(context, item) is { } copy && PackOf(context, copy.PackId) is { } copyPack)
                {
                    return new DiscordPicture(copy.Url, DiscordSource.OwnerCopy, copyPack.BehaviourArt, copyPack.Name, null);
                }

                if (item.Link is { } link)
                {
                    if (LinkPolicy.Check(link).IsValid)
                    {
                        return new DiscordPicture(link, DiscordSource.Link, false, null, null);
                    }

                    why = PictureOnlineStatus.Unusable;
                }
                else if (context.LocalPathOf(item) is null)
                {
                    why = PictureOnlineStatus.Unusable;
                }
                else
                {
                    var online = context.OnlineOf(item);
                    if (online is { Status: PictureOnlineStatus.Saved, Url: { } saved } && IsHttps(saved))
                    {
                        return new DiscordPicture(saved, DiscordSource.SavedOnline, false, null, null);
                    }

                    why = online.Status == PictureOnlineStatus.Saved ? PictureOnlineStatus.Saving : online.Status;
                }
            }
        }

        foreach (var (source, pack) in Layers(context).Where(l => !l.Pack.IsImported))
        {
            if (Find(context, pack.Id, key) is { Url: { } url } && IsHttps(url))
            {
                return new DiscordPicture(url, ToDiscord(source), pack.BehaviourArt, pack.Name, why);
            }
        }

        if (GenericKey(role) is { } generic)
        {
            foreach (var (_, pack) in Layers(context).Where(l => !l.Pack.IsImported))
            {
                if (Find(context, pack.Id, generic) is { Url: { } url } && IsHttps(url))
                {
                    return new DiscordPicture(url, DiscordSource.GenericTile, pack.BehaviourArt, pack.Name, why);
                }
            }
        }

        return new DiscordPicture(null, DiscordSource.None, false, null, why);
    }

    private static DiscordSource ToDiscord(PictureSource source) => source switch
    {
        PictureSource.CharactersPack => DiscordSource.CharactersPack,
        PictureSource.DefaultPack => DiscordSource.DefaultPack,
        _ => DiscordSource.FallbackPack,
    };

    // The layers under the picture in use: the pack chosen for characters, then the default pack, then the fallback pack. A pack appears once.
    private static IEnumerable<(PictureSource Source, PicturePack Pack)> Layers(PictureContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (context.CharactersPackId is { } chosen && PackOf(context, chosen) is { } characters && seen.Add(characters.Id))
        {
            yield return (PictureSource.CharactersPack, characters);
        }

        // Only the first default pack counts, as the index parser reads it.
        if (context.Packs.FirstOrDefault(p => p.IsDefault && !p.IsImported) is { } defaults && seen.Add(defaults.Id))
        {
            yield return (PictureSource.DefaultPack, defaults);
        }

        foreach (var fallback in context.Packs.Where(p => p.IsFallback && !p.IsImported))
        {
            if (seen.Add(fallback.Id))
            {
                yield return (PictureSource.FallbackPack, fallback);
            }
        }
    }

    private static PicturePiece? OwnerCopy(PictureContext context, PictureItem item)
    {
        foreach (var piece in context.Pieces)
        {
            if (piece.Url is not null && IsHttps(piece.Url) && PackOf(context, piece.PackId) is { IsImported: false }
                && (Matches(piece.Sha256, item) || (piece.SourceSha256 is { } source && Matches(source, item))))
            {
                return piece;
            }
        }

        return null;
    }

    private static bool Matches(string hash, PictureItem item) =>
        string.Equals(hash, item.Sha256, StringComparison.Ordinal) || (item.SourceSha256 is { } source && string.Equals(hash, source, StringComparison.Ordinal));

    private static PreviewPicture FromPiece(PictureSource source, PicturePiece piece, PictureContext context) =>
        new(source, piece.LocalPath, null, PackOf(context, piece.PackId)?.Name, piece.PackId, piece.EntryId, PackOf(context, piece.PackId)?.IsImported ?? false);

    private static PicturePiece? Find(PictureContext context, string packId, string key) =>
        context.Pieces.FirstOrDefault(p => string.Equals(p.PackId, packId, StringComparison.Ordinal) && string.Equals(p.Key, key, StringComparison.Ordinal));

    private static PicturePiece? FindEntry(PictureContext context, string? packId, string? entryId) =>
        context.Pieces.FirstOrDefault(p => string.Equals(p.PackId, packId, StringComparison.Ordinal) && string.Equals(p.EntryId, entryId, StringComparison.Ordinal));

    private static PicturePack? PackOf(PictureContext context, string packId) =>
        context.Packs.FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.Ordinal));

    private static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string? GenericKey(Role role) => role switch
    {
        Role.Killer => "character:generic-killer",
        Role.Survivor => "character:generic-survivor",
        _ => null,
    };

    private static string? RoleKey(Role role) => role switch
    {
        Role.Killer => "role:killer",
        Role.Survivor => "role:survivor",
        _ => null,
    };
}
