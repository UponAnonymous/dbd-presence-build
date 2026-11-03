using System.Globalization;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Presentation.Characters;

/// <summary>
/// The words of the Characters page that depend on state (docs/design/characters-page.md §6.2, §7). One place, so the view only binds and the
/// reviewer can check every string against the design. The words say "unlisted", never "private", and never "when first needed".
/// </summary>
public static class PictureTexts
{
    /// <summary>The short Discord line on a picture option for an own picture that is not on Discord, by why.</summary>
    public static string DiscordLine(PictureOnlineStatus status) => status switch
    {
        PictureOnlineStatus.Saved => "On Discord · saved online, unlisted",
        PictureOnlineStatus.SignedOut => "Preview only · sign in to use it",
        PictureOnlineStatus.RulesNotAccepted => "Preview only · accept the picture rules",
        PictureOnlineStatus.Blocked => "Preview only · online pictures are off",
        PictureOnlineStatus.Paused => "Preview only · saving is paused",
        PictureOnlineStatus.StorageFull => "Preview only · storage full",
        PictureOnlineStatus.ServiceUnreachable => "Waiting for the service",
        PictureOnlineStatus.Failed => "Couldn't save online",
        PictureOnlineStatus.DailyLimit => "Saving resumes tomorrow",
        PictureOnlineStatus.Unusable => "Missing from this PC",
        _ => "Saving online…",
    };

    /// <summary>The sentence under the preview pair when Discord shows something other than the picture in use, by why.</summary>
    public static string Difference(PictureOnlineStatus status) => status switch
    {
        PictureOnlineStatus.SignedOut =>
            "Discord shows the default portrait while you're signed out. Your picture is saved online when you sign in.",
        PictureOnlineStatus.RulesNotAccepted => "Discord shows the default portrait until you accept the picture rules.",
        PictureOnlineStatus.Blocked => "Online pictures are turned off for your account, so Discord shows the default portrait.",
        PictureOnlineStatus.Paused =>
            "Saving pictures online is paused for now, so Discord shows the default portrait. It's saved when saving is back on.",
        PictureOnlineStatus.StorageFull =>
            "Your 60 MB of online storage is full, so Discord shows the default portrait. Remove pictures you don't use to free space.",
        PictureOnlineStatus.ServiceUnreachable => "Discord shows the default portrait until the service is back.",
        PictureOnlineStatus.Failed =>
            "This picture couldn't be saved online. Discord shows the default portrait.",
        PictureOnlineStatus.DailyLimit =>
            "Today's limit for saving pictures online is reached, so Discord shows the default portrait until this one is saved tomorrow.",
        PictureOnlineStatus.Unusable => "This picture is missing from this PC, so Discord shows the default portrait.",
        _ => "Discord shows the default portrait until this picture is saved online.",
    };

    /// <summary>The tooltip of the Preview only mark on a tile.</summary>
    public static string PreviewOnlyTooltip(PictureOnlineStatus? status) => status == PictureOnlineStatus.Saving
        ? "Saving online. Discord shows the default portrait until it's saved."
        : "Preview only. Discord shows the default portrait.";

    /// <summary>"12.4" for a number of bytes, in MB with one decimal place, as the storage meter shows it.</summary>
    public static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"12.4 of 60 MB used".</summary>
    public static string StorageUsed(long usedBytes, long limitBytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{Megabytes(usedBytes)} of {(limitBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB used");

    /// <summary>The storage line of the import review (design §7.7).</summary>
    public static string ImportStorageLine(StorageEstimate estimate, long usedBytes, long limitBytes, bool signedIn)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var limit = (limitBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        if (!signedIn)
        {
            return $"Uses up to {Megabytes(estimate.SaveBytes)} MB of your {limit} MB online when you sign in.";
        }

        return estimate.Fits
            ? $"Saves up to {Megabytes(estimate.SaveBytes)} MB online. {Megabytes(usedBytes)} of {limit} MB used now; up to {Megabytes(usedBytes + estimate.SaveBytes)} MB after importing."
            : $"Needs more online space than is free ({Megabytes(estimate.FreeBytes)} MB). The first {estimate.FitCount.ToString(CultureInfo.InvariantCulture)} pictures (those you'll use first) are saved online; the other {estimate.PreviewOnlyCount.ToString(CultureInfo.InvariantCulture)} stay in the preview only until you free space.";
    }
}
