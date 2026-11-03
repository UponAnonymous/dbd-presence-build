// Covers: QA review F3 (a remove never deletes a file an older per-image choice still uses), F8 (the busy state and the stage are always left again,
// whatever the helper does), F9 (nothing is handed to the saving-online queue before the picture rules are accepted and the user is signed in),
// F12 (the confirm step of an import shows progress and can be stopped), security review L1 (a network path is refused), FR-PACK-10, FR-PACK-12,
// FR-PACK-16, D28. S5b-5.
using DbdPresence.Core.Pictures;
using DbdPresence.Presentation.Characters;
using DbdPresence.Presentation.Tests.Support;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class CharactersViewModelHardeningTests
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

    // ---- F9: the upload gate -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, PictureRules.Accepted, 0)] // signed out
    [InlineData(true, PictureRules.Unset, 0)] // rules not answered
    [InlineData(true, PictureRules.Declined, 0)] // rules declined
    [InlineData(true, PictureRules.Accepted, 1)]
    public async Task APictureIsHandedToTheQueue_OnlyWhenSignedInAndTheRulesAreAccepted(bool signedIn, PictureRules rules, int expected)
    {
        using var h = new CharactersHarness();
        h.Environment.SignedIn = signedIn;
        h.Host.Settings.Update(s => s with { PictureRules = rules });
        h.Select("K27");

        await AddFromPc(h);

        Assert.Equal(expected, h.Sink.Saved.Count);
        Assert.Single(h.Current.Pictures["character:the-onryo"].Items); // the picture itself is kept either way
    }

    [Fact]
    public async Task AnImportedPicture_IsNotHandedToTheQueue_WhileTheRulesAreUnanswered()
    {
        using var h = new CharactersHarness();
        h.Environment.SignedIn = true;
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"));

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(h.Sink.Saved);
        Assert.Single(h.Current.Pictures["character:the-onryo"].Items);
    }

    [Fact]
    public async Task PicturesAddedWhileSignedOut_AreHandedOver_WhenTheUserSignsIn()
    {
        using var h = new CharactersHarness();
        h.Host.Settings.Update(s => s with { PictureRules = PictureRules.Accepted });
        h.Select("K27");
        await AddFromPc(h);
        Assert.Empty(h.Sink.Saved);

        h.Environment.SignedIn = true;
        h.Environment.Raise();

        var request = Assert.Single(h.Sink.Saved);
        Assert.Equal("character:the-onryo", request.Key);
    }

    [Fact]
    public async Task APictureThatIsAlreadySavedOnline_IsNotHandedOverAgain()
    {
        using var h = new CharactersHarness();
        h.Host.Settings.Update(s => s with { PictureRules = PictureRules.Accepted });
        h.Select("K27");
        await AddFromPc(h);
        var sha = h.Current.Pictures["character:the-onryo"].Items.Single().Sha256;
        h.Environment.SavedUrlBySha256 = new Dictionary<string, string> { [sha] = "https://cdn.example.org/u/0123456789abcdef0123456789abcdef.png" };

        h.Environment.SignedIn = true;
        h.Environment.Raise();

        Assert.Empty(h.Sink.Saved);
    }

    // ---- F3: a file an older choice still uses -----------------------------------------------------------------------

    [Fact]
    public async Task Remove_KeepsTheFile_WhileAnOlderPerImageChoiceStillPointsAtIt()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        await AddFromPc(h);
        var item = h.Current.Pictures["character:the-onryo"].Items.Single();
        var path = h.Store.PathOf(item)!;
        h.Host.Settings.Update(s => s with { Packs = s.Packs with { Overrides = new Dictionary<string, string> { ["role:killer"] = item.File! } } });

        h.Page.PictureOptions.Single(o => o.Item is not null).RemoveCommand.Execute(null);
        h.Page.PictureOptions.Single(o => o.Item is not null).ConfirmRemoveCommand.Execute(null);

        Assert.False(h.Current.Pictures.ContainsKey("character:the-onryo"));
        Assert.True(h.Storage.Exists(path));
        Assert.Equal(item.File, h.Current.Packs.Overrides["role:killer"]);
        Assert.Equal([item.Sha256], h.Sink.Removed); // the online copy of a picture nobody uses is still dropped
    }

    // ---- F8: the busy state is always left ---------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheHelperThrows_TheBusyTextIsCleared_AndAMessageIsShown()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Fitter.Override = (_, _) => throw new InvalidOperationException("helper crashed");

        await AddFromPc(h);

        Assert.Null(h.Page.BusyText);
        Assert.Contains("could not be added", h.Page.ErrorMessage, StringComparison.Ordinal);
        Assert.False(h.Current.Pictures.ContainsKey("character:the-onryo"));
    }

    [Fact]
    public async Task WhenTheArchiveCannotBeRead_TheStageGoesBackToBrowse()
    {
        using var h = new CharactersHarness();
        h.Picker.Archive = "/in/missing.zip";

        await h.Page.ImportPackCommand.ExecuteAsync(null);

        Assert.Equal(CharactersStage.Browse, h.Page.Stage);
        Assert.Null(h.Page.ReadingText);
        Assert.NotNull(h.Page.ImportError);
    }

    [Fact]
    public async Task ANetworkPath_IsRefused_AndNothingIsReadFromIt()
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Picker.Image = @"\\server\share\me.png";

        await h.Page.AddFromPcCommand.ExecuteAsync(null);

        Assert.Null(h.Page.BusyText);
        Assert.NotNull(h.Page.ErrorMessage);
        Assert.Empty(h.Fitter.Edges);
        Assert.False(h.Current.Pictures.ContainsKey("character:the-onryo"));
    }

    [Fact]
    public async Task WhenTheHelperThrowsForOneImportedPicture_ThatPictureIsCountedFailed_AndTheRestAreImported()
    {
        using var h = new CharactersHarness();
        var calls = 0;
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"), ("K01_TheTrapper.png", "two"));
        h.Fitter.Override = (edge, salt) => ++calls == 1 ? throw new InvalidOperationException("helper crashed") : FakePng.Make(edge, edge, 3000, salt);

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(CharactersStage.Browse, h.Page.Stage);
        Assert.Null(h.Page.ReadingText);
        Assert.Single(h.Current.Pictures);
        Assert.Contains("1 could not be imported", h.Page.ImportResultText, StringComparison.Ordinal);
    }

    // ---- F12: progress and stop on the confirm step ------------------------------------------------------------------

    [Fact]
    public async Task TheConfirmStep_ShowsWhichPictureIsBeingSaved()
    {
        using var h = new CharactersHarness();
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"), ("K01_TheTrapper.png", "two"), ("K02_TheWraith.png", "three"));
        var seen = new List<string?>();
        h.Fitter.Override = (edge, salt) =>
        {
            seen.Add(h.Page.ReadingText);
            return FakePng.Make(edge, edge, 3000, salt);
        };

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["Saving picture 1 of 3…", "Saving picture 2 of 3…", "Saving picture 3 of 3…"], seen);
        Assert.Null(h.Page.ReadingText);
    }

    [Fact]
    public async Task StoppingTheConfirmStep_KeepsWhatWasSaved_AndSaysItStoppedEarly()
    {
        using var h = new CharactersHarness();
        var review = await OpenReview(h, ("K27_TheOnryo.png", "one"), ("K01_TheTrapper.png", "two"), ("K02_TheWraith.png", "three"));
        var calls = 0;
        h.Fitter.Override = (edge, salt) =>
        {
            if (++calls == 2)
            {
                h.Page.CancelReadingCommand.Execute(null);
            }

            return FakePng.Make(edge, edge, 3000, salt);
        };

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Equal(2, h.Current.Pictures.Count);
        Assert.Equal(CharactersStage.Browse, h.Page.Stage);
        Assert.Contains("stopped it early", h.Page.ImportResultText, StringComparison.Ordinal);
        Assert.True(h.Page.CanUndoImport);
    }

    // ---- L6 through the path the real presence uses ----------------------------------------------------------------

    [Theory]
    [InlineData(PictureRules.Unset, true)]
    [InlineData(PictureRules.Accepted, true)]
    [InlineData(PictureRules.Declined, false)]
    public void AChosenLink_ReachesTheRealPresencePath_OnlyWhenTheRulesAreNotDeclined(PictureRules rules, bool reachesDiscord)
    {
        using var h = new CharactersHarness();
        h.Select("K27");
        h.Page.LinkText = "https://example.com/onryo.png";
        h.Page.AddLinkCommand.Execute(null);
        h.Host.Settings.Update(s => s with { PictureRules = rules });

        var resolution = h.Resolver.Resolve(h.Current, "The Onryō");

        Assert.NotNull(resolution);
        Assert.Equal(reachesDiscord ? "https://example.com/onryo.png" : null, resolution.Discord.Reference);
    }
}
