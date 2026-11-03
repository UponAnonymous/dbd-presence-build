namespace DbdPresence.Core.Pictures;

/// <summary>Where an own picture came from (PLAN_V2_S5B §2.6).</summary>
public enum PictureOrigin
{
    /// <summary>A file the user chose on this PC.</summary>
    Pc,

    /// <summary>A picture of an imported pack.</summary>
    Import,

    /// <summary>An https link; nothing is stored on this PC or online.</summary>
    Link,
}

/// <summary>Which picture a character uses when it is not the default portrait.</summary>
public enum PictureUseKind
{
    /// <summary>One of the user's own pictures, named by <see cref="PictureUse.Sha256"/>.</summary>
    Personal,

    /// <summary>An entry of an owner pack, named by <see cref="PictureUse.Pack"/> and <see cref="PictureUse.Entry"/>.</summary>
    Owner,
}

/// <summary>Whether the user accepted the picture rules (PLAN_V2_S5B §2.6). Declined keeps own pictures preview-only.</summary>
public enum PictureRules
{
    /// <summary>Not answered yet.</summary>
    Unset,

    /// <summary>Accepted.</summary>
    Accepted,

    /// <summary>Declined: own pictures stay in the preview.</summary>
    Declined,
}

/// <summary>
/// One own picture of a character. A picture from the PC or an import has a content-addressed file; a link has none.
/// </summary>
/// <param name="Sha256">Lower-case hex SHA-256 of the stored PNG; for a link, of the link text.</param>
/// <param name="SourceSha256">SHA-256 of the file before the app made it smaller, or null when unknown. Used to match an owner copy.</param>
/// <param name="Bytes">Size of the stored file in bytes; 0 for a link.</param>
/// <param name="File">The stored file name (<c>&lt;sha256&gt;.png</c>), or null for a link.</param>
/// <param name="Link">The https link, or null for a file.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="AddedAt">When it was added.</param>
public sealed record PictureItem(
    string Sha256, string? SourceSha256, long Bytes, string? File, string? Link, PictureOrigin Origin, DateTimeOffset AddedAt);

/// <summary>The picture a character uses instead of the default portrait.</summary>
/// <param name="Kind">Personal or owner.</param>
/// <param name="Sha256">The own picture's hash for <see cref="PictureUseKind.Personal"/>.</param>
/// <param name="Pack">The owner pack id for <see cref="PictureUseKind.Owner"/>.</param>
/// <param name="Entry">The owner entry id for <see cref="PictureUseKind.Owner"/>.</param>
public sealed record PictureUse(PictureUseKind Kind, string? Sha256, string? Pack, string? Entry)
{
    /// <summary>Uses the own picture with this hash.</summary>
    public static PictureUse Personal(string sha256) => new(PictureUseKind.Personal, sha256, null, null);

    /// <summary>Uses an entry of an owner pack.</summary>
    public static PictureUse Owner(string pack, string entry) => new(PictureUseKind.Owner, null, pack, entry);
}

/// <summary>A character's own pictures (at most <see cref="PictureLimits.MaxOwnPerCharacter"/>) and the one in use.</summary>
/// <param name="Items">The own pictures in the order they were added.</param>
/// <param name="Use">The picture in use, or null for the default portrait.</param>
public sealed record CharacterPictures(IReadOnlyList<PictureItem> Items, PictureUse? Use)
{
    /// <summary>A character with no own pictures and the default portrait in use.</summary>
    public static CharacterPictures Empty { get; } = new([], null);
}

/// <summary>The limits of the picture model (D28).</summary>
public static class PictureLimits
{
    /// <summary>Own pictures per character, besides the default portrait.</summary>
    public const int MaxOwnPerCharacter = 3;

    /// <summary>Every saved file is strictly smaller than this many bytes (2 MB).</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024;

    /// <summary>The default size of a saved picture (longest side, pixels).</summary>
    public const int DefaultSize = 512;

    /// <summary>The large size.</summary>
    public const int LargeSize = 1024;

    /// <summary>Longest accepted link.</summary>
    public const int MaxLinkLength = 512;

    /// <summary>The online storage each person may use, in bytes (D28; the service reports the real limit).</summary>
    public const long OnlineQuotaBytes = 60L * 1024 * 1024;

    /// <summary>True for the two sizes the user can choose.</summary>
    public static bool IsSize(int size) => size is DefaultSize or LargeSize;

    /// <summary>True when a saved file of <paramref name="bytes"/> bytes meets the 2 MB rule.</summary>
    public static bool FitsFileLimit(long bytes) => bytes is > 0 and < MaxFileBytes;
}

/// <summary>Mapping keys of own pictures: only <c>character:&lt;id&gt;</c>, never a generic tile, role or map (O-3).</summary>
public static class PictureKeys
{
    private const string Prefix = "character:";

    /// <summary>The key of a character id (a pack slug).</summary>
    public static string Of(string characterId) => Prefix + characterId;

    /// <summary>True for <c>character:&lt;slug&gt;</c> where the slug is 1 to 64 characters of <c>[a-z0-9-]</c> and is not a generic tile.</summary>
    public static bool IsCharacterKey(string? key)
    {
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var slug = key.AsSpan(Prefix.Length);
        if (slug.Length is 0 or > 64 || slug.SequenceEqual("generic") || slug.StartsWith("generic-", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in slug)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True for 64 lower-case hex characters.</summary>
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
