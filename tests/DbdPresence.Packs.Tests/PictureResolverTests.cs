// Covers: security review L6 and QA F6 (declined rules keep every own picture preview-only; the page can tell when the two sides differ without a reason), FR-PACK-11 (default pack below the Characters pack, above the fallback pack), FR-PACK-13 (one case per resolution step, withdrawn and unsynced
// entries skipped, Behaviour label from the source pack, an unsaved picture falls to the next step and nothing is queued), FR-PACK-06 and D29 (only
// https addresses of synced packs reach Discord; imported packs are preview-only), FR-ART-04 and D16 (role icon / no picture when nothing applies).
using DbdPresence.Core.Game;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Packs.Tests;

public sealed class PictureResolverTests
{
    private const string Key = "character:the-onryo";
    private static readonly DateTimeOffset s_when = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly string s_mine = new('a', 64);
    private static readonly string s_other = new('b', 64);

    private static PicturePack Owner(string id, bool isDefault = false, bool fallback = false, bool behaviour = false) =>
        new(id, id + " pack", isDefault, fallback, behaviour, false);

    private static PicturePack Imported(string id) => new(id, id + " pack", false, false, false, true);

    private static PicturePiece Piece(string pack, string key, string? sha = null, string? source = null, bool url = true, bool local = true) =>
        new(pack, $"{pack}-{key.Replace(':', '-')}", key, sha ?? new string('f', 64), source, url ? $"https://cdn.example/{pack}/{key.Replace(':', '-')}.png" : null, local ? $"/cache/{pack}/{key.Replace(':', '-')}.png" : null);

    private static PictureItem File(string sha, string? source = null) => new(sha, source, 1000, sha + ".png", null, PictureOrigin.Pc, s_when);

    private static PictureContext Context(
        IReadOnlyList<PicturePack> packs,
        IReadOnlyList<PicturePiece> pieces,
        string? characters = null,
        Func<PictureItem, PictureOnline>? online = null,
        Func<PictureItem, string?>? path = null,
        PictureRules rules = PictureRules.Unset) =>
        new(packs, pieces, characters, path ?? (i => i.File is null ? null : "/pictures/" + i.File), online ?? (_ => new PictureOnline(PictureOnlineStatus.Saving)), rules);

    private static PictureResolution Resolve(PictureContext context, CharacterPictures? pictures = null, Role role = Role.Killer) =>
        PictureResolver.Resolve(Key, role, pictures, context);

    [Fact]
    public void DefaultPortrait_IsTheBaseLayer_ForPreviewAndDiscord()
    {
        var context = Context([Owner("default", isDefault: true)], [Piece("default", Key)]);

        var result = Resolve(context);

        Assert.Equal(PictureSource.DefaultPack, result.Preview.Source);
        Assert.Equal("/cache/default/character-the-onryo.png", result.Preview.LocalPath);
        Assert.Equal(DiscordSource.DefaultPack, result.Discord.Source);
        Assert.Equal("https://cdn.example/default/character-the-onryo.png", result.Discord.Reference);
        Assert.False(result.DiffersFromPreview);
    }

    [Fact]
    public void Order_CharactersPackBeatsDefaultPackBeatsFallbackPack()
    {
        var packs = new[] { Owner("fallback", fallback: true), Owner("default", isDefault: true), Owner("mine") };
        var all = new[] { Piece("fallback", Key), Piece("default", Key), Piece("mine", Key) };

        Assert.Equal("mine", Resolve(Context(packs, all, characters: "mine")).Preview.PackId);
        Assert.Equal(DiscordSource.CharactersPack, Resolve(Context(packs, all, characters: "mine")).Discord.Source);
        Assert.Equal("default", Resolve(Context(packs, all)).Preview.PackId);
        Assert.Equal(DiscordSource.DefaultPack, Resolve(Context(packs, [all[0], all[1]])).Discord.Source);
        var fallbackOnly = Resolve(Context(packs, [all[0]]));
        Assert.Equal(PictureSource.FallbackPack, fallbackOnly.Preview.Source);
        Assert.Equal(DiscordSource.FallbackPack, fallbackOnly.Discord.Source);
    }

