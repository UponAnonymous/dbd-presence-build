using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;

namespace DbdPresence.Packs.Resolution;

/// <summary>Where a resolved image comes from.</summary>
public enum ResolvedImageSource
{
    /// <summary>No image applies; presence shows none for this slot.</summary>
    None,

    /// <summary>The user's own override file.</summary>
    UserOverride,

    /// <summary>An entry of an active synced pack, present in the cache.</summary>
    ActivePack,

    /// <summary>An entry of the owner-published fallback pack (flagged <c>fallback</c> in its signed index), present in the cache.</summary>
    FallbackPack,
}

/// <summary>The image chosen for one slot.</summary>
/// <param name="Source">Where it came from.</param>
/// <param name="Key">The requested mapping key.</param>
/// <param name="Entry">The pack entry for pack sources; null for overrides and none.</param>
/// <param name="OverridePath">The user's file for <see cref="ResolvedImageSource.UserOverride"/>.</param>
public sealed record ResolvedImage(ResolvedImageSource Source, string Key, PackEntry? Entry, string? OverridePath)
{
    /// <summary>The "no image" result for a key.</summary>
    public static ResolvedImage None(string key) => new(ResolvedImageSource.None, key, null, null);
}

/// <summary>The user's current manual picks and detection state (FR-PACK-06).</summary>
/// <param name="Role">Role slug such as <c>killer</c> or <c>survivor</c>, or null.</param>
/// <param name="Character">Character slug picked manually, or null (characters are never auto-detected).</param>
/// <param name="Map">Map slug, or null.</param>
/// <param name="MapConfirmed">True only when the map field is Confirmed by validated detection.</param>
public sealed record ImageSelection(string? Role, string? Character, string? Map, bool MapConfirmed);

/// <summary>Everything the resolver needs; no I/O happens in the resolver itself.</summary>
/// <param name="Synced">
/// The current signed, synced index, or null before the first sync. A pack flagged <c>fallback</c> in it is the fallback pack.
/// </param>
/// <param name="ActivePackIds">Ids of active packs in the Synced index, highest priority first.</param>
/// <param name="Overrides">User per-image overrides: mapping key to the user's file path.</param>
/// <param name="Cache">Tells which synced files are in the local cache.</param>
/// <param name="Withdrawn">Entry ids to skip even if cached, for example items withdrawn since the cache was filled.</param>
public sealed record ResolverContext(
    PackIndex? Synced,
    IReadOnlyList<string> ActivePackIds,
    IReadOnlyDictionary<string, string> Overrides,
    IPackContentCache Cache,
    IReadOnlySet<string> Withdrawn);

/// <summary>The three slot results for a selection.</summary>
/// <param name="Role">Image for the role.</param>
/// <param name="Character">Image for the character.</param>
/// <param name="Map">Image for the map; none unless the map is Confirmed.</param>
public sealed record ResolvedSelection(ResolvedImage Role, ResolvedImage Character, ResolvedImage Map);

/// <summary>
/// Chooses the image for a mapping key (FR-PACK-02, FR-PACK-04, FR-PACK-06; D16). Order: user override, active pack entry
/// (not withdrawn, present in the cache), entry of the owner-published fallback pack for the same key (same checks), none.
/// There is no bundled art: with none, presence is sent without an image for the slot. A map image is offered only when
/// the map is Confirmed.
/// </summary>
public static class PackImageResolver
{
    /// <summary>Resolves role, character and map for the selection.</summary>
    public static ResolvedSelection Resolve(ImageSelection selection, ResolverContext context)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(context);
        return new ResolvedSelection(
            ResolveSlot(PackEntryKind.Role, selection.Role, context),
            ResolveSlot(PackEntryKind.Character, selection.Character, context),
            selection.MapConfirmed
                ? ResolveSlot(PackEntryKind.Map, selection.Map, context)
                : ResolvedImage.None(selection.Map is { } map && PackKey.IsSlug(map) ? PackKey.Make(PackEntryKind.Map, map) : PackKey.Generic(PackEntryKind.Map)));
    }

    /// <summary>Resolves one mapping key such as <c>role:killer</c>.</summary>
    public static ResolvedImage ResolveKey(string key, ResolverContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PackKey.TryParse(key, out _, out _))
        {
            return ResolvedImage.None(key);
        }

        if (context.Overrides.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return new ResolvedImage(ResolvedImageSource.UserOverride, key, null, path);
        }

        if (FindActive(key, context) is { } active)
        {
            return new ResolvedImage(ResolvedImageSource.ActivePack, key, active, null);
        }

        return FindFallback(key, context) is { } fallback
            ? new ResolvedImage(ResolvedImageSource.FallbackPack, key, fallback, null)
            : ResolvedImage.None(key);
    }

    private static ResolvedImage ResolveSlot(PackEntryKind kind, string? slug, ResolverContext context)
    {
        if (slug is null || !PackKey.IsSlug(slug))
        {
            return ResolvedImage.None(PackKey.Generic(kind));
        }

        return ResolveKey(PackKey.Make(kind, slug), context);
    }

    private static PackEntry? FindActive(string key, ResolverContext context)
    {
        if (context.Synced is null)
        {
            return null;
        }

        foreach (var packId in context.ActivePackIds)
        {
            if (FindUsable(key, packId, context) is { } entry)
            {
                return entry;
            }
        }

        return null;
    }

    private static PackEntry? FindFallback(string key, ResolverContext context)
    {
        if (context.Synced is null)
        {
            return null;
        }

        foreach (var pack in context.Synced.Packs.Where(p => p.Fallback))
        {
            if (FindUsable(key, pack.Id, context) is { } entry)
            {
                return entry;
            }
        }

        return null;
    }

    private static PackEntry? FindUsable(string key, string packId, ResolverContext context)
    {
        var entry = context.Synced!.Entries.FirstOrDefault(e =>
            string.Equals(e.PackId, packId, StringComparison.Ordinal) && string.Equals(e.Key, key, StringComparison.Ordinal));
        return entry is not null && !context.Withdrawn.Contains(entry.Id) && context.Cache.Contains(entry) ? entry : null;
    }
}
