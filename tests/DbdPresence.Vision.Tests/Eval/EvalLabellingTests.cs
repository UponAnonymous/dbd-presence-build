// Covers: FR-DET-16 and FR-DET-24 (the evaluator tool's labelling half), DETECTION_VALIDATION section 4 (labels, blind re-check) and section 16 (how labels are
// produced and why they stay independent), PLAN_V2 D25 (the 30-day clean-up covers the files the tool adds).
// MECHANICS ONLY: the recordings are drawn text on flat colour. Nothing here is accuracy.
using System.Text.Json;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class EvalLabellingTests : IDisposable
{
    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private readonly EvalWorkspace _w = new();

    public void Dispose() => _w.Dispose();

    private string Recording(string name = EvalWorkspace.FirstRecording)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        return folder;
    }

    // ---- Command line -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCommandLine_IsStrict_AndNeverEchoesAnOptionValue()
    {
        var code = _w.Run("evaluate", "--owner-name", "SECRETNAME", "--bogus", "x");

        Assert.Equal(2, code);
        Assert.DoesNotContain("SECRETNAME", _w.Output + _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(2, _w.Run("evaluate", "--out"));
        Assert.Equal(2, _w.Run("evaluate", "--out", "a", "--out", "b"));
        Assert.Equal(2, _w.Run("nonsense"));
        Assert.Equal(0, _w.Run("help"));
    }

    // ---- Preparing a recording ----------------------------------------------------------------------------------------

    [Fact]
    public void Prepare_FilesARecordingInPlace_TheEvaluatorLoadsIt_AndNoPictureIsCopied()
    {
        var folder = Recording();
        var before = Directory.GetFiles(folder, "frame-*.png").Length;

        Assert.Equal(0, _w.Run("prepare"));

        var session = DatasetLoader.LoadSession(folder);
        Assert.Equal(16, session.Frames.Count);
        Assert.Equal("tuning", session.Metadata.Split);
        Assert.Equal("Borderless", session.Metadata.WindowMode);
        Assert.Equal(1280, session.Metadata.Width);
        Assert.Equal(before, Directory.GetFiles(folder, "frame-*.png").Length);
        Assert.Equal("frame-00001.png", session.Frames[0].File);
        Assert.Equal(0, _w.Run("prepare"));
        Assert.Contains("Nothing new", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_FilesTheFirstTwoRecordingsAsTuning_ThenHeldOut_AndTheOwnerCanOverride()
    {
        var names = new[] { "2026-10-24_19-30-12", "2026-10-24_20-30-12", "2026-10-24_21-30-12", "2026-10-24_22-30-12" };
        foreach (var n in names)
        {
            _w.AddRecording(n, EvalWorkspace.MatchScreens());
        }

        Assert.Equal(0, _w.Run("prepare"));
        Assert.Equal(["tuning", "tuning", "heldout", "heldout"], StudySessions.LoadPrepared(_w.Root).Select(s => s.Metadata.Split).ToArray());

        var extra = _w.AddRecording("2026-10-24_23-30-12", EvalWorkspace.MatchScreens());
        Assert.Equal(0, _w.Run("prepare", "--split", "tuning", "--window-mode", "Fullscreen"));
        var session = DatasetLoader.LoadSession(extra);
        Assert.Equal("tuning", session.Metadata.Split);
        Assert.Equal("Fullscreen", session.Metadata.WindowMode);
    }

    [Fact]
    public void Prepare_RefusesARecordingWhoseWindowChangedSize_AndOneThatDoesNotSayHowItWasShown()
    {
        var resized = _w.AddRecording("2026-10-24_19-30-12", EvalWorkspace.MatchScreens());
        var metaPath = Path.Combine(resized, "metadata.json");
        File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace("\"1280x720\"", "\"1280x720\", \"1920x1080\"", StringComparison.Ordinal));

        var unknown = _w.AddRecording("2026-10-24_20-30-12", EvalWorkspace.MatchScreens(), mode: DbdPresence.Core.Capture.WindowDisplayMode.Unknown);

        Assert.Equal(1, _w.Run("prepare"));
        Assert.Contains("changed size", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Contains("--window-mode", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(resized, SessionFiles.Session)));
        Assert.False(File.Exists(Path.Combine(unknown, SessionFiles.Session)));
    }

    [Fact]
    public void TheThirtyDayCleanUp_DeletesTheFilesTheEvaluatorAdded_WithThePictures()
    {
        var folder = Recording();
        _w.LabelAndRecheck(folder, s_scenes, "heldout");
        var vocabulary = Vocabulary.LoadEmbedded();
        _ = ReviewSessions.BuildLabelPage(folder, vocabulary, new ExtensionFrameDecoder(), _ => { });
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.ReviewPage)));
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.RecheckResult)));

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 19, 30, 12, TimeSpan.Zero).AddDays(31));
        var report = SessionRetention.Sweep(_w.Root, time);

        Assert.Equal([EvalWorkspace.FirstRecording], report.Deleted);
        Assert.Empty(report.Problems);
        Assert.False(Directory.Exists(folder));
    }

    // ---- Proposals (a navigation aid, independent of the detectors) ---------------------------------------------------

    private static byte[] Flat(byte value) => Enumerable.Repeat(value, SegmentProposer.GridWidth * SegmentProposer.GridHeight).ToArray();

    [Fact]
    public void Proposals_BreakWhereThePicturesChange_AndCoverEveryPictureOnce()
    {
        var times = Enumerable.Range(0, 12).Select(i => DateTimeOffset.UnixEpoch.AddSeconds(2 * i)).ToList();
        var sigs = Enumerable.Range(0, 12).Select(i => Flat(i < 4 ? (byte)20 : i < 8 ? (byte)120 : (byte)220)).ToList();

        var ranges = SegmentProposer.Propose(sigs, times);

        Assert.Equal([(0, 3), (4, 7), (8, 11)], ranges);
    }

    [Fact]
    public void Proposals_ShowAGapInTimeAsABreak_AndMergeRunsOfChangingPictures()
    {
        var times = Enumerable.Range(0, 6).Select(i => DateTimeOffset.UnixEpoch.AddSeconds(2 * i)).ToList();
        times[3] = times[2].AddSeconds(60);
        times[4] = times[3].AddSeconds(2);
        times[5] = times[4].AddSeconds(2);
        var gap = SegmentProposer.Propose(Enumerable.Range(0, 6).Select(_ => Flat(50)).ToList(), times);

        Assert.Equal([(0, 2), (3, 5)], gap);

        // Live 3D: every picture differs from the next. One run of changing pictures is one segment, not one per picture.
        var moving = Enumerable.Range(0, 9).Select(i => Flat((byte)(i % 2 == 0 ? 30 : 200))).ToList();
        var steadyTimes = Enumerable.Range(0, 9).Select(i => DateTimeOffset.UnixEpoch.AddSeconds(2 * i)).ToList();
        Assert.Equal([(0, 8)], SegmentProposer.Propose(moving, steadyTimes));
    }

    [Fact]
    public void Proposals_KeepAShortSteadyScreenBetweenLongOnes_ForExampleTheMatchStartCard()
    {
        var values = new byte[] { 30, 30, 30, 30, 200, 200, 90, 90, 90, 90 };
        var times = Enumerable.Range(0, values.Length).Select(i => DateTimeOffset.UnixEpoch.AddSeconds(2 * i)).ToList();

        var ranges = SegmentProposer.Propose([.. values.Select(Flat)], times);

        Assert.Equal([(0, 3), (4, 5), (6, 9)], ranges);
    }

    [Fact]
    public void Thumbnails_AreTheFirstMiddleAndLastPicture()
    {
        Assert.Equal([5], SegmentProposer.ThumbnailFrames(5, 5));
        Assert.Equal([5, 6], SegmentProposer.ThumbnailFrames(5, 6));
        Assert.Equal([0, 3, 7], SegmentProposer.ThumbnailFrames(0, 7));
    }

    [Fact]
    public void ThePlan_IsMadeFromThePicturesAlone_AndCoversTheWholeSession()
    {
        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, EvalWorkspace.MatchScreens());
        _w.Run("prepare");

        var page = ReviewSessions.BuildLabelPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder(), _ => { });

        var plan = ReviewPlan.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
        Assert.Equal(1, plan.Segments[0].FromFrame);
        Assert.Equal(16, plan.Segments[^1].ToFrame);
        Assert.True(File.Exists(page));
    }

    // ---- The review page ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheReviewPage_IsOneSelfContainedFile_WithNoAddressAndTheEmbeddedDataParses()
    {
        var folder = Recording();
        _w.Run("prepare");

        var page = ReviewSessions.BuildLabelPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder(), _ => { });

        var html = File.ReadAllText(page);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"//", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", html, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
        var start = html.IndexOf("const D = ", StringComparison.Ordinal) + "const D = ".Length;
        var end = html.IndexOf(";\n", start, StringComparison.Ordinal);
        using var data = JsonDocument.Parse(html[start..end]);
        Assert.Equal("labels", data.RootElement.GetProperty("mode").GetString());
        Assert.Equal(4, data.RootElement.GetProperty("segments").GetArrayLength());
        Assert.False(data.RootElement.TryGetProperty("prefill", out _));
        Assert.Equal(ReviewSessions.AnswerFileName(EvalWorkspace.FirstRecording), data.RootElement.GetProperty("answerFile").GetString());
    }

    // ---- Applying answers --------------------------------------------------------------------------------------------

    [Fact]
    public void Answers_BecomeLabelsTheEvaluatorLoads_WithOneUnitPerConfirmedScreenAndPerFact()
    {
        var folder = Recording();
        _w.Run("prepare");
        var file = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(file, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));

        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", file));

        var session = DatasetLoader.LoadSession(folder);
        var scene = session.Labels.Where(l => l.Detector == "scene").ToList();
        Assert.Equal(s_scenes, scene.Select(l => l.Label).ToArray());
        Assert.Equal(["000001", "000005", "000009", "000013"], scene.Select(l => l.FromFrame).ToArray());
        Assert.All(session.Labels, l => Assert.Equal("labelled", l.ReviewStatus));
        Assert.All(session.Labels, l => Assert.Equal("t01", l.TrialId));
        var facts = session.Labels.Where(l => l.Type == "trial-fact").ToDictionary(l => l.Detector, l => l.Label);
        Assert.Equal("Survivor", facts["role"]);
        Assert.Equal("dwight-fairfield", facts["character"]);
        Assert.Equal("ormond/mount-ormond-resort", facts["map"]);
        Assert.Equal("the-lich", facts["killer"]);
        Assert.Equal("Escaped", facts["result.survivor"]);
        Assert.False(facts.ContainsKey("queue-mode"));
        Assert.All(session.Labels.Where(l => l.Type == "trial-fact"), l => Assert.Equal(("000001", "000016"), (l.FromFrame, l.ToFrame)));
        Assert.Equal(1, GuidedLabel.CountMatches(_w.Root));
    }

    [Fact]
    public void ACantTellAnswer_IsWrittenAsIllegible_AndNoSceneAnswerMeansNoUnit()
    {
        var folder = Recording();
        _w.Run("prepare");
        var file = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(file, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, ["MainMenu", "None", "InMatchMenu", "Results"], role: "?", character: "?"));

        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", file));

        var labels = DatasetLoader.LoadSession(folder).Labels;
        Assert.Equal(3, labels.Count(l => l.Detector == "scene"));
        Assert.DoesNotContain(labels, l => l.Detector == "scene" && l.FromFrame == "000005");
        Assert.All(labels.Where(l => l.Detector is "role" or "character"), l =>
        {
            Assert.False(l.Legible);
            Assert.Equal(LabelFactory.UnclearLabel, l.Label);
        });
    }

    [Fact]
    public void Answers_AreValidated_AgainstTheClosedListsAndThePlan()
    {
        var folder = Recording();
        _w.Run("prepare");
        var id = EvalWorkspace.FirstRecording;
        var plan = ReviewPlan.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
        var lists = HudVocabulary.Create(Vocabulary.LoadEmbedded());
        var good = EvalWorkspace.Answers(id, s_scenes);

        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(EvalWorkspace.Answers(id, s_scenes, character: "not-a-character"), lists, plan, "labels"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(EvalWorkspace.Answers(id, s_scenes, map: "nowhere/nothing"), lists, plan, "labels"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(EvalWorkspace.Answers(id, s_scenes, role: "Spectator"), lists, plan, "labels"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(EvalWorkspace.Answers(id, ["MainMenu", "Lobby", "Banana", "Results"]), lists, plan, "labels"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(EvalWorkspace.Answers(id, s_scenes, only: [0, 1]), lists, plan, "labels"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(good.Replace(id, "2020-01-01_00-00-00", StringComparison.Ordinal), lists, plan, "labels"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse(good, lists, plan, "recheck"));
        Assert.Throws<FormatException>(() => ReviewAnswers.Parse("{", lists, plan, "labels"));
        Assert.NotNull(ReviewAnswers.Parse(good, lists, plan, "labels"));
    }

    // ---- The blind re-check -------------------------------------------------------------------------------------------

    [Fact]
    public void TheSecondLook_IsDueAfterADay_Samples15PercentWithAFloorOf20_AndTheSampleIgnoresTheAnswers()
    {
        var folder = Recording();
        _w.Run("prepare");
        var file = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(file, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", file);

        var due = ReviewSessions.RecheckDueAt(folder);

        Assert.Equal(_w.Time.GetUtcNow() + TimeSpan.FromHours(24), due);
        Assert.Null(ReviewSessions.RecheckDueAt(Path.Combine(_w.Root, "nowhere")));

        // 200 scene ranges and 20 matches: 15% is 33; 10 units: all of them (below the floor of 20).
        static ReviewAnswers Big(int segments, int matches, string role) => new(
            "labels",
            "s",
            [.. Enumerable.Range(0, segments).Select(i => new SegmentAnswer(i, "InTrial", i % 10 == 0))],
            [.. Enumerable.Range(0, matches).Select(m => new MatchAnswer(m * 10, role, "dwight-fairfield", "ormond/mount-ormond-resort", "Escaped", string.Empty, string.Empty))],
            "2026-10-25T12:00:00.000Z");
        var (bigSegments, bigMatches) = ReviewSessions.Sample("s", Big(200, 20, "Survivor"));
        var (otherSegments, otherMatches) = ReviewSessions.Sample("s", Big(200, 20, "Killer"));
        var (smallSegments, smallMatches) = ReviewSessions.Sample("s", Big(6, 4, "Survivor"));
        var (otherTime, _) = ReviewSessions.Sample("s", Big(200, 20, "Survivor") with { AppliedAtUtc = "2026-10-26T12:00:00.000Z" });

        Assert.Equal(33, bigSegments.Count + bigMatches.Count);
        Assert.Equal(bigSegments, otherSegments);
        Assert.Equal(bigMatches, otherMatches);
        Assert.Equal(10, smallSegments.Count + smallMatches.Count);
        Assert.NotEqual(bigSegments, otherTime);
    }

    [Fact]
    public void TheSecondLookPage_ShowsNoEarlierAnswer_AndRefusesToBeMadeBeforeTheLabels()
    {
        var folder = Recording();
        _w.Run("prepare");
        var file = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(file, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", file);

        var page = ReviewSessions.BuildRecheckPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder());

        var html = File.ReadAllText(page);
        Assert.Contains("\"mode\":\"recheck\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("prefill", html.Replace("data.prefill", string.Empty, StringComparison.Ordinal).Replace("D.prefill", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\"scene\":", html[html.IndexOf("const D = ", StringComparison.Ordinal)..html.IndexOf(";\n", html.IndexOf("const D = ", StringComparison.Ordinal), StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void AgreeingSecondLook_PassesAndMarksEveryLabelRechecked_ADisagreementOnTheMapDisputesThem()
    {
        var folder = Recording();
        _w.LabelAndRecheck(folder, s_scenes, "tuning");

        Assert.All(DatasetLoader.LoadSession(folder).Labels, l => Assert.Equal("rechecked", l.ReviewStatus));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckResult)));
        Assert.Equal("rechecked", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, result.RootElement.GetProperty("sceneDisagreements").GetInt32());

        // Redo the labels (this starts a new second look) and disagree on the map the second time.
        var again = Path.Combine(_w.Root, "a2.json");
        File.WriteAllText(again, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", again);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.RecheckResult)));
        _w.Time.Advance(TimeSpan.FromHours(25));
        _ = ReviewSessions.BuildRecheckPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder());
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckPlan)));
        var picked = plan.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var second = Path.Combine(_w.Root, "b.json");
        File.WriteAllText(second, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes, kind: "recheck", only: picked, map: "ormond/ormond-lake-mine"));

        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", second, "--recheck"));

        Assert.Contains("disputed", _w.Output, StringComparison.Ordinal);
        Assert.All(DatasetLoader.LoadSession(folder).Labels, l => Assert.Equal("disputed", l.ReviewStatus));
    }

    [Fact]
    public void ASceneDisagreementBelowKappa09_Disputes_AndSecondLookAnswersForOtherUnitsAreRefused()
    {
        var folder = Recording();
        _w.Run("prepare");
        var file = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(file, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", file);
        _w.Time.Advance(TimeSpan.FromHours(25));
        _ = ReviewSessions.BuildRecheckPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder());

        var wrong = Path.Combine(_w.Root, "w.json");
        File.WriteAllText(wrong, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, ["Lobby", "MainMenu", "InMatchMenu", "Results"], kind: "recheck"));
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", wrong, "--recheck"));

        Assert.Contains("disputed", _w.Output, StringComparison.Ordinal);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckResult)));
        Assert.Equal(2, result.RootElement.GetProperty("sceneDisagreements").GetInt32());

        var partial = Path.Combine(_w.Root, "p.json");
        File.WriteAllText(partial, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes, kind: "recheck", only: [0]));
        Assert.NotEqual(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", partial, "--recheck"));
    }

    // ---- The guided flow ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheGuidedFlow_PreparesMakesThePageReadsTheSavedAnswersAndReportsProgress()
    {
        var folder = Recording();
        var id = EvalWorkspace.FirstRecording;
        _w.QueueEnter(string.Empty);

        // The owner saves the answers in the browser's downloads folder while the tool waits for Enter: simulated by writing the file before Enter is read.
        var env = _w.Environment();
        var enter = env.ReadLine;
        var downloaded = Path.Combine(_w.Downloads, ReviewSessions.AnswerFileName(id));
        var guided = new EvalEnvironment
        {
            Out = env.Out,
            Error = env.Error,
            Time = env.Time,
            StudyRoot = env.StudyRoot,
            DownloadsFolder = env.DownloadsFolder,
            CreateRecognizer = env.CreateRecognizer,
            ReadLine = () =>
            {
                File.WriteAllText(downloaded, EvalWorkspace.Answers(id, s_scenes));
                File.SetLastWriteTimeUtc(downloaded, _w.Time.GetUtcNow().UtcDateTime);
                return enter();
            },
        };

        var code = EvalApp.Run([], guided);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.ReviewPage)));
        Assert.Contains("Found a new recording", _w.Output, StringComparison.Ordinal);
        Assert.Contains("Matches recorded so far: 1 of 30.", _w.Output, StringComparison.Ordinal);
        Assert.Contains("A second look is due", _w.Output, StringComparison.Ordinal);
        Assert.Equal(1, GuidedLabel.CountMatches(_w.Root));
    }

    [Fact]
    public void TheGuidedFlow_WhenTheAnswersWereNotSaved_ChangesNothingAndSaysSo()
    {
        var folder = Recording();
        _w.QueueEnter(string.Empty);

        var code = _w.Run();

        Assert.Equal(0, code);
        Assert.Contains("could not find the saved answers", _w.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, new FileInfo(Path.Combine(folder, SessionFiles.Labels)).Length);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
    }

    [Fact]
    public void FromTheMenu_ANewRecordingIsNotOpenedForHandLabelling_UnlessTheOwnerTypesL()
    {
        var folder = Recording();
        _w.QueueEnter(string.Empty);

        var code = _w.Run("label", "--ask-new");

        Assert.Equal(0, code);
        Assert.Contains("is new and nobody has labelled it yet", _w.Output, StringComparison.Ordinal);
        Assert.Contains("choose 2", _w.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Open this file in your browser", _w.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.ReviewPage)));
    }

    [Fact]
    public void FromTheMenu_StopEndsTheRunAndChangesNothing()
    {
        var folder = Recording();
        _w.QueueEnter("s");

        var code = _w.Run("label", "--ask-new");

        Assert.Equal(0, code);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.ReviewPage)));
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
    }

    [Fact]
    public void FromTheMenu_TypingL_OpensTheLabellingPageAsBefore()
    {
        var folder = Recording();
        _w.QueueEnter("L", string.Empty);

        var code = _w.Run("label", "--ask-new");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.ReviewPage)));
        Assert.Contains("Open this file in your browser", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuidedFlow_WithNoRecordings_TellsTheOwnerWhatToDo()
    {
        var code = _w.Run();

        Assert.Equal(1, code);
        Assert.Contains("Start the recorder", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ADayLater_TheGuidedFlowOffersTheSecondLook_AndBeforeThatSaysWhen()
    {
        var folder = Recording();
        var id = EvalWorkspace.FirstRecording;
        var file = Path.Combine(_w.Root, "a.json");
        _w.Run("prepare");
        File.WriteAllText(file, EvalWorkspace.Answers(id, s_scenes));
        _w.Run("apply", "--session", id, "--answers", file);

        _w.Run();
        Assert.Contains("not yet", _w.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.RecheckPage)));

        _w.Time.Advance(TimeSpan.FromHours(25));
        _w.QueueEnter(string.Empty);
        _w.Run();

        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.RecheckPage)));
        Assert.Contains("quick second look", _w.Output, StringComparison.Ordinal);
    }
}
