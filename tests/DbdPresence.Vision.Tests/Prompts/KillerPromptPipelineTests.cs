// Covers: FR-DET-22 follow-up (killer identity without TAB: power prompts, the owner's own killer row), D31 and D35 (a closed map from screens, no website),
// DETECTION_VALIDATION section 7 (the detector stays Disabled; no validation report exists).
// MECHANICS ONLY: frames are drawn text on flat colour read back by a fake recogniser. They prove the rules (agreement, reset, hand-over, name filters),
// not that the real game prints these prompts where the profile looks or that Windows OCR reads them. All of that is UNKNOWN (PENDING-ENV).
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Prompts;

public sealed class KillerPromptPipelineTests
{
    private sealed class RecordingSink : IPromptObservationSink
    {
        public List<PromptAssociation> Associations { get; } = [];

        public List<UnmatchedKillerPrompts> Unmatched { get; } = [];

        public void PromptsAssociated(PromptAssociation association) => Associations.Add(association);

        public void KillerNotRecognised(UnmatchedKillerPrompts report) => Unmatched.Add(report);
    }

    private static Observation? Prompt(HudFrame f) => HudHarness.Of(f, ObservationKind.KillerPrompt);

    private static HudHarness Harness(IPromptObservationSink? sink = null, string? owner = HudScenes.Owner) =>
        sink is null ? new HudHarness(owner) : new HudHarness(owner, sink: sink);

    private static ImageBuffer Trial(string? prompt = null) => HudScenes.TrialHud(1920, survivorView: false, prompt: prompt);

