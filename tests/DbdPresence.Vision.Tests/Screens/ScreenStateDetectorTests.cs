// Covers: FR-DET-02, FR-DET-18 (screen state from text anchors, hud-cue-catalogue.md "States").
// MECHANICS ONLY: the frames are drawn text and flat colour at the catalogue's regions, read back by a fake recogniser. Nothing here measures how
// well Windows OCR reads the game (UNKNOWN, PENDING-ENV) and nothing is accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class ScreenStateDetectorTests
{
    public static TheoryData<string, int> AllScreensAtAllSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var width in HudScenes.Widths)
        {
            foreach (var name in s_screens.Keys)
            {
                data.Add(name, width);
            }
        }

        return data;
    }

    private static readonly Dictionary<string, (Func<int, ImageBuffer> Render, SceneKind Scene, ScreenKind Screen)> s_screens = new()
    {
        ["main menu"] = (w => HudScenes.MainMenu(w), SceneKind.MainMenu, ScreenKind.MainMenu),
        ["mode list"] = (w => HudScenes.ModeList(w, eventList: false), SceneKind.Other, ScreenKind.ModeList),
        ["event mode list"] = (w => HudScenes.ModeList(w, eventList: true), SceneKind.Other, ScreenKind.ModeList),
        ["lobby"] = (w => HudScenes.Lobby(w), SceneKind.Lobby, ScreenKind.Lobby),
        ["searching"] = (w => HudScenes.Searching(w), SceneKind.Matchmaking, ScreenKind.Searching),
        ["searching (event)"] = (w => HudScenes.Searching(w, "HAUNTED BY DAYLIGHT"), SceneKind.Matchmaking, ScreenKind.Searching),
        ["pre-match lobby"] = (w => HudScenes.PreMatchSurvivor(w, "DWIGHT FAIRFIELD"), SceneKind.Lobby, ScreenKind.PreMatchLobby),
        ["match start"] = (w => HudScenes.MatchStart(w, "MOUNT ORMOND RESORT", "ORMOND"), SceneKind.InTrial, ScreenKind.MatchStart),
        ["trial HUD (survivor)"] = (w => HudScenes.TrialHud(w), SceneKind.InTrial, ScreenKind.TrialHud),
        ["trial HUD (killer)"] = (w => HudScenes.TrialHud(w, survivorView: false), SceneKind.InTrial, ScreenKind.TrialHud),
        ["spectating"] = (w => HudScenes.TrialHud(w, spectatorBar: true), SceneKind.Spectating, ScreenKind.Spectating),
        ["match result"] = (w => HudScenes.MatchResult(w), SceneKind.Results, ScreenKind.MatchResult),
        ["scoreboard (survivor)"] = (w => HudScenes.Scoreboard(w, HudScenes.SurvivorGame(), 1), SceneKind.Results, ScreenKind.Scoreboard),
        ["scoreboard (killer)"] = (w => HudScenes.Scoreboard(w, HudScenes.SurvivorGame("THE LICH", 4), 4, survivorView: false, matchLine: false), SceneKind.Results, ScreenKind.Scoreboard),
        ["TAB match details"] = (w => HudScenes.MatchDetails(w, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT"), SceneKind.InMatchMenu, ScreenKind.MatchDetails),
    };

    [Theory]
    [MemberData(nameof(AllScreensAtAllSizes))]
    public async Task EveryCataloguedScreen_IsReadAsItsScene_AtThreeFrameSizes(string name, int width)
    {
        var (render, expectedScene, expectedScreen) = s_screens[name];
        var h = new HudHarness();

        var frame = await h.Frame(render(width));

        var scene = HudHarness.Of(frame, ObservationKind.Scene);
        Assert.NotNull(scene);
        Assert.Equal(expectedScene.ToString(), scene.Value);
        Assert.Equal(expectedScreen, frame.Screen);
        Assert.InRange(scene.Score, 0.6, 1.0);
        Assert.Equal(DetectorId.Scene, scene.Detector);
    }

    [Fact]
    public async Task TheWeakCues_ScoreBelowEveryTwoCueScreen_AndSayWhyInTheirEvidence()
    {
        var h = new HudHarness(ownerName: null);
        var survivorHud = HudHarness.Of(await h.Frame(HudScenes.TrialHud(1920)), ObservationKind.Scene)!;
        var killerHud = HudHarness.Of(await h.Frame(HudScenes.TrialHud(1920, survivorView: false)), ObservationKind.Scene)!;
        var spectating = HudHarness.Of(await h.Frame(HudScenes.TrialHud(1920, spectatorBar: true)), ObservationKind.Scene)!;
        var lobby = HudHarness.Of(await h.Frame(HudScenes.Lobby(1920)), ObservationKind.Scene)!;

        var weak = HudProfile.Catalogue.Thresholds.WeakCueScore;
        Assert.Equal(weak, survivorHud.Score);
        Assert.Equal(weak, killerHud.Score);
        Assert.Equal(weak, spectating.Score);
        Assert.True(lobby.Score > weak);
        Assert.Contains("TrialHud", survivorHud.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWeakCues_NeverOutrankAButton_AndStayBelowTheEnginesMenuToTrialFloor()
    {
        var t = HudProfile.Catalogue.Thresholds;
        var engine = new EngineOptions();

        Assert.True(t.WeakCueScore <= t.OwnNameBonusScore);
        Assert.True(t.OwnNameBonusScore < t.SingleCueCap, "the owner-name bonus must not outrank a lone READY button");
        Assert.True(t.OwnNameBonusScore < engine.TrialEntryFromMenuMinScore, "a weak cue must not carry a menu scene into a trial");
        Assert.True(t.WeakCueScore >= t.MinScore, "a weak cue is still a readable frame for the scene floor of the detector");
    }

    [Fact]
    public async Task TheTrialHud_ScoresHigher_WhenTheFirstRowCarriesTheOwnersName()
    {
        var withName = new HudHarness();
        var without = new HudHarness(ownerName: null);

        var known = HudHarness.Of(await withName.Frame(HudScenes.TrialHud(1920)), ObservationKind.Scene)!;
        var unknown = HudHarness.Of(await without.Frame(HudScenes.TrialHud(1920)), ObservationKind.Scene)!;

        Assert.Equal(HudProfile.Catalogue.Thresholds.OwnNameBonusScore, known.Score);
        Assert.Equal(HudProfile.Catalogue.Thresholds.WeakCueScore, unknown.Score);
    }

    [Fact]
    public async Task ASpectatorBar_IsNeverReadAsTheTrialHud()
    {
        var h = new HudHarness();

        var frame = await h.Frame(HudScenes.TrialHud(1920, spectatorBar: true));

        Assert.Equal("Spectating", HudHarness.Of(frame, ObservationKind.Scene)!.Value);
    }

    [Fact]
    public async Task AnUnreadScreen_WithTextInAnotherScreensAnchor_IsNeverReadAsTheTrialHud()
    {
        // The player column holds names, but the scoreboard header (or a button) holds text that was not understood: that is an unread menu,
        // not a trial. The trial HUD is recognised by the absence of menu anchors, so any unexplained anchor text blocks it.
        string[] anchors =
        [
            HudRegionIds.ScoreHeader, HudRegionIds.Button, HudRegionIds.ResultLabel, HudRegionIds.TabTabs, HudRegionIds.SearchTimer,
            HudRegionIds.Escape, HudRegionIds.ProfileName, // review U5 F10: the corner key hint and the profile name
        ];
        foreach (var anchor in anchors)
        {
            var canvas = HudScenes.Canvas(1920);
            canvas.TextIn(HudScenes.R(HudRegionIds.HudRow(2)), "ALPHA").TextIn(HudScenes.R(HudRegionIds.HudRow(3)), "BRAVO");

            var frame = await new HudHarness().Frame(canvas.TextIn(HudScenes.R(anchor), "ZZZZ QQQQ").ToImage());

            Assert.Null(HudHarness.Of(frame, ObservationKind.Scene)!.Value);
            Assert.Equal(ScreenKind.Unknown, frame.Screen);
        }

        var clean = HudScenes.TrialHud(1920, survivorView: false);
        Assert.Equal("InTrial", HudHarness.Of(await new HudHarness().Frame(clean), ObservationKind.Scene)!.Value);
    }

    [Fact]
    public async Task AMenuListEntry_WithTheTitleAndKeyHintUnread_IsStillAMenu_NeverTheTrialHud()
    {
        // Review U5 F10. The menu list overlaps the player column, so free text there cannot block the HUD cue; a line that reads as one of the
        // menu's own entries can.
        foreach (var entry in new[] { "STORE", "ARCHIVES", "PLAY", "SURVIVOR" })
        {
            var canvas = HudScenes.Canvas(1920);
            canvas.TextIn(HudScenes.R(HudRegionIds.HudRow(2)), "ALPHA").TextIn(HudScenes.R(HudRegionIds.HudRow(3)), "BRAVO").TextIn(HudScenes.R(HudRegionIds.MenuList), entry);

            var frame = await new HudHarness().Frame(canvas.ToImage());

            Assert.Null(HudHarness.Of(frame, ObservationKind.Scene)!.Value);
            Assert.NotEqual(ScreenKind.TrialHud, frame.Screen);
        }
    }

    [Theory]
    [InlineData("A ALPHA D", true)]
    [InlineData("[A] ALPHA [D]", true)]
    [InlineData("< ALPHA >", true)]
    [InlineData("\u2039 A  JOHN SMITH  D \u203A", true)]
    [InlineData("LB ALPHA RB", true)]
    [InlineData("Q J SMITH E", true)]
    [InlineData("YOU HAVE BEEN HOOKED", false)]
    [InlineData("ALPHA", false)]
    [InlineData("A ALPHA", false)]
    [InlineData("ALPHA D", false)]
    [InlineData("ZZZZ QQQQ", false)]
    [InlineData("IT IS ON", false)]
    [InlineData("- HOOKED -", false)]
    [InlineData("A B D", false)]
    [InlineData("", false)]
    public void TheSpectatorBarShape_IsANameBetweenTwoKeyGlyphs_AndNothingElse(string text, bool expected)
    {
        Assert.Equal(expected, AnchorText.HasSpectatorBarShape(text, HudProfile.Catalogue.Thresholds.MinNameLetters));
    }

    [Theory]
    [InlineData("YOU HAVE BEEN HOOKED")]
    [InlineData("ZZZZ QQQQ")]
    [InlineData("MATCH STARTING")]
    public async Task ABannerInTheTopBar_IsNeitherSpectatingNorTheTrialHud(string banner)
    {
        // Review U5 F3: text in the top bar that is not a name between two key glyphs supports no scene at all (and in particular does not mark
        // generators stale by calling the frame Spectating).
        var canvas = HudScenes.Canvas(1920);
        canvas.TextIn(HudScenes.R(HudRegionIds.HudRow(2)), "ALPHA").TextIn(HudScenes.R(HudRegionIds.HudRow(3)), "BRAVO").TextIn(HudScenes.R(HudRegionIds.SpectateBar), banner);

        var frame = await new HudHarness().Frame(canvas.ToImage());

        var scene = HudHarness.Of(frame, ObservationKind.Scene)!;
        Assert.Null(scene.Value);
        Assert.Equal(ScreenKind.Unknown, frame.Screen);
    }

    [Fact]
    public async Task ASpectatorBar_WithKeyGlyphsAroundAName_IsSpectating_AtAWeakScore()
    {
        var canvas = HudScenes.Canvas(1920);
        canvas.TextIn(HudScenes.R(HudRegionIds.HudRow(2)), "ALPHA").TextIn(HudScenes.R(HudRegionIds.HudRow(3)), "BRAVO").TextIn(HudScenes.R(HudRegionIds.SpectateBar), "[Q] JOHN SMITH [E]");

        var scene = HudHarness.Of(await new HudHarness().Frame(canvas.ToImage()), ObservationKind.Scene)!;

        Assert.Equal("Spectating", scene.Value);
        Assert.Equal(HudProfile.Catalogue.Thresholds.WeakCueScore, scene.Score);
        Assert.True(scene.Score < new EngineOptions().TrialEntryFromMenuMinScore);
    }

    [Theory]
    [InlineData(SceneKind.InTrial, SceneKind.InMatchMenu)]
    [InlineData(SceneKind.Spectating, SceneKind.InMatchMenu)]
    [InlineData(SceneKind.InMatchMenu, SceneKind.InMatchMenu)]
    [InlineData(SceneKind.MainMenu, SceneKind.Other)]
    [InlineData(SceneKind.Lobby, SceneKind.Other)]
    [InlineData(SceneKind.Unknown, SceneKind.Other)]
    public async Task TheSettingsMenu_BelongsToTheTrialOnlyWhenTheEngineWasInOne(SceneKind prior, SceneKind expected)
    {
        var h = new HudHarness();

        var frame = await h.Frame(HudScenes.Settings(1920), prior);

        Assert.Equal(expected.ToString(), HudHarness.Of(frame, ObservationKind.Scene)!.Value);
        Assert.Equal(ScreenKind.Settings, frame.Screen);
    }

    [Fact]
    public async Task ABlankFrame_IsUnreadable_WithTheReason()
    {
        var h = new HudHarness();

        var scene = HudHarness.Of(await h.Frame(HudScenes.Blank(1920)), ObservationKind.Scene)!;

        Assert.Null(scene.Value);
        Assert.Contains("score floor", scene.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnchorOffByOneLetter_IsNotAccepted_ButALongerWordMayLoseOne()
    {
        var h = new HudHarness();
        var playTypo = HudScenes.Canvas(1920).TextIn(HudScenes.R(HudRegionIds.Escape), "BACK [ESC]").TextIn(HudScenes.R(HudRegionIds.Button), "PLAV").ToImage();
        var readyTypo = HudScenes.Canvas(1920).TextIn(HudScenes.R(HudRegionIds.Button), "READV").ToImage();

        var play = HudHarness.Of(await h.Frame(playTypo), ObservationKind.Scene)!;
        var ready = HudHarness.Of(await h.Frame(readyTypo), ObservationKind.Scene)!;

        Assert.Null(play.Value); // four letters allow no edit
        Assert.Equal("Lobby", ready.Value); // five letters allow one, and the score shows it
        Assert.True(ready.Score <= 0.8 + 1e-9);
    }

    [Fact]
    public async Task TwoScreensThatAreEquallyStrong_AreAmbiguous_NotGuessed()
    {
        // The lobby's PLAY button and BACK hint, and also the mode list's header and entries: both fully read.
        var clash = HudScenes.Canvas(1920)
            .TextIn(HudScenes.R(HudRegionIds.Escape), "BACK [ESC]")
            .TextIn(HudScenes.R(HudRegionIds.Button), "PLAY")
            .TextIn(HudScenes.R(HudRegionIds.MenuList), "PLAY", "KILLER", "SURVIVOR")
            .ToImage();

        var frame = await new HudHarness().Frame(clash);

        var scene = HudHarness.Of(frame, ObservationKind.Scene)!;
        Assert.Null(scene.Value);
        Assert.Contains("ambiguous", scene.Evidence, StringComparison.Ordinal);
        Assert.Equal(ScreenKind.Unknown, frame.Screen);
    }

    [Fact]
    public async Task AFrameOfAnotherAspectRatio_IsUnreadable_BecauseTheRegionsWereMeasuredOn16By9()
    {
        var h = new HudHarness();
        var wide = new HudCanvas(1920, 800, HudScenes.Background).TextIn(HudScenes.R(HudRegionIds.Button), "PLAY").ToImage();

        var scene = HudHarness.Of(await h.Frame(wide), ObservationKind.Scene)!;

        Assert.Null(scene.Value);
        Assert.Contains("aspect", scene.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonEnglishGame_IsUnreadable()
    {
        var h = new HudHarness();

        var frame = await h.Frame(HudScenes.Lobby(1920), context: HudHarness.Context with { Locale = "de-DE" });

        var scene = HudHarness.Of(frame, ObservationKind.Scene)!;
        Assert.Null(scene.Value);
        Assert.Contains("language", scene.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTextRecognitionIsUnavailable_EveryFrameIsUnreadable_NeverGuessed()
    {
        var h = new HudHarness();
        h.Ocr.ForceStatus = DbdPresence.Core.Capture.TextRecognitionStatus.EnglishLanguageMissing;

        var frame = await h.Frame(HudScenes.MainMenu(1920));

        Assert.Null(HudHarness.Of(frame, ObservationKind.Scene)!.Value);
        Assert.Equal(ScreenKind.Unknown, frame.Screen);
    }

    [Fact]
    public void Detectors_AreDisabledByDefault_AndEmitOnlyTheirOwnKind()
    {
        var h = new HudHarness();

        Assert.All(h.Pipeline.Detectors, d => Assert.Equal(DetectionTier.Disabled, d.DefaultTier));
        Assert.Equal(
            [DetectorId.Scene, DetectorId.QueueMode, DetectorId.Map, DetectorId.Role, DetectorId.Character, DetectorId.Killer, DetectorId.KillerPrompt, DetectorId.Status, DetectorId.SurvivorResult, DetectorId.KillerResult, DetectorId.HookCount],
            h.Pipeline.Detectors.Select(d => d.Id).ToArray());
        Assert.All(h.Pipeline.Detectors, d => Assert.Equal(1, d.Version));
    }

    // This test only records the provenance and the Disabled default; it does not show that the profile cannot publish. That is HudProfileGateTests.
    [Fact]
    public void TheCatalogueProfile_IsAnInitialGuess_AndTheSettingsDefaultsAreDisabled()
    {
        Assert.Equal(ThresholdProvenance.InitialGuess, HudProfile.Catalogue.Provenance);
        Assert.Equal((1920, 1080), (HudProfile.Catalogue.ReferenceWidth, HudProfile.Catalogue.ReferenceHeight));
        Assert.Equal(DetectionTier.Disabled, DetectorSettings.Default.Tier);
        Assert.NotNull(new DetectorSettings(DetectionTier.Validated, ThresholdProvenance.InitialGuess).Problem());
    }

    [Fact]
    public void EveryRegion_IsInsideTheUnitSquare_AndIdsAreUnique()
    {
        var ids = HudProfile.Catalogue.Regions.Select(r => r.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(HudProfile.Catalogue.Regions, r => Assert.True(r.Rect.X + r.Rect.Width <= 1.0 + 1e-9 && r.Rect.Y + r.Rect.Height <= 1.0 + 1e-9));
        Assert.Contains(HudProfile.Catalogue.Regions, r => r.Id == HudRegionIds.Escape && r.ReadsText && !r.Detail);
        Assert.Contains(HudProfile.Catalogue.Regions, r => r.Id == HudRegionIds.TabBand(0) && r.MeasuresColour && !r.ReadsText);
    }

    [Fact]
    public void TheProfile_RejectsInconsistentValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithThresholds(new HudThresholds(MinScore: 1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithThresholds(new HudThresholds(MaxEditFraction: 0.6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithThresholds(new HudThresholds(OwnNameBonusScore: 0.90))); // would outrank READY (0.85)
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithThresholds(new HudThresholds(WeakCueScore: 0.80, OwnNameBonusScore: 0.70)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithHighlight(RowHighlightSettings.InitialGuess with { MinFraction = 0.5, FullFraction = 0.4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithHighlight(RowHighlightSettings.InitialGuess with { Gold = new HueBand(400, 10, 0.5, 0.5) }));
        Assert.Throws<ArgumentException>(() => new HudProfile("p", ThresholdProvenance.InitialGuess, 1920, 1080, 0.02, [HudProfile.Catalogue.Regions[0], HudProfile.Catalogue.Regions[0]], new HudThresholds(), RowHighlightSettings.InitialGuess));
    }
}
