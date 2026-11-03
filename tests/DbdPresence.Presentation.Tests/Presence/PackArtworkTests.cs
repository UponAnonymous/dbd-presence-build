// Covers: FR-PACK-06 (resolver wiring: override, active pack, fallback pack, none; role, picked character, map only when Confirmed),
// FR-ART-02/05 and D16 (no bundled art: nothing resolves means no image is sent), FR-RPC-12 (IsBehaviourArtwork stays false),
// FR-ART-04 (images referenced through mappings, user-customised keys kept), FR-ART-01/05 (owner-uploaded asset keys of the official
// application survive when no pack supplies one; regression for the owner's report of 2026-10-24: "app_logo" never reached Discord).
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class PackArtworkTests
{
    private static readonly string s_sha = new('c', 64);
    private static readonly string[] s_shippedKeys = ["role_killer", "role_survivor", "app_logo"];

    private static ArtworkSettings Apply(PackHarness packs, AppSettings settings, Role role, string? character = null) =>
        packs.Provider.Apply(settings, settings.ActiveProfile, role, character);

    [Fact]
    public void WithNoPacks_TheOwnersUploadedAssetKeysSurvive()
    {
        using var host = new Harness();
        var packs = new PackHarness(host);
        var settings = new AppSettings();

        var artwork = Apply(packs, settings, Role.Killer);

        Assert.Equal("role_killer", artwork.RoleKiller);
        Assert.Equal("role_survivor", artwork.RoleSurvivor);
        Assert.Equal("app_logo", artwork.AppLogo);
        Assert.Equal("app_logo", artwork.Fallback);
        Assert.Null(artwork.CharacterImage);
        Assert.All(s_shippedKeys, k => Assert.Contains(k, artwork.ConfirmedUploadedKeys));
        Assert.False(artwork.IsBehaviourArtwork);
    }

    [Fact]
    public void AConfirmedCharacter_GetsNoImageWhenNoPackHasOne()
    {
        using var host = new Harness();
        var packs = new PackHarness(host);

        var killer = Apply(packs, new AppSettings(), Role.Killer, "The Trapper");
        var survivor = Apply(packs, new AppSettings(), Role.Survivor, "Dwight Fairfield");
        var noRole = Apply(packs, new AppSettings(), Role.None, "Dwight Fairfield");

        Assert.Null(killer.CharacterImage);
        Assert.Null(survivor.CharacterImage);
        Assert.Null(noRole.CharacterImage);
    }

    [Fact]
    public void OverridesAndImportedPacks_NeverReachDiscord_SoTheAssetKeyIsKept()
    {
        using var host = new Harness();
        var packs = new PackHarness(host);
        packs.Storage.Files[Path.Combine(PackFixtures.Root, "overrides", s_sha + ".png")] = PackFixtures.Png("o");
        var settings = new AppSettings
        {
            Packs = new PackSettings { KillerPack = "my-pack", Overrides = new Dictionary<string, string> { ["role:killer"] = s_sha + ".png" } },
        };

        var artwork = Apply(packs, settings, Role.Killer);

        Assert.Equal("role_killer", artwork.RoleKiller);
        Assert.Equal("role_survivor", artwork.RoleSurvivor);
    }

    [Fact]
    public void AKeyTheUserChangedOnPurpose_IsKept()
    {
        using var host = new Harness();
        var packs = new PackHarness(host);
        var settings = new AppSettings();
        settings = settings with { Profiles = [settings.ActiveProfile with { Artwork = new ArtworkSettings { RoleKiller = "my_own_killer", AppLogo = "https://example.org/a.png" } }] };

        var artwork = Apply(packs, settings, Role.Killer);

        Assert.Equal("my_own_killer", artwork.RoleKiller);
        Assert.Equal("https://example.org/a.png", artwork.AppLogo);
        Assert.Equal("role_survivor", artwork.RoleSurvivor);
        Assert.DoesNotContain("https://example.org/a.png", artwork.ConfirmedUploadedKeys);
    }

    [Fact]
    public void TheOwnersExactConfiguration_SendsAppLogoAsSmallImageAndNoLargeImage()
    {
        var settings = new AppSettings();
        var template = settings.ActiveProfile.Templates.ByState[PresenceState.Running] with
        {
            LargeImage = ImageSlot.None,
            SmallImage = ImageSlot.AppLogo,
        };
        var byState = new Dictionary<PresenceState, PresenceTemplate>(settings.ActiveProfile.Templates.ByState) { [PresenceState.Running] = template };
        settings = settings with { Profiles = [settings.ActiveProfile with { Templates = new TemplateSet { ByState = byState } }] };
        using var host = new Harness(settings);
        var packs = new PackHarness(host);
        using var coordinator = new PresenceCoordinator(host.Settings, host.Catalog, host.Game, host.Sink, host.Time, packs.Provider);
        host.Game.SetRunning(host.Time.GetUtcNow().AddMinutes(-3));

        coordinator.Start();

        Assert.NotNull(host.Sink.Last);
        Assert.Null(host.Sink.Last!.LargeImage);
        Assert.Equal("app_logo", host.Sink.Last.SmallImage);
    }

    [Fact]
    public void TheUsersOwnDiscordApplication_KeepsItsOwnKeysUntouched()
    {
        using var host = new Harness();
        var packs = new PackHarness(host);
        var settings = Harness.Configured();

        var artwork = Apply(packs, settings, Role.Killer, "The Trapper");

        Assert.Equal("role_killer", artwork.RoleKiller);
        Assert.Equal("app_logo", artwork.AppLogo);
        Assert.Null(artwork.CharacterImage);
        Assert.Empty(artwork.ConfirmedUploadedKeys);
    }

    [Fact]
    public void ProfileCharacterImage_IsNeverReadFromOrWrittenToSettingsFiles()
    {
        var property = typeof(ArtworkSettings).GetProperty(nameof(ArtworkSettings.CharacterImage))!;

        Assert.NotNull(property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: true).SingleOrDefault());
    }

    [Fact]
    public void RoleAndCharacterSlugs_AreOnlyGivenForUsableText()
    {
        Assert.Equal("killer", PackArtworkProvider.RoleSlug(Role.Killer));
        Assert.Equal("survivor", PackArtworkProvider.RoleSlug(Role.Survivor));
        Assert.Null(PackArtworkProvider.RoleSlug(Role.None));
        Assert.Equal("dwight-fairfield", PackArtworkProvider.CharacterSlug("  Dwight  Fairfield "));
        Assert.Null(PackArtworkProvider.CharacterSlug("   "));
        Assert.Null(PackArtworkProvider.CharacterSlug("???"));
        Assert.Null(PackArtworkProvider.CharacterSlug(new string('a', 65)));
    }

    [Fact]
    public void Coordinator_WithNoPacks_PublishesTheProfilesAssetKeys()
    {
        using var host = new Harness(detectedRole: Role.Killer);
        var packs = new PackHarness(host);
        using var coordinator = new PresenceCoordinator(host.Settings, host.Catalog, host.Game, host.Sink, host.Time, packs.Provider, detection: host.Feed);
        host.Game.SetRunning(host.Time.GetUtcNow().AddMinutes(-3));

        coordinator.Start();

        Assert.NotNull(host.Sink.Last);
        Assert.Equal("role_killer", host.Sink.Last!.LargeImage);
        Assert.DoesNotContain(coordinator.Snapshot.Composition.Notes, n => n.Contains("not confirmed", StringComparison.Ordinal));
        Assert.Equal("role_killer", coordinator.Snapshot.Artwork!.RoleKiller);
    }

    [Fact]
    public void Coordinator_TheRoleChangingFollowsTheRoleAssetKey()
    {
        using var host = new Harness(detectedRole: Role.Killer);
        var packs = new PackHarness(host);
        using var coordinator = new PresenceCoordinator(host.Settings, host.Catalog, host.Game, host.Sink, host.Time, packs.Provider, detection: host.Feed);
        host.Game.SetRunning(host.Time.GetUtcNow().AddMinutes(-3));
        coordinator.Start();

        host.DetectRole(Role.Survivor);

        Assert.NotNull(host.Sink.Last);
        Assert.Equal("role_survivor", host.Sink.Last!.LargeImage);
    }

    [Fact]
    public void Coordinator_WithoutPacks_KeepsTheProfilesOwnKeys()
    {
        using var host = new Harness(detectedRole: Role.Killer);
        host.Game.SetRunning(host.Time.GetUtcNow().AddMinutes(-3));
        host.Coordinator.Start();

        Assert.Equal("role_killer", host.Sink.Last!.LargeImage);
        Assert.Equal("role_killer", host.Coordinator.Snapshot.Artwork!.RoleKiller);
    }

    [Fact]
    public void Coordinator_NeverLabelsLocalPacksAsBehaviourArtwork()
    {
        using var host = new Harness(detectedRole: Role.Killer);
        var packs = new PackHarness(host);
        using var coordinator = new PresenceCoordinator(host.Settings, host.Catalog, host.Game, host.Sink, host.Time, packs.Provider, detection: host.Feed);
        host.Game.SetRunning(host.Time.GetUtcNow().AddMinutes(-3));
        coordinator.Start();

        Assert.DoesNotContain("Behaviour", host.Sink.Last!.LargeText ?? string.Empty, StringComparison.Ordinal);
    }
}
