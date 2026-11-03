using DbdPresence.Configuration;
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Resolution;
using DbdPresence.Presentation.Characters;

namespace DbdPresence.Presentation.Presence;

/// <summary>
/// Gives the composer the image references the image packs resolve (FR-PACK-06, FR-ART-04). The order is the user's
/// override, the active pack of the category, the owner-published fallback pack, then no image (D16: nothing is bundled).
/// Discord only ever receives an https URL of a synced pack: overrides and imported packs are preview-only, so Discord gets
/// no image for them. The map is offered only when it is Confirmed, which the coordinator does not have yet (Stage C), so
/// no map image is sent.
/// <see cref="ArtworkSettings.IsBehaviourArtwork"/> is true exactly when an image sent to Discord belongs to a pack the signed index flags
/// <c>behaviourArt</c> (ADR-0013), so the composer adds "Unofficial · © Behaviour Interactive" to the hover text (FR-RPC-12). It is never guessed
/// from names or pictures. If any used slot has Behaviour art the whole activity is labelled, which can only over-label, never under-label.
/// </summary>
public sealed class PackArtworkProvider
{
    private readonly PackLibrary _library;
    private readonly CharacterPictureResolver? _pictures;

    /// <summary>Initializes a new instance of the <see cref="PackArtworkProvider"/> class.</summary>
    /// <param name="library">The pack library.</param>
    /// <param name="pictures">
    /// The resolver the Characters page also uses (D29). With it, the character picture of a status is the one the page shows as "Discord shows"; without it
    /// (or for a name the roster does not know) the pack choices of the settings resolve it as before. The role icons and the app logo always resolve here.
    /// </param>
    public PackArtworkProvider(PackLibrary library, CharacterPictureResolver? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _pictures = pictures;
    }

    /// <summary>Gets the library the provider resolves from.</summary>
    public PackLibrary Library => _library;

    /// <summary>Finds the local picture behind an image reference of an activity (D29); null when it is not a pack or upload address.</summary>
    /// <param name="reference">The image reference.</param>
    public PackReferenceMatch? Match(string? reference) => _library.MatchReference(reference);

    /// <summary>Converts the settings to the resolver's per-category pack choice.</summary>
    public static PackChoices ChoicesOf(PackSettings packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        return new PackChoices(packs.KillerPack, packs.SurvivorPack, packs.CharactersPack, packs.MapsPack);
    }

    /// <summary>The slug of a role for the resolver, or null when no role applies.</summary>
    public static string? RoleSlug(Role role) => role switch
    {
        Role.Killer => "killer",
        Role.Survivor => "survivor",
        _ => null,
    };

