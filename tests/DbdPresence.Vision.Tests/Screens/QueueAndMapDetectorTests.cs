// Covers: FR-DET-04 (map and realm from text, closed vocabulary), FR-DET-20 (map from the match start card and the REALM - MAP line, realm must
// belong to the map), FR-DET-21 (normal versus event queue). MECHANICS ONLY: drawn text read back by a fake recogniser; not accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class QueueAndMapDetectorTests
{
    private const string EventId = "event-haunted-by-daylight";

    // ---- Queue mode ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TheNormalModeList_ReportsNormal_AtEverySize(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.ModeList(width, eventList: false));

        var queue = HudHarness.Of(frame, ObservationKind.QueueMode)!;
        Assert.Equal(QueueModes.Normal, queue.Value);
        Assert.Equal(DetectorId.QueueMode, queue.Detector);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TheEventModeList_ReportsTheEventFromTheSecondLine_AtEverySize(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.ModeList(width, eventList: true));

        Assert.Equal(EventId, HudHarness.Of(frame, ObservationKind.QueueMode)!.Value);
    }

    [Fact]
    public async Task AnEventNameThatIsNotInTheClosedList_IsUnreadable_NotNormal_AndNotInvented()
    {
        var frame = await new HudHarness().Frame(HudScenes.ModeList(1920, eventList: true, eventName: "SPRING SHOWDOWN"));

        var queue = HudHarness.Of(frame, ObservationKind.QueueMode)!;
        Assert.Null(queue.Value);
        Assert.Contains("not in the closed list", queue.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEventNameIsReadFromTheTextNotHardCoded_SoAnExtraListEntryIsFoundToo()
    {
        var extra = HudHarness.Vocabulary.WithEventModes([new VocabularyEntry("event-spring-showdown", "Spring Showdown")]);
        var h = new HudHarness();
        var pipeline = new HudDetectionPipeline(h.Profile, HudVocabulary.Create(extra), h.Ocr, h.Time, ownerNameOverride: HudScenes.Owner);
        var image = HudScenes.ModeList(1920, eventList: true, eventName: "SPRING SHOWDOWN");

        var frame = await pipeline.ProcessAsync(HudHarness.Crops(image, pipeline.Regions), image.Width, image.Height, HudHarness.Context, Scene.Unknown, CancellationToken.None);

        Assert.Equal("event-spring-showdown", HudHarness.Of(frame, ObservationKind.QueueMode)!.Value);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task SearchingWithAnEventLabel_ReportsTheEvent_AtEverySize(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.Searching(width, "HAUNTED BY DAYLIGHT"));

        Assert.Equal(EventId, HudHarness.Of(frame, ObservationKind.QueueMode)!.Value);
    }

    [Fact]
    public async Task SearchingWithNoLabel_ReportsNothing_BecauseAbsenceIsNotEvidenceOfTheNormalQueue()
    {
        var frame = await new HudHarness().Frame(HudScenes.Searching(1920));

        Assert.Null(HudHarness.Of(frame, ObservationKind.QueueMode));
        Assert.Equal("Matchmaking", HudHarness.Of(frame, ObservationKind.Scene)!.Value);
    }

    [Fact]
    public async Task SearchingWithAnUnlistedLabel_IsUnreadable()
    {
        var frame = await new HudHarness().Frame(HudScenes.Searching(1920, "SOMETHING NEW"));

        Assert.Null(HudHarness.Of(frame, ObservationKind.QueueMode)!.Value);
    }

    [Fact]
    public async Task TheMainMenuListsPlayEvent_ButThatIsNotTheModeListHeader()
    {
        // The main menu has PLAY EVENT as an entry; with QUIT [ESC] in the corner it is the main menu, and no queue is reported.
        var frame = await new HudHarness().Frame(HudScenes.MainMenu(1920));

        Assert.Null(HudHarness.Of(frame, ObservationKind.QueueMode));
        Assert.Equal(ScreenKind.MainMenu, frame.Screen);
    }

    // ---- Map -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TheMatchStartCard_GivesTheMap_WhenMapAndRealmBelongTogether(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.MatchStart(width, "THE SHATTERED SQUARE", "THE DECIMATED BORGO"));

        var map = HudHarness.Of(frame, ObservationKind.Map)!;
        Assert.Equal("the-decimated-borgo/the-shattered-square", map.Value);
        Assert.Equal(DetectorId.Map, map.Detector);
        Assert.Equal(ScreenKind.MatchStart, frame.Screen);
    }

    [Fact]
    public async Task AMapWhoseRealmTextDisagrees_IsUnreadable()
    {
        // Mount Ormond Resort belongs to Ormond, not to The Decimated Borgo.
        var frame = await new HudHarness().Frame(HudScenes.MatchStart(1920, "MOUNT ORMOND RESORT", "THE DECIMATED BORGO"));

        Assert.Null(HudHarness.Of(frame, ObservationKind.Map)?.Value);
        Assert.NotEqual(ScreenKind.MatchStart, frame.Screen);
    }

    [Fact]
    public async Task AMapNameWithTwoLettersMisread_IsStillTheMap_ButAnUnknownNameIsNot()
    {
        var misread = await new HudHarness().Frame(HudScenes.MatchStart(1920, "MOUNT 0RMOND RES0RT", "ORMOND"));
        var unknown = await new HudHarness().Frame(HudScenes.MatchStart(1920, "THE MADE UP PLACE", "ORMOND"));

        Assert.Equal("ormond/mount-ormond-resort", HudHarness.Of(misread, ObservationKind.Map)!.Value);
        Assert.Null(HudHarness.Of(unknown, ObservationKind.Map)?.Value);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(1280)]
    public async Task TabMatchDetails_GivesTheMapFromTheRealmMapLine_AtEverySize(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(width, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"));

        Assert.Equal("ormond/mount-ormond-resort", HudHarness.Of(frame, ObservationKind.Map)!.Value);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(2560)]
    public async Task TheLongestSeenRealmMapLine_IsReadWhereTheTextFits(int width)
    {
        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(width, HudScenes.SurvivorGame(), 1, "THE DECIMATED BORGO - THE SHATTERED SQUARE"));

        Assert.Equal("the-decimated-borgo/the-shattered-square", HudHarness.Of(frame, ObservationKind.Map)!.Value);
    }

    [Fact]
    public async Task TheRealmMapLine_DoesNotNeedTheDashToBeRead()
    {
        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND MOUNT ORMOND RESORT"));

        Assert.Equal("ormond/mount-ormond-resort", HudHarness.Of(frame, ObservationKind.Map)!.Value);
    }

    [Fact]
    public async Task ARealmMapLineWhoseMapIsFromAnotherRealm_IsUnreadable()
    {
        var frame = await new HudHarness().Frame(HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - THE SHATTERED SQUARE"));

        var map = HudHarness.Of(frame, ObservationKind.Map)!;
        Assert.Null(map.Value);
        Assert.Contains("agrees", map.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherScreensReportNoMap()
    {
        var h = new HudHarness();

        Assert.Null(HudHarness.Of(await h.Frame(HudScenes.MainMenu(1920)), ObservationKind.Map));
        Assert.Null(HudHarness.Of(await h.Frame(HudScenes.Lobby(1920)), ObservationKind.Map));
        Assert.Null(HudHarness.Of(await h.Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1)), ObservationKind.Map));
    }

    [Fact]
    public void EveryMapInTheVocabulary_BelongsToExactlyOneRealm_TheLinkTheDetectorsRelyOn()
    {
        foreach (var map in HudHarness.Vocabulary.Entries(VocabularyKind.Map))
        {
            Assert.NotNull(map.Group);
            Assert.Equal(map.Group, HudHarness.Lists.RealmOfMap(map.Id));
            Assert.Contains(HudHarness.Vocabulary.Entries(VocabularyKind.Realm), r => r.Id == map.Group);
            Assert.StartsWith(map.Group + "/", map.Id, StringComparison.Ordinal);
        }
    }
}
