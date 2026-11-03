// Covers: the Characters page contract (docs/design/characters-page.md sections 5-9), FR-PACK-12 (3 own pictures per character, default portrait never
// removable, 2 MB rule), FR-PACK-13 (preview vs Discord, reason shown), FR-PACK-14 (links validated as typed, never fetched), FR-PACK-07 (import by code,
// T_UI_ and _Portrait, review before saving, conflicts, full characters, undo), FR-PACK-17 (size estimate and storage text), FR-PACK-16 (migration
// notice), FR-PACK-10 (untrusted images only through the helper interfaces), D28, O-1..O-3. S5b-5.
using DbdPresence.Configuration;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Pictures;
using DbdPresence.Presentation.Characters;
using DbdPresence.Presentation.Tests.Support;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class CharactersViewModelTests
{
    private static async Task AddFromPc(CharactersHarness h, string path = "/in/me.png")
    {
        h.Storage.Files[path] = [1, 2, 3, (byte)path.Length];
        h.Picker.Image = path;
        await h.Page.AddFromPcCommand.ExecuteAsync(null);
    }

    private static async Task<ImportReviewViewModel> OpenReview(CharactersHarness h, params (string Name, string Marker)[] files)
    {
        h.Storage.Files["/in/pack.zip"] = PackFixtures.Zip([.. files.Select(f => (f.Name, PackFixtures.Png(f.Marker)))]);
        h.Picker.Archive = "/in/pack.zip";
        await h.Page.ImportPackCommand.ExecuteAsync(null);
        return h.Page.ImportReview ?? throw new InvalidOperationException("no review");
    }

    // ---- list, filter, search ----

    [Fact]
    public void List_ShowsEveryRosterCharacter_KillersFirst_WithCountsInTheFilters()
    {
        using var h = new CharactersHarness();

        Assert.Equal(98, h.Page.Tiles.Count);
        Assert.All(h.Page.Tiles.Take(44), t => Assert.Equal("Killers", t.Group));
        Assert.All(h.Page.Tiles.Skip(44), t => Assert.Equal("Survivors", t.Group));
        Assert.Equal("All 98", h.Page.AllFilterText);
        Assert.Equal("Killers 44", h.Page.KillersFilterText);
        Assert.Equal("Survivors 54", h.Page.SurvivorsFilterText);
        Assert.Null(h.Page.ResultCountText);
        Assert.Equal("The Animatronic", h.Page.SelectedTile!.Name);
    }

    [Fact]
    public void Filter_KillersAndSurvivors_AndTheResultCountIsAnnounced()
    {
        using var h = new CharactersHarness();

        h.Page.Filter = CharacterFilter.Survivors;
        Assert.Equal(54, h.Page.Tiles.Count);
        Assert.Equal("Showing 54 of 98 characters", h.Page.ResultCountText);
        h.Page.Filter = CharacterFilter.Killers;
        Assert.Equal(44, h.Page.Tiles.Count);
        h.Page.Filter = CharacterFilter.All;
        Assert.Equal(98, h.Page.Tiles.Count);
        Assert.Null(h.Page.ResultCountText);
    }

    [Theory]
    [InlineData("onryo", "K27")]
    [InlineData("ONRYŌ", "K27")]
    [InlineData("k27", "K27")]
    [InlineData("the-onryo", "K27")]
    [InlineData("  trapper ", "K01")]
    public void Search_FindsByNameCodeOrId_IgnoringCaseAndAccents(string text, string code)
    {
        using var h = new CharactersHarness();

        h.Page.SearchText = text;

        Assert.Contains(h.Page.Tiles, t => t.Code == code);
        Assert.True(h.Page.Tiles.Count < 98);
    }

    [Fact]
    public void Search_WithNoMatch_ShowsTheSentence_AndClearSearchRestoresTheList()
    {
        using var h = new CharactersHarness();

        h.Page.SearchText = "zzzz";
        Assert.Empty(h.Page.Tiles);
        Assert.Equal("No characters match “zzzz”.", h.Page.NoResultsText);
        h.Page.ClearSearchCommand.Execute(null);

        Assert.Equal(98, h.Page.Tiles.Count);
        Assert.Null(h.Page.NoResultsText);
    }

    [Fact]
    public async Task ChangedByMe_ListsOnlyCharactersWithAPicture_AndSaysSoWhenNone()
    {
        using var h = new CharactersHarness();
        h.Page.ChangedByMeOnly = true;
        Assert.Equal("You haven't changed any characters yet.", h.Page.NoResultsText);
        h.Page.ChangedByMeOnly = false;
        h.Select("K27");
        await AddFromPc(h);

        h.Page.ChangedByMeOnly = true;

        Assert.Equal(["K27"], h.Page.Tiles.Select(t => t.Code));
    }

    // ---- tiles: source, fallbacks, unknown characters ----

    [Fact]
    public void Tiles_BeforeAnyPortrait_SayNotDownloaded_WhenSignedOut_AndNoPictureWhenSignedIn()
    {
        using var h = new CharactersHarness();
        Assert.Equal("Not downloaded", h.Tile("K27").SourceCaption);
        Assert.True(h.Tile("K27").ShowsRoleFallback);

        h.Environment.SignedIn = true;
        h.Environment.Raise();

        Assert.Equal("No picture", h.Tile("K27").SourceCaption);
        Assert.Equal(CharactersStatusKind.Ready, h.Page.StatusKind);
    }

    [Fact]
    public void Tiles_ShowTheDefaultPortrait_WhenThePackHasOne_AndTheRoleIconOtherwise()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");

        var onryo = h.Tile("K27");
        var trapper = h.Tile("K01");

        Assert.Equal("Default", onryo.SourceCaption);
        Assert.False(onryo.ShowsRoleFallback);
        Assert.StartsWith("/cache/", onryo.PreviewPath, StringComparison.Ordinal);
        Assert.True(trapper.ShowsRoleFallback);
        Assert.StartsWith("/cache/", trapper.PreviewPath, StringComparison.Ordinal);
        Assert.Equal("No picture", trapper.SourceCaption);
        Assert.Contains("On Discord", onryo.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void NewCharacterFromTheRoster_AppearsWithTheRoleIconFallback_AndTheNewBadge_UntilDismissed()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        var extended = Packs.Roster.CharacterRoster.Create(
            [.. PackFixtures.Roster.Characters, new Packs.Roster.RosterCharacter("K99", "the-test-killer", Core.Game.Role.Killer, "The Test Killer", [])]);

        h.Page.SetRoster(extended, new HashSet<string> { "K99" });

        var ghoul = h.Tile("K99");
        Assert.Equal(99, h.Page.Tiles.Count);
        Assert.True(ghoul.IsNew);
        Assert.True(ghoul.ShowsRoleFallback);
        Assert.Equal("No picture", ghoul.SourceCaption);
        Assert.StartsWith("/cache/", ghoul.PreviewPath, StringComparison.Ordinal);
        Assert.Contains(", new", ghoul.AutomationName, StringComparison.Ordinal);
        Assert.Equal("New in the list: The Test Killer.", h.Page.NewCharactersText);
        h.Page.DismissNewCommand.Execute(null);
        Assert.Null(h.Page.NewCharactersText);
        h.Page.SelectByCode("K99");
        Assert.Equal("The Test Killer", h.Page.SelectedTile!.Name);
        Assert.Equal("No default portrait yet. The default pack adds one automatically when it's available; until then Discord shows no picture for this character.", h.Page.NoDefaultText);
    }

    // ---- adding, the 3-picture cap, the 2 MB rule ----

    [Fact]
    public async Task AddFromPc_SavesTheHelpersPicture_UsesIt_AndReportsItForSavingOnline()
    {
        using var h = new CharactersHarness();
        h.AllowSavingOnline();
        h.Select("K27");

        await AddFromPc(h);

        var pictures = h.Current.Pictures["character:the-onryo"];
        var item = Assert.Single(pictures.Items);
        Assert.Equal(PictureOrigin.Pc, item.Origin);
        Assert.Equal(PictureUse.Personal(item.Sha256), pictures.Use);
        Assert.Equal([512], h.Fitter.Edges);
        Assert.NotNull(h.Store.PathOf(item));
        Assert.Equal("From my PC", h.Tile("K27").SourceCaption);
        Assert.Equal("Your pictures: 1 of 3", h.Page.CounterText);
        Assert.Equal([new PictureUploadRequest("character:the-onryo", item.Sha256, item.Bytes, true)], h.Sink.Saved);
        Assert.Contains("now uses From my PC", h.Page.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(CharacterPanelMode.Character, h.Page.PanelMode);
    }

    [Fact]
    public async Task AddFromPc_FourthPicture_IsRefused_ButTheCommandNeverEvenOpensTheDialog()
    {
        using var h = new CharactersHarness();
        h.AllowSavingOnline();
        h.Select("K27");
        for (var i = 0; i < 3; i++)
        {
            await AddFromPc(h, $"/in/pic{i}-{new string('x', i)}.png");
        }

        Assert.Equal(3, h.Current.Pictures["character:the-onryo"].Items.Count);
        Assert.False(h.Page.CanAddPicture);
        Assert.Equal("This character has 3 of your pictures. Remove one to add another.", h.Page.FullText);
        h.Picker.Image = "/in/fourth.png";
        await h.Page.AddFromPcCommand.ExecuteAsync(null);
        h.Page.LinkText = "https://example.com/four.png";
        h.Page.AddLinkCommand.Execute(null);

        Assert.Equal(3, h.Current.Pictures["character:the-onryo"].Items.Count);
        Assert.Equal("This character has 3 of your pictures. Remove one to add another.", h.Page.ErrorMessage);
        Assert.Equal(3, h.Sink.Saved.Count);
    }

    [Fact]
    public async Task AddFromPc_At1024_RedoesAt512_WhenTheFileIs2MbOrMore_AndSaysSo()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Host.Settings.Update(s => s with { PictureSize = 1024 });
        h.Fitter.Override = (edge, seed) => FakePng.Make(edge, edge, edge == 1024 ? (int)PictureLimits.MaxFileBytes : 90_000, seed);

        await AddFromPc(h);

        Assert.Equal([1024, 512], h.Fitter.Edges);
        Assert.Equal(90_000, h.Current.Pictures["character:the-onryo"].Items.Single().Bytes);
        Assert.Contains("512", h.Page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFromPc_ThatCannotGetUnder2Mb_IsRefusedAndNothingIsSaved()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Fitter.Override = (edge, seed) => FakePng.Make(edge, edge, (int)PictureLimits.MaxFileBytes, seed);

        await AddFromPc(h);

        Assert.False(h.Current.Pictures.ContainsKey("character:the-onryo"));
        Assert.Contains("2 MB", h.Page.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Sink.Saved);
        Assert.DoesNotContain(h.Storage.Files.Keys, k => k.Contains("/pictures/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddFromPc_HelperFailure_ShowsTheMessage()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Fitter.Override = (_, _) => null;

        await AddFromPc(h);

        Assert.Contains("could not be opened", h.Page.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Current.Pictures);
    }

    // ---- links ----

    [Theory]
    [InlineData("http://example.com/a.png", "Use a link that starts with https://.")]
    [InlineData("https://user:pw@example.com/a.png", "Links can't contain a user name or password.")]
    [InlineData("https://127.0.0.1/a.png", "Use a site name, not an IP address.")]
    public void Link_IsCheckedAsTheUserTypes_AndRefusedWhenAdded(string link, string error)
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Page.ShowAddLinkCommand.Execute(null);

        h.Page.LinkText = link;
        Assert.Equal(error, h.Page.LinkError);
        h.Page.AddLinkCommand.Execute(null);

        Assert.Empty(h.Current.Pictures);
        Assert.Equal(error, h.Page.ErrorMessage);
        Assert.Equal(CharacterPanelMode.AddLink, h.Page.PanelMode);
    }

    [Fact]
    public void Link_Valid_IsAddedAndInUse_NothingIsReportedForSaving_AndTheTileSaysMyLink()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Page.ShowAddLinkCommand.Execute(null);
        h.Page.LinkText = "https://example.com/a.png";
        Assert.Null(h.Page.LinkError);

        h.Page.AddLinkCommand.Execute(null);

        Assert.Equal("My link", h.Tile("K27").SourceCaption);
        Assert.Equal("https://example.com/a.png", h.Page.PreviewLink);
        Assert.Null(h.Page.PreviewImagePath);
        Assert.Equal("https://example.com/a.png", h.Page.DiscordReference);
        Assert.Empty(h.Sink.Saved);
        Assert.True(h.Page.PreviewAndDiscordSame);
        Assert.Equal("Discord loads it from your link", h.Page.PictureOptions.Single(o => o.IsInUse).DiscordLine);
        Assert.True(h.Page.PictureOptions.Single(o => o.IsInUse).IsLink);
    }

    // ---- preview vs Discord, reasons ----

    [Fact]
    public async Task OwnPicture_WhileSignedOut_ShowsTwoPictures_AndTheReason()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(true, "character:the-onryo");
        h.Select("K27");

        await AddFromPc(h);

        Assert.False(h.Page.PreviewAndDiscordSame);
        Assert.True(h.Tile("K27").IsPreviewOnly);
        Assert.Contains("/pictures/", h.Page.PreviewImagePath?.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.StartsWith("/cache/", h.Page.DiscordImagePath, StringComparison.Ordinal);
        Assert.Equal("Discord shows the default portrait while you're signed out. Your picture is saved online when you sign in.", h.Page.DifferenceText);
        Assert.Equal("Preview only · sign in to use it", h.Page.PictureOptions.Single(o => o.IsInUse).DiscordLine);
        Assert.True(h.Page.DiscordPictureIsBehaviourArt);
        Assert.Equal(CharactersStatusKind.SignedOut, h.Page.StatusKind);
    }

    [Fact]
    public async Task OwnPicture_SavedOnline_ShowsOnePicture_NoPreviewOnlyMark()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        h.Environment.SignedIn = true;
        h.Select("K27");
        await AddFromPc(h);
        var sha = h.Current.Pictures["character:the-onryo"].Items.Single().Sha256;
        h.Library.SetUploads(new Dictionary<string, string> { [sha] = "https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png" }, "cdn.example.org");
        h.Environment.SavedUrlBySha256 = new Dictionary<string, string> { [sha] = "https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png" };
        h.Environment.Raise();

        Assert.True(h.Page.PreviewAndDiscordSame);
        Assert.False(h.Tile("K27").IsPreviewOnly);
        Assert.Null(h.Page.DifferenceText);
        Assert.Equal("https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png", h.Page.DiscordReference);
        Assert.Equal("/data/packs/pictures/" + sha + ".png", h.Page.DiscordImagePath?.Replace('\\', '/'));
        Assert.Equal("On Discord · saved online, unlisted", h.Page.PictureOptions.Single(o => o.IsInUse).DiscordLine);
    }

    [Fact]
    public async Task OwnPicture_SignedInButRulesUnanswered_ShowsTheRulesPanel_AcceptReportsPictures()
    {
        using var h = new CharactersHarness();
        h.Environment.SignedIn = true;
        h.Select("K27");
        await AddFromPc(h);
        h.Sink.Saved.Clear();

        Assert.True(h.Page.ShowRulesPanel);
        Assert.Contains("1 of your pictures on this PC", h.Page.RulesCountText, StringComparison.Ordinal);
        Assert.Equal("Preview only · accept the picture rules", h.Page.PictureOptions.Single(o => o.IsInUse).DiscordLine);
        h.Page.AcceptRulesCommand.Execute(null);

        Assert.False(h.Page.ShowRulesPanel);
        Assert.Equal(PictureRules.Accepted, h.Current.PictureRules);
        Assert.Single(h.Sink.Saved);
        Assert.Equal("Saving online…", h.Page.PictureOptions.Single(o => o.IsInUse).DiscordLine);
    }

    [Fact]
    public async Task DecliningTheRules_KeepsPicturesInThePreview_AndShowsTheStatus()
    {
        using var h = new CharactersHarness();
        h.Environment.SignedIn = true;
        h.Select("K27");
        await AddFromPc(h);

        h.Page.DeclineRulesCommand.Execute(null);

        Assert.Equal(PictureRules.Declined, h.Current.PictureRules);
        Assert.Equal(CharactersStatusKind.RulesDeclined, h.Page.StatusKind);
        Assert.False(h.Page.ShowRulesPanel);
        Assert.True(h.Page.RulesDeclined);
        h.Page.ReviewRulesCommand.Execute(null);
        Assert.Equal(PictureRules.Unset, h.Current.PictureRules);
    }

    [Fact]
    public void OwnerCopy_OfAnOwnPicture_IsShownAsTheSameAsTheOwnersCopy()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        h.Select("K27");
        var owner = h.Library.Synced!.Index.Entries.Single(e => e.Key == "character:the-onryo");
        var item = new PictureItem(owner.Sha256, null, 1000, owner.Sha256 + ".png", null, PictureOrigin.Pc, h.Host.Time.GetUtcNow());
        h.Storage.Files[$"{CharactersHarness.Root}/pictures/{item.File}"] = FakePng.Make(8, 8, 100);
        h.Host.Settings.Update(s => s with { Pictures = new Dictionary<string, CharacterPictures> { ["character:the-onryo"] = new([item], PictureUse.Personal(item.Sha256)) } });

        var option = h.Page.PictureOptions.Single(o => o.IsInUse);

        Assert.Equal("On Discord (same as the owner's copy)", option.DiscordLine);
        Assert.True(h.Page.PreviewAndDiscordSame);
    }

    // ---- choosing, removing ----

    [Fact]
    public async Task Options_AreTheDefaultPortraitPlusOwnPictures_AndTheDefaultCannotBeRemoved()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        h.Select("K27");
        await AddFromPc(h, "/in/a.png");
        await AddFromPc(h, "/in/bb.png");

        var options = h.Page.PictureOptions;

        Assert.Equal(3, options.Count);
        Assert.Equal("Default portrait", options[0].Title);
        Assert.False(options[0].CanRemove);
        Assert.False(options[0].IsInUse);
        Assert.True(options[2].IsInUse);
        Assert.True(options[1].CanRemove);
        Assert.Equal(["Picture-0", "Picture-1", "Picture-2"], options.Select(o => $"Picture-{o.Index}"));
        Assert.Equal("Your pictures: 2 of 3", h.Page.CounterText);
        Assert.True(h.Page.CanAddPicture);
    }

    [Fact]
    public async Task ChoosingAnotherOption_SwapsThePicture_AndAnnouncesIt()
    {
        using var h = new CharactersHarness();
        h.AllowSavingOnline();
        h.SyncDefaultPack(false, "character:the-onryo");
        h.Select("K27");
        await AddFromPc(h, "/in/a.png");
        await AddFromPc(h, "/in/bb.png");
        var first = h.Page.PictureOptions[1];
        h.Sink.Saved.Clear();

        first.ChooseCommand.Execute(null);

        Assert.True(h.Page.PictureOptions[1].IsInUse);
        Assert.Equal(first.Item!.Sha256, h.Current.Pictures["character:the-onryo"].Use!.Sha256);
        Assert.Equal("The Onryō now uses From my PC.", h.Page.StatusMessage);
        Assert.Single(h.Sink.Saved);
        h.Page.PictureOptions[0].ChooseCommand.Execute(null);
        Assert.Null(h.Current.Pictures["character:the-onryo"].Use);
        Assert.Equal("Default", h.Tile("K27").SourceCaption);
    }

    [Fact]
    public async Task Remove_AsksFirst_ThenDeletesTheFileAndTellsTheQueue_AndGoesBackToTheDefaultPortrait()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        await AddFromPc(h);
        var option = h.Page.PictureOptions.Single(o => o.Item is not null);
        var item = option.Item!;
        var path = h.Store.PathOf(item)!;

        option.RemoveCommand.Execute(null);
        Assert.True(h.Page.PictureOptions.Single(o => o.Item is not null).IsConfirmingRemove);
        Assert.Equal("Remove this picture? It's deleted from this PC.", option.RemoveConfirmText);
        option.KeepPictureCommand.Execute(null);
        Assert.Single(h.Current.Pictures["character:the-onryo"].Items);
        h.Page.PictureOptions.Single(o => o.Item is not null).ConfirmRemoveCommand.Execute(null);

        Assert.False(h.Current.Pictures.ContainsKey("character:the-onryo"));
        Assert.False(h.Storage.Exists(path));
        Assert.Equal([item.Sha256], h.Sink.Removed);
        Assert.Equal("Removed. The Onryō now uses the default portrait.", h.Page.StatusMessage);
        Assert.Equal("Not downloaded", h.Tile("K27").SourceCaption);
    }

    [Fact]
    public async Task Remove_KeepsTheFile_WhileAnotherCharacterStillUsesIt()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        await AddFromPc(h);
        var item = h.Current.Pictures["character:the-onryo"].Items.Single();
        h.Host.Settings.Update(s => s with
        {
            Pictures = new Dictionary<string, CharacterPictures>(s.Pictures) { ["character:the-trapper"] = new([item], PictureUse.Personal(item.Sha256)) },
        });

        h.Page.PictureOptions.Single(o => o.Item is not null).ConfirmRemoveCommand.Execute(null);

        Assert.NotNull(h.Store.PathOf(item));
        Assert.Empty(h.Sink.Removed);
    }

    [Fact]
    public void RemoveConfirm_ForALink_IsShort_AndForAnOnlineCopyNamesTheFreedSpace()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Page.LinkText = "https://example.com/a.png";
        h.Page.AddLinkCommand.Execute(null);

        Assert.Equal("Remove this link?", h.Page.PictureOptions.Single(o => o.IsLink).RemoveConfirmText);
    }

    // ---- other packs ----

    [Fact]
    public void AddFromAnotherPack_OwnerPicture_BecomesInUse_WithoutUsingASlot()
    {
        using var h = new CharactersHarness();
        var packs = new[] { new Packs.Index.PackInfo("hollow", "Hollow Ink", 1, "owner", "note", null) };
        h.Library.SetSynced(new Packs.Local.SyncedPacks(
            new Packs.Index.PackIndex(packs, [CharactersHarness.Entry("hollow", "character:the-onryo", "h")]), new CachedAll(), e => $"https://cdn.example.org/{e.CacheFileName}", null, e => $"/cache/{e.CacheFileName}"));
        h.Select("K27");
        h.Page.ShowAddPictureCommand.Execute(null);

        var choice = Assert.Single(h.Page.PackPictures);
        Assert.Equal("Add", choice.ActionText);
        choice.AddCommand.Execute(null);

        var pictures = h.Current.Pictures["character:the-onryo"];
        Assert.Empty(pictures.Items);
        Assert.Equal(PictureUse.Owner("hollow", "hollow-character-the-onryo"), pictures.Use);
        Assert.Equal("Hollow Ink", h.Page.PictureOptions.Single(o => o.IsInUse).Title);
        Assert.StartsWith("https://cdn.example.org/", h.Page.DiscordReference, StringComparison.Ordinal);
        Assert.Equal("Added", h.Page.PackPictures.Single().ActionText);
        Assert.Equal("Your pictures: 0 of 3", h.Page.CounterText);
    }

    [Fact]
    public void AddFromAnotherPack_NoneAvailable_SaysSo()
    {
        using var h = new CharactersHarness();
        h.Select("K27");

        h.Page.ShowAddPictureCommand.Execute(null);

        Assert.Empty(h.Page.PackPictures);
        Assert.Equal("None of your other packs has a picture of The Onryō.", h.Page.NoPackPicturesText);
        h.Page.BackCommand.Execute(null);
        Assert.Equal(CharacterPanelMode.Character, h.Page.PanelMode);
    }

    // ---- import ----

    [Fact]
    public async Task Import_MatchesByCode_StripsUiPrefixAndPortraitSuffix_AndReviewsBeforeSaving()
    {
        using var h = new CharactersHarness();

        var review = await OpenReview(h, ("T_UI_K27_TheOnryo_Portrait.png", "one"), ("K01_TheTrapper.png", "two"), ("TheWraith.png", "three"), ("mystery.png", "four"), ("hawkins.png", "five"));

        Assert.Equal(CharactersStage.Review, h.Page.Stage);
        Assert.Equal(2, review.Matched.Count());
        Assert.Equal(2, review.NeedsChoice.Count());
        Assert.Single(review.NotImported);
        Assert.Equal("2 matched", review.MatchedBadge);
        Assert.Equal("2 need a choice", review.NeedsChoiceBadge);
        Assert.Equal("1 not imported", review.NotImportedBadge);
        Assert.Equal("The Onryō", review.Matched.First().Character!.Name);
        var suggested = review.NeedsChoice.First();
        Assert.Equal("The Wraith", suggested.SelectedCharacter!.Name);
        Assert.Equal("Suggested from the file name: The Wraith", suggested.Caption);
        Assert.Null(review.NeedsChoice.Last().SelectedCharacter);
        Assert.Equal(3, review.ImportCount);
        Assert.Equal("Import 3 pictures", review.ImportButtonText);
        Assert.Empty(h.Current.Pictures);
        Assert.DoesNotContain(h.Storage.Files.Keys, k => k.Contains("/pictures/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_Confirm_AssignsByCode_UsesImportedPictures_ReportsThemAndOffersUndo()
    {
        using var h = new CharactersHarness();
        h.AllowSavingOnline();
        var review = await OpenReview(h, ("T_UI_K27_TheOnryo_Portrait.png", "one"), ("K01_TheTrapper_Portrait.png", "two"));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(CharactersStage.Browse, h.Page.Stage);
        Assert.Null(h.Page.ImportReview);
        var onryo = h.Current.Pictures["character:the-onryo"];
        Assert.Equal(PictureOrigin.Import, onryo.Items.Single().Origin);
        Assert.Equal(PictureUse.Personal(onryo.Items.Single().Sha256), onryo.Use);
        Assert.True(h.Current.Pictures.ContainsKey("character:the-trapper"));
        Assert.Equal(2, h.Sink.Saved.Count);
        Assert.Equal("Imported 2 pictures from pack. 2 characters now use them.", h.Page.ImportResultText);
        Assert.Equal("Imported", h.Tile("K27").SourceCaption);
        Assert.True(h.Page.CanUndoImport);
    }

    [Fact]
    public async Task Import_KeepMyChoices_AddsThePictures_ButLeavesTheDefaultInUse()
    {
        using var h = new CharactersHarness();
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"));
        review.UseImported = false;

        await review.ConfirmCommand.ExecuteAsync(null);

        var onryo = h.Current.Pictures["character:the-onryo"];
        Assert.Single(onryo.Items);
        Assert.Null(onryo.Use);
        Assert.Equal("Imported 1 pictures from pack. Your current choices are unchanged.", h.Page.ImportResultText);
    }

    [Fact]
    public async Task Import_Undo_RemovesWhatWasAdded_AndRestoresEarlierPictures()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        await AddFromPc(h);
        var before = h.Current.Pictures["character:the-onryo"];
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"), ("K01_TheTrapper.png", "two"));
        await review.ConfirmCommand.ExecuteAsync(null);
        var added = h.Current.Pictures["character:the-trapper"].Items.Single();
        Assert.Equal(2, h.Current.Pictures["character:the-onryo"].Items.Count);

        h.Page.UndoImportCommand.Execute(null);

        Assert.Equal(before.Items, h.Current.Pictures["character:the-onryo"].Items);
        Assert.Equal(before.Use, h.Current.Pictures["character:the-onryo"].Use);
        Assert.False(h.Current.Pictures.ContainsKey("character:the-trapper"));
        Assert.Null(h.Store.PathOf(added));
        Assert.Contains(added.Sha256, h.Sink.Removed);
        Assert.False(h.Page.CanUndoImport);
        Assert.Null(h.Page.ImportResultText);
        Assert.Equal("Import undone. Nothing from pack is left on this PC or online.", h.Page.StatusMessage);
    }

    [Fact]
    public async Task Import_CodeAndNameDisagreeing_IsReportedAndNeverGuessed()
    {
        using var h = new CharactersHarness();
        var review = await OpenReview(h, ("K27_TheTrapper_Portrait.png", "one"));

        var row = Assert.Single(review.NeedsChoice);
        Assert.Null(row.SelectedCharacter);
        Assert.Contains("The code says The Onryō but the name says The Trapper.", row.Caption, StringComparison.Ordinal);
        Assert.Equal(0, review.ImportCount);
        Assert.False(review.CanImport);
        Assert.Equal("Choose at least one picture.", review.ImportHint);
        Assert.Equal("Import pictures", review.ImportButtonText);
        row.SelectedCharacter = review.Choices.Single(c => c.Code == "K27");
        Assert.Equal(1, review.ImportCount);
        Assert.True(review.CanImport);
        await review.ConfirmCommand.ExecuteAsync(null);
        Assert.True(h.Current.Pictures.ContainsKey("character:the-onryo"));
    }

    [Fact]
    public async Task Import_ACharacterWithThreePictures_IsInTheFullSection_SkipByDefault_OrReplace()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        for (var i = 0; i < 3; i++)
        {
            await AddFromPc(h, $"/in/p{new string('x', i)}.png");
        }

        var review = await OpenReview(h, ("K27_TheOnryo.png", "new"));

        var row = Assert.Single(review.Full);
        Assert.Equal("1 character is full", review.FullBadge);
        Assert.Equal("Skip this picture", row.SelectedReplace!.Label);
        Assert.Equal(4, row.ReplaceChoices.Count);
        Assert.Equal(0, review.ImportCount);
        var oldest = h.Current.Pictures["character:the-onryo"].Items[0];
        row.SelectedReplace = row.ReplaceChoices[1];
        Assert.Equal(1, review.ImportCount);
        await review.ConfirmCommand.ExecuteAsync(null);

        var after = h.Current.Pictures["character:the-onryo"];
        Assert.Equal(3, after.Items.Count);
        Assert.DoesNotContain(after.Items, i => i.Sha256 == oldest.Sha256);
        Assert.NotNull(h.Store.PathOf(oldest));
        h.Page.DismissImportResultCommand.Execute(null);
        Assert.Null(h.Store.PathOf(oldest));
    }

    [Fact]
    public async Task Import_StorageLine_ShowsTheEstimate_SignedInSignedOutAndTooBig()
    {
        using var h = new CharactersHarness();
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"));
        Assert.StartsWith("Uses up to ", review.StorageLine, StringComparison.Ordinal);
        Assert.EndsWith("when you sign in.", review.StorageLine, StringComparison.Ordinal);
        Assert.Equal("Imported pictures appear in your preview. They're saved online, so Discord can show them, when you sign in on the Account page.", review.Note);
        review.CancelCommand.Execute(null);
        Assert.Equal(CharactersStage.Browse, h.Page.Stage);

        h.Environment.SignedIn = true;
        h.Environment.UsedBytes = 12_400_000;
        h.Environment.Raise();
        var signedIn = await OpenReview(h, ("K27_TheOnryo.png", "one"));
        Assert.StartsWith("Saves up to ", signedIn.StorageLine, StringComparison.Ordinal);
        Assert.True(signedIn.Estimate.Fits);
        signedIn.CancelCommand.Execute(null);

        h.Environment.UsedBytes = PictureLimits.OnlineQuotaBytes - 10;
        h.Environment.Raise();
        var full = await OpenReview(h, ("K27_TheOnryo.png", "one"), ("K01_TheTrapper.png", "two"));
        Assert.False(full.Estimate.Fits);
        Assert.Equal(2, full.Estimate.PreviewOnlyCount);
        Assert.Contains("stay in the preview only", full.StorageLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_OwnerCopy_UsesNoSpace()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");
        var png = PackFixtures.Png("one");
        var loaded = (await new FakeImageLoader(h.Storage).LoadBytesAsync(png, TestContext.Current.CancellationToken)).PngBytes!; // what the fake helper makes of the file
        var owner = h.Library.Synced!.Index.Entries.Single(e => e.Key == "character:the-onryo");
        var sameAsOwner = new Packs.Index.PackEntry(owner.Id, owner.PackId, owner.Kind, owner.Key, Packs.Local.ImportedPackStore.Sha256Of(loaded), 1000, 512, 512, owner.Format, null);
        h.Library.SetSynced(new Packs.Local.SyncedPacks(
            new Packs.Index.PackIndex(h.Library.Synced.Index.Packs, [sameAsOwner]), new CachedAll(), e => $"https://cdn.example.org/{e.CacheFileName}", null, e => $"/cache/{e.CacheFileName}"));
        h.Environment.SignedIn = true;
        h.Environment.Raise();

        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"));

        Assert.Equal(0, review.Estimate.SaveBytes);
        Assert.True(review.Estimate.Fits);
    }

    [Fact]
    public async Task Import_NotAZip_ShowsAnErrorAndChangesNothing()
    {
        using var h = new CharactersHarness();
        h.Storage.Files["/in/bad.zip"] = System.Text.Encoding.ASCII.GetBytes("This is not a zip");
        h.Picker.Archive = "/in/bad.zip";

        await h.Page.ImportPackCommand.ExecuteAsync(null);

        Assert.Equal(CharactersStage.Browse, h.Page.Stage);
        Assert.Null(h.Page.ImportReview);
        Assert.StartsWith("Couldn't read bad.zip.", h.Page.ImportError, StringComparison.Ordinal);
        Assert.Contains("nothing was imported", h.Page.ImportError, StringComparison.OrdinalIgnoreCase);
        h.Page.DismissImportErrorCommand.Execute(null);
        Assert.Null(h.Page.ImportError);
        Assert.Empty(h.Current.Pictures);
    }

    [Fact]
    public async Task Import_CancelledPicker_DoesNothing()
    {
        using var h = new CharactersHarness();
        h.Picker.Archive = null;

        await h.Page.ImportPackCommand.ExecuteAsync(null);

        Assert.Equal(CharactersStage.Browse, h.Page.Stage);
        Assert.Null(h.Page.ImportError);
    }

    // ---- packs panel, size, migration notice ----

    [Fact]
    public void PacksPanel_ShowsTheStorageMeter_FromTheServiceFigures()
    {
        using var h = new CharactersHarness();
        h.Environment.UsedBytes = 13_000_000;
        h.Environment.SavedUrlBySha256 = new Dictionary<string, string> { [new string('a', 64)] = "https://x" };
        h.Environment.Raise();

        h.Page.ShowPacksCommand.Execute(null);

        Assert.Equal(CharacterPanelMode.Packs, h.Page.PanelMode);
        Assert.Equal("12.4 of 60 MB used", h.Page.StorageText);
        Assert.Equal("12.4 of 60 MB used · 1 pictures", h.Page.StorageCaption);
        Assert.Equal(13_000_000d / PictureLimits.OnlineQuotaBytes, h.Page.StorageFraction, 6);
    }

    [Fact]
    public void PictureSize_ChoiceWritesTheSetting_AndOnlyTheTwoSizesExist()
    {
        using var h = new CharactersHarness();
        Assert.Equal([512, 1024], h.Page.PictureSizes.Select(c => c.Value));
        Assert.Equal(512, h.Page.SelectedPictureSize.Value);

        h.Page.SelectedPictureSize = h.Page.PictureSizes[1];

        Assert.Equal(1024, h.Current.PictureSize);
        h.Host.Settings.Update(s => s with { PictureSize = 512 });
        Assert.Equal(512, h.Page.SelectedPictureSize.Value);
    }

    [Fact]
    public void MigrationNotice_NamesWhatWasLeftOut_UntilDismissed()
    {
        using var h = new CharactersHarness();

        h.Host.Settings.Update(s => s with { PictureNotice = new PictureMigrationNotice { GenericOverrides = ["role:killer"], OverLimit = 2 } });
        Assert.Contains("role:killer", h.Page.MigrationNoticeText, StringComparison.Ordinal);
        Assert.Contains("2 pictures were left out", h.Page.MigrationNoticeText, StringComparison.Ordinal);
        h.Page.DismissMigrationNoticeCommand.Execute(null);

        Assert.Null(h.Page.MigrationNoticeText);
        Assert.Null(h.Current.PictureNotice);
    }

    [Fact]
    public void StatusStrip_FollowsTheAccount()
    {
        using var h = new CharactersHarness();
        Assert.Equal(CharactersStatusKind.FirstRun, h.Page.StatusKind);
        Assert.StartsWith("Default portraits download when you sign in.", h.Page.StatusStripText, StringComparison.Ordinal);

        h.SyncDefaultPack(false, "character:the-onryo");
        Assert.Equal(CharactersStatusKind.SignedOut, h.Page.StatusKind);

        h.Environment.SignedIn = true;
        h.Environment.Raise();
        Assert.Equal(CharactersStatusKind.Ready, h.Page.StatusKind);
        Assert.Contains("unlisted address", h.Page.StatusStripText, StringComparison.Ordinal);
        Assert.DoesNotContain("private", h.Page.StatusStripText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first needed", h.Page.StatusStripCaption, StringComparison.OrdinalIgnoreCase);

        h.Environment.UploadsBlocked = true;
        h.Environment.Raise();
        Assert.Equal(CharactersStatusKind.SavingOff, h.Page.StatusKind);
    }

    [Fact]
    public void BehaviourNotice_IsTheSharedSentence()
    {
        using var h = new CharactersHarness();

        Assert.Equal(DbdPresence.Presentation.ViewModels.AppCopy.BehaviourNotice, h.Page.BehaviourNotice);
    }

    [Fact]
    public void PictureTexts_NeverSayPrivateOrWhenFirstNeeded()
    {
        var all = Enum.GetValues<PictureOnlineStatus>().SelectMany(s => new[] { PictureTexts.DiscordLine(s), PictureTexts.Difference(s), PictureTexts.PreviewOnlyTooltip(s) });

        Assert.All(all, text =>
        {
            Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("first needed", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("first uses", text, StringComparison.OrdinalIgnoreCase);
        });
    }
}
