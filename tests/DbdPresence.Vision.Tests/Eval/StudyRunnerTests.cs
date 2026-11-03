// Covers: FR-LAB-03 (the automatic run: review file, labels, import as reviewer labels), PLAN_AUTO_LABELLING section 4 steps 2 and 5, DETECTION_VALIDATION
// section 16.2 (reviewer labels are not final; the owner's blind spot-check is due at once; a failure leaves the recording unlabelled).
// MECHANICS ONLY: the recordings are drawn text on flat colour and the labeller is a fake that answers from the segment list.
using System.Text.Json;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class StudyRunnerTests : IDisposable
{
    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();
    private readonly EvalWorkspace _w = new();

    public void Dispose() => _w.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Labeller : ILabellerTransport
    {
        public int Calls { get; private set; }

        public LabellerOutcome? Fail { get; init; }

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
            if (texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal)))
            {
                return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, """{"role":"Survivor","character":"dwight-fairfield","map":"ormond/mount-ormond-resort","result":"Escaped","killer":"the-lich","queue":""}""", "end_turn", 700, 30, null, null));
            }

            var indexes = texts.Where(t => t.StartsWith("Segment ", StringComparison.Ordinal)).Select(t => int.Parse(t.Split(':')[0][8..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var answer = JsonSerializer.Serialize(new { segments = indexes.Select(i => new { i, scene = s_scenes[i], matchStart = i == 1 }) });
            return Task.FromResult(new LabellerReply(LabellerOutcome.Ok, answer, "end_turn", 5000, 120, null, null));
        }
    }

    private string Recording(string name = EvalWorkspace.FirstRecording)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        return folder;
    }

    private Task<IReadOnlyList<RecordingRunSummary>> Run(ILabellerTransport transport, Vocabulary? vocabulary = null, IReadOnlySet<string>? skip = null, int maxRequests = AutoLabelRun.MaxRequestsPerRun, Action<string>? log = null) =>
        AutoLabelRun.RunAsync(_w.Root, _w.Environment(), vocabulary ?? s_vocabulary, transport, null, Ct, skip, maxRequests, log);

    /// <summary>A labeller whose answer for every call is chosen by the test: (call number from 1, the request's text blocks) to a reply.</summary>
    private sealed class Scripted(Func<int, IReadOnlyList<string>, LabellerReply> answer) : ILabellerTransport
    {
        public int Calls { get; private set; }

        public int FactsCalls { get; private set; }

        public Task<LabellerReply> SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            Calls++;
            using var doc = JsonDocument.Parse(body);
            var texts = doc.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()!).ToList();
            if (texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal)))
            {
                FactsCalls++;
            }

            return Task.FromResult(answer(Calls, texts));
        }

        public static IReadOnlyList<int> Indexes(IReadOnlyList<string> texts) =>
            [.. texts.Where(t => t.StartsWith("Segment ", StringComparison.Ordinal)).Select(t => int.Parse(t.Split(':')[0][8..], System.Globalization.CultureInfo.InvariantCulture))];

        public static bool IsFacts(IReadOnlyList<string> texts) => texts.Any(t => t.StartsWith("Task: facts", StringComparison.Ordinal));

        public static LabellerReply Ok(string text) => new(LabellerOutcome.Ok, text, "end_turn", 100, 10, null, null);

        public static LabellerReply Scenes(IReadOnlyList<string> texts, Func<int, bool> start) =>
            Ok(JsonSerializer.Serialize(new { segments = Indexes(texts).Select(i => new { i, scene = "MainMenu", matchStart = start(i) }) }));

        public const string Facts = """{"role":"Survivor","character":"dwight-fairfield","map":"ormond/mount-ormond-resort","result":"Escaped","killer":"the-lich","queue":""}""";
    }

    /// <summary>A recording of many one-picture screens with 60-second ranges (so match starts are plausible), with its own plan.</summary>
    private string LongRecording(int segments)
    {
        var image = HudScenes.MainMenu(1280);
        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, [.. Enumerable.Range(0, segments).Select(_ => ("MainMenu", image, 1))]);
        var plan = new ReviewPlan(Path.GetFileName(folder), [.. Enumerable.Range(0, segments).Select(i => new PlanSegment(i, i + 1, i + 1, "19:30:12", 60))]);
        File.WriteAllText(Path.Combine(folder, SessionFiles.Plan), plan.ToJson());
        return folder;
    }

    [Fact]
    public async Task ANewRecording_GetsItsReviewFile_IsLabelled_AndTheLabelsAreImportedAsTheReviewersWithTheSpotCheckDue()
    {
        var folder = Recording();
        var labeller = new Labeller();

        var summaries = await Run(labeller);

        var summary = Assert.Single(summaries);
        Assert.True(summary.MadeReviewFile);
        Assert.True(summary.Labelled);
        Assert.Null(summary.Failure);
        Assert.Equal((4, 1), (summary.LabelledSegments, summary.LabelledMatches));
        Assert.Equal(2, summary.Requests);
        Assert.Equal((5700, 150), (summary.InputTokens, summary.OutputTokens));
        Assert.Contains("labelled 4 segments and 1 matches", summary.Describe(), StringComparison.Ordinal);
        Assert.NotEmpty(ReviewExport.ExistingFiles(folder));
        Assert.Equal(LabelSources.Reviewer, ReviewSessions.AnswersSource(folder));
        var labels = File.ReadAllLines(Path.Combine(folder, SessionFiles.Labels)).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.NotEmpty(labels);
        Assert.All(labels, l => Assert.Equal("reviewer", l.GetProperty("source").GetString()));
        Assert.Equal((DateTimeOffset?)_w.Time.GetUtcNow(), ReviewSessions.RecheckDueAt(folder));
        Assert.Equal(0, GuidedLabel.CountMatches(_w.Root)); // reviewer labels do not count before the owner's spot-check
        Assert.False(File.Exists(Path.Combine(folder, ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording))), "the way-in file must not stay in the folder");
        Assert.False(ReviewFiles.IsWaitingForReviewer(folder));
    }

    [Fact]
    public async Task ARun_LeavesNoFileBehindButTheKnownOnes_NotEvenTheWayInLabelsFile()
    {
        var folder = Recording();
        var before = Directory.EnumerateFiles(_w.Root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        _ = await Run(new Labeller());

        var added = Directory.EnumerateFiles(_w.Root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Where(n => !before.Contains(n)).ToList();
        Assert.DoesNotContain(added, n => n!.Contains("reviewer-labels", StringComparison.Ordinal));
        Assert.All(added, n => Assert.True(
            n is "session.json" or "frames.jsonl" or "labels.jsonl" or "review-plan.json" or "review-answers.json" || n!.EndsWith("-review.zip", StringComparison.Ordinal),
            $"unexpected file {n}"));
        Assert.Equal(LabelSources.Reviewer, ReviewSessions.AnswersSource(folder));
    }

    [Fact]
    public async Task ACharacterAddedByTheSignedRoster_IsAcceptedByTheImport_BecauseItUsesTheSameMergedListsAsTheLabeller()
    {
        var folder = Recording();
        var added = new VocabularyEntry("zelda-quillfeather", "Zelda Quillfeather", null, null, "test", "S99");
        var merged = Vocabulary.LoadEmbedded().WithCharacters([], [added]);
        var labeller = new Scripted((_, texts) => Scripted.IsFacts(texts)
            ? Scripted.Ok("""{"role":"Survivor","character":"zelda-quillfeather","map":"ormond/mount-ormond-resort","result":"Escaped","killer":"","queue":""}""")
            : Scripted.Scenes(texts, i => i == 1));

        var summary = Assert.Single(await Run(labeller, merged));

        Assert.Null(summary.Failure);
        Assert.True(summary.Labelled);
        Assert.Equal(LabelSources.Reviewer, ReviewSessions.AnswersSource(folder));
        Assert.Contains("zelda-quillfeather", File.ReadAllText(Path.Combine(folder, SessionFiles.Answers)), StringComparison.Ordinal);
        // Control: with the embedded lists alone the labeller itself refuses the id, so the roster list is really what carried it.
        var other = Recording("2026-10-24_18-00-00");
        var refused = (await Run(new Scripted((_, texts) => Scripted.IsFacts(texts)
            ? Scripted.Ok("""{"role":"Survivor","character":"zelda-quillfeather","map":"ormond/mount-ormond-resort","result":"Escaped","killer":"","queue":""}""")
            : Scripted.Scenes(texts, i => i == 1)))).Single(r => r.SessionId == "2026-10-24_18-00-00");
        Assert.False(refused.Labelled);
        Assert.False(File.Exists(Path.Combine(other, SessionFiles.Answers)));
    }

    [Fact]
    public async Task ARecordingGivenUpOn_IsNotTriedInThisRun_AndNothingIsSentForIt()
    {
        var folder = Recording();
        var labeller = new Labeller();

        var summaries = await Run(labeller, skip: new HashSet<string>([EvalWorkspace.FirstRecording], StringComparer.Ordinal));

        Assert.Empty(summaries);
        Assert.Equal(0, labeller.Calls);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
    }

    [Fact]
    public async Task TheRunStopsAtItsRequestLimit_WithAPlainMessage_AndTheNextRecordingIsNotStarted()
    {
        var first = Recording("2026-10-24_18-00-00");
        var second = Recording();
        var labeller = new Labeller();

        var summaries = await Run(labeller, maxRequests: 1);

        Assert.Equal(1, labeller.Calls);
        Assert.Equal(1, summaries.Count(s => s.Problem == LabellerOutcome.RunLimitReached && s.SessionId == "2026-10-24_18-00-00"));
        var stopped = summaries[0];
        Assert.False(stopped.Labelled);
        Assert.Contains("limit of 1 requests", stopped.Failure, StringComparison.Ordinal);
        Assert.False(stopped.CountsAsFailure);
        Assert.False(File.Exists(Path.Combine(first, SessionFiles.Answers)));
        Assert.False(File.Exists(Path.Combine(second, SessionFiles.Answers)));
        Assert.DoesNotContain(summaries, s => s.SessionId == EvalWorkspace.FirstRecording); // the run stopped before it
        Assert.Equal(200, AutoLabelRun.MaxRequestsPerRun);
    }

    [Fact]
    public async Task ARunThatIsAllowedEnoughRequests_FinishesBothRecordings_AndCountsEveryRequest()
    {
        _ = Recording("2026-10-24_18-00-00");
        _ = Recording();
        var labeller = new Labeller();

        var summaries = await Run(labeller, maxRequests: 4);

        Assert.All(summaries, s => Assert.True(s.Labelled));
        Assert.Equal(4, labeller.Calls);
        Assert.Equal(4, summaries.Sum(s => s.Requests));
    }

    [Fact]
    public async Task AScenesAnswerWithImplausiblyManyMatchStarts_IsAFailedReply_AskedOnceMore_ThenGivenUp_WithNoFactsRequestAndNothingWritten()
    {
        var folder = Recording();
        var labeller = new Scripted((_, texts) => Scripted.Scenes(texts, _ => true));

        var summary = Assert.Single(await Run(labeller));

        Assert.False(summary.Labelled);
        Assert.Equal(2, labeller.Calls);
        Assert.Equal(0, labeller.FactsCalls);
        Assert.Contains("twice in a row", summary.Failure, StringComparison.Ordinal);
        Assert.Contains("more match starts", summary.Failure, StringComparison.Ordinal);
        Assert.True(summary.CountsAsFailure);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Labels)) && File.ReadAllText(Path.Combine(folder, SessionFiles.Labels)).Contains("reviewer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMatchStartBound_IsOnePerFourMinutesWithAnAbsoluteCeiling_AndAtLeastOne()
    {
        Assert.Equal(1, AutoLabeller.AllowedMatchStarts(0));
        Assert.Equal(1, AutoLabeller.AllowedMatchStarts(240));
        Assert.Equal(2, AutoLabeller.AllowedMatchStarts(241));
        Assert.Equal(15, AutoLabeller.AllowedMatchStarts(3600));
        Assert.Equal(AutoLabeller.MaxMatchStartsPerRecording, AutoLabeller.AllowedMatchStarts(10_000_000));
        Assert.Equal(40, AutoLabeller.MaxMatchStartsPerRecording);
    }

    [Fact]
    public async Task APartialFailure_ValidFirstTwoChunksAndAnInvalidThirdTwice_LeavesNothingBehind()
    {
        var folder = LongRecording(90);
        var labeller = new Scripted((call, texts) => call <= 2 ? Scripted.Scenes(texts, i => i == 0) : Scripted.Ok("""{"segments":[]}"""));

        var summary = Assert.Single(await Run(labeller));

        Assert.False(summary.Labelled);
        Assert.Equal(4, labeller.Calls); // three chunks, the third asked twice
        Assert.Equal(0, labeller.FactsCalls);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Labels)) && File.ReadAllText(Path.Combine(folder, SessionFiles.Labels)).Contains("reviewer", StringComparison.Ordinal));
        Assert.True(ReviewFiles.IsWaitingForReviewer(folder));
    }

    [Fact]
    public async Task AFactsAnswerThatIsInvalidTwice_AfterValidScenes_LeavesNothingBehind()
    {
        var folder = Recording();
        var labeller = new Scripted((_, texts) => Scripted.IsFacts(texts) ? Scripted.Ok("""{"role":"Survivor","character":"not-a-character","map":"x","result":"Escaped","killer":"","queue":""}""") : Scripted.Scenes(texts, i => i == 1));

        var summary = Assert.Single(await Run(labeller));

        Assert.False(summary.Labelled);
        Assert.Equal(3, labeller.Calls);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
        Assert.True(ReviewFiles.IsWaitingForReviewer(folder));
    }

    [Fact]
    public async Task AnImportThatFailsAfterValidAnswers_ShowsAFixedSentence_WritesNoReviewerRows_AndLeavesNoFile()
    {
        var folder = Recording();
        var logged = new List<string>();
        // The owner saves their own labels while the run is waiting for the answer: the import then refuses to write over them.
        var labeller = new Scripted((_, texts) =>
        {
            if (Scripted.IsFacts(texts))
            {
                File.WriteAllText(Path.Combine(folder, SessionFiles.Answers), "{}");
                return Scripted.Ok(Scripted.Facts);
            }

            return Scripted.Scenes(texts, i => i == 1);
        });

        var summary = Assert.Single(await Run(labeller, log: logged.Add));

        Assert.False(summary.Labelled);
        Assert.Equal("The labels could not be taken in (they did not fit the recording's files). Nothing was changed.", summary.Failure);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, SessionFiles.Answers)));
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Labels)) && File.ReadAllText(Path.Combine(folder, SessionFiles.Labels)).Contains("reviewer", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(folder), f => f.Contains("reviewer-labels", StringComparison.Ordinal));
        Assert.Contains(logged, l => l.Contains("could not be taken in", StringComparison.Ordinal));
        Assert.DoesNotContain(folder, summary.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMatchStartOnTheLastRangeOfOneChunkAndTheFirstOfTheNext_GivesTwoMatchesWithOneFactsRequestEach()
    {
        var folder = LongRecording(90);
        var labeller = new Scripted((_, texts) => Scripted.IsFacts(texts) ? Scripted.Ok(Scripted.Facts) : Scripted.Scenes(texts, i => i is 39 or 40));

        var summary = Assert.Single(await Run(labeller));

        Assert.True(summary.Labelled);
        Assert.Equal(2, labeller.FactsCalls);
        Assert.Equal(5, labeller.Calls); // three scene chunks and two matches
        Assert.Equal((90, 2), (summary.LabelledSegments, summary.LabelledMatches));
        var answers = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Answers))).RootElement;
        Assert.Equal([39, 40], answers.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("startIndex").GetInt32()).ToArray());
    }

    [Fact]
    public void AFailureSentence_IsOneCleanLineOfAtMostTwoHundredCharacters()
    {
        var dirty = new RecordingRunSummary("id", false, 0, 0, 0, 0, 0, "line one\r\nline\ttwo \u0007\u001b[31m" + new string('x', 400));

        Assert.True(dirty.Failure!.Length <= StudyText.MaxLength);
        Assert.DoesNotContain(dirty.Failure, char.IsControl);
        Assert.EndsWith("…", dirty.Failure, StringComparison.Ordinal);
        Assert.Null(StudyText.Plain("  \u0007 "));
        Assert.Equal("a b", StudyText.Plain("a\n\n b"));
    }

    [Fact]
    public async Task ARecordingThatAlreadyHasLabels_IsNotTouched_AndNothingIsSent()
    {
        var folder = Recording();
        Assert.Equal(0, _w.Run("prepare"));
        var mine = Path.Combine(_w.Root, "mine.json");
        File.WriteAllText(mine, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", mine));
        var before = File.ReadAllText(Path.Combine(folder, SessionFiles.Labels));
        var answersBefore = File.ReadAllText(Path.Combine(folder, SessionFiles.Answers));
        var labeller = new Labeller();

        var summaries = await Run(labeller);

        Assert.Empty(summaries);
        Assert.Equal(0, labeller.Calls);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, SessionFiles.Labels)));
        Assert.Equal(answersBefore, File.ReadAllText(Path.Combine(folder, SessionFiles.Answers)));
        Assert.Empty(ReviewExport.ExistingFiles(folder));
    }

    [Fact]
    public async Task AFailure_LeavesTheRecordingWithoutLabels_ButKeepsTheReviewFile_SoTheNextRunLabelsIt()
    {
        var folder = Recording();

        var failed = Assert.Single(await Run(new Labeller { Fail = LabellerOutcome.NotAllowed }));

        Assert.False(failed.Labelled);
        Assert.True(failed.MadeReviewFile);
        Assert.Contains("may not use automatic labelling", failed.Failure, StringComparison.Ordinal);
        Assert.Contains("failed:", failed.Describe(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
        var labelsFile = Path.Combine(folder, SessionFiles.Labels);
        Assert.DoesNotContain("reviewer", File.Exists(labelsFile) ? File.ReadAllText(labelsFile) : string.Empty, StringComparison.Ordinal);
        Assert.True(ReviewFiles.IsWaitingForReviewer(folder));

        var labeller = new Labeller();
        var again = Assert.Single(await Run(labeller));
        Assert.False(again.MadeReviewFile);
        Assert.True(again.Labelled);
        Assert.Equal(2, labeller.Calls);
    }

    [Fact]
    public async Task ARecordingWhosePicturesAreGone_IsReportedAsFailed_AndTheOthersStillRun()
    {
        var broken = Recording("2026-10-24_18-00-00");
        File.Delete(Path.Combine(broken, "frame-00001.png"));
        var good = Recording();
        var labeller = new Labeller();

        var summaries = await Run(labeller);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("2026-10-24_18-00-00", summaries[0].SessionId);
        Assert.NotNull(summaries[0].Failure);
        Assert.False(File.Exists(Path.Combine(broken, SessionFiles.Answers)));
        Assert.True(summaries[1].Labelled);
        Assert.Equal(LabelSources.Reviewer, ReviewSessions.AnswersSource(good));
    }

    [Fact]
    public async Task ACancelledRun_StopsBeforeTheNextRecording()
    {
        _ = Recording();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoLabelRun.RunAsync(_w.Root, _w.Environment(), s_vocabulary, new Labeller(), null, cts.Token));
    }
}
