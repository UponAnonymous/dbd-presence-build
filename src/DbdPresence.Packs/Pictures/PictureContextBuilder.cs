using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;

namespace DbdPresence.Packs.Pictures;

/// <summary>
/// Turns the pack library into the resolver's input (PLAN_V2_S5B §2.6). Only usable entries are listed: synced entries that are not
/// withdrawn and are in the cache, and the imported packs' own files. Nothing is read from disk and nothing is fetched.
/// </summary>
public static class PictureContextBuilder
{
    /// <summary>Builds the context.</summary>
    /// <param name="library">The pack library.</param>
    /// <param name="store">Finds the files of own pictures.</param>
    /// <param name="charactersPackId">The pack chosen for characters (settings), or null or empty for none.</param>
    /// <param name="defaultPackId">
    /// The id of the owner's default pack (D31), or null. The signed index's <c>default</c> flag (S5b-3) is read by the caller until
    /// <c>PackInfo</c> carries it.
    /// </param>
    /// <param name="sourceShaByEntryId">The optional <c>sourceSha256</c> of owner entries (S5b-3), by entry id; null for none.</param>
    /// <param name="onlineOf">The online state of an own picture.</param>
    /// <param name="rules">Whether the user accepted the picture rules; declined keeps every own picture preview-only.</param>
    public static PictureContext Build(
        PackLibrary library,
        PictureStore store,
        string? charactersPackId,
        string? defaultPackId,
        IReadOnlyDictionary<string, string>? sourceShaByEntryId,
        Func<PictureItem, PictureOnline> onlineOf,
        PictureRules rules = PictureRules.Unset)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(onlineOf);
        var packs = new List<PicturePack>();
        var pieces = new List<PicturePiece>();
        var synced = library.Synced;
        foreach (var card in library.Cards())
        {
            var imported = card.Origin == PackOrigin.Imported;
            packs.Add(new PicturePack(
                card.Info.Id,
                card.Info.Name,
                !imported && !string.IsNullOrEmpty(defaultPackId) && string.Equals(card.Info.Id, defaultPackId, StringComparison.Ordinal),
                !imported && card.Info.Fallback,
                !imported && card.Info.BehaviourArt,
                imported));
            foreach (var entry in card.Entries)
            {
                string? source = null;
                sourceShaByEntryId?.TryGetValue(entry.Id, out source);
                pieces.Add(new PicturePiece(
                    card.Info.Id,
                    entry.Id,
                    entry.Key,
                    entry.Sha256,
                    source,
                    imported ? null : synced?.UrlFor(entry),
                    library.LocationOf(card, entry)?.Path));
            }
        }

        return new PictureContext(
            packs, pieces, string.IsNullOrEmpty(charactersPackId) ? null : charactersPackId, store.PathOf, onlineOf, rules);
    }
}
