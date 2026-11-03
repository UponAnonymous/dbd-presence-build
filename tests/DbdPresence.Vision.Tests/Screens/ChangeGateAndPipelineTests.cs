// Covers: FR-DET-23 (change-gated text recognition: cheap check every frame, one OCR call per region per change, TimeProvider), FR-DET-13 (only the
// profile's regions are requested), the owner-name rules of FR-DET-19 and the privacy rule (no names in evidence). MECHANICS ONLY: counts of calls
// of a fake recogniser on a fake clock. How long Windows OCR takes per call is UNKNOWN (PENDING-ENV, detection-speed-and-confidence §5.1).
using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class ChangeGateAndPipelineTests
{
    private static readonly NormalizedRect s_whole = new(0, 0, 1, 1);

    private static PixelBuffer Crop(string text, int width = 240, int height = 60) =>
        new HudCanvas(width, height, HudScenes.Background).TextIn(s_whole, text).ToImage().ToPixelBuffer();

    private static (ChangeGatedTextReader Reader, BitmapFontOcr Ocr, FakeTimeProvider Time) Gate(ChangeGateOptions? options = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var ocr = new BitmapFontOcr();
        return (new ChangeGatedTextReader(ocr, time, options), ocr, time);
    }

    // ---- The gate ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnchangedRegion_IsRecognisedOnce_AndThenServedFromTheCache()
    {
        var (reader, ocr, time) = Gate();
        var crop = Crop("ARCHIVES");

        var first = await reader.ReadAsync("r", crop, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);

        Assert.Equal("ARCHIVES", first.Text);
        Assert.True(first.Recognized);
        Assert.Equal("ARCHIVES", second.Text);
        Assert.False(second.Recognized);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal((1L, 1L), (reader.OcrCalls, reader.CacheHits));
    }

    [Fact]
    public async Task AChangedRegion_IsRecognisedAgain_OncePerChange()
    {
        var (reader, ocr, time) = Gate();

        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var changed = await reader.ReadAsync("r", Crop("STORE"), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await reader.ReadAsync("r", Crop("STORE"), CancellationToken.None);
        await reader.ReadAsync("r", Crop("STORE"), CancellationToken.None);

        Assert.Equal("STORE", changed.Text);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task ARegionThatChangesAgainInsideTheMinimumInterval_IsReportedUnread_NotOutdated()
    {
        var (reader, ocr, time) = Gate(new ChangeGateOptions(MinInterval: TimeSpan.FromMilliseconds(500)));
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));

        var deferred = await reader.ReadAsync("r", Crop("STORE"), CancellationToken.None);

        Assert.Null(deferred.Text);
        Assert.False(deferred.Recognized);
        Assert.Equal((1L, 1L), (reader.OcrCalls, reader.Deferred));

        time.Advance(TimeSpan.FromMilliseconds(500));
        var later = await reader.ReadAsync("r", Crop("STORE"), CancellationToken.None);
        Assert.Equal("STORE", later.Text);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task AnUnchangedRegion_IsRecognisedAgainAfterTheMaximumAge()
    {
        var (reader, ocr, time) = Gate(new ChangeGateOptions(MaxAge: TimeSpan.FromSeconds(5)));
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(4));
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        Assert.Equal(1, ocr.Calls);

        time.Advance(TimeSpan.FromSeconds(2));
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task AClockThatStepsBack_IsTreatedAsLongAgo_SoTheNextRecordingSessionIsRead()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 5, 0, TimeSpan.Zero));
        var ocr = new BitmapFontOcr();
        var reader = new ChangeGatedTextReader(ocr, clock);
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);

        clock.Set(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var again = await reader.ReadAsync("r", Crop("STORE"), CancellationToken.None);

        Assert.Equal("STORE", again.Text);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task AnUnchangedRegion_IsReadAgainAfterTheClockStepsBack_NotServedFromAFutureCache()
    {
        // Review U5 F5(c): the same pixels as before, but the clock now reads earlier than the last recognition (a changed system time, or the next
        // recording session of an offline run). The age of the cached reading is unknowable, so it is treated as old and the region is recognised again.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 5, 0, TimeSpan.Zero));
        var ocr = new BitmapFontOcr();
        var reader = new ChangeGatedTextReader(ocr, clock);
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        clock.Set(new DateTimeOffset(2026, 10, 24, 12, 5, 5, TimeSpan.Zero));
        await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        Assert.Equal((1, 1L), (ocr.Calls, reader.CacheHits)); // forward in time: the cache serves

        clock.Set(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var again = await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);

        Assert.Equal("ARCHIVES", again.Text);
        Assert.True(again.Recognized);
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(1L, reader.CacheHits);
    }

    [Fact]
    public async Task AFailedRecognition_IsNeverCached_AndIsRetriedAfterTheInterval()
    {
        var (reader, ocr, time) = Gate();
        ocr.ForceStatus = TextRecognitionStatus.Unavailable;
        var failed = await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        Assert.Null(failed.Text);
        Assert.Equal(TextRecognitionStatus.Unavailable, failed.Status);

        var tooSoon = await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);
        Assert.Null(tooSoon.Text);
        Assert.Equal(1, ocr.Calls);

        ocr.ForceStatus = null;
        time.Advance(TimeSpan.FromSeconds(1));
        var recovered = await reader.ReadAsync("r", Crop("ARCHIVES"), CancellationToken.None);

        Assert.Equal("ARCHIVES", recovered.Text);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task OneChangedDigitInALargeRegion_IsNoticed_ByTheCellThreshold()
    {
        var (reader, ocr, time) = Gate();
        await reader.ReadAsync("t", Crop("EST. 1:30", 400, 40), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));

        var next = await reader.ReadAsync("t", Crop("EST. 1:31", 400, 40), CancellationToken.None);

        Assert.Equal("EST. 1:31", next.Text);
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task EachRegionHasItsOwnState_AndReset_ForgetsThemAll()
    {
        var (reader, ocr, time) = Gate();
        await reader.ReadAsync("a", Crop("ARCHIVES"), CancellationToken.None);
        await reader.ReadAsync("b", Crop("ARCHIVES"), CancellationToken.None);
        Assert.Equal(2, ocr.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        reader.Reset();
        await reader.ReadAsync("a", Crop("ARCHIVES"), CancellationToken.None);

        Assert.Equal(3, ocr.Calls);
    }

    [Fact]
    public void TheGateOptions_RejectUnusableValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeGateOptions(ChangeThreshold: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeGateOptions(CellThreshold: 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeGateOptions(MinInterval: TimeSpan.FromSeconds(5), MaxAge: TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentNullException>(() => new ChangeGatedTextReader(new BitmapFontOcr(), null!));
    }

    // ---- The pipeline --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStaticScreen_CostsRecognitionOnlyOnTheFirstFrame()
    {
        var h = new HudHarness();
        var image = HudScenes.MainMenu(1920);

        var first = await h.Frame(image);
        var afterFirst = h.Pipeline.OcrCalls;
        for (var i = 0; i < 8; i++)
        {
            var frame = await h.Frame(image);
            Assert.Equal(0, frame.OcrCalls);
        }

        Assert.True(first.OcrCalls > 0);
        Assert.Equal(afterFirst, h.Pipeline.OcrCalls);
        Assert.True(h.Pipeline.CacheHits > 0);

        // Past the maximum age (10 s) every region is read once more, so a stale reading cannot live for ever.
        h.Time.Advance(TimeSpan.FromSeconds(5));
        var refreshed = await h.Frame(image);
        Assert.Equal(first.OcrCalls, refreshed.OcrCalls);
    }

    [Fact]
    public async Task OnlyTheAnchorRegions_AreReadUntilAScreenNeedsTheDetailRegions()
    {
        var anchors = HudProfile.Catalogue.Regions.Count(r => r.ReadsText && !r.Detail);
        var details = HudProfile.Catalogue.Regions.Count(r => r.ReadsText && r.Detail);

        // Main menu: the anchors only (the profile name is an anchor since review U5 F10: it can block the weak trial cues).
        var menu = await new HudHarness().Frame(HudScenes.MainMenu(1920));
        Assert.Equal(anchors, menu.OcrCalls);

        // Blank frame: the anchors only (no screen, no detail).
        var blank = await new HudHarness().Frame(HudScenes.Blank(1920));
        Assert.Equal(anchors, blank.OcrCalls);

        // TAB: the anchors, five rows and the REALM - MAP line.
        var tab = await new HudHarness().Frame(HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"));
        Assert.Equal(anchors + HudProfile.PlayerRows + 1, tab.OcrCalls);

        // Scoreboard: the anchors and five rows.
        var scoreboard = await new HudHarness().Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1));
        Assert.Equal(anchors + HudProfile.PlayerRows, scoreboard.OcrCalls);

        // Pre-match lobby: the anchors, four columns and the killer list.
        var lobby = await new HudHarness().Frame(HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"));
        Assert.Equal(anchors + HudProfile.LobbyColumns + 1, lobby.OcrCalls);

        Assert.True(details > HudProfile.PlayerRows);
    }

    [Fact]
    public async Task ChangingFromOneScreenToAnother_ReadsOnlyWhatChanged()
    {
        var h = new HudHarness();
        await h.Frame(HudScenes.MainMenu(1920));

        var toLobby = await h.Frame(HudScenes.Lobby(1920));

        Assert.True(toLobby.OcrCalls > 0);
        Assert.True(toLobby.OcrCalls < HudProfile.Catalogue.Regions.Count(r => r.ReadsText && !r.Detail));
        Assert.Equal(ScreenKind.Lobby, toLobby.Screen);
    }

    [Fact]
    public async Task ThePipelineRequestsOnlyTheProfilesRegions_ForTheCaptureSide()
    {
        var h = new HudHarness();

        Assert.Equal(HudProfile.Catalogue.Regions.Count, h.Pipeline.Regions.Count);
        Assert.All(h.Pipeline.Regions, r => Assert.NotNull(HudProfile.Catalogue.Region(r.Id)));
        foreach (var detector in h.Pipeline.Detectors)
        {
            var wanted = detector.Regions(new DetectionContext(HudHarness.Context, h.Time.GetUtcNow()));
            Assert.All(wanted, r => Assert.Contains(h.Pipeline.Regions, p => p.Id == r.Id));
        }

        // A crop missing from the capture is simply not read; nothing throws.
        var image = HudScenes.MainMenu(1920);
        var partial = HudHarness.Crops(image, h.Pipeline.Regions.Where(r => r.Id == HudRegionIds.Escape));
        var frame = await h.Pipeline.ProcessAsync(partial, image.Width, image.Height, HudHarness.Context, Scene.Unknown, CancellationToken.None);
        Assert.Null(HudHarness.Of(frame, ObservationKind.Scene)!.Value);
    }

    [Fact]
    public async Task TheDetectorsRequestFewerRegions_ThanThePipelineWhenTheyDoNotNeedTheDetail()
    {
        var h = new HudHarness();
        var ctx = new DetectionContext(HudHarness.Context, h.Time.GetUtcNow());
        var scene = h.Pipeline.Detectors.Single(d => d.Id == DetectorId.Scene).Regions(ctx);
        var character = h.Pipeline.Detectors.Single(d => d.Id == DetectorId.Character).Regions(ctx);

        Assert.DoesNotContain(scene, r => r.Id == HudRegionIds.TabRow(0));
        Assert.Contains(character, r => r.Id == HudRegionIds.TabRow(0));
        Assert.Contains(character, r => r.Id == HudRegionIds.TabBand(0));
        await Task.CompletedTask;
    }

    // ---- The owner's name ----------------------------------------------------------------------------------------

    [Fact]
    public void TheOwnerName_IsLearnedAfterThreeIdenticalReadings_AndABreakStartsAgain()
    {
        var tracker = new OwnerNameTracker();

        Assert.Null(tracker.Observe("Tester"));
        Assert.Null(tracker.Observe("Tester"));
        Assert.Equal("Tester", tracker.Observe("Tester"));
        Assert.Equal("Tester", tracker.Observe("Tester"));
        Assert.Equal("Tester", tracker.Observe(null)); // an unread frame breaks a run but keeps what is known

        Assert.Equal("Tester", tracker.Observe("Other"));
        Assert.Equal("Tester", tracker.Observe("Other"));
        Assert.Equal("Other", tracker.Observe("Other")); // a different stable reading replaces the name
    }

    [Fact]
    public void ReadingsThatDifferByOcrConfusionOnly_CountAsTheSameName()
    {
        var tracker = new OwnerNameTracker();

        tracker.Observe("Dl1l");
        tracker.Observe("D1ll");
        Assert.Null(tracker.Current);

        Assert.Equal("Dl1l", tracker.Observe("DIlI")); // l, 1 and I fold to one letter; the first spelling is kept
    }

    [Fact]
    public void AnAlternatingReading_NeverEstablishesAName_AndResetForgetsIt()
    {
        var tracker = new OwnerNameTracker();
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(tracker.Observe(i % 2 == 0 ? "Alpha" : "Bravo"));
        }

        tracker.Observe("Alpha");
        tracker.Observe("Alpha");
        tracker.Observe("Alpha");
        Assert.Equal("Alpha", tracker.Current);
        tracker.Reset();
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task ThePipeline_LearnsTheOwnersNameFromTheMenusTopRight_ThenFindsTheRow()
    {
        var h = new HudHarness(ownerName: null);
        Assert.False(h.Pipeline.OwnerNameKnown);

        // Before the name is known, TAB cannot name the owner's row.
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT");
        Assert.Null(HudHarness.Of(await h.Frame(tab), ObservationKind.Character)!.Value);

        // Three menu frames show the name at the top right.
        for (var i = 0; i < 3; i++)
        {
            await h.Frame(i % 2 == 0 ? HudScenes.MainMenu(1920) : HudScenes.Lobby(1920));
        }

        Assert.True(h.Pipeline.OwnerNameKnown);
        Assert.Equal("dwight-fairfield", HudHarness.Of(await h.Frame(tab), ObservationKind.Character)!.Value);

        h.Pipeline.Reset();
        Assert.False(h.Pipeline.OwnerNameKnown);
    }

    [Fact]
    public async Task ANameFromASetting_WinsOverWhateverTheMenuShows()
    {
        var h = new HudHarness(ownerName: "TESTOWNER");

        for (var i = 0; i < 4; i++)
        {
            await h.Frame(HudScenes.MainMenu(1920, owner: "SOMEONEELSE"));
        }

        var tab = await h.Frame(HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"));
        Assert.Equal("dwight-fairfield", HudHarness.Of(tab, ObservationKind.Character)!.Value);
    }

    // ---- Caches (review U5 F8) -----------------------------------------------------------------------------------

    private static bool HoldsRowText(IEnumerable<string> ids) =>
        ids.Any(id => id.StartsWith("tab.row.", StringComparison.Ordinal) || id.StartsWith("score.row.", StringComparison.Ordinal) || id.StartsWith("hud.row.", StringComparison.Ordinal));

    [Fact]
    public async Task WhenTheScreenChanges_TheRowAndTextCachesOfTheOldScreenAreDropped()
    {
        var h = new HudHarness();
        await h.Frame(HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"));
        Assert.True(h.Pipeline.CachedRowCount > 0);
        Assert.True(HoldsRowText(h.Pipeline.CachedRegionIds));

        await h.Frame(HudScenes.MainMenu(1920));

        Assert.Equal(0, h.Pipeline.CachedRowCount);
        Assert.False(HoldsRowText(h.Pipeline.CachedRegionIds));
        Assert.Contains(HudRegionIds.Escape, h.Pipeline.CachedRegionIds); // the anchors of the new screen are cached as usual
    }

    [Fact]
    public async Task TheTrialColumnAndTheSpectatorBar_AreDroppedWhenTheTrialScreenIsLeft()
    {
        var h = new HudHarness();
        await h.Frame(HudScenes.TrialHud(1920, spectatorBar: true));
        Assert.Contains(HudRegionIds.SpectateBar, h.Pipeline.CachedRegionIds);
        Assert.True(HoldsRowText(h.Pipeline.CachedRegionIds));

        await h.Frame(HudScenes.Lobby(1920));

        Assert.DoesNotContain(HudRegionIds.SpectateBar, h.Pipeline.CachedRegionIds);
        Assert.False(HoldsRowText(h.Pipeline.CachedRegionIds));
    }

    [Fact]
    public async Task WhileTheScreenStaysTheSame_TheCachesAreKept_SoNothingIsRecognisedTwice()
    {
        var h = new HudHarness();
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT");
        await h.Frame(tab);
        var rows = h.Pipeline.CachedRowCount;

        var again = await h.Frame(tab);

        Assert.Equal(0, again.OcrCalls);
        Assert.Equal(rows, h.Pipeline.CachedRowCount);
    }

    [Fact]
    public async Task Reset_DropsTheRowCacheAndEveryCachedText()
    {
        var h = new HudHarness();
        await h.Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1));
        Assert.True(h.Pipeline.CachedRowCount > 0);
        Assert.NotEmpty(h.Pipeline.CachedRegionIds);

        h.Pipeline.Reset();

        Assert.Equal(0, h.Pipeline.CachedRowCount);
        Assert.Empty(h.Pipeline.CachedRegionIds);
    }

    [Fact]
    public async Task TheReaderForgets_OnlyTheRegionsTheCallerSelects()
    {
        var (reader, ocr, time) = Gate();
        await reader.ReadAsync("keep", Crop("ARCHIVES"), CancellationToken.None);
        await reader.ReadAsync("drop.1", Crop("STORE"), CancellationToken.None);
        await reader.ReadAsync("drop.2", Crop("QUESTS"), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));

        reader.Forget(id => id.StartsWith("drop.", StringComparison.Ordinal));

        Assert.Equal(["keep"], reader.CachedRegionIds.ToArray());
        await reader.ReadAsync("keep", Crop("ARCHIVES"), CancellationToken.None);
        await reader.ReadAsync("drop.1", Crop("STORE"), CancellationToken.None);
        Assert.Equal(4, ocr.Calls); // keep came from the cache, drop.1 was read afresh
        Assert.Throws<ArgumentNullException>(() => reader.Forget(null!));
    }

    // ---- Privacy -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoNameReadFromTheScreen_EverReachesAnObservationsEvidence()
    {
        var h = new HudHarness();
        var screens = new[]
        {
            HudScenes.MainMenu(1920),
            HudScenes.PreMatchSurvivor(1920, "DWIGHT FAIRFIELD"),
            HudScenes.PreMatchKiller(1920),
            HudScenes.TrialHud(1920),
            HudScenes.TrialHud(1920, spectatorBar: true),
            HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"),
            HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1),
            HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1, ownerColour: HudScenes.Plain),
        };
        string[] names = [HudScenes.Owner, "ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "ZULU"];

        foreach (var image in screens)
        {
            var frame = await h.Frame(image);
            foreach (var observation in frame.Observations)
            {
                foreach (var name in names)
                {
                    Assert.DoesNotContain(name, observation.Evidence, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        var context = new DetectionContext(HudHarness.Context, h.Time.GetUtcNow(), Scene.InTrial, HudScenes.Owner);
        Assert.DoesNotContain(HudScenes.Owner, context.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OwnerName = set", context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameMemo_ComputesOncePerFrame()
    {
        var crops = new FrameCrops(1920, 1080, new Dictionary<string, ImageBuffer>());
        var calls = 0;

        var a = crops.Memo("k", () => new object[] { ++calls });
        var b = crops.Memo("k", () => new object[] { ++calls });

        Assert.Same(a, b);
        Assert.Equal(1, calls);
        Assert.Null(crops.GetText("none"));
        Assert.Equal("x", crops.WithText(new Dictionary<string, string> { ["r"] = "x" }).GetText("r"));
    }
}