    [Fact]
    public void OnlyTheFirstDefaultPackCounts()
    {
        var packs = new[] { Owner("first", isDefault: true), Owner("second", isDefault: true) };

        var result = Resolve(Context(packs, [Piece("first", Key), Piece("second", Key)]));

        Assert.Equal("first", result.Preview.PackId);
    }

    [Fact]
    public void ImportedCharactersPack_IsPreviewOnly_DiscordFallsToTheDefaultPack()
    {
        var context = Context([Imported("fan"), Owner("default", isDefault: true)], [Piece("fan", Key, url: false), Piece("default", Key)], characters: "fan");

        var result = Resolve(context);

        Assert.Equal(PictureSource.CharactersPack, result.Preview.Source);
        Assert.True(result.Preview.LocalOnly);
        Assert.Equal(DiscordSource.DefaultPack, result.Discord.Source);
        Assert.True(result.DiffersFromPreview);
    }

    [Fact]
    public void ImportedPack_NeverReachesDiscord_EvenIfAnAddressIsGivenToTheResolver()
    {
        var context = Context([Imported("fan")], [Piece("fan", Key)], characters: "fan");

        Assert.Null(Resolve(context).Discord.Reference);
    }

    [Fact]
    public void OwnPictureSavedOnline_ShowsInThePreview_AndDiscordGetsItsUnlistedAddress()
    {
        var item = File(s_mine);
        var context = Context([Owner("default", isDefault: true)], [Piece("default", Key)], online: _ => new PictureOnline(PictureOnlineStatus.Saved, "https://cdn.example/u/0123456789abcdef0123456789abcdef.png"));

        var result = Resolve(context, new CharacterPictures([item], PictureUse.Personal(s_mine)));

        Assert.Equal(PictureSource.OwnFile, result.Preview.Source);
        Assert.Equal("/pictures/" + s_mine + ".png", result.Preview.LocalPath);
        Assert.Equal(DiscordSource.SavedOnline, result.Discord.Source);
        Assert.Equal("https://cdn.example/u/0123456789abcdef0123456789abcdef.png", result.Discord.Reference);
        Assert.Null(result.Discord.WhyNotChosen);
        Assert.False(result.DiffersFromPreview);
    }

    [Theory]
    [InlineData(PictureOnlineStatus.Saving)]
    [InlineData(PictureOnlineStatus.SignedOut)]
    [InlineData(PictureOnlineStatus.RulesNotAccepted)]
    [InlineData(PictureOnlineStatus.Blocked)]
    [InlineData(PictureOnlineStatus.Paused)]
    [InlineData(PictureOnlineStatus.StorageFull)]
    [InlineData(PictureOnlineStatus.ServiceUnreachable)]
    [InlineData(PictureOnlineStatus.Failed)]
    [InlineData(PictureOnlineStatus.DailyLimit)]
    public void UnsavedOwnPicture_FallsToTheDefaultPortrait_WithTheReason(PictureOnlineStatus status)
    {
        var context = Context([Owner("default", isDefault: true)], [Piece("default", Key)], online: _ => new PictureOnline(status));

        var result = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.Equal(PictureSource.OwnFile, result.Preview.Source);
        Assert.Equal(DiscordSource.DefaultPack, result.Discord.Source);
        Assert.Equal(status, result.Discord.WhyNotChosen);
        Assert.True(result.DiffersFromPreview);
    }

    [Fact]
    public void UnsavedOwnPicture_WithNoDefaultPortrait_GivesDiscordNothing()
    {
        var result = Resolve(Context([], []), new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.Null(result.Discord.Reference);
        Assert.Equal(DiscordSource.None, result.Discord.Source);
        Assert.Equal(PictureOnlineStatus.Saving, result.Discord.WhyNotChosen);
    }

    [Fact]
    public void OwnerCopy_MatchesOnSha256_AndOnSourceSha256_AndBeatsLinkAndSavedCopy()
    {
        var packs = new[] { Owner("default", isDefault: true, behaviour: true) };
        var byHash = Piece("default", "character:the-trapper", sha: s_mine);
        var bySource = Piece("default", "character:the-nurse", sha: s_other, source: new string('c', 64));
        var online = new Func<PictureItem, PictureOnline>(_ => new PictureOnline(PictureOnlineStatus.Saved, "https://cdn.example/u/0123456789abcdef0123456789abcdef.png"));
        var context = Context(packs, [Piece("default", Key), byHash, bySource], online: online);

        var viaSha = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));
        var viaSource = Resolve(context, new CharacterPictures([File(new string('9', 64), source: new string('c', 64))], PictureUse.Personal(new string('9', 64))));
        var viaItemSource = Resolve(context, new CharacterPictures([File(new string('8', 64), source: s_mine)], PictureUse.Personal(new string('8', 64))));

