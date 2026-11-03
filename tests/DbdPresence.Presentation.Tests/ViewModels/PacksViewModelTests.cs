// Covers (added for gate V2.5): FR-PACK-06 dismissible error card; NFR-ACC missing-image tile names.
// Covers: FR-PACK-06 (cards, "In use" badge, switch per category, per-image override, preview-only note), FR-PACK-07 (import:
// file picker, mapping confirmation with pre-filled proposals, unconfirmed entries ignored, source name and creator shown,
// skipped entries with reasons, removal), FR-PACK-10 (images only through the image loader), NFR-ACC (automation names),
// D16 (no bundled art: the page starts with an empty state and offers Import pack...).
using DbdPresence.Packs.Local;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class PacksViewModelTests : IDisposable
{
    private readonly Harness _host = new();
    private readonly PackHarness _packs;
    private readonly PacksViewModel _page;

    public PacksViewModelTests()
    {
        _packs = new PackHarness(_host);
        _page = _packs.Page();
    }

    public void Dispose()
    {
        _page.Dispose();
        _host.Dispose();
    }

    private async Task<PacksViewModel> OpenMappingAsync(params (string Name, byte[] Data)[] entries)
    {
        _packs.Storage.Files["/in/Trapper Pack.zip"] = PackFixtures.Zip(entries);
        _packs.Picker.Archive = "/in/Trapper Pack.zip";
        await _page.ImportPackCommand.ExecuteAsync(null);
        return _page;
    }

    private async Task<PackCardViewModel> ImportPackAsync(string name = "Mine", string creator = "Some Creator")
    {
        await OpenMappingAsync(("killer.png", PackFixtures.Png("k")), ("survivor.png", PackFixtures.Png("s")), ("the-trapper.png", PackFixtures.Png("t")));
        _page.ImportPackName = name;
        _page.ImportCreator = creator;
        _page.ConfirmSuggestionsCommand.Execute(null);
        _page.SaveImportCommand.Execute(null);
        return _page.Cards.Single(c => c.IsImported);
    }

    [Fact]
    public void Starts_WithAnEmptyStateAndNoCards()
    {
        Assert.Empty(_page.Cards);
        Assert.True(_page.HasNoPacks);
        Assert.Equal(
            "No image packs yet. Import a NightLight pack you've downloaded, or official packs appear here once available.",
            _page.EmptyStateText);
        Assert.Null(_page.LocalImagesNote);
        Assert.True(_page.IsBrowsing);
        Assert.True(_page.ImportPackCommand.CanExecute(null));
    }

    [Fact]
    public void NoBundledPackCardIsListed()
    {
        Assert.DoesNotContain(_page.Cards, c => c.Name.Contains("Classic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_page.Cards, c => c.Kind.Contains("Bundled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoOriginalOrComingSoonCardIsShownBeforeSyncExists()
    {
        Assert.DoesNotContain(_page.Cards, c => c.Name.Contains("Original", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_page.Cards, c => c.Name.Contains("soon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscordNote_SaysLocalImagesStayInThePreview()
    {
        Assert.Contains("only in your preview", _page.DiscordNote, StringComparison.Ordinal);
        Assert.DoesNotContain("Classic", _page.DiscordNote, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRows_ShowNoImageForTheKillerAndSurvivorIconsWithoutPacks()
    {
        Assert.Equal(["Killer icon", "Survivor icon"], _page.PreviewRows.Select(r => r.Label));
        Assert.All(_page.PreviewRows, r =>
        {
            Assert.Equal("No image", r.Discord);
            Assert.False(r.LocalOnly);
            Assert.Null(r.Preview);
        });
    }

    [Fact]
    public async Task Import_PicksAFileReadsItAndOpensTheMappingScreenWithUnconfirmedProposals()
    {
        await OpenMappingAsync(
            ("killer.png", PackFixtures.Png("k")), ("the-trapper.png", PackFixtures.Png("t")), ("random.png", PackFixtures.Png("r")),
            ("broken.png", PackFixtures.Png("BAD")), ("readme.txt", [1, 2, 3]));

        Assert.True(_page.IsMapping);
        Assert.Equal("Trapper Pack.zip", _page.ImportSourceName);
        Assert.Equal("Trapper Pack", _page.ImportPackName);
        Assert.Equal(3, _page.ImportRows.Count);
        var killer = _page.ImportRows.Single(r => r.DisplayName == "killer.png");
        Assert.Equal(ImportSlotKind.Killer, killer.SlotKind);
        Assert.Equal("role:killer", killer.Key);
        var trapper = _page.ImportRows.Single(r => r.DisplayName == "the-trapper.png");
        Assert.Equal((ImportSlotKind.Character, "the-trapper"), (trapper.SlotKind, trapper.SlotName));
        var random = _page.ImportRows.Single(r => r.DisplayName == "random.png");
        Assert.Equal(ImportSlotKind.None, random.SlotKind);
        Assert.Null(random.Key);
        Assert.All(_page.ImportRows, r => Assert.False(r.IsConfirmed));
        Assert.True(_page.HasSkipped);
        Assert.Contains(_page.ImportSkipped, s => s.Name == "readme.txt" && s.Reason.Length > 0);
        Assert.Contains(_page.ImportSkipped, s => s.Name == "broken.png" && s.Reason.Contains("could not be opened", StringComparison.Ordinal));
        Assert.DoesNotContain(_page.Cards, c => c.IsImported);
    }

    [Fact]
    public async Task Import_DoesNothingWhenTheFileDialogIsCancelled()
    {
        _packs.Picker.Archive = null;

        await _page.ImportPackCommand.ExecuteAsync(null);

        Assert.True(_page.IsBrowsing);
        Assert.Null(_page.ErrorMessage);
    }

    [Fact]
    public async Task Import_ShowsAClearErrorForAFileThatIsNotAnArchive()
    {
        _packs.Storage.Files["/in/bad.zip"] = [1, 2, 3, 4];
        _packs.Picker.Archive = "/in/bad.zip";

        await _page.ImportPackCommand.ExecuteAsync(null);

        Assert.True(_page.IsBrowsing);
        Assert.Contains("not a valid zip", _page.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DismissError_ClearsTheMessageAndIsAvailableAnytime()
    {
        _packs.Storage.Files["/in/bad.zip"] = [1, 2, 3, 4];
        _packs.Picker.Archive = "/in/bad.zip";
        await _page.ImportPackCommand.ExecuteAsync(null);
        Assert.NotNull(_page.ErrorMessage);

        _page.DismissErrorCommand.Execute(null);

        Assert.Null(_page.ErrorMessage);
        Assert.True(_page.IsBrowsing);
    }

    [Fact]
    public void PackTile_NamesAMissingPictureForScreenReaders()
    {
        var present = new PackTile("Killer icon", new PackImageLocation("/x.png"));
        var missing = new PackTile("Killer icon", null);

        Assert.Equal("Killer icon", present.AccessibleName);
        Assert.Equal("Killer icon, missing", missing.AccessibleName);
        Assert.Equal("Missing", missing.MissingText);
        Assert.Equal(string.Empty, present.MissingText);
    }

    [Fact]
    public async Task Import_ShowsWhyNothingWasUsable()
    {
        await OpenMappingAsync(("only.txt", [1, 2, 3]));

        Assert.True(_page.IsBrowsing);
        Assert.Contains("No usable images", _page.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(_page.ImportSkipped, s => s.Name == "only.txt");
    }

    [Fact]
    public async Task Confirming_ARowWithoutASlotIsRefusedWithAReason()
    {
        await OpenMappingAsync(("random.png", PackFixtures.Png("r")));
        var row = _page.ImportRows[0];

        row.IsConfirmed = true;

        Assert.False(row.IsConfirmed);
        Assert.Contains("Choose what this image is for", row.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_ACharacterNeedsAName_AndEditingItUnconfirms()
    {
        await OpenMappingAsync(("random.png", PackFixtures.Png("r")));
        var row = _page.ImportRows[0];
        row.SlotKind = ImportSlotKind.Character;

        row.IsConfirmed = true;
        Assert.False(row.IsConfirmed);
        Assert.Contains("name", row.Problem, StringComparison.Ordinal);

        row.SlotName = "Dwight Fairfield";
        row.IsConfirmed = true;
        Assert.True(row.IsConfirmed);
        Assert.Equal("character:dwight-fairfield", row.Key);

        row.SlotName = "!!!";
        Assert.False(row.IsConfirmed);
    }

    [Fact]
    public async Task Save_IgnoresUnconfirmedImagesAndStoresOnlyTheConfirmedOnes()
    {
        await OpenMappingAsync(("killer.png", PackFixtures.Png("k")), ("survivor.png", PackFixtures.Png("s")));
        _page.ImportRows.Single(r => r.DisplayName == "killer.png").IsConfirmed = true;

        _page.SaveImportCommand.Execute(null);

        var pack = _page.Cards.Single(c => c.IsImported);
        Assert.Equal(1, pack.ImageCount);
        Assert.Equal(["Killer icon"], pack.Tiles.Select(t => t.Label));
        Assert.True(_page.IsBrowsing);
        Assert.Contains("Saved", _page.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_WithNothingConfirmedExplainsAndKeepsTheMappingScreen()
    {
        await OpenMappingAsync(("killer.png", PackFixtures.Png("k")));

        _page.SaveImportCommand.Execute(null);

        Assert.True(_page.IsMapping);
        Assert.Contains("Confirm at least one image", _page.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(_page.Cards, c => c.IsImported);
    }

    [Fact]
    public async Task Save_ShowsTheSourceNameAndCreatorOnTheCard()
    {
        var card = await ImportPackAsync("Trapper set", "Jane Doe");

        Assert.Equal("Trapper set", card.Name);
        Assert.Equal("Jane Doe", card.Author);
        Assert.Contains("Trapper Pack.zip", card.Credit, StringComparison.Ordinal);
        Assert.Equal("Imported by you", card.Kind);
        Assert.Equal(3, card.ImageCount);
        Assert.All(card.Tiles, t => Assert.True(_packs.Storage.Exists(t.Location!.Path)));
        Assert.True(card.RemoveCommand.CanExecute(null));
        Assert.False(card.InUse);
    }

    [Fact]
    public async Task Save_SurvivesAnAppRestart()
    {
        await ImportPackAsync();

        var library = new PackLibrary(_packs.Storage, PackFixtures.Root, new FakeImageLoader(_packs.Storage), PackFixtures.Roster);
        using var page = new PacksViewModel(_host.Settings, _host.Dispatcher, library, _packs.Picker);
        Assert.Equal(1, page.Cards.Count(c => c.IsImported));
    }

    [Fact]
    public async Task Cancelling_TheMappingScreenSavesNothing()
    {
        await OpenMappingAsync(("killer.png", PackFixtures.Png("k")));
        _page.ImportRows[0].IsConfirmed = true;

        _page.CancelImportCommand.Execute(null);

        Assert.True(_page.IsBrowsing);
        Assert.Empty(_page.ImportRows);
        Assert.DoesNotContain(_page.Cards, c => c.IsImported);
        Assert.DoesNotContain(_packs.Storage.Files.Keys, k => VirtualPaths.IsUnder(k, Path.Combine(PackFixtures.Root, "imported"), out _));
    }

    [Fact]
    public async Task ChoosingAnImportedPack_ForACategory_UpdatesSettingsBadgeAndNote()
    {
        var card = await ImportPackAsync();
        var killer = card.Categories.Single(c => c.Category == PackCategory.Killer);

        killer.IsChosen = true;

        Assert.Equal(card.Id, _host.Settings.Current.Packs.KillerPack);
        Assert.Equal(string.Empty, _host.Settings.Current.Packs.SurvivorPack);
        Assert.True(card.InUse);
        Assert.Equal("In use for Killer", card.InUseText);
        Assert.Equal("Shown only in your preview; Discord shows no image for it.", _page.LocalImagesNote);
        var row = _page.PreviewRows.Single(r => r.Label == "Killer icon");
        Assert.True(row.LocalOnly);
        Assert.True(_packs.Storage.Exists(row.Preview!.Path));
        Assert.Equal("No image", row.Discord);
    }

    [Fact]
    public async Task SwitchingAPackOff_ReturnsTheCategoryToNoPack()
    {
        var card = await ImportPackAsync();
        var survivor = card.Categories.Single(c => c.Category == PackCategory.Survivor);
        survivor.IsChosen = true;
        Assert.Equal(card.Id, _host.Settings.Current.Packs.SurvivorPack);

        survivor.IsChosen = false;

        Assert.Equal(string.Empty, _host.Settings.Current.Packs.SurvivorPack);
        Assert.False(card.InUse);
        Assert.Null(_page.LocalImagesNote);
    }

    [Fact]
    public async Task ImportingTheFirstPack_ReplacesTheEmptyStateWithItsCard()
    {
        Assert.True(_page.HasNoPacks);

        var card = await ImportPackAsync("First pack");

        Assert.False(_page.HasNoPacks);
        Assert.Same(card, Assert.Single(_page.Cards));
        Assert.Equal("First pack", card.Name);
        Assert.Contains("Some Creator", card.Author, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackWithoutMapsOffersNoMapSwitch()
    {
        var card = await ImportPackAsync();

        Assert.False(card.Categories.Single(c => c.Category == PackCategory.Maps).IsAvailable);
        Assert.False(card.Categories.Single(c => c.Category == PackCategory.Maps).CanToggle);
        Assert.True(card.Categories.Single(c => c.Category == PackCategory.Characters).IsAvailable);
    }

    [Fact]
    public async Task Removing_APack_NeedsConfirmationThenReturnsItsCategoriesToNoPack()
    {
        var card = await ImportPackAsync();
        card.Categories.Single(c => c.Category == PackCategory.Killer).IsChosen = true;

        card.RemoveCommand.Execute(null);
        Assert.True(_page.IsRemovalPending);
        Assert.Contains("Mine", _page.RemovalPrompt, StringComparison.Ordinal);
        Assert.Equal(1, _page.Cards.Count(c => c.IsImported));

        _page.ConfirmRemovalCommand.Execute(null);

        Assert.False(_page.IsRemovalPending);
        Assert.DoesNotContain(_page.Cards, c => c.IsImported);
        Assert.Equal(string.Empty, _host.Settings.Current.Packs.KillerPack);
        Assert.True(_page.HasNoPacks);
        Assert.Null(_page.LocalImagesNote);
        Assert.DoesNotContain(_packs.Storage.Files.Keys, k => VirtualPaths.IsUnder(k, Path.Combine(PackFixtures.Root, "imported"), out _));
    }

    [Fact]
    public async Task Removing_CanBeCancelled()
    {
        var card = await ImportPackAsync();

        card.RemoveCommand.Execute(null);
        _page.CancelRemovalCommand.Execute(null);

        Assert.False(_page.IsRemovalPending);
        Assert.Equal(1, _page.Cards.Count(c => c.IsImported));
    }

    [Fact]
    public async Task Override_StoresACopyUnderOverridesAndShowsItInThePreviewOnly()
    {
        _packs.Storage.Files["/pics/mine.png"] = PackFixtures.Png("mine");
        _packs.Picker.Image = "/pics/mine.png";
        var row = _page.Overrides.Single(o => o.Key == "role:killer");

        await row.ChooseCommand.ExecuteAsync(null);

        var file = _host.Settings.Current.Packs.Overrides["role:killer"];
        Assert.Matches("^[0-9a-f]{64}\\.png$", file);
        Assert.True(_packs.Storage.Exists(Path.Combine(PackFixtures.Root, "overrides", file)));
        Assert.True(row.HasOverride);
        Assert.True(_packs.Storage.Exists(row.Current!.Path));
        Assert.Contains("only in your preview", row.CurrentText, StringComparison.Ordinal);
        Assert.Equal("Shown only in your preview; Discord shows no image for it.", _page.LocalImagesNote);
        var preview = _page.PreviewRows.Single(r => r.Label == "Killer icon");
        Assert.True(preview.LocalOnly);
        Assert.Equal("No image", preview.Discord);
    }

    [Fact]
    public async Task Override_ShowsTheLoaderMessageForAnImageThatCannotBeOpened()
    {
        _packs.Storage.Files["/pics/bad.png"] = PackFixtures.Png("BAD");
        _packs.Picker.Image = "/pics/bad.png";
        var row = _page.Overrides[0];

        await row.ChooseCommand.ExecuteAsync(null);

        Assert.False(row.HasOverride);
        Assert.Contains("could not be opened", _page.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(_host.Settings.Current.Packs.Overrides);
    }

    [Fact]
    public async Task Override_DoesNothingWhenTheDialogIsCancelled()
    {
        _packs.Picker.Image = null;

        await _page.Overrides[0].ChooseCommand.ExecuteAsync(null);

        Assert.Empty(_host.Settings.Current.Packs.Overrides);
    }

    [Fact]
    public async Task Override_ResetRemovesTheSettingAndDeletesTheUnusedFile()
    {
        _packs.Storage.Files["/pics/mine.png"] = PackFixtures.Png("mine");
        _packs.Picker.Image = "/pics/mine.png";
        var row = _page.Overrides.Single(o => o.Key == "role:survivor");
        await row.ChooseCommand.ExecuteAsync(null);
        var file = _host.Settings.Current.Packs.Overrides["role:survivor"];

        row.ResetCommand.Execute(null);

        Assert.Empty(_host.Settings.Current.Packs.Overrides);
        Assert.False(_packs.Storage.Exists(Path.Combine(PackFixtures.Root, "overrides", file)));
        Assert.False(row.HasOverride);
        Assert.Null(row.Current);
        Assert.False(row.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task Override_KeepsAFileThatAnotherSlotStillUses()
    {
        _packs.Storage.Files["/pics/mine.png"] = PackFixtures.Png("mine");
        _packs.Picker.Image = "/pics/mine.png";
        var killer = _page.Overrides.Single(o => o.Key == "role:killer");
        var survivor = _page.Overrides.Single(o => o.Key == "role:survivor");
        await killer.ChooseCommand.ExecuteAsync(null);
        await survivor.ChooseCommand.ExecuteAsync(null);
        var file = _host.Settings.Current.Packs.Overrides["role:killer"];

        killer.ResetCommand.Execute(null);

        Assert.True(_packs.Storage.Exists(Path.Combine(PackFixtures.Root, "overrides", file)));
        Assert.True(survivor.HasOverride);
    }

    [Fact]
    public async Task EveryInteractiveElement_HasAnAccessibleName()
    {
        var card = await ImportPackAsync();

        Assert.All(_page.Cards, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.AutomationName));
            Assert.All(c.Categories, k => Assert.StartsWith("Use ", k.AutomationName, StringComparison.Ordinal));
            Assert.All(c.Tiles, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
        });
        Assert.All(_page.Overrides, o => Assert.False(string.IsNullOrWhiteSpace(o.AutomationName)));
        Assert.Contains("Mine", card.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalSettingsChange_RefreshesTheBadgesWithoutRebuildingTheCards()
    {
        var card = await ImportPackAsync();
        var before = _page.Cards;
        Assert.False(card.InUse);

        _host.Settings.Update(s => s with { Packs = s.Packs with { KillerPack = card.Id } });

        Assert.Same(before, _page.Cards);
        Assert.True(card.InUse);
    }

    [Fact]
    public void ABrokenImportedPackOnDisk_IsReportedNotHidden()
    {
        _packs.Storage.Files[Path.Combine(PackFixtures.Root, "imported", "broken", "pack.json")] = [0x7B];
        _packs.Library.Reload();
        _host.Settings.Update(s => s with { Packs = s.Packs });
        _page.Dispose();
        using var page = _packs.Page();

        Assert.Single(page.LoadProblems);
        Assert.Contains("broken", page.LoadProblems[0], StringComparison.Ordinal);
    }
}
