// Covers: FR-LAB-03 (the automatic run seen from the Study page: states, token counts and skipped pictures kept per recording, a failure that stops the run),
// FR-LAB-04 (the blind spot-check built from the existing re-check, graded by the existing grading, written as the study tool's re-check path writes it),
// DETECTION_VALIDATION sections 4.2, 16.1 and 16.2, PLAN_AUTO_LABELLING section 4. MECHANICS ONLY: drawn text on flat colour and a labeller that is a fake.
using System.IO.Compression;
using System.Text.Json;
using DbdPresence.Core.Images;
using DbdPresence.Study;
using DbdPresence.TestSupport;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class StudyWorkbenchTests : IDisposable
{
    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private readonly EvalWorkspace _w = new();
    private readonly List<string> _warnings = [];
    private readonly string _log;

    public StudyWorkbenchTests()
    {
        _log = Path.Combine(_w.AppData, "study-runs.json");
    }

    public void Dispose() => _w.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Labeller : ILabellerTransport
    {
        public int Calls { get; private set; }

        public LabellerOutcome? Fail { get; set; }

        /// <summary>Gets or sets a scenes answer to give instead of the correct one (to make a recording fail), or null.</summary>
        public string? BadScenes { get; set; }

        /// <summary>Gets what each call carried: its pictures, the ranges asked about, and whether it asked for facts.</summary>
        public List<(int Images, int Segments, bool Facts)> Seen { get; } = [];

        public Task<LabellerReply> SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail is { } outcome)
            {
                return Task.FromResult(LabellerReply.Without(outcome));
            }

            using var doc = JsonDocument.Parse(body);
            var texts = doc.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()!).ToList();
            Seen.Add((doc.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray().Count(b => b.GetProperty("type").GetString() == "image"), texts.Count(t => t.StartsWith("Segment ", StringComparison.Ordinal)), texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal))));
            if (BadScenes is not null && !texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal)))
            {
                return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, BadScenes, "end_turn", 10, 1, null, null));
            }

            if (texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal)))
            {
                return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, """{"role":"Survivor","character":"dwight-fairfield","map":"ormond/mount-ormond-resort","result":"Escaped","killer":"the-lich","queue":""}""", "end_turn", 700, 30, null, null));
            }

            var indexes = texts.Where(t => t.StartsWith("Segment ", StringComparison.Ordinal)).Select(t => int.Parse(t.Split(':')[0][8..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var answer = JsonSerializer.Serialize(new { segments = indexes.Select(i => new { i, scene = s_scenes[i], matchStart = i == 1 }) });
            return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, answer, "end_turn", 5000, 120, null, null));
        }
    }

    private StudyWorkbench Bench(ILabellerTransport transport) =>
        new(_w.Root, Vocabulary.LoadEmbedded(), transport, _w.Time, _log, _warnings.Add) { CompactPictures = false };

    private string Recording(string name = EvalWorkspace.FirstRecording)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        return folder;
    }

    [Fact]
    public async Task ANewRecording_IsNew_ThenTheRunMakesItsReviewFileAndLabelsIt_WithTheSpotCheckDueAtOnce()
    {
        Recording();
        var labeller = new Labeller();
        var bench = Bench(labeller);

        var first = Assert.Single(await bench.ListAsync(Ct));
        Assert.Equal((RecordingStage.New, false, null), (first.Stage, first.Automatic, first.InputTokens));

        var report = await bench.LabelNewAsync(true, null, Ct);

        Assert.Equal(new StudyRunReport(1, 0, 1, StudyProblem.None, Requests: 2, InputTokens: 5700, OutputTokens: 150), report);
        var done = Assert.Single(await bench.ListAsync(Ct));
        Assert.Equal(RecordingStage.SpotCheckReady, done.Stage);
        Assert.True(done.Automatic);
        Assert.Equal((5700L, 150L), (done.InputTokens, done.OutputTokens));
        Assert.Equal(0, done.SkippedPictures);
        Assert.Equal(_w.Time.GetUtcNow(), done.SpotCheckDueAt);
    }

    [Fact]
    public async Task ASeparateReviewFile_ShowsAsReviewFileMade()
    {
        var folder = Recording();
        ReviewFiles.ExportFolder(folder, ReviewExport.DefaultLimitMegabytes, _w.Environment());

        var status = Assert.Single(await Bench(new Labeller()).ListAsync(Ct));

        Assert.Equal(RecordingStage.ReviewFileMade, status.Stage);
    }

    [Fact]
    public async Task TheTokenCounts_AreKeptAcrossARestart_AndNothingElseIsInTheRecordKeptForThem()
    {
        Recording();
        _ = await Bench(new Labeller()).LabelNewAsync(true, null, Ct);

        var afterRestart = Assert.Single(await Bench(new Labeller()).ListAsync(Ct));

        Assert.Equal((5700L, 150L), (afterRestart.InputTokens, afterRestart.OutputTokens));
        var text = File.ReadAllText(_log);
        Assert.DoesNotContain("dwight", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$", text, StringComparison.Ordinal);
        Assert.All(_warnings, w => Assert.StartsWith("PRIVACY:", w, StringComparison.Ordinal)); // nothing but the privacy lines reach the log
    }

    [Fact]
    public async Task AFailure_ShowsAsFailedWithTheReason_AndTheRunStopsAtOnceForAnAnswerThatWouldRepeat()
    {
        Recording("2026-10-24_18-00-00");
        Recording();
        var labeller = new Labeller { Fail = LabellerOutcome.NotSetUp };
        var bench = Bench(labeller);

        var report = await bench.LabelNewAsync(true, null, Ct);

        Assert.Equal(StudyProblem.NotSetUp, report.Problem);
        Assert.Equal(1, labeller.Calls);
        var list = await bench.ListAsync(Ct);
        Assert.Equal(RecordingStage.Failed, list[0].Stage);
        Assert.Contains("not set up", list[0].Failure, StringComparison.Ordinal);
        Assert.Equal(RecordingStage.New, list[1].Stage); // never reached: it waits for the next run

        labeller.Fail = null;
        var again = await bench.LabelNewAsync(true, null, Ct);
        Assert.Equal(2, again.Labelled);
        Assert.All(await bench.ListAsync(Ct), r => Assert.Equal(RecordingStage.SpotCheckReady, r.Stage));
    }

    [Theory]
    [InlineData("NotAllowed", StudyProblem.NotAllowed)]
    [InlineData("DailyCapReached", StudyProblem.DailyCap)]
    [InlineData("Refused", StudyProblem.Other)]
    [InlineData("Failed", StudyProblem.Other)]
    public async Task EachRelayOutcome_BecomesTheMatchingProblem(string outcome, StudyProblem problem)
    {
        Recording();

        var report = await Bench(new Labeller { Fail = Enum.Parse<LabellerOutcome>(outcome) }).LabelNewAsync(true, null, Ct);

        Assert.Equal(problem, report.Problem);
        Assert.Equal((0, 1), (report.Labelled, report.Failed));
    }

    [Fact]
    public async Task ABusyLabeller_BecomesTheBusyProblem()
    {
        Recording();

        var run = Bench(new Labeller { Fail = LabellerOutcome.Busy }).LabelNewAsync(true, null, Ct);
        while (!run.IsCompleted)
        {
            _w.Time.Advance(TimeSpan.FromSeconds(30)); // the labeller waits out the busy answer on the injected clock
            await Task.Yield();
        }

        var report = await run;

        Assert.Equal(StudyProblem.Busy, report.Problem);
    }

    [Fact]
    public async Task APictureMissingFromTheZip_IsSkippedAndCounted_EveryOtherPictureIsStillSent_AndEveryRangeIsStillAskedAbout()
    {
        // Two recordings of the same pictures; one picture is really removed from the second one's zip. Everything else must be the same.
        var twin = Recording("2026-10-24_18-00-00");
        var damaged = Recording();
        ReviewFiles.ExportFolder(twin, ReviewExport.DefaultLimitMegabytes, _w.Environment());
        ReviewFiles.ExportFolder(damaged, ReviewExport.DefaultLimitMegabytes, _w.Environment());
        var zipPath = Assert.Single(ReviewExport.ExistingFiles(damaged));
        string removed;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            removed = entry.FullName;
            entry.Delete();
        }

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName == removed);
        }

        var labeller = new Labeller();
        var bench = Bench(labeller);
        _ = await bench.LabelNewAsync(true, null, Ct);

        // Calls 1 and 2 are the twin (scenes, facts), calls 3 and 4 the damaged one.
        Assert.Equal(4, labeller.Seen.Count);
        Assert.Equal(labeller.Seen[0].Segments, labeller.Seen[2].Segments);
        Assert.True(labeller.Seen[2].Segments > 0);
        Assert.Equal(labeller.Seen[0].Images + labeller.Seen[1].Images - 1, labeller.Seen[2].Images + labeller.Seen[3].Images);
        var list = await bench.ListAsync(Ct);
        Assert.Equal(0, list.Single(r => r.Id == "2026-10-24_18-00-00").SkippedPictures);
        var status = list.Single(r => r.Id == EvalWorkspace.FirstRecording);
        Assert.Equal(1, status.SkippedPictures);
        Assert.Equal(RecordingStage.SpotCheckReady, status.Stage);
    }

    [Fact]
    public async Task ARecordingThatFailedTwice_IsNotTriedAgainByTheAutomaticRun_ButTheButtonStillTriesIt_AndASuccessClearsTheCount()
    {
        Recording();
        var labeller = new Labeller { BadScenes = """{"segments":[]}""" };
        var bench = Bench(labeller);

        var first = await bench.LabelNewAsync(false, null, Ct);
        Assert.Equal((0, 1), (first.Labelled, first.Failed));
        Assert.Equal(2, first.Requests); // asked, then once more
        Assert.False(Assert.Single(await bench.ListAsync(Ct)).AutomaticRetryStopped);
        _ = await bench.LabelNewAsync(false, null, Ct);
        var stopped = Assert.Single(await bench.ListAsync(Ct));
        Assert.True(stopped.AutomaticRetryStopped);
        Assert.Equal(RecordingStage.Failed, stopped.Stage);
        var callsBefore = labeller.Calls;

        var third = await bench.LabelNewAsync(false, null, Ct);

        Assert.Equal(callsBefore, labeller.Calls); // nothing sent
        Assert.Equal(StudyRunReport.Nothing, third);
        using (var record = JsonDocument.Parse(File.ReadAllText(_log)))
        {
            var entry = record.RootElement.GetProperty("recordings").EnumerateArray().Single();
            Assert.Equal(2, entry.GetProperty("failures").GetInt32());
            Assert.Equal("2026-10-25T12:00:00.000Z", entry.GetProperty("lastFailureUtc").GetString());
        }

        labeller.BadScenes = null;
        var manual = await bench.LabelNewAsync(true, null, Ct); // the button tries it again
        Assert.Equal(1, manual.Labelled);
        var done = Assert.Single(await bench.ListAsync(Ct));
        Assert.False(done.AutomaticRetryStopped);
        Assert.DoesNotContain("failures", File.ReadAllText(_log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopThatIsNotTheRecordingsFault_NotSetUpOrOffline_DoesNotCountTowardsGivingUp()
    {
        Recording();
        var labeller = new Labeller { Fail = LabellerOutcome.NotSetUp };
        var bench = Bench(labeller);
        for (var i = 0; i < 3; i++)
        {
            _ = await bench.LabelNewAsync(false, null, Ct);
        }

        Assert.Equal(3, labeller.Calls);
        Assert.False(Assert.Single(await bench.ListAsync(Ct)).AutomaticRetryStopped);
    }

    [Fact]
    public async Task TheReportCarriesTheRequestsAndTokensOfTheRun_AndARunStopsAtTheRequestLimit()
    {
        for (var i = 0; i < 3; i++)
        {
            Recording($"2026-10-2{i}_18-00-00");
        }

        var labeller = new Labeller();
        var report = await Bench(labeller).LabelNewAsync(true, null, Ct);

        Assert.Equal(new StudyRunReport(3, 0, 3, StudyProblem.None, Requests: 6, InputTokens: 17100, OutputTokens: 450), report);
        Assert.Equal(200, AutoLabelRun.MaxRequestsPerRun);
    }

    [Fact]
    public async Task FailureLines_AreFixedSentences_NoPathNoExceptionTextAndNothingTheModelChose()
    {
        var broken = Recording("2026-10-24_18-00-00");
        File.Delete(Path.Combine(broken, "frame-00001.png"));
        Recording();
        var labeller = new Labeller { BadScenes = """{"segments":[{"i":0,"scene":"MainMenu","matchStart":false,"\u0007evil-member-name":1}]}""" };
        var bench = Bench(labeller);

        _ = await bench.LabelNewAsync(true, null, Ct);

        var list = await bench.ListAsync(Ct);
        Assert.All(list, r =>
        {
            Assert.Equal(RecordingStage.Failed, r.Stage);
            Assert.True(r.Failure!.Length <= StudyText.MaxLength);
            Assert.DoesNotContain(r.Failure, char.IsControl);
            Assert.DoesNotContain("evil-member-name", r.Failure, StringComparison.Ordinal);
            Assert.DoesNotContain(_w.Root, r.Failure, StringComparison.Ordinal);
            Assert.DoesNotContain("metadata.json", r.Failure, StringComparison.Ordinal);
            Assert.DoesNotContain("Could not find", r.Failure, StringComparison.Ordinal);
        });
        Assert.Equal("The review file could not be made (the recording could not be read or is incomplete).", list[0].Failure);
        Assert.DoesNotContain("evil-member-name", File.ReadAllText(_log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureTextWrittenIntoTheRecord_IsCleanedWhenItIsReadBack()
    {
        Recording();
        var dirty = new string('x', 500) + "\u0007";
        Directory.CreateDirectory(_w.AppData);
        File.WriteAllText(_log, JsonSerializer.Serialize(new { schemaVersion = 1, recordings = new[] { new { id = EvalWorkspace.FirstRecording, requests = 1, inputTokens = 1, outputTokens = 1, skipped = 0, failure = dirty } } }));

        var status = Assert.Single(await Bench(new Labeller()).ListAsync(Ct));

        Assert.True(status.Failure!.Length <= StudyText.MaxLength);
        Assert.DoesNotContain(status.Failure, char.IsControl);
    }

    [Fact]
    public async Task ThePrivacyLinesOfTheReviewFile_AreKeptInTheLog_AndSayWhereThePicturesGo_WithoutNamingAProvider()
    {
        Recording();

        _ = await Bench(new Labeller()).LabelNewAsync(true, null, Ct);

        var privacy = _warnings.Where(w => w.StartsWith("PRIVACY:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(privacy);
        Assert.All(privacy, line =>
        {
            Assert.Contains("other players' names", line, StringComparison.Ordinal);
            Assert.Contains("through your own service", line, StringComparison.Ordinal);
            Assert.DoesNotContain("developer's private session", line, StringComparison.Ordinal);
            Assert.DoesNotContain("claude", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("review-answers.json")]
    [InlineData("review-plan.json")]
    [InlineData("recheck.json")]
    public async Task ATruncatedStateFile_ShowsThatRecordingAsFailed_NotTheOthers_AndOpeningOrSavingItsSpotCheckAnswersWithASentence(string file)
    {
        var (bench, sheet) = await LabelledAndOpened();
        var good = "2026-10-24_18-00-00";
        var other = Recording(good);
        _ = other;
        var folder = Path.Combine(_w.Root, EvalWorkspace.FirstRecording);
        File.WriteAllText(Path.Combine(folder, file), "{\"schemaVersion\": 1, \"sess");

        var list = await bench.ListAsync(Ct); // must not throw
        Assert.Equal(2, list.Count);
        Assert.Equal(RecordingStage.New, list.Single(r => r.Id == good).Stage);
        if (file != "review-plan.json")
        {
            Assert.Equal(RecordingStage.Failed, list.Single(r => r.Id == EvalWorkspace.FirstRecording).Stage);
        }

        // Neither opening nor saving throws: each answers with null or a sentence.
        SpotCheckSheet? opened = null;
        var openFailure = await Record.ExceptionAsync(async () => opened = await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));
        Assert.True(openFailure is null or FormatException or IOException, openFailure?.GetType().Name); // the page maps these two to a sentence
        var saveFailure = await Record.ExceptionAsync(async () => _ = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, Right(sheet), Ct));
        Assert.Null(saveFailure);
        _ = opened;
    }

    [Fact]
    public async Task SavingTwice_WritesOneResult_AndASaveAfterTheFolderIsGone_IsASentenceNotACrash()
    {
        var (bench, sheet) = await LabelledAndOpened();
        var folder = Path.Combine(_w.Root, EvalWorkspace.FirstRecording);

        var first = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, Right(sheet), Ct);
        var second = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, Right(sheet), Ct);

        Assert.NotNull(first.Verdict);
        Assert.Null(second.Verdict); // nothing is due any more
        Assert.Equal("This recording has no spot-check due.", second.Problem);
        Assert.Single(Directory.GetFiles(folder, "recheck.json"));

        Directory.Delete(folder, recursive: true); // the 30-day clean-up removed it
        var gone = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, Right(sheet), Ct);
        Assert.Null(gone.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(gone.Problem));
        Assert.Null(await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));
    }

    [Fact]
    public async Task AnOpenSheet_IsUnchanged_ByALabellingPassOverAnotherRecording()
    {
        var (bench, sheet) = await LabelledAndOpened();
        var before = (sheet.Segments.Count, sheet.Matches.Count, sheet.Segments.SelectMany(s => s.Pictures).Sum(p => p.Length));
        _ = Recording("2026-10-26_18-00-00");

        _ = await bench.LabelNewAsync(true, null, Ct);

        Assert.Equal(before, (sheet.Segments.Count, sheet.Matches.Count, sheet.Segments.SelectMany(s => s.Pictures).Sum(p => p.Length)));
        Assert.Equal(2, (await bench.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task TheOwnersOwnLabels_WaitADayForTheSpotCheck_AndAreNotAutomatic()
    {
        var folder = Recording();
        Assert.Equal(0, _w.Run("prepare"));
        var mine = Path.Combine(_w.Root, "mine.json");
        File.WriteAllText(mine, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", mine));
        var bench = Bench(new Labeller());

        var waiting = Assert.Single(await bench.ListAsync(Ct));
        Assert.Equal((RecordingStage.SpotCheckLater, false), (waiting.Stage, waiting.Automatic));
        Assert.Null(await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));

        _w.Time.Advance(TimeSpan.FromHours(25));
        var due = Assert.Single(await bench.ListAsync(Ct));
        Assert.Equal(RecordingStage.SpotCheckReady, due.Stage);
        Assert.NotNull(await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));
        Assert.NotEmpty(folder);
    }

    private async Task<(StudyWorkbench Bench, SpotCheckSheet Sheet)> LabelledAndOpened()
    {
        Recording();
        var bench = Bench(new Labeller());
        _ = await bench.LabelNewAsync(true, null, Ct);
        var sheet = await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct);
        return (bench, Assert.IsType<SpotCheckSheet>(sheet));
    }

    private static SpotCheckAnswers Right(SpotCheckSheet sheet) => new(
        [.. sheet.Segments.Select(s => new SpotCheckSegmentAnswer(s.Index, s_scenes[s.Index]))],
        [.. sheet.Matches.Select(m => new SpotCheckMatchAnswer(m.StartIndex, "Survivor", "dwight-fairfield", "ormond/mount-ormond-resort", "Escaped", "the-lich", string.Empty))]);

    [Fact]
    public async Task TheSheet_HoldsThePicturesTheExistingReCheckAsks_AsCleanPictures_AndTheClosedLists()
    {
        var (_, sheet) = await LabelledAndOpened();
        var folder = Path.Combine(_w.Root, EvalWorkspace.FirstRecording);

        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckPlan)));
        Assert.Equal(plan.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToArray(), sheet.Segments.Select(s => s.Index).ToArray());
        Assert.Equal(plan.RootElement.GetProperty("matches").EnumerateArray().Select(e => e.GetInt32()).ToArray(), sheet.Matches.Select(m => m.StartIndex).ToArray());
        Assert.All(sheet.Segments.SelectMany(s => s.Pictures).Concat(sheet.Matches.SelectMany(m => m.Pictures)), picture =>
        {
            Assert.True(StrictPng.TryValidate(picture, out var width, out _, out var failure), failure);
            Assert.Equal(160, width);
        });
        Assert.All(sheet.Segments, s => Assert.NotEmpty(s.Pictures));
        Assert.Contains(sheet.Scenes, c => c.Value == "InTrial");
        Assert.Contains(sheet.Characters, c => c.Value == "dwight-fairfield");
        Assert.Contains(sheet.Maps, c => c.Value == "ormond/mount-ormond-resort");
        Assert.Contains(sheet.Killers, c => c.Value == "the-lich");
        Assert.Contains(sheet.Queues, c => c.Value == DbdPresence.Core.Detection.QueueModes.Normal);
        Assert.Equal(["Survivor", "Killer", "?"], sheet.Roles.Select(r => r.Value).ToArray());
    }

    [Fact]
    public async Task TheSheet_CarriesNoEarlierAnswer_NotInItsPicturesChoicesOrAnyText()
    {
        var (_, sheet) = await LabelledAndOpened();

        // Every choice list is the same closed list whatever was answered: nothing in them is preselected or reordered, and the types hold no answer.
        var fields = typeof(SpotCheckSheet).GetProperties().Select(p => p.Name).Concat(typeof(SpotCheckSegment).GetProperties().Select(p => p.Name)).Concat(typeof(SpotCheckMatch).GetProperties().Select(p => p.Name)).ToArray();
        Assert.DoesNotContain(fields, f => f.Contains("Answer", StringComparison.Ordinal) || f.Contains("Scene", StringComparison.Ordinal) && f != "Scenes");
        Assert.Equal(ReviewVocabulary.Scenes.Select(s => s.Id).ToArray(), sheet.Scenes.Select(s => s.Value).ToArray());
    }

    /// <summary>A labeller that answers a different scene for every range and different facts than <see cref="Labeller"/> does, with the same match starts.</summary>
    private sealed class OtherLabeller(string character, string map) : ILabellerTransport
    {
        public Task<LabellerReply> SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(body);
            var texts = doc.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()!).ToList();
            if (texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal)))
            {
                return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, $$"""{"role":"Killer","character":"{{character}}","map":"{{map}}","result":"?","killer":"?","queue":"?"}""", "end_turn", 1, 1, null, null));
            }

            var indexes = texts.Where(t => t.StartsWith("Segment ", StringComparison.Ordinal)).Select(t => int.Parse(t.Split(':')[0][8..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var answer = JsonSerializer.Serialize(new { segments = indexes.Select(i => new { i, scene = "Other", matchStart = i == 1 }) });
            return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, answer, "end_turn", 1, 1, null, null));
        }
    }

    [Fact]
    public async Task TheSheets_OfTwoRecordingsLabelledDifferently_AreIdentical_InChoicesDefaultsPicturesAndText()
    {
        // Two workspaces with the same recording and the same clock; only the labeller's answers differ (other scenes, role, character, map, killer, queue).
        var survivors = Vocabulary.LoadEmbedded().Entries(VocabularyKind.Survivor).Where(e => e.Id != "dwight-fairfield").ToList();
        var maps = Vocabulary.LoadEmbedded().Entries(VocabularyKind.Map).Where(e => e.Id != "ormond/mount-ormond-resort").ToList();
        using var second = new EvalWorkspace();
        var folderA = Recording();
        var folderB = second.AddRecording(EvalWorkspace.FirstRecording, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folderB, 4);
        var benchA = Bench(new Labeller());
        var benchB = new StudyWorkbench(second.Root, Vocabulary.LoadEmbedded(), new OtherLabeller(survivors[0].Id, maps[0].Id), second.Time, Path.Combine(second.AppData, "study-runs.json"), _warnings.Add) { CompactPictures = false };
        _ = await benchA.LabelNewAsync(true, null, Ct);
        var reportB = await benchB.LabelNewAsync(true, null, Ct);
        Assert.True(reportB.Labelled == 1, string.Join(" / ", (await benchB.ListAsync(Ct)).Select(r => r.Failure)));
        Assert.NotEqual(File.ReadAllText(Path.Combine(folderA, SessionFiles.Answers)), File.ReadAllText(Path.Combine(folderB, SessionFiles.Answers)));

        var a = Assert.IsType<SpotCheckSheet>(await benchA.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));
        var b = Assert.IsType<SpotCheckSheet>(await benchB.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));

        Assert.Equal(a.Segments.Select(s => (s.Index, s.StartLocal, s.Seconds)).ToArray(), b.Segments.Select(s => (s.Index, s.StartLocal, s.Seconds)).ToArray());
        Assert.Equal(a.Matches.Select(m => m.StartIndex).ToArray(), b.Matches.Select(m => m.StartIndex).ToArray());
        Assert.Equal(a.Segments.SelectMany(s => s.Pictures).Select(Convert.ToHexString).ToArray(), b.Segments.SelectMany(s => s.Pictures).Select(Convert.ToHexString).ToArray());
        Assert.Equal(a.Matches.SelectMany(m => m.Pictures).Select(Convert.ToHexString).ToArray(), b.Matches.SelectMany(m => m.Pictures).Select(Convert.ToHexString).ToArray());
        foreach (var (left, right) in new[] { (a.Scenes, b.Scenes), (a.Roles, b.Roles), (a.Characters, b.Characters), (a.Maps, b.Maps), (a.Results, b.Results), (a.Killers, b.Killers), (a.Queues, b.Queues) })
        {
            Assert.Equal(left.Select(c => (c.Value, c.Label)).ToArray(), right.Select(c => (c.Value, c.Label)).ToArray());
        }
    }

    [Fact]
    public async Task SavingTheSameAnswers_PassesTheSpotCheck_AndWritesWhatTheStudyToolsRecheckPathWrites()
    {
        var (bench, sheet) = await LabelledAndOpened();
        var folder = Path.Combine(_w.Root, EvalWorkspace.FirstRecording);

        var saved = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, Right(sheet), Ct);

        Assert.Null(saved.Problem);
        var verdict = Assert.IsType<SpotCheckVerdict>(saved.Verdict);
        Assert.True(verdict.Passed);
        Assert.Equal(sheet.Segments.Count + sheet.Matches.Count, verdict.SampledUnits);
        Assert.Empty(verdict.FactDisagreements);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckResult)));
        Assert.Equal("rechecked", result.RootElement.GetProperty("status").GetString());
        Assert.All(File.ReadAllLines(Path.Combine(folder, SessionFiles.Labels)).Where(l => l.Length > 0), l =>
        {
            using var doc = JsonDocument.Parse(l);
            Assert.Equal("rechecked", doc.RootElement.GetProperty("reviewStatus").GetString());
        });
        Assert.Equal(RecordingStage.SpotCheckDone, Assert.Single(await bench.ListAsync(Ct)).Stage);
    }

    [Fact]
    public async Task TheWorkbenchGrade_IsTheLibrarysGrade_ForTheSameAnswers()
    {
        // The same answers through ReviewSessions.ApplyRecheck on a twin recording give the same outcome as the workbench: it adds no grading of its own.
        var (bench, sheet) = await LabelledAndOpened();
        var wrong = Right(sheet) with { Matches = [.. Right(sheet).Matches.Select(m => m with { Map = "?" })] };
        var twin = Path.Combine(_w.Root, "..", "twin-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(_w.Root, EvalWorkspace.FirstRecording), Path.Combine(twin, EvalWorkspace.FirstRecording));
        try
        {
            var viaWorkbench = (await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, wrong, Ct)).Verdict!;
            var json = new ReviewAnswers(
                ReviewAnswers.RecheckKind,
                EvalWorkspace.FirstRecording,
                [.. wrong.Segments.Select(s => new SegmentAnswer(s.Index, s.Scene, false))],
                [.. wrong.Matches.Select(m => new MatchAnswer(m.StartIndex, m.Role, m.Character, m.Map, m.Result, m.Killer, m.Queue))],
                null).ToJson();
            var direct = ReviewSessions.ApplyRecheck(Path.Combine(twin, EvalWorkspace.FirstRecording), json, Vocabulary.LoadEmbedded(), _w.Time.GetUtcNow());

            Assert.False(viaWorkbench.Passed);
            Assert.Equal((direct.Status == "rechecked", direct.SampledUnits, direct.SceneDisagreements), (viaWorkbench.Passed, viaWorkbench.SampledUnits, viaWorkbench.SceneDisagreements));
            Assert.Equal(direct.FactDisagreements, viaWorkbench.FactDisagreements);
            Assert.Equal(direct.Kappa, viaWorkbench.Kappa);
        }
        finally
        {
            Directory.Delete(twin, recursive: true);
        }
    }

    [Fact]
    public async Task ADisagreement_IsDisputed_AndShownThatWay()
    {
        var (bench, sheet) = await LabelledAndOpened();
        var other = new SpotCheckAnswers([.. sheet.Segments.Select(s => new SpotCheckSegmentAnswer(s.Index, "Other"))], Right(sheet).Matches);

        var saved = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, other, Ct);

        Assert.False(saved.Verdict!.Passed);
        Assert.Equal(RecordingStage.Disputed, Assert.Single(await bench.ListAsync(Ct)).Stage);
        Assert.Null(await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct)); // done: it cannot be asked again
    }

    [Fact]
    public async Task AnswerThatDoNotFit_SaveNothing_AndSayWhy()
    {
        var (bench, sheet) = await LabelledAndOpened();
        var folder = Path.Combine(_w.Root, EvalWorkspace.FirstRecording);
        var bad = Right(sheet) with { Matches = [.. Right(sheet).Matches.Select(m => m with { Character = "not-a-character" })] };

        var saved = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, bad, Ct);

        Assert.Null(saved.Verdict);
        Assert.Contains("could not be saved", saved.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.RecheckResult)));
        Assert.Equal(RecordingStage.SpotCheckReady, Assert.Single(await bench.ListAsync(Ct)).Stage);
    }

    [Fact]
    public async Task ARecordingWithNoSpotCheckDue_CannotBeSaved()
    {
        Recording();
        var bench = Bench(new Labeller());

        var saved = await bench.SaveSpotCheckAsync(EvalWorkspace.FirstRecording, new SpotCheckAnswers([], []), Ct);

        Assert.Null(saved.Verdict);
        Assert.Equal("This recording has no spot-check due.", saved.Problem);
        Assert.Null(await bench.OpenSpotCheckAsync("2000-01-01_00-00-00", Ct));
    }

    [Fact]
    public async Task ADamagedRunRecord_IsToldAboutAndStartedAgain_NotSilentlyIgnored()
    {
        Recording();
        Directory.CreateDirectory(Path.GetDirectoryName(_log)!);
        File.WriteAllText(_log, "not json");

        var list = await Bench(new Labeller()).ListAsync(Ct);

        Assert.Single(list);
        Assert.Single(_warnings);
        Assert.Contains("could not be read", _warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSharedFixtureRecording_IsReadyForItsSpotCheck_AndOpens()
    {
        // The Windows UI tests of the Study page build their recording with this same fixture, so the fixture is proven here, where it can run.
        _ = StudyRecordingFixture.AddReviewerLabelled(_w.Root, EvalWorkspace.FirstRecording, _w.Time.GetUtcNow());
        var bench = Bench(new Labeller());

        var status = Assert.Single(await bench.ListAsync(Ct));
        Assert.Equal((RecordingStage.SpotCheckReady, true), (status.Stage, status.Automatic));
        var sheet = Assert.IsType<SpotCheckSheet>(await bench.OpenSpotCheckAsync(EvalWorkspace.FirstRecording, Ct));
        Assert.Equal(4, sheet.Segments.Count);
        Assert.Single(sheet.Matches);
        Assert.All(sheet.Segments, s => Assert.NotEmpty(s.Pictures));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }
}
