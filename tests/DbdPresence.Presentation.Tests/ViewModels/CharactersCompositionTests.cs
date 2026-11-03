// Covers: FR-PACK-16 (imported packs move into the picture model once at startup, notice for what was left out), FR-ROS-04 (a merged roster adds
// characters to the page and to the next detection vocabulary; a clash changes nothing), FR-DET-18 (roster additions change no running detector),
// and the import review headings (FR-PACK-07).
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Roster;
using DbdPresence.Presentation.Characters;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class CharactersCompositionTests
{
    private static RosterDocument Document(params RosterEntry[] entries) => new(entries);

    private static RosterEntry Entry(string code, string id, string name, Role role = Role.Killer, string? screen = null) =>
        new(code, id, role, name, screen ?? name, [], null, false);

    [Fact]
    public async Task ImportedPacks_AreMovedOnce_AndRemovedWhenEveryPictureWasCopied()
    {
        using var h = new CharactersHarness();
        h.Storage.Files["/in/pack.zip"] = PackFixtures.Zip(("the-onryo.png", PackFixtures.Png("o")));
        var analysis = await h.Library.AnalyseImportAsync("/in/pack.zip", CancellationToken.None);
        var saved = h.Library.SaveImport("Mine", "Me", "pack.zip", [new ConfirmedMapping(analysis.Items[0], "character:the-onryo")]);
        Assert.NotNull(saved.Pack);

        var first = ImportedPackMigrationRunner.Run(h.Host.Settings, h.Library, h.Store);
        var second = ImportedPackMigrationRunner.Run(h.Host.Settings, h.Library, h.Store);

        Assert.NotNull(first);
        Assert.True(first.AllCopied);
        Assert.Equal(1, first.Migrated);
        Assert.Single(h.Current.Pictures["character:the-onryo"].Items);
        Assert.Empty(h.Library.Imported);
        Assert.Null(second);
    }

    private sealed class IssueRaisingSettings(ISettingsService inner, bool keepChange) : ISettingsService
    {
        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add => inner.SaveFailed += value;
            remove => inner.SaveFailed -= value;
        }

        public bool IsReadOnly => inner.IsReadOnly;

        public AppSettings Current => inner.Current;

        public IReadOnlyList<SettingsIssue> Update(Func<AppSettings, AppSettings> change)
        {
            _ = keepChange ? inner.Update(change) : [];
            return [new SettingsIssue("pictures", "a picture was dropped")];
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportedPacks_AreKept_WhenTheSettingsReportAnIssue_OrDidNotKeepThePictures(bool keepChange)
    {
        // Round-2 QA review F2: the source files go only when the settings saved every migrated key with no issue.
        using var h = new CharactersHarness();
        h.Storage.Files["/in/pack.zip"] = PackFixtures.Zip(("the-onryo.png", PackFixtures.Png("o")));
        var analysis = await h.Library.AnalyseImportAsync("/in/pack.zip", CancellationToken.None);
        Assert.NotNull(h.Library.SaveImport("Mine", "Me", "pack.zip", [new ConfirmedMapping(analysis.Items[0], "character:the-onryo")]).Pack);

        var result = ImportedPackMigrationRunner.Run(new IssueRaisingSettings(h.Host.Settings, keepChange), h.Library, h.Store);

        Assert.NotNull(result);
        Assert.True(result.AllCopied);
        Assert.Single(h.Library.Imported);

        // With a settings service that keeps the pictures and reports nothing, the next start finishes the job.
        var again = ImportedPackMigrationRunner.Run(h.Host.Settings, h.Library, h.Store);
        Assert.NotNull(again);
        Assert.Empty(h.Library.Imported);
        Assert.Single(h.Current.Pictures["character:the-onryo"].Items);
    }

    /// <summary>Saves the change but without the migrated pictures (a whole character, or only its items) and reports no issue: the "saved pictures hold what was migrated" check must catch it.</summary>
    private sealed class QuietlyLossySettings(ISettingsService inner, bool dropWholeCharacter) : ISettingsService
    {
        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add => inner.SaveFailed += value;
            remove => inner.SaveFailed -= value;
        }

        public bool IsReadOnly => inner.IsReadOnly;

        public AppSettings Current => inner.Current;

        public IReadOnlyList<SettingsIssue> Update(Func<AppSettings, AppSettings> change) =>
            inner.Update(s =>
            {
                var next = change(s);
                var pictures = dropWholeCharacter
                    ? new Dictionary<string, DbdPresence.Core.Pictures.CharacterPictures>()
                    : next.Pictures.ToDictionary(kv => kv.Key, kv => kv.Value with { Items = [] }, StringComparer.Ordinal);
                return next with { Pictures = pictures };
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportedPacks_AreKept_WhenTheSavedPicturesDoNotHoldWhatWasMigrated_EvenWithNoIssueReported(bool dropWholeCharacter)
    {
        // Round-2 QA review F2: the check compares the saved pictures with the migrated ones; an issue list alone is not enough.
        using var h = new CharactersHarness();
        h.Storage.Files["/in/pack.zip"] = PackFixtures.Zip(("the-onryo.png", PackFixtures.Png("o")));
        var analysis = await h.Library.AnalyseImportAsync("/in/pack.zip", CancellationToken.None);
        Assert.NotNull(h.Library.SaveImport("Mine", "Me", "pack.zip", [new ConfirmedMapping(analysis.Items[0], "character:the-onryo")]).Pack);

        var result = ImportedPackMigrationRunner.Run(new QuietlyLossySettings(h.Host.Settings, dropWholeCharacter), h.Library, h.Store);

        Assert.NotNull(result);
        Assert.True(result.AllCopied);
        Assert.Single(h.Library.Imported);
    }

    [Fact]
    public void NoImportedPacks_MeansNothingToMove()
    {
        using var h = new CharactersHarness();

        Assert.Null(ImportedPackMigrationRunner.Run(h.Host.Settings, h.Library, h.Store));
    }

    [Fact]
    public void RosterConsumer_AddsNewCharactersToThePage_AndBadgesOnlyLaterOnes()
    {
        using var h = new CharactersHarness();
        var vocabulary = Vocabulary.LoadEmbedded();
        var consumer = new RosterConsumer(vocabulary, h.Page, h.Host.Dispatcher);

        consumer.Apply(Document(Entry("K90", "the-first-new", "The First New")));
        Assert.Contains(h.Page.Tiles, t => t.Code == "K90");
        Assert.False(h.Tile("K90").IsNew);

        consumer.Apply(Document(Entry("K90", "the-first-new", "The First New"), Entry("K91", "the-second-new", "The Second New")));
        Assert.True(h.Tile("K91").IsNew);
        Assert.False(h.Tile("K90").IsNew);
    }

    [Fact]
    public void RosterConsumer_RefusesARosterThatClashes_AndChangesNothing()
    {
        using var h = new CharactersHarness();
        var consumer = new RosterConsumer(Vocabulary.LoadEmbedded(), h.Page, h.Host.Dispatcher);
        var before = h.Page.Tiles.Count;

        Assert.Throws<FormatException>(() => consumer.Apply(Document(Entry("K90", "someone-else", "The Trapper"))));
        Assert.Throws<FormatException>(() => consumer.Apply(Document(Entry("K90", "the-onryo", "Another"))));

        Assert.Equal(before, h.Page.Tiles.Count);
    }

    [Fact]
    public void RosterVocabulary_DetectsTheNewCharacterByItsScreenName_WithoutChangingTheEmbeddedOne()
    {
        var embedded = Vocabulary.LoadEmbedded();
        var merged = RosterVocabulary.Merge(embedded, Document(Entry("K90", "the-first-new", "The First New", screen: "FIRST NEW")));

        Assert.Equal(embedded.Entries(VocabularyKind.Killer).Count + 1, merged.Entries(VocabularyKind.Killer).Count);
        Assert.Equal("K90", merged.FindByCode("K90")?.Code);
        Assert.Null(embedded.FindByCode("K90"));
    }
}
