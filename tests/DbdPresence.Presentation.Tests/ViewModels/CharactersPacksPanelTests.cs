// Covers: the Packs panel of the Characters page (docs/design/characters-page.md §5.5, §7.8): pack rows, no removal of owner packs, update checks without
// a sync service hidden (FR-ONL-07, "show nothing that cannot work"), and the shell pointing the Characters item at this page (UI_SPEC §2).
using DbdPresence.Presentation.Characters;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class CharactersPacksPanelTests
{
    [Fact]
    public void OwnerPacksAreListedAndCannotBeRemoved()
    {
        using var h = new CharactersHarness();
        h.SyncDefaultPack(false, "character:the-onryo");

        var row = h.Page.PackRows.Single(r => r.Id == "def");

        Assert.Equal("Default portraits", row.Name);
        Assert.StartsWith("Owner pack · 1 pictures", row.Caption, StringComparison.Ordinal);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public void ShowPacksOpensThePanel_AndBackReturnsToTheCharacter()
    {
        using var h = new CharactersHarness();

        h.Page.ShowPacksCommand.Execute(null);
        Assert.Equal(CharacterPanelMode.Packs, h.Page.PanelMode);
        h.Page.BackCommand.Execute(null);

        Assert.Equal(CharacterPanelMode.Character, h.Page.PanelMode);
    }

    [Fact]
    public void NoSyncService_HidesTheUpdateSection()
    {
        using var h = new CharactersHarness();

        Assert.False(h.Page.ShowSyncSection);
        Assert.False(h.Page.CanCheckForPacks);
        Assert.False(h.Page.SyncUnavailable);
    }

    [Fact]
    public void ShellPointsTheCharactersItemAtThisPage()
    {
        using var host = new Harness();
        using var kit = new AppKit(host, new AppInfo("1.0", "abc1234", false, "/s", "/d", "/p"));

        Assert.True(kit.Shell.SelectByShortcut(3));
        Assert.IsType<CharactersViewModel>(kit.Shell.SelectedPage.Page);
    }

    [Fact]
    public void GoToAccount_AsksTheShellToOpenTheAccountPage()
    {
        using var host = new Harness();
        using var kit = new AppKit(host, new AppInfo("1.0", "abc1234", false, "/s", "/d", "/p"));
        _ = kit.Shell.SelectByShortcut(3);

        kit.Characters.GoToAccountCommand.Execute(null);

        Assert.Equal("account", kit.Shell.SelectedPage.Key);
    }

    [Fact]
    public async Task CharacterBox_StartsOnDontImport_AndPickingACharacterAssignsTheFile()
    {
        using var h = new CharactersHarness();
        h.Storage.Files["/in/pack.zip"] = PackFixtures.Zip([("mystery.png", PackFixtures.Png("m"))]);
        h.Picker.Archive = "/in/pack.zip";
        await h.Page.ImportPackCommand.ExecuteAsync(null);
        var review = h.Page.ImportReview ?? throw new InvalidOperationException("no review");
        var row = review.NeedsChoice.Single();

        Assert.Same(CharacterChoice.None, row.SelectedOrNone);
        Assert.Same(CharacterChoice.None, review.ChoicesWithNone[0]);
        Assert.Equal(review.Choices.Count + 1, review.ChoicesWithNone.Count);
        Assert.Null(row.Assigned);

        row.SelectedOrNone = review.Choices.First(c => c.Code == "K27");
        Assert.Equal("K27", row.Assigned?.Code);
        row.SelectedOrNone = CharacterChoice.None;
        Assert.Null(row.Assigned);
    }
}
