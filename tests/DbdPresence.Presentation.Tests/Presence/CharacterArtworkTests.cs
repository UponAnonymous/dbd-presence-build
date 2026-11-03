// Covers: D29 (the preview shows exactly what Discord gets), FR-ART-04 (override, pack, fallback, none), FR-PACK-13 (preview vs Discord),
// FR-PACK-14 (a link goes to Discord as the link), FR-RPC-12 (Behaviour label from the signed flag), and the b3752b0 rule
// (owner-uploaded asset keys app_logo / role_killer / role_survivor survive with no pack). S5b-5 wiring.
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;
using DbdPresence.Presentation.Tests.Support;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class CharacterArtworkTests
{
    private const string Saved = "https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png";

    private static async Task AddFromPc(CharactersHarness h)
    {
        h.Storage.Files["/in/me.png"] = [1, 2, 3, 9];
        h.Picker.Image = "/in/me.png";
        await h.Page.AddFromPcCommand.ExecuteAsync(null);
    }

    private static ArtworkSettings Discord(CharactersHarness h, string name, Role role = Role.Killer) =>
        h.Provider.Apply(h.Current, h.Current.ActiveProfile, role, name);

    [Fact]
    public void DefaultPortrait_PreviewAndDiscordAreTheSameAddress()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(true, "character:the-onryo");
        h.Select("K27");

        var artwork = Discord(h, "The Onryō");

        Assert.NotNull(artwork.CharacterImage);
        Assert.Equal(h.Page.DiscordReference, artwork.CharacterImage);
        Assert.True(artwork.IsBehaviourArtwork);
        Assert.Equal(h.Page.DiscordPictureIsBehaviourArt, artwork.IsBehaviourArtwork);
    }

    [Fact]
    public async Task OwnPictureNotSavedOnline_DiscordGetsTheDefaultPortrait_AsThePageSays()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        h.Select("K27");
        await AddFromPc(h);

        var artwork = Discord(h, "the onryo");

        Assert.False(h.Page.PreviewAndDiscordSame);
        Assert.Equal(h.Page.DiscordReference, artwork.CharacterImage);
        Assert.StartsWith("https://cdn.example.org/", artwork.CharacterImage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnPictureSavedOnline_DiscordGetsTheUnlistedAddress_TheStudioPreviewAlsoFinds()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        h.Environment.SignedIn = true;
        h.Select("K27");
        await AddFromPc(h);
        var sha = h.Current.Pictures["character:the-onryo"].Items.Single().Sha256;
        h.Library.SetUploads(new Dictionary<string, string> { [sha] = Saved }, "cdn.example.org");
        h.Environment.SavedUrlBySha256 = new Dictionary<string, string> { [sha] = Saved };
        h.Environment.Raise();

        var artwork = Discord(h, "The Onryō");

        Assert.Equal(Saved, artwork.CharacterImage);
        Assert.Equal(h.Page.DiscordReference, artwork.CharacterImage);
        Assert.Equal(h.Page.DiscordImagePath, h.Provider.Match(artwork.CharacterImage)?.Location?.Path);
    }

    [Fact]
    public void LinkInUse_DiscordGetsTheLink()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Page.LinkText = "https://example.org/my-onryo.png";
        h.Page.AddLinkCommand.Execute(null);

        var artwork = Discord(h, "The Onryō");

        Assert.Equal("https://example.org/my-onryo.png", artwork.CharacterImage);
        Assert.Equal(h.Page.DiscordReference, artwork.CharacterImage);
    }

    [Fact]
    public void WithNoPack_RoleIconsAndAppLogoSurvive_AndNoCharacterImageIsInvented()
    {
        using var h = new CharactersHarness();

        var artwork = Discord(h, "The Onryō");

        Assert.Null(artwork.CharacterImage);
        Assert.Equal("role_killer", artwork.RoleKiller);
        Assert.Equal("role_survivor", artwork.RoleSurvivor);
        Assert.Equal("app_logo", artwork.AppLogo);
        Assert.Contains("app_logo", artwork.ConfirmedUploadedKeys);
    }

    [Fact]
    public void UnknownName_FallsBackToTheSettingsPackChoices()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");

        var artwork = Discord(h, "Nobody In The Roster");

        Assert.Null(artwork.CharacterImage);
    }

    [Fact]
    public void ResolverFindsByNameAndSlug_IgnoringAccentsAndCase()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");

        foreach (var name in new[] { "The Onryō", "the onryo", "THE-ONRYO" })
        {
            Assert.NotNull(h.Resolver.Resolve(h.Current, name));
        }

        Assert.Null(h.Resolver.Resolve(h.Current, "  "));
        Assert.Null(h.Resolver.Resolve(h.Current, null));
        Assert.Null(h.Resolver.Resolve(h.Current, "???"));
    }
}