    /// <summary>The resolver slug of a character's display name, or null when it cannot be a slug.</summary>
    public static string? CharacterSlug(string? character)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return null;
        }

        var slug = MappingProposer.Slugify(character);
        return PackKey.IsSlug(slug) ? slug : null;
    }

    /// <summary>
    /// The artwork the composer should use. References the user changed on purpose (not the shipped defaults) and the
    /// user's own Discord application (Advanced) are left exactly as they are. A shipped default is replaced by what the
    /// packs resolve, and stays the asset key the owner uploaded to the official application when no pack supplies one.
    /// </summary>
    /// <param name="settings">The current settings.</param>
    /// <param name="profile">The profile being composed.</param>
    /// <param name="role">The confirmed role (None when the app has not confirmed one).</param>
    /// <param name="characterName">The confirmed character's display name, or null when the app has not confirmed one (D30: never declared by hand).</param>
    /// <param name="ownStatus">The confirmed state of the owner's own row; only the picture in use can add the Behaviour label (FR-RPC-12).</param>
    /// <param name="outcomeId">The confirmed outcome id of the result screen, with the same purpose.</param>
    public ArtworkSettings Apply(AppSettings settings, PresenceProfile profile, Role role, string? characterName = null, OwnStatus ownStatus = OwnStatus.None, string? outcomeId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        var artwork = profile.Artwork;
        if (settings.Discord.UseOwnApplication)
        {
            return artwork with { IsBehaviourArtwork = false, CharacterImage = null, StatusImages = ConfirmedStatusKeys(artwork) };
        }

        var choices = ChoicesOf(settings.Packs);
        var overrides = settings.Packs.Overrides;
        var character = CharacterSlug(characterName);
        var current = _library.ResolveDiscord(new ImageSelection(RoleSlug(role), character, null, false), choices, overrides);
        var killerImage = _library.ResolveDiscord(new ImageSelection("killer", null, null, false), choices, overrides).Role;
        var survivorImage = _library.ResolveDiscord(new ImageSelection("survivor", null, null, false), choices, overrides).Role;
        var defaults = new ArtworkSettings();
        var picture = _pictures?.Resolve(settings, characterName)?.Discord;
        var characterImage = picture is null ? current.Character.Reference : picture.Reference;
        var characterBehaviour = picture is null ? current.Character.BehaviourArt : picture.BehaviourArt;
        var (statusImages, statusBehaviour) = ResolveAutomaticPictures(choices, ownStatus, outcomeId);
        var behaviour = characterBehaviour
            || statusBehaviour
            || current.Map.BehaviourArt
            || (IsDefault(artwork.AppLogo, defaults.AppLogo) && current.AppMark.BehaviourArt)
            || (IsDefault(artwork.Fallback, defaults.Fallback) && current.AppMark.BehaviourArt)
            || (IsDefault(artwork.RoleKiller, defaults.RoleKiller) && killerImage.BehaviourArt)
            || (IsDefault(artwork.RoleSurvivor, defaults.RoleSurvivor) && survivorImage.BehaviourArt);

        var appLogo = Replace(artwork.AppLogo, defaults.AppLogo, current.AppMark.Reference);
        var roleKiller = Replace(artwork.RoleKiller, defaults.RoleKiller, killerImage.Reference);
        var roleSurvivor = Replace(artwork.RoleSurvivor, defaults.RoleSurvivor, survivorImage.Reference);
        var fallback = Replace(artwork.Fallback, defaults.Fallback, current.AppMark.Reference);

        // A shipped default that no pack replaced is an art asset the owner uploaded to the official application (D16), so it is
        // sent as it is and, like a pack image, needs no confirmation from the user (FR-ART-05).
        var confirmed = new HashSet<string>(artwork.ConfirmedUploadedKeys, StringComparer.Ordinal);
        foreach (var key in new[] { appLogo, roleKiller, roleSurvivor, fallback })
        {
            if (IsAssetKey(key))
            {
                confirmed.Add(key);
            }
        }

        return artwork with
        {
            AppLogo = appLogo,
            RoleKiller = roleKiller,
            RoleSurvivor = roleSurvivor,
            Fallback = fallback,
            CharacterImage = characterImage,
            StatusImages = statusImages,
            ConfirmedUploadedKeys = [.. confirmed],
            IsBehaviourArtwork = behaviour,
        };
    }

    /// <summary>
    /// The nine automatic pictures as Discord gets them, through the same pack library the role icons and the character pictures use (D29), so the
    /// preview shows exactly what Discord shows. The order is the pack chosen for the moment (Survivor or Killer pack, then the Characters pack), the
    /// owner's default pack, then the fallback pack (PLAN_AUTO_ICONS §4). A user override never applies. The Behaviour flag is true only when the
    /// picture of the moment in use belongs to a <c>behaviourArt</c> pack.
    /// </summary>
    private (IReadOnlyDictionary<string, string> Images, bool Behaviour) ResolveAutomaticPictures(PackChoices choices, OwnStatus ownStatus, string? outcomeId)
    {
        var synced = _library.Synced;
        var defaultPack = synced?.Index.Packs.FirstOrDefault(p => p.Default)?.Id;
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var inUse = AutomaticIcons.SlugOf(ownStatus) ?? AutomaticIcons.SlugOfOutcome(outcomeId);
        var behaviour = false;
        foreach (var slug in AutomaticIcons.AllSlugs)
        {
            var rolePack = IsKillerResultSlug(slug) ? choices.Killer : choices.Survivor;
            var picture = _library.ResolveAutomaticPicture(slug, [rolePack, choices.Characters, defaultPack], synced);
            if (picture.Reference is { } reference)
            {
                images[slug] = reference;
                behaviour |= picture.BehaviourArt && string.Equals(slug, inUse, StringComparison.Ordinal);
            }
        }

        return (images, behaviour);
    }

    private static bool IsKillerResultSlug(string slug) =>
        slug is AutomaticIcons.ResultEntityHungers or AutomaticIcons.ResultBrutal or AutomaticIcons.ResultRuthless or AutomaticIcons.ResultMerciless;

    /// <summary>
    /// For the user's own Discord application: a picture is offered only when its asset key (the slug with underscores, for example
    /// <c>status_hooked</c>) is on the user's confirmed list. These keys are never confirmed on the user's behalf, because the owner may never
    /// upload them and an unknown key would show a broken picture (FR-ART-05, FR-AUTO-02).
    /// </summary>
    private static Dictionary<string, string> ConfirmedStatusKeys(ArtworkSettings artwork)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slug in AutomaticIcons.AllSlugs)
        {
            var key = slug.Replace('-', '_');
            if (artwork.ConfirmedUploadedKeys.Contains(key, StringComparer.Ordinal))
            {
                images[slug] = key;
            }
        }

        return images;
    }

    private static bool IsDefault(string configured, string shippedDefault) => string.Equals(configured, shippedDefault, StringComparison.Ordinal);

    private static string Replace(string configured, string shippedDefault, string? resolved) =>
        string.Equals(configured, shippedDefault, StringComparison.Ordinal) ? resolved ?? configured : configured;

    private static bool IsAssetKey(string reference) =>
        !string.IsNullOrWhiteSpace(reference) && !reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
