using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;

namespace DbdPresence.Packs.Pictures;

/// <summary>The outcome of moving imported packs into the picture model.</summary>
/// <param name="Pictures">The pictures per key after the move.</param>
/// <param name="Migrated">How many pictures were added.</param>
/// <param name="OverLimit">How many were left out because the character already had three.</param>
/// <param name="GenericKeys">The role, generic and map keys the packs had; only owner packs fill those now.</param>
/// <param name="AllCopied">True when every picture that should be copied was copied; the imported packs may then be removed.</param>
public sealed record ImportedPackMigrationResult(
    IReadOnlyDictionary<string, CharacterPictures> Pictures, int Migrated, int OverLimit, IReadOnlyList<string> GenericKeys, bool AllCopied);

/// <summary>
/// The imported-pack half of the settings v2 to v3 migration (PLAN_V2_S5B §2.6, FR-PACK-16). Each character picture of an imported pack becomes an
/// own picture (origin Import, up to three per character), and the pack chosen for characters sets the picture in use for the keys it covers. The
/// result is applied to the settings by the caller, and the imported packs are removed only when <see cref="ImportedPackMigrationResult.AllCopied"/>
/// is true. Running it again changes nothing: a picture already in the list is not added twice. Nothing is queued for saving online.
/// </summary>
public static class ImportedPackMigration
{
    /// <summary>Moves the character pictures of <paramref name="packs"/> into <paramref name="current"/>.</summary>
    /// <param name="packs">The imported packs.</param>
    /// <param name="current">The pictures now.</param>
    /// <param name="charactersPackId">The imported pack chosen for characters, or null or empty.</param>
    /// <param name="store">Copies the files into the pictures folder.</param>
    public static ImportedPackMigrationResult Migrate(
        IReadOnlyList<ImportedPack> packs, IReadOnlyDictionary<string, CharacterPictures> current, string? charactersPackId, PictureStore store)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(store);
        var result = new Dictionary<string, CharacterPictures>(current, StringComparer.Ordinal);
        var generic = new SortedSet<string>(StringComparer.Ordinal);
        var migrated = 0;
        var over = 0;
        var allCopied = true;
        foreach (var pack in packs.OrderBy(p => p.Info.Id, StringComparer.Ordinal))
        {
            var chosen = !string.IsNullOrEmpty(charactersPackId) && string.Equals(pack.Info.Id, charactersPackId, StringComparison.Ordinal);
            foreach (var entry in pack.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!PictureKeys.IsCharacterKey(entry.Key))
                {
                    generic.Add(entry.Key);
                    continue;
                }

                var path = pack.PathOf(entry);
                var item = path is null ? null : store.AdoptFile(path);
                if (item is null)
                {
                    allCopied = false;
                    continue;
                }

                var pictures = result.GetValueOrDefault(entry.Key) ?? CharacterPictures.Empty;
                if (pictures.Items.Any(i => i.Sha256 == item.Sha256))
                {
                    continue;
                }

                var added = PictureSlots.Add(pictures, entry.Key, item, makeInUse: false);
                if (!added.Succeeded)
                {
                    over++;
                    continue;
                }

                migrated++;
                result[entry.Key] = chosen && added.Pictures.Use is null
                    ? PictureSlots.Use(added.Pictures, PictureUse.Personal(item.Sha256)).Pictures
                    : added.Pictures;
            }
        }

        return new ImportedPackMigrationResult(result, migrated, over, [.. generic], allCopied);
    }
}