    private static ImageBuffer KillerScoreboard(string character = "THE LICH") =>
        HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(character, 4), 4, survivorView: false, matchLine: false);

    // ---- The agreement rule, end to end -----------------------------------------------------------------------------

    [Fact]
    public async Task OnePrompt_NamesNoKiller_HoweverLongItStaysOnScreen()
    {
        var h = Harness();

        var first = await h.Frame(Trial("VILE DARKNESS"));
        HudFrame last = first;
        for (var i = 0; i < 5; i++)
        {
            last = await h.Frame(Trial("VILE DARKNESS"));
        }

        Assert.Equal(ScreenKind.TrialHud, first.Screen);
        Assert.NotNull(Prompt(first));
        Assert.Null(Prompt(first)!.Value);
        Assert.Null(Prompt(last)!.Value);
        Assert.Contains("1 of 2", Prompt(last)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDistinctMappedPrompts_NameTheLich_BelowTheStrongCues()
    {
        var h = Harness();

        _ = await h.Frame(Trial("VILE DARKNESS"));
        var second = await h.Frame(Trial("CAST FLY"));

        var observation = Prompt(second)!;
        Assert.Equal("the-lich", observation.Value);
        Assert.Equal(DetectorId.KillerPrompt, observation.Detector);
        Assert.InRange(observation.Score, 0.5, HudProfile.Catalogue.Thresholds.PromptAgreementCap);
        Assert.DoesNotContain("DARKNESS", observation.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAgreement_HoldsWhileNoPromptIsOnScreen()
    {
        var h = Harness();
        _ = await h.Frame(Trial("VILE DARKNESS"));
        _ = await h.Frame(Trial("CAST FLY"));

        var quiet = await h.Frame(Trial());

        Assert.Equal("the-lich", Prompt(quiet)!.Value);
    }

    [Fact]
    public async Task APromptGlyph_AndOcrNoise_DoNotStopTheMatch()
    {
        var h = Harness();

        _ = await h.Frame(Trial("[RMB] VILE DARKNE55"));
        var second = await h.Frame(Trial("E CAST FLIGHT OF THE DAMNED"));

        Assert.Equal("the-lich", Prompt(second)!.Value);
    }

    [Fact]
    public async Task AnUnmappedPrompt_IsSilence_NotAnUnreadableObservation()
    {
        var h = Harness();

        var frame = await h.Frame(Trial("PICK UP SURVIVOR"));

        Assert.Equal(ScreenKind.TrialHud, frame.Screen);
        Assert.Null(Prompt(frame));
    }

    [Fact]
    public async Task OnAnyOtherScreen_NoPromptObservationIsMade()
    {
        var h = Harness();
        var rows = HudScenes.SurvivorGame();

        foreach (var image in new[] { HudScenes.MainMenu(1920), HudScenes.Lobby(1920), HudScenes.MatchDetails(1920, rows, 1, "ORMOND - MOUNT ORMOND RESORT"), HudScenes.Scoreboard(1920, rows, 1) })
        {
            Assert.Null(Prompt(await h.Frame(image)));
        }
    }

    // ---- Another cue --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnePrompt_PlusTheOwnersKillerRowOnTab_NamesTheKiller()
    {
        var h = Harness();
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame("THE LICH", 4), 4, "ORMOND - MOUNT ORMOND RESORT", survivorView: false);
        var tabFrame = await h.Frame(tab);
        Assert.Equal("the-lich", HudHarness.Of(tabFrame, ObservationKind.Character)!.Value);

        var trial = await h.Frame(Trial("VILE DARKNESS"));

        Assert.Equal("the-lich", Prompt(trial)!.Value);
    }

    [Fact]
    public async Task APromptThatContradictsTheOwnersKillerRow_NamesNobody()
    {
        var h = Harness();
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame("THE NIGHTMARE", 4), 4, "ORMOND - MOUNT ORMOND RESORT", survivorView: false);
        _ = await h.Frame(tab);

        var first = await h.Frame(Trial("VILE DARKNESS"));
        var second = await h.Frame(Trial("CAST FLY"));

        Assert.Null(Prompt(first)!.Value);
        Assert.Null(Prompt(second)!.Value);
        Assert.Contains("contradicts", Prompt(second)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASurvivorsOwnRow_IsNotACorroboratingKiller()
    {
        var h = Harness();
        var tab = HudScenes.MatchDetails(1920, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT");
        _ = await h.Frame(tab);

        var trial = await h.Frame(Trial("VILE DARKNESS"));

        Assert.Null(Prompt(trial)!.Value);
        Assert.Contains("1 of 2", Prompt(trial)!.Evidence, StringComparison.Ordinal); // not "contradicts": the survivor row was never a killer cue
    }

    // ---- A new match starts clean -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("menu")]
    [InlineData("lobby")]
    public async Task WhenTheNextMatchBegins_TheEarlierMatchsPromptsAreForgotten(string screen)
    {
        var h = Harness();
        _ = await h.Frame(Trial("VILE DARKNESS"));
        Assert.Equal("the-lich", Prompt(await h.Frame(Trial("CAST FLY")))!.Value);

        _ = await h.Frame(screen == "menu" ? HudScenes.MainMenu(1920) : HudScenes.Lobby(1920));
        var next = await h.Frame(Trial("VILE DARKNESS"));

        Assert.Null(Prompt(next)!.Value);
        Assert.Contains("1 of 2", Prompt(next)!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_ForgetsThePromptsToo()
    {
        var h = Harness();
        _ = await h.Frame(Trial("VILE DARKNESS"));
        Assert.Equal("the-lich", Prompt(await h.Frame(Trial("CAST FLY")))!.Value);

        h.Pipeline.Reset();
        var next = await h.Frame(Trial("VILE DARKNESS"));

        Assert.Null(Prompt(next)!.Value);
    }

    // ---- The learning hand-over ---------------------------------------------------------------------------------------

    private static async Task PlayKillerMatch(HudHarness h, params string[] prompts)
    {
        // Each prompt appears twice with a quiet frame between (a power prompt comes back whenever the power is used).
        foreach (var prompt in prompts)
        {
            _ = await h.Frame(Trial(prompt));
            _ = await h.Frame(Trial());
            _ = await h.Frame(Trial(prompt));
            _ = await h.Frame(Trial());
        }
    }

    [Fact]
    public async Task WhenTheScoreboardNamesTheKiller_TheRecurringPromptsAreHandedOverOnce()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS", "CAST FLY", "PICK UP SURVIVOR");

        _ = await h.Frame(KillerScoreboard());
        _ = await h.Frame(KillerScoreboard());

        var association = Assert.Single(sink.Associations);
        Assert.Equal("the-lich", association.KillerId);
        Assert.Equal(["CAST FLY", "PICK UP SURVIVOR", "VILE DARKNESS"], association.Prompts.ToArray());
        Assert.Empty(sink.Unmatched);
    }

    [Fact]
    public async Task APromptThatStayedOnScreen_IsOneSighting_AndIsNotHandedOver()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        for (var i = 0; i < 4; i++)
        {
            _ = await h.Frame(Trial("VILE DARKNESS"));
        }

        _ = await h.Frame(KillerScoreboard());

        Assert.Empty(sink.Associations);
    }

    [Fact]
    public async Task AsASurvivor_NothingIsHandedOver()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");

        _ = await h.Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame(), 1));

        Assert.Empty(sink.Associations);
        Assert.Empty(sink.Unmatched);
    }

    [Fact]
    public async Task WhenTheOwnersKillerRowIsNotInTheClosedList_ThePromptsAreReportedWithoutAKiller()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");

        _ = await h.Frame(HudScenes.Scoreboard(1920, HudScenes.SurvivorGame("ZZZZ QQQQ", 4), 4, survivorView: false, matchLine: false));

        Assert.Empty(sink.Associations);
        var report = Assert.Single(sink.Unmatched);
        Assert.Equal(["VILE DARKNESS"], report.Prompts.ToArray());
    }

    [Fact]
    public async Task WhenTheOwnersRowAndTheKillerRowDisagree_NothingIsHandedOver()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");
        var rows = HudScenes.SurvivorGame("THE LICH", 3, killer: "THE NIGHTMARE"); // the owner's row says The Lich, the last row says another killer

        _ = await h.Frame(HudScenes.Scoreboard(1920, rows, 3, survivorView: false, matchLine: false));

        Assert.Empty(sink.Associations);
    }

    [Fact]
    public async Task TheNextMatch_CanHandOverAgain()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");
        _ = await h.Frame(KillerScoreboard());
        _ = await h.Frame(HudScenes.MainMenu(1920));
        await PlayKillerMatch(h, "CAST FLY");

        _ = await h.Frame(KillerScoreboard());

        Assert.Equal(2, sink.Associations.Count);
        Assert.Equal(["CAST FLY"], sink.Associations[1].Prompts.ToArray());
    }

    // ---- Names never reach the learning path ----------------------------------------------------------------------------

    [Theory]
    [InlineData("ALPHA HAS BEEN HOOKED")] // a name read in the player column of the same frame
    [InlineData("TESTOWNER ESCAPED")] // the owner's name
    [InlineData("NEA KARLSSON")] // a character name
    [InlineData("THE NIGHTMARE")] // a killer name
    [InlineData("12 34")] // not a phrase
    [InlineData("OK")] // too short
    [InlineData("FOUR")] // one short word: fewer than six letters
    [InlineData("ONE TWO THREE FOUR FIVE SIX SEVEN")] // a sentence, not a prompt
    public async Task TextThatCouldBeAName_OrIsNotPromptShaped_IsNeverHandedOver(string text)
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, text, "CAST FLY");

        _ = await h.Frame(KillerScoreboard());

        var association = Assert.Single(sink.Associations);
        Assert.Equal(["CAST FLY"], association.Prompts.ToArray());
    }

    [Fact]
    public async Task AShortOwnerName_IsFilteredToo()
    {
        var sink = new RecordingSink();
        var h = Harness(sink, owner: "AMY");
        await PlayKillerMatch(h, "AMY IS BACK NOW", "CAST FLY");

        var rows = HudScenes.SurvivorGame("THE LICH", 4);
        rows[4] = new PlayerLine("THE LICH", "AMY");
        _ = await h.Frame(HudScenes.Scoreboard(1920, rows, 4, survivorView: false, matchLine: false));

        Assert.Equal(["CAST FLY"], Assert.Single(sink.Associations).Prompts.ToArray());
    }

    [Fact]
    public async Task APlayerNameThatAppearsOnlyInThePromptRegion_ButIsListedOnTheScoreboard_IsNeverHandedOver()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "ZEBRAKING HOOKED", "CAST FLY");
        var rows = HudScenes.SurvivorGame("THE LICH", 4);
        rows[1] = new PlayerLine("CLAUDETTE MOREL", "ZEBRAKING");

        _ = await h.Frame(HudScenes.Scoreboard(1920, rows, 4, survivorView: false, matchLine: false));

        Assert.Equal(["CAST FLY"], Assert.Single(sink.Associations).Prompts.ToArray());
    }

    [Fact]
    public async Task AsASurvivor_AnUnknownKillerAndAnUnknownCharacter_ReportNothing()
    {
        // Review F10, mutant M30: without the killer-role check a survivor match whose rows name nothing of the list would look like an unknown killer.
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");
        var rows = HudScenes.SurvivorGame("ZZZZ QQQQ", 1, killer: "XXXX YYYY");

        _ = await h.Frame(HudScenes.Scoreboard(1920, rows, 1));

        Assert.Empty(sink.Associations);
        Assert.Empty(sink.Unmatched);
    }

    [Fact]
    public async Task WhenOnlyTheOwnersCharacterIsUnknown_AndTheKillerRowNamesAKiller_NothingIsReported()
    {
        // Review F10, mutant M31: "unrecognised" needs both rows to be read and unknown.
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");
        var rows = HudScenes.SurvivorGame("ZZZZ QQQQ", 3, killer: "THE LICH");

        _ = await h.Frame(HudScenes.Scoreboard(1920, rows, 3, survivorView: false, matchLine: false));

        Assert.Empty(sink.Associations);
        Assert.Empty(sink.Unmatched);
    }

    [Fact]
    public async Task WhenTheOwnersRowWasNotReadProperly_TheMatchIsNotReportedAsAnUnknownKiller()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        await PlayKillerMatch(h, "VILE DARKNESS");
        var rows = HudScenes.SurvivorGame("THE LICH", 4);
        rows[4] = new PlayerLine(null, HudScenes.Owner); // the character line was not read: one line only

        _ = await h.Frame(HudScenes.Scoreboard(1920, rows, 4, survivorView: false, matchLine: false));

        Assert.Empty(sink.Associations);
        Assert.Empty(sink.Unmatched);
    }

    [Fact]
    public async Task OnlyAHandfulOfDistinctTexts_AreHeldForALongMatch()
    {
        var sink = new RecordingSink();
        var h = Harness(sink);
        var texts = Enumerable.Range(0, 20).Select(i => "SPELL " + string.Concat(Enumerable.Repeat((char)('A' + i), 5)));
        await PlayKillerMatch(h, [.. texts]);

        _ = await h.Frame(KillerScoreboard());

        Assert.True(sink.Associations.Single().Prompts.Count <= 16);
    }

    // ---- Safety -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDetector_IsDisabledByDefault_AndEmitsOnlyItsOwnKind()
    {
        var detector = new KillerPromptDetector(HudProfile.Catalogue, HudHarness.Lists);

        Assert.Equal(DetectionTier.Disabled, detector.DefaultTier);
        Assert.Equal(DetectorId.KillerPrompt, detector.Id);
        Assert.Equal(ObservationKind.KillerPrompt, Observation.KindOf(detector.Id));
        Assert.Contains(detector.Regions(new DbdPresence.Vision.Detection.DetectionContext(HudHarness.Context, DateTimeOffset.UnixEpoch)), r => r.Id == HudRegionIds.PowerPrompt);
    }

    [Fact]
    public async Task ThePromptRegion_IsReadOnlyWhileTheTrialHudIsUp()
    {
        var h = Harness();

        _ = await h.Frame(HudScenes.MainMenu(1920));
        Assert.DoesNotContain(HudRegionIds.PowerPrompt, h.Pipeline.CachedRegionIds);
        _ = await h.Frame(Trial("VILE DARKNESS"));
        Assert.Contains(HudRegionIds.PowerPrompt, h.Pipeline.CachedRegionIds);
        _ = await h.Frame(HudScenes.MainMenu(1920));
        Assert.DoesNotContain(HudRegionIds.PowerPrompt, h.Pipeline.CachedRegionIds);
    }
}
