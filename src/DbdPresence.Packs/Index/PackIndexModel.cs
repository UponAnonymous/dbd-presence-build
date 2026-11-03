namespace DbdPresence.Packs.Index;

/// <summary>What an image stands for (FR-PACK-01 tags).</summary>
public enum PackEntryKind
{
    Role,
    Character,
    Map,
    App,
}

/// <summary>Image file format; selects the cache file extension.</summary>
public enum PackImageFormat
{
    Png,
    Jpeg,
}

/// <summary>Names the rights holder of a licensed item (FR-PACK-04).</summary>
/// <param name="Holder">The rights holder shown in credits.</param>
public sealed record LicensedInfo(string Holder);

/// <summary>Pack metadata (FR-PACK-01).</summary>
/// <param name="Id">Slug identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">Pack version, at least 1.</param>
/// <param name="Author">Author or owner credit.</param>
/// <param name="LicenceNote">Licence note shown on the Packs page.</param>
/// <param name="Credit">Optional extra credit, for example a permitted creator.</param>
/// <param name="Fallback">
/// True when the owner flagged this pack as the fallback pack in the signed index (D16): its entries are used when the
/// active pack lacks a key. Optional in the index; false when absent.
/// </param>
/// <param name="BehaviourArt">
/// True when the owner flagged the pack as containing Behaviour Interactive artwork (ADR-0013, FR-RPC-12). The app then shows
/// Behaviour's notice on the card and adds "Unofficial · © Behaviour Interactive" to the image hover text while one of its images is
/// sent to Discord. Optional in the index (<c>behaviourArt</c>); false when absent. Decided by this signed field, never by guessing.
/// </param>
/// <param name="Default">
/// True for the owner pack marked as the default pictures for characters (PLAN_V2_S5B §2.5, S5b-3): the base layer below the user's
/// Characters pack. Optional in the index (<c>default</c>); false when absent. At most one pack carries it: the parser keeps the first.
/// </param>
public sealed record PackInfo(
    string Id, string Name, int Version, string Author, string LicenceNote, string? Credit, bool Fallback = false, bool BehaviourArt = false, bool Default = false);

/// <summary>One image in a pack (FR-PACK-01).</summary>
/// <param name="Id">Globally unique slug.</param>
/// <param name="PackId">The pack this entry belongs to.</param>
/// <param name="Kind">Role, character, map or app.</param>
/// <param name="Key">Mapping key such as <c>role:killer</c>, <c>character:the-trapper</c> or <c>map:generic</c>.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the file; also its cache name.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Format">PNG or JPEG.</param>
/// <param name="Licensed">Null, or the rights holder when the item is licensed third-party content.</param>
/// <param name="SourceSha256">
/// Lowercase hex SHA-256 of the file the owner uploaded, before the service's lossless clean-up, or null (S5b-3). Lets the app recognise
/// a user's own copy of the same file (PLAN_V2_S5B §2.6). Optional in the index (<c>sourceSha256</c>).
/// </param>
public sealed record PackEntry(
    string Id,
    string PackId,
    PackEntryKind Kind,
    string Key,
    string Sha256,
    long Size,
    int Width,
    int Height,
    PackImageFormat Format,
    LicensedInfo? Licensed,
    string? SourceSha256 = null)
{
    /// <summary>The file name in the content-addressed cache: <c>&lt;sha256&gt;.&lt;ext&gt;</c>.</summary>
    public string CacheFileName => Sha256 + (Format == PackImageFormat.Png ? ".png" : ".jpg");
}

/// <summary>The payload of a <c>pack-index</c> signed document.</summary>
/// <param name="Packs">Packs described by the index.</param>
/// <param name="Entries">All entries of all packs.</param>
public sealed record PackIndex(IReadOnlyList<PackInfo> Packs, IReadOnlyList<PackEntry> Entries)
{
    /// <summary>An index with no packs.</summary>
    public static PackIndex Empty { get; } = new([], []);
}

/// <summary>Mapping keys: <c>kind:slug</c> with slug characters <c>[a-z0-9-]</c>.</summary>
public static class PackKey
{
    /// <summary>The slug of a pack's generic tile, for example <c>character:generic</c>.</summary>
    public const string GenericSlug = "generic";

    /// <summary>Longest accepted slug or id.</summary>
    public const int MaxSlugLength = 64;

    /// <summary>The lowercase tag of a kind as it appears in keys and JSON.</summary>
    public static string KindTag(PackEntryKind kind) => kind switch
    {
        PackEntryKind.Role => "role",
        PackEntryKind.Character => "character",
        PackEntryKind.Map => "map",
        PackEntryKind.App => "app",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind."),
    };

    /// <summary>Parses a kind tag.</summary>
    public static bool TryParseKind(string? tag, out PackEntryKind kind)
    {
        (var ok, kind) = tag switch
        {
            "role" => (true, PackEntryKind.Role),
            "character" => (true, PackEntryKind.Character),
            "map" => (true, PackEntryKind.Map),
            "app" => (true, PackEntryKind.App),
            _ => (false, default(PackEntryKind)),
        };
        return ok;
    }

    /// <summary>Builds a key. The slug must satisfy <see cref="IsSlug"/>.</summary>
    public static string Make(PackEntryKind kind, string slug)
    {
        if (!IsSlug(slug))
        {
            throw new ArgumentException("Slug must be 1 to 64 characters of [a-z0-9-].", nameof(slug));
        }

        return KindTag(kind) + ":" + slug;
    }

    /// <summary>The generic fallback key of a kind.</summary>
    public static string Generic(PackEntryKind kind) => Make(kind, GenericSlug);

    /// <summary>True for 1 to 64 characters from <c>[a-z0-9-]</c>.</summary>
    public static bool IsSlug(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSlugLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits <c>kind:slug</c>.</summary>
    public static bool TryParse(string? key, out PackEntryKind kind, out string slug)
    {
        kind = default;
        slug = string.Empty;
        if (key is null)
        {
            return false;
        }

        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !TryParseKind(key[..colon], out kind) || !IsSlug(key[(colon + 1)..]))
        {
            return false;
        }

        slug = key[(colon + 1)..];
        return true;
    }
}
