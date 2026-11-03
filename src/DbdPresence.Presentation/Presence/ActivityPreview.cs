using System.Globalization;
using DbdPresence.Core.Presence;
using DbdPresence.Core.Settings;
using DbdPresence.Packs.Local;

namespace DbdPresence.Presentation.Presence;

/// <summary>What a preview image tile is (D29, FR-ART-06).</summary>
public enum ImageTileKind
{
    /// <summary>A real picture from the local, hash-verified pack cache or the user's own uploaded picture.</summary>
    Picture,

    /// <summary>A pack picture that Discord shows but whose file is not on this PC.</summary>
    PackPictureMissing,

    /// <summary>A Discord Developer Portal art asset key: shown by Discord, not previewed here.</summary>
    AssetKey,

    /// <summary>A link the user typed: shown by Discord from the link, never fetched.</summary>
    UserLink,

    /// <summary>The Discord application's own icon, which Discord shows when no large image is sent.</summary>
    ApplicationIcon,
}

/// <summary>An image slot in the preview. Nothing is fetched (FR-ART-06): a picture is read from a local file, anything else is a labelled tile.</summary>
/// <param name="Reference">The asset key or https URL Discord receives. Display and tests only: the UI never loads it.</param>
/// <param name="Caption">What the tile says.</param>
/// <param name="HoverText">The image text Discord shows on hover, if any.</param>
/// <param name="IsUnconfirmed">True when the asset key is not confirmed as uploaded to the user's Discord application (FR-ART-05).</param>
/// <param name="IsApplicationIcon">True for the stand-in tile of the Discord application's own icon, which Discord shows when no large image is sent (PENDING-ENV, owner's report of 2026-10-24).</param>
/// <param name="Kind">What the tile is.</param>
/// <param name="Location">The local file to show for <see cref="ImageTileKind.Picture"/>; never a remote address.</param>
public sealed record ImagePreview(
    string Reference, string Caption, string? HoverText, bool IsUnconfirmed, bool IsApplicationIcon = false,
    ImageTileKind Kind = ImageTileKind.AssetKey, PackImageLocation? Location = null)
{
    /// <summary>The caption of the application-icon tile.</summary>
    public const string ApplicationIconCaption = "Discord application icon";

    /// <summary>The caption of a tile for a link the user typed.</summary>
    public const string UserLinkCaption = "Shown by Discord from your link";

    /// <summary>The caption of a tile for a pack picture that is not on this PC.</summary>
    public const string PackPictureMissingCaption = "Pack picture: shown by Discord, not saved on this PC yet";

    /// <summary>The caption of a real picture.</summary>
    public const string PictureCaption = "Picture from your packs";

    /// <summary>The caption of a tile for a Discord art asset key: plain words, never the key itself (the app cannot see what Discord draws for it).</summary>
    public const string AssetKeyCaption = "Shown by Discord";
}

/// <summary>
/// An approximation of Discord's activity card built from the exact payload requested from Discord
/// (UI_SPEC §3, §9.4). It contains no Discord logos and never fetches artwork.
/// </summary>
/// <param name="IsEmpty">True when nothing is shown on Discord.</param>
/// <param name="Heading">For example "Playing DBD Presence".</param>
/// <param name="Details">The first line.</param>
/// <param name="State">The second line.</param>
/// <param name="Elapsed">Elapsed timer text, if a start timestamp is sent.</param>
/// <param name="LargeImage">Large image tile.</param>
/// <param name="SmallImage">Small image badge.</param>
/// <param name="Buttons">Button labels (Discord hides buttons on your own profile).</param>
public sealed record ActivityPreview(
    bool IsEmpty,
    string Heading,
    string? Details,
    string? State,
    string? Elapsed,
    ImagePreview? LargeImage,
    ImagePreview? SmallImage,
    IReadOnlyList<string> Buttons)
{
    /// <summary>Builds the preview for <paramref name="activity"/> (null = nothing shown).</summary>
    public static ActivityPreview From(ValidatedActivity? activity, string applicationName, ArtworkSettings artwork, DateTimeOffset now, Func<string, PackReferenceMatch?>? match = null)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (activity is null)
        {
            return new ActivityPreview(true, "Nothing shown on Discord", null, null, null, null, null, []);
        }

        var name = activity.NameOverride ?? (string.IsNullOrWhiteSpace(applicationName) ? "your Discord application" : applicationName.Trim());
        var heading = activity.Kind switch
        {
            ActivityKind.Listening => $"Listening to {name}",
            ActivityKind.Watching => $"Watching {name}",
            ActivityKind.Competing => $"Competing in {name}",
            _ => $"Playing {name}",
        };

        // Unconfirmed asset keys are explained once, in the composer's notes; the tile only flags them.
        var large = Image(activity.LargeImage, activity.LargeText, artwork, match);
        var small = Image(activity.SmallImage, activity.SmallText, artwork, match);
        if (large is null)
        {
            // Discord draws the small image as a badge on the large one. With no large image the owner's live test showed the
            // application's own icon and no small image (FR-ART-01; PENDING-ENV), so the preview shows exactly that.
            large = new ImagePreview(string.Empty, ImagePreview.ApplicationIconCaption, null, IsUnconfirmed: false, IsApplicationIcon: true, ImageTileKind.ApplicationIcon);
            small = null;
        }

        return new ActivityPreview(
            false, heading, activity.Details, activity.State, ElapsedText(activity.StartTimestamp, now), large, small,
            [.. activity.Buttons.Select(b => b.Label)]);
    }

    /// <summary>Formats elapsed time like Discord's card: "12:34 elapsed" or "1:02:03 elapsed".</summary>
    public static string? ElapsedText(DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is not { } started)
        {
            return null;
        }

        var elapsed = now - started;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00} elapsed")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes:00}:{elapsed.Seconds:00} elapsed");
    }

    private static ImagePreview? Image(string? reference, string? hoverText, ArtworkSettings artwork, Func<string, PackReferenceMatch?>? match)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        if (reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // A pack or upload address resolves to a local file only; anything else is the user's own link, which is never fetched.
            return match?.Invoke(reference) switch
            {
                { Location: { } location } => new ImagePreview(reference, ImagePreview.PictureCaption, hoverText, false, false, ImageTileKind.Picture, location),
                not null => new ImagePreview(reference, ImagePreview.PackPictureMissingCaption, hoverText, false, false, ImageTileKind.PackPictureMissing),
                null => new ImagePreview(reference, ImagePreview.UserLinkCaption, hoverText, false, false, ImageTileKind.UserLink),
            };
        }

        return new ImagePreview(
            reference, ImagePreview.AssetKeyCaption, hoverText, !artwork.ConfirmedUploadedKeys.Contains(reference), false, ImageTileKind.AssetKey);
    }
}