        Assert.Equal(DiscordSource.OwnerCopy, viaSha.Discord.Source);
        Assert.Equal(byHash.Url, viaSha.Discord.Reference);
        Assert.Equal(DiscordSource.OwnerCopy, viaSource.Discord.Source);
        Assert.Equal(bySource.Url, viaSource.Discord.Reference);
        Assert.Equal(DiscordSource.OwnerCopy, viaItemSource.Discord.Source);
        Assert.True(viaSha.Discord.BehaviourArt);
        Assert.Null(viaSha.Discord.WhyNotChosen);
    }

    [Fact]
    public void OwnerCopy_IgnoresImportedPacks_AndEntriesWithoutAnAddress()
    {
        var packs = new[] { Imported("fan"), Owner("default", isDefault: true) };
        var context = Context(packs, [Piece("fan", "character:the-trapper", sha: s_mine), Piece("default", "character:the-nurse", sha: s_mine, url: false)]);

        var result = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.NotEqual(DiscordSource.OwnerCopy, result.Discord.Source);
    }

    [Fact]
    public void Link_GoesToDiscordAsIs_AndTheLinkTileIsNotAFile()
    {
        var link = PictureSlots.LinkItem("https://example.com/a.png", s_when)!;

        var result = Resolve(Context([], []), new CharacterPictures([link], PictureUse.Personal(link.Sha256)));

        Assert.Equal(PictureSource.OwnLink, result.Preview.Source);
        Assert.Null(result.Preview.LocalPath);
        Assert.Equal("https://example.com/a.png", result.Preview.Link);
        Assert.Equal(DiscordSource.Link, result.Discord.Source);
        Assert.Equal("https://example.com/a.png", result.Discord.Reference);
        Assert.False(result.DiffersFromPreview);
    }

    [Fact]
    public void Link_ThatBreaksThePolicyNow_IsNeverSent()
    {
        var bad = new PictureItem(s_mine, null, 0, null, "http://example.com/a.png", PictureOrigin.Link, s_when);

        var result = Resolve(Context([], []), new CharacterPictures([bad], PictureUse.Personal(s_mine)));

        Assert.Null(result.Discord.Reference);
        Assert.Equal(PictureOnlineStatus.Unusable, result.Discord.WhyNotChosen);
    }

    [Fact]
    public void OwnerPictureInUse_GoesToDiscordFirst()
    {
        var packs = new[] { Owner("default", isDefault: true), Owner("hollow", behaviour: true) };
        var chosen = Piece("hollow", Key);
        var context = Context(packs, [Piece("default", Key), chosen]);

        var result = Resolve(context, new CharacterPictures([], PictureUse.Owner("hollow", chosen.EntryId)));

        Assert.Equal(PictureSource.ChosenOwnerPack, result.Preview.Source);
        Assert.Equal(DiscordSource.ChosenOwnerPack, result.Discord.Source);
        Assert.Equal(chosen.Url, result.Discord.Reference);
        Assert.True(result.Discord.BehaviourArt);
    }

    [Fact]
    public void OwnerPictureInUse_WithdrawnOrNotSynced_FallsBackToTheBaseLayer()
    {
        var packs = new[] { Owner("default", isDefault: true), Owner("hollow") };
        var context = Context(packs, [Piece("default", Key)]);

        var result = Resolve(context, new CharacterPictures([], PictureUse.Owner("hollow", "gone")));

        Assert.Equal(PictureSource.DefaultPack, result.Preview.Source);
        Assert.Equal(DiscordSource.DefaultPack, result.Discord.Source);
    }

    [Fact]
    public void EntryWithoutAnAddress_IsSkippedForDiscord_EntryWithoutAFile_IsSkippedForThePreview()
    {
        var packs = new[] { Owner("default", isDefault: true), Owner("fallback", fallback: true) };
        var context = Context(packs, [Piece("default", Key, url: false), Piece("fallback", Key, local: false)]);

        var result = Resolve(context);

        Assert.Equal("default", result.Preview.PackId);
        Assert.Equal(DiscordSource.FallbackPack, result.Discord.Source);
        Assert.Equal("https://cdn.example/fallback/character-the-onryo.png", result.Discord.Reference);
    }

    [Fact]
    public void MissingFile_ShowsMissing_AndDiscordSaysWhy()
    {
        var context = Context([], [], path: _ => null);

        var result = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.Equal(PictureSource.Missing, result.Preview.Source);
        Assert.Equal(PictureOnlineStatus.Unusable, result.Discord.WhyNotChosen);
    }

    [Fact]
    public void NoPicture_FallsToTheGenericTile_ThenTheRoleIcon_ThenNothing()
    {
        var packs = new[] { Owner("fallback", fallback: true) };
        var generic = Piece("fallback", "character:generic-killer");
        var roleIcon = Piece("fallback", "role:killer");

        var withGeneric = Resolve(Context(packs, [generic, roleIcon]));
        var withRoleOnly = Resolve(Context(packs, [roleIcon]));
        var nothing = Resolve(Context(packs, []));
        var survivor = Resolve(Context(packs, [generic]), role: Role.Survivor);

        Assert.Equal(PictureSource.GenericTile, withGeneric.Preview.Source);
        Assert.Equal(DiscordSource.GenericTile, withGeneric.Discord.Source);
        Assert.Equal(PictureSource.RoleIcon, withRoleOnly.Preview.Source);
        Assert.Null(withRoleOnly.Discord.Reference);
        Assert.Equal(PictureSource.None, nothing.Preview.Source);
        Assert.Equal(DiscordSource.None, nothing.Discord.Source);
        Assert.Equal(PictureSource.None, survivor.Preview.Source);
    }

    [Fact]
    public void NewCharacterWithNoEntries_ShowsTheRoleIcon_AndDiscordKeepsItsRoleArtwork()
    {
        var packs = new[] { Owner("default", isDefault: true) };
        var context = Context(packs, [Piece("default", "role:survivor"), Piece("default", "character:the-trapper")]);

        var result = PictureResolver.Resolve("character:brand-new", Role.Survivor, null, context);

        Assert.Equal(PictureSource.RoleIcon, result.Preview.Source);
        Assert.Null(result.Discord.Reference);
        Assert.Equal(DiscordSource.None, result.Discord.Source);
    }

    [Fact]
    public void BehaviourLabel_ComesFromTheSourcePack()
    {
        var packs = new[] { Owner("default", isDefault: true, behaviour: true), Owner("fallback", fallback: true) };

        Assert.True(Resolve(Context(packs, [Piece("default", Key)])).Discord.BehaviourArt);
        Assert.False(Resolve(Context(packs, [Piece("fallback", Key)])).Discord.BehaviourArt);
    }

    [Fact]
    public void Resolution_CallsOnlyTheOnlineLookup_NeverStartsAnything()
    {
        var asked = new List<string>();
        var context = Context([], [], online: item =>
        {
            asked.Add(item.Sha256);
            return new PictureOnline(PictureOnlineStatus.Saving);
        });

        Resolve(context, new CharacterPictures([File(s_mine), File(s_other)], PictureUse.Personal(s_mine)));

        Assert.Equal([s_mine], asked);
    }

    // ---- L6: declined rules ------------------------------------------------------------------------------------------

    [Fact]
    public void DeclinedRules_KeepALinkOutOfDiscord_AndTheDefaultPortraitTakesOver()
    {
        var link = PictureSlots.LinkItem("https://example.com/a.png", s_when)!;
        var context = Context([Owner("default", isDefault: true)], [Piece("default", Key)], rules: PictureRules.Declined);

        var result = Resolve(context, new CharacterPictures([link], PictureUse.Personal(link.Sha256)));

        Assert.Equal(PictureSource.OwnLink, result.Preview.Source);
        Assert.Equal(DiscordSource.DefaultPack, result.Discord.Source);
        Assert.Equal(PictureOnlineStatus.RulesNotAccepted, result.Discord.WhyNotChosen);
        Assert.True(result.DiffersFromPreview);
    }

    [Fact]
    public void DeclinedRules_AlsoKeepAnOwnerCopyAndASavedCopyOut()
    {
        var packs = new[] { Owner("default", isDefault: true) };
        var copy = Piece("default", "character:the-trapper", sha: s_mine);
        var context = Context(packs, [copy], online: _ => new PictureOnline(PictureOnlineStatus.Saved, "https://cdn.example/u/0123456789abcdef0123456789abcdef.png"), rules: PictureRules.Declined);

        var result = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.Null(result.Discord.Reference);
        Assert.Equal(PictureOnlineStatus.RulesNotAccepted, result.Discord.WhyNotChosen);
    }

    [Theory]
    [InlineData(PictureRules.Unset)]
    [InlineData(PictureRules.Accepted)]
    public void RulesNotDeclined_LetALinkThrough(PictureRules rules)
    {
        var link = PictureSlots.LinkItem("https://example.com/a.png", s_when)!;

        var result = Resolve(Context([], [], rules: rules), new CharacterPictures([link], PictureUse.Personal(link.Sha256)));

        Assert.Equal(DiscordSource.Link, result.Discord.Source);
    }

    [Fact]
    public void DeclinedRules_DoNotTouchAnOwnerPictureInUse()
    {
        var packs = new[] { Owner("hollow") };
        var chosen = Piece("hollow", Key);

        var result = Resolve(Context(packs, [chosen], rules: PictureRules.Declined), new CharacterPictures([], PictureUse.Owner("hollow", chosen.EntryId)));

        Assert.Equal(DiscordSource.ChosenOwnerPack, result.Discord.Source);
    }

    // ---- F6: the two sides part ways without a reason ----------------------------------------------------------------

    [Fact]
    public void MissingFile_WithAnOwnerCopy_PreviewSaysMissing_DiscordGetsTheCopy_AndTheyDiffer()
    {
        var packs = new[] { Owner("default", isDefault: true) };
        var context = Context(packs, [Piece("default", "character:the-trapper", sha: s_mine)], path: _ => null);

        var result = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.Equal(PictureSource.Missing, result.Preview.Source);
        Assert.Equal(DiscordSource.OwnerCopy, result.Discord.Source);
        Assert.Null(result.Discord.WhyNotChosen);
        Assert.True(result.DiffersFromPreview);
    }

    [Fact]
    public void OwnerEntryWithAnAddressButNoCachedFile_PreviewFallsDown_DiscordGetsTheEntry_AndTheyDiffer()
    {
        var packs = new[] { Owner("default", isDefault: true), Owner("hollow") };
        var chosen = Piece("hollow", Key, local: false);
        var context = Context(packs, [Piece("default", Key), chosen]);

        var result = Resolve(context, new CharacterPictures([], PictureUse.Owner("hollow", chosen.EntryId)));

        Assert.Equal(PictureSource.DefaultPack, result.Preview.Source);
        Assert.Equal(DiscordSource.ChosenOwnerPack, result.Discord.Source);
        Assert.True(result.DiffersFromPreview);
    }

    [Fact]
    public void WhenBothSidesShowTheSamePictures_NothingDiffers()
    {
        var packs = new[] { Owner("default", isDefault: true) };
        var online = new Func<PictureItem, PictureOnline>(_ => new PictureOnline(PictureOnlineStatus.Saved, "https://cdn.example/u/0123456789abcdef0123456789abcdef.png"));

        Assert.False(Resolve(Context(packs, [Piece("default", Key)])).DiffersFromPreview);
        Assert.False(Resolve(Context(packs, [], online: online), new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine))).DiffersFromPreview);
        Assert.False(Resolve(Context(packs, [])).DiffersFromPreview);
    }

    [Fact]
    public void ASavedOnlineAddress_ThatIsNotHttps_IsNeverSent()
    {
        // Review F10, mutant M10.
        var context = Context([Owner("default", isDefault: true)], [Piece("default", Key)], online: _ => new PictureOnline(PictureOnlineStatus.Saved, "http://cdn.example/u/0123456789abcdef0123456789abcdef.png"));

        var result = Resolve(context, new CharacterPictures([File(s_mine)], PictureUse.Personal(s_mine)));

        Assert.NotEqual(DiscordSource.SavedOnline, result.Discord.Source);
        Assert.Equal(DiscordSource.DefaultPack, result.Discord.Source);
        Assert.Equal(PictureOnlineStatus.Saving, result.Discord.WhyNotChosen);
    }
}
