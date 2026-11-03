using DbdPresence.Packs.Index;

namespace DbdPresence.Packs.Resolution;

/// <summary>What Discord is given for one image slot.</summary>
/// <param name="Reference">An https URL of a synced pack, or null when Discord gets no image for the slot.</param>
/// <param name="LocalOnly">
/// True when the user's preview shows a local image (an override or an imported pack) that Discord cannot show, so Discord
/// gets <paramref name="Reference"/> (a synced pack image, or no image). The UI says so (FR-PACK-06, PLAN_V2 §2.3).
/// </param>
/// <param name="BehaviourArt">
/// True when <paramref name="Reference"/> is an image of a pack the signed index flags <c>behaviourArt</c>, so the hover text must carry
/// "Unofficial · © Behaviour Interactive" (FR-RPC-12). False when there is no reference.
/// </param>
public sealed record DiscordImageRef(string? Reference, bool LocalOnly, bool BehaviourArt = false)
{
    /// <summary>No image.</summary>
    public static DiscordImageRef None { get; } = new(null, false);
}

/// <summary>The Discord side of the three slots plus the app mark.</summary>
/// <param name="Role">Role image.</param>
/// <param name="Character">Character image.</param>
/// <param name="Map">Map image (none unless the map is Confirmed).</param>
/// <param name="AppMark">The app mark (from the fallback pack, if the owner publishes one).</param>
public sealed record DiscordSelection(DiscordImageRef Role, DiscordImageRef Character, DiscordImageRef Map, DiscordImageRef AppMark);

/// <summary>
/// Turns a resolved pack image into what Discord can show (FR-PACK-06). Only https URLs of synced packs (including the
/// owner's fallback pack) reach Discord. Anything on the user's PC is preview-only, and nothing is bundled (D16).
/// </summary>
public static class DiscordImageMapper
{
    /// <summary>Maps a resolved image. Overrides and local imports never map to a Discord reference.</summary>
    /// <param name="image">The image resolved for the Discord side (see <see cref="PackImageResolver"/>).</param>
    /// <param name="syncedUrl">Gives the https URL of an entry of a synced pack; null while no synced pack exists.</param>
    public static string? Map(ResolvedImage image, Func<PackEntry, string?>? syncedUrl)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.Source switch
        {
            ResolvedImageSource.ActivePack or ResolvedImageSource.FallbackPack when image.Entry is { } entry
                && syncedUrl?.Invoke(entry) is { } url
                && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => url,
            _ => null,
        };
    }
}
