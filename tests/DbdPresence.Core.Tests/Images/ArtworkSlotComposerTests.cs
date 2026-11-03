// Covers: FR-ART-02/FR-ART-05 (no bundled art, text-only activity when nothing resolves; D16), FR-PACK-06 (character
// artwork slot, confirmed character only; D30: nothing is declared by hand).
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;

namespace DbdPresence.Core.Tests.Images;

public sealed class ArtworkSlotComposerTests
{
    private const string CharacterRef = "https://cdn.example.org/pack/character-dwight.png";
    private const string FallbackRef = "https://cdn.example.org/pack/app-mark.png";
    private static readonly DateTimeOffset s_now = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CharacterSlot_UsesTheResolvedCharacterImage()
    {
        var profile = ProfileWithCharacterSlot(new ArtworkSettings { CharacterImage = CharacterRef });

        var activity = PresenceComposer.Compose(Running("Dwight Fairfield"), profile, new PrivacyState()).Activity!;

        Assert.Equal(CharacterRef, activity.LargeImage);
    }

    [Fact]
    public void CharacterSlot_WithoutAConfirmedCharacter_UsesTheFallbackNotACharacterImage()
    {
        var profile = ProfileWithCharacterSlot(new ArtworkSettings { CharacterImage = CharacterRef, Fallback = FallbackRef });

        var activity = PresenceComposer.Compose(Running(), profile, new PrivacyState()).Activity!;

        Assert.Equal(FallbackRef, activity.LargeImage);
    }

    [Fact]
    public void CharacterSlot_HiddenWhenCharactersAreHidden()
    {
        var profile = ProfileWithCharacterSlot(new ArtworkSettings { CharacterImage = CharacterRef, Fallback = FallbackRef }) with
        {
            Visibility = new VisibilitySettings { ShowCharacter = false },
        };

        var activity = PresenceComposer.Compose(Running("Dwight Fairfield"), profile, new PrivacyState()).Activity!;

        Assert.Equal(FallbackRef, activity.LargeImage);
    }

    [Fact]
    public void NothingResolves_TheActivityHasNoImagesAndStaysValid()
    {
        var artwork = new ArtworkSettings { AppLogo = string.Empty, RoleKiller = string.Empty, RoleSurvivor = string.Empty, Fallback = string.Empty, CharacterImage = null };
        var running = new PresenceTemplate
        {
            Details = "Playing Dead by Daylight",
            LargeImage = ImageSlot.Character,
            LargeText = "Large text",
            SmallImage = ImageSlot.Role,
            SmallText = "Small text",
        };
        var profile = new PresenceProfile
        {
            Artwork = artwork,
            Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Running] = running } },
        };

        var result = PresenceComposer.Compose(Running("The Trapper") with { DetectedRole = Role.Killer }, profile, new PrivacyState());

        var activity = result.Activity!;
        Assert.Equal("Playing Dead by Daylight", activity.Details);
        Assert.Null(activity.LargeImage);
        Assert.Null(activity.SmallImage);
        Assert.Null(activity.LargeText);
        Assert.Null(activity.SmallText);
        Assert.DoesNotContain(result.Notes, n => n.Contains("not confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void HttpsReferencesNeedNoUserConfirmation()
    {
        var artwork = new ArtworkSettings { RoleKiller = CharacterRef };
        var profile = new PresenceProfile { Artwork = artwork };

        var result = PresenceComposer.Compose(Running() with { DetectedRole = Role.Killer }, profile, new PrivacyState());

        Assert.Equal(CharacterRef, result.Activity!.LargeImage);
        Assert.DoesNotContain(result.Notes, n => n.Contains("not confirmed", StringComparison.Ordinal));
        Assert.False(artwork.IsBehaviourArtwork);
    }

    private static PresenceInputs Running(string? character = null) =>
        new() { State = PresenceState.Running, Now = s_now, GameStartedUtc = s_now.AddMinutes(-5), CharacterName = character };

    private static PresenceProfile ProfileWithCharacterSlot(ArtworkSettings artwork)
    {
        var running = new PresenceTemplate { Details = "Playing Dead by Daylight", LargeImage = ImageSlot.Character };
        return new PresenceProfile
        {
            Artwork = artwork,
            Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Running] = running } },
        };
    }
}
