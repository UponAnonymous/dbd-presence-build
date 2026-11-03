using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.Core.Pictures;

/// <summary>Why a change to a character's pictures was refused.</summary>
public enum SlotProblem
{
    /// <summary>The change was made.</summary>
    None,

    /// <summary>Own pictures attach only to <c>character:&lt;id&gt;</c> keys (O-3).</summary>
    NotACharacterKey,

    /// <summary>The character already has three own pictures.</summary>
    Full,

    /// <summary>The character already has this picture.</summary>
    Duplicate,

    /// <summary>The picture breaks a rule: a file of 2 MB or more, a bad hash or file name, or a bad link.</summary>
    InvalidPicture,

    /// <summary>The picture to use or remove is not one of the character's own pictures.</summary>
    NotFound,
}

/// <summary>The outcome of a change to a character's pictures.</summary>
/// <param name="Pictures">The pictures after the change (unchanged when refused).</param>
/// <param name="Problem">Why it was refused, or <see cref="SlotProblem.None"/>.</param>
public sealed record SlotResult(CharacterPictures Pictures, SlotProblem Problem)
{
    /// <summary>True when the change was made.</summary>
    public bool Succeeded => Problem == SlotProblem.None;

    /// <summary>The sentence for the user, or null when the change was made.</summary>
    public string? Message => Problem switch
    {
        SlotProblem.None => null,
        SlotProblem.NotACharacterKey => "Pictures can only be added to a killer or survivor.",
        SlotProblem.Full => "This character has 3 of your pictures. Remove one to add another.",
        SlotProblem.Duplicate => "This character already has that picture.",
        SlotProblem.NotFound => "That picture is no longer in the list.",
        _ => "That picture can't be used: it must be a PNG under 2 MB, or an https link.",
    };
}

/// <summary>
/// The rules for one character's pictures (FR-PACK-12): the default portrait is always there and cannot be removed, there are at most three own
/// pictures, own pictures attach only to character keys, and each saved file is under 2 MB. All methods are pure: they return the new value.
/// </summary>
public static class PictureSlots
{
    /// <summary>The hash a link picture is filed under: the SHA-256 of its text.</summary>
    public static string HashOfLink(string link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(link)));
    }

    /// <summary>Builds the item of a checked link. Returns null when the link is not acceptable.</summary>
    public static PictureItem? LinkItem(string? link, DateTimeOffset now)
    {
        var check = LinkPolicy.Check(link);
        return check.Normalised is { } normalised
            ? new PictureItem(HashOfLink(normalised), null, 0, null, normalised, PictureOrigin.Link, now)
            : null;
    }

    /// <summary>Adds an own picture. It becomes the one in use when <paramref name="makeInUse"/> is true.</summary>
    /// <param name="current">The character's pictures, or null for none.</param>
    /// <param name="key">The mapping key, <c>character:&lt;id&gt;</c>.</param>
    /// <param name="item">The picture.</param>
    /// <param name="makeInUse">Whether to use it from now on.</param>
    public static SlotResult Add(CharacterPictures? current, string key, PictureItem item, bool makeInUse)
    {
        ArgumentNullException.ThrowIfNull(item);
        var pictures = current ?? CharacterPictures.Empty;
        if (!PictureKeys.IsCharacterKey(key))
        {
            return new SlotResult(pictures, SlotProblem.NotACharacterKey);
        }

        if (!IsValid(item))
        {
            return new SlotResult(pictures, SlotProblem.InvalidPicture);
        }

        if (pictures.Items.Any(i => string.Equals(i.Sha256, item.Sha256, StringComparison.Ordinal)))
        {
            return new SlotResult(pictures, SlotProblem.Duplicate);
        }

        if (pictures.Items.Count >= PictureLimits.MaxOwnPerCharacter)
        {
            return new SlotResult(pictures, SlotProblem.Full);
        }

        return new SlotResult(
            new CharacterPictures([.. pictures.Items, item], makeInUse ? PictureUse.Personal(item.Sha256) : pictures.Use), SlotProblem.None);
    }

    /// <summary>Removes an own picture. When it was in use the character goes back to the default portrait.</summary>
    public static SlotResult Remove(CharacterPictures? current, string sha256)
    {
        var pictures = current ?? CharacterPictures.Empty;
        if (!pictures.Items.Any(i => string.Equals(i.Sha256, sha256, StringComparison.Ordinal)))
        {
            return new SlotResult(pictures, SlotProblem.NotFound);
        }

        var inUse = pictures.Use is { Kind: PictureUseKind.Personal } use && string.Equals(use.Sha256, sha256, StringComparison.Ordinal);
        return new SlotResult(
            new CharacterPictures([.. pictures.Items.Where(i => !string.Equals(i.Sha256, sha256, StringComparison.Ordinal))], inUse ? null : pictures.Use),
            SlotProblem.None);
    }

    /// <summary>Chooses the picture in use: an own picture, an owner entry, or null for the default portrait.</summary>
    public static SlotResult Use(CharacterPictures? current, PictureUse? use)
    {
        var pictures = current ?? CharacterPictures.Empty;
        if (use is { Kind: PictureUseKind.Personal }
            && !pictures.Items.Any(i => string.Equals(i.Sha256, use.Sha256, StringComparison.Ordinal)))
        {
            return new SlotResult(pictures, SlotProblem.NotFound);
        }

        if (use is { Kind: PictureUseKind.Owner } && (string.IsNullOrEmpty(use.Pack) || string.IsNullOrEmpty(use.Entry)))
        {
            return new SlotResult(pictures, SlotProblem.InvalidPicture);
        }

        return new SlotResult(pictures with { Use = use }, SlotProblem.None);
    }

    /// <summary>
    /// True when <paramref name="item"/> keeps every rule of the model. A file's size of 0 means "not read yet" (pictures migrated from settings
    /// v2 carry no size); any size read must be under 2 MB.
    /// </summary>
    public static bool IsValid(PictureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!PictureKeys.IsSha256(item.Sha256) || (item.SourceSha256 is not null && !PictureKeys.IsSha256(item.SourceSha256)))
        {
            return false;
        }

        return item.Origin == PictureOrigin.Link
            ? item.File is null && item.Bytes == 0 && LinkPolicy.Check(item.Link).Normalised == item.Link && item.Sha256 == HashOfLink(item.Link!)
            : item.Link is null && item.File == item.Sha256 + ".png" && item.Bytes is >= 0 and < PictureLimits.MaxFileBytes;
    }
}
