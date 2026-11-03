// Covers: FR-LAB-03 (the automatic run starts when the game has closed, only with the setting on, never while the game runs, one at a time, with the plain
// error sentences), FR-LAB-04 (the owner-only Study page: absent for everyone else, the recordings in plain words with token counts, the blind spot-check
// with no earlier answer and no suggestion, saving through the grading), PLAN_AUTO_LABELLING sections 4 and 6, D35, D38. Fakes only: no network, no files.
using DbdPresence.Core.Game;
using DbdPresence.Presentation.Labelling;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Study;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class StudyViewModelTests : IDisposable
{
    private static readonly AppInfo s_app = new("0.3.0+abc1234", "abc1234", IsSigned: false, @"C:\settings", @"C:\data");
    private readonly Harness _host = new();
    private readonly FakeWorkbench _bench = new();
    private readonly OwnerAccountState _owner = new();
    private readonly List<bool> _recorder = [];
    private readonly List<IDisposable> _owned = [];
    private bool _recorderRunning;

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }

        _host.Dispose();
    }

    private StudyViewModel Page(bool owner = true, bool confirmed = true)
    {
        if (owner)
        {
            _owner.Note(true);
        }

        if (confirmed)
        {
            _ = _host.Settings.Update(s => s with { Study = s.Study with { LabellingConfirmed = true } });
        }

        var page = new StudyViewModel(_bench, _owner, _host.Settings, _host.Game, _host.Dispatcher, _host.Time, () =>
        {
            _recorder.Add(_recorderRunning);
            return _recorderRunning;
        });
        _owned.Add(page);
        return page;
    }

    private static GameLifecycle Closed => GameLifecycle.Initial;

    private void PlayAndClose()
    {
        _host.Game.SetRunning(_host.Time.GetUtcNow());
        _host.Game.Set(Closed);
    }

    // ----- Who sees it -----

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void WhenTheAccountIsNotKnownToBeTheOwners_ThePageDoesNotExist_AndTheShellHasNoNavigationItemForIt(bool? flag)
    {
        if (flag is { } known)
        {
            _owner.Note(known);
        }

        var page = Page(owner: false);
        using var kit = new AppKit(_host, s_app, study: page);

        Assert.False(page.IsAvailable);
        Assert.DoesNotContain(kit.Shell.Pages, p => p.Key == "study");
        Assert.False(kit.Shell.SelectByKey("study"));
        Assert.False(kit.Shell.SelectByShortcut(6));
        Assert.Equal(5, kit.Shell.Pages.Count);
    }

    [Fact]
    public void ForTheOwner_TheNavigationItemExists_AndComesAndGoesWithTheFlag()
    {
        var page = Page();
        using var kit = new AppKit(_host, s_app, study: page);

        var item = Assert.Single(kit.Shell.Pages, p => p.Key == "study");
        Assert.Same(page, item.Page);
        Assert.Equal(6, item.Shortcut);
        Assert.True(kit.Shell.SelectByShortcut(6));

        _owner.Note(false); // the account lost the flag: the page goes away and Home is shown

        Assert.DoesNotContain(kit.Shell.Pages, p => p.Key == "study");
        Assert.Equal("home", kit.Shell.SelectedPage.Key);
        _owner.Note(true);
        Assert.Contains(kit.Shell.Pages, p => p.Key == "study");
    }

    [Fact]
    public void SigningOut_ForgetsTheFlag_AndThePageGoes()
    {
        var page = Page();
        using var kit = new AppKit(_host, s_app, study: page);

        _owner.Forget();

        Assert.False(page.IsAvailable);
        Assert.DoesNotContain(kit.Shell.Pages, p => p.Key == "study");
    }

    [Fact]
    public async Task WithoutTheFlag_NothingRuns_NoMatterWhatIsPressedOrWhatTheGameDoes()
    {
        var page = Page(owner: false);

        PlayAndClose();
        await page.LabelNowAsync();
        await page.RefreshAsync();
        _host.Time.Advance(StudyViewModel.SettleDelay);

        Assert.Equal(0, _bench.RunCount);
        Assert.Equal(0, _bench.ListCount);
        Assert.False(page.LabelNowCommand.CanExecute(null));
    }

    [Fact]
    public void TheOwnerFlag_StartsUnknown_AndRaisesChangedOnlyWhenTheAnswerChanges()
    {
        var changes = 0;
        _owner.Changed += (_, _) => changes++;
        Assert.Null(_owner.IsOwner);

        _owner.Note(true);
        _owner.Note(true);
        _owner.Note(false);
        _owner.Forget();
        _owner.Forget();

        Assert.Equal(3, changes);
        Assert.Null(_owner.IsOwner);
    }

    // ----- The list -----

    [Fact]
    public async Task EveryRecording_IsShownWithItsStateInPlainWords_AndTheTokenCountsOfItsLastRun_NeverAMoneyFigure()
    {
        _bench.Recordings.AddRange(
        [
            FakeWorkbench.Status("2026-10-20_10-00-00", RecordingStage.New),
            FakeWorkbench.Status("2026-10-21_10-00-00", RecordingStage.ReviewFileMade),
            FakeWorkbench.Status("2026-10-22_10-00-00", RecordingStage.SpotCheckReady, input: 5700, output: 150, skipped: 2),
            FakeWorkbench.Status("2026-10-23_10-00-00", RecordingStage.SpotCheckDone, input: 1_234_567, output: 8900),
            FakeWorkbench.Status("2026-10-24_10-00-00", RecordingStage.Disputed),
            FakeWorkbench.Status("2026-10-25_10-00-00", RecordingStage.Failed, automatic: false, failure: "The labeller refused the request. Nothing was written.", input: 10, output: 0),
            FakeWorkbench.Status("2026-10-26_10-00-00", RecordingStage.SpotCheckLater, automatic: false),
        ]);
        var page = Page();
        await page.LastWork;

        var states = page.Recordings.Select(r => r.StateText).ToArray();
        Assert.Equal(
            [
                "New",
                "Review file made",
                "Labelled automatically — spot-check ready (about 2 minutes)",
                "Spot-check done",
                "Disputed — rework it in the study tool",
                "Failed: The labeller refused the request. Nothing was written.",
                "Labelled in the study tool — spot-check not due yet",
            ],
            states);
        Assert.Equal("Last automatic run: 5,700 tokens read, 150 written", page.Recordings[2].TokensText);
        Assert.Equal("2 pictures could not be sent", page.Recordings[2].SkippedText);
        Assert.Equal("Last automatic run: 1,234,567 tokens read, 8,900 written", page.Recordings[3].TokensText);
        Assert.Null(page.Recordings[0].TokensText);
        Assert.Null(page.Recordings[0].SkippedText);
        Assert.All(page.Recordings, r => Assert.DoesNotContain("$", (r.TokensText ?? string.Empty) + r.StateText + (r.SkippedText ?? string.Empty), StringComparison.Ordinal));
        Assert.Equal([false, false, true, false, false, false, false], page.Recordings.Select(r => r.CanSpotCheck).ToArray());
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task NoRecordings_IsTheEmptyState()
    {
        var page = Page();
        await page.LastWork;

        Assert.True(page.IsEmpty);
        Assert.Empty(page.Recordings);
    }

    // ----- The automatic run -----

    [Fact]
    public async Task WhenTheGameCloses_WithTheSettingOn_TheRunStartsAfterTheSettleDelay()
    {
        Assert.True(_host.Settings.Current.Study.LabelAfterPlay);
        var page = Page();
        await page.LastWork;

        PlayAndClose();
        Assert.Equal(0, _bench.RunCount);
        _host.Time.Advance(StudyViewModel.SettleDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _bench.RunCount);
        _host.Time.Advance(TimeSpan.FromSeconds(1));
        await page.LastWork;

        Assert.Equal(1, _bench.RunCount);
        Assert.False(page.IsRunning);
    }

    [Fact]
    public async Task WithTheSettingOff_TheGameClosingStartsNothing()
    {
        var page = Page();
        page.LabelAfterPlay = false;
        await page.LastWork;

        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay + StudyViewModel.RecorderPoll);

        Assert.Equal(0, _bench.RunCount);
        Assert.False(_host.Settings.Current.Study.LabelAfterPlay);
    }

    [Fact]
    public async Task TheSwitch_IsStoredInTheSettings_AndDefaultsToOn()
    {
        var page = Page();
        await page.LastWork;
        var seen = new List<string?>();
        page.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        Assert.True(page.LabelAfterPlay);
        page.LabelAfterPlay = false;
        Assert.False(_host.Settings.Current.Study.LabelAfterPlay);
        Assert.Contains(nameof(StudyViewModel.LabelAfterPlay), seen);
        page.LabelAfterPlay = true;
        Assert.True(_host.Settings.Current.Study.LabelAfterPlay);
    }

    [Fact]
    public async Task ARunNeverStartsWhileTheGameIsRunning_NotAfterAnEarlierCloseAndNotFromTheButton()
    {
        var page = Page();
        await page.LastWork;
        PlayAndClose();

        _host.Game.SetRunning(_host.Time.GetUtcNow()); // the game starts again during the settle delay
        _host.Time.Advance(StudyViewModel.SettleDelay);
        await page.LastWork;

        Assert.Equal(0, _bench.RunCount);
        Assert.False(page.LabelNowCommand.CanExecute(null));
        await page.LabelNowAsync();
        Assert.Equal(0, _bench.RunCount);

        _host.Game.Set(Closed);
        Assert.True(page.LabelNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileTheStudyRecorderRuns_TheRunWaits_ThenStartsWhenItHasStopped()
    {
        var page = Page();
        await page.LastWork;
        _recorderRunning = true;
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        Assert.Equal(0, _bench.RunCount);

        _host.Time.Advance(StudyViewModel.RecorderPoll);
        Assert.Equal(0, _bench.RunCount);
        _recorderRunning = false;
        _host.Time.Advance(StudyViewModel.RecorderPoll);
        await page.LastWork;

        Assert.Equal(1, _bench.RunCount);
        Assert.True(_recorder.Count >= 3);
    }

    [Fact]
    public async Task IfTheRecorderNeverStops_TheRunGivesUpWithAPlainSentence_AndTheButtonStillWorks()
    {
        var page = Page();
        await page.LastWork;
        _recorderRunning = true;
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        for (var i = 0; i < StudyViewModel.RecorderPolls; i++)
        {
            _host.Time.Advance(StudyViewModel.RecorderPoll);
        }

        await page.LastWork;

        Assert.Equal(0, _bench.RunCount);
        Assert.Contains("study recorder is still running", page.ProgressText, StringComparison.Ordinal);
        _recorderRunning = false;
        await page.LabelNowAsync();
        Assert.Equal(1, _bench.RunCount);
    }

    [Fact]
    public async Task OnlyOneRunIsActiveAtATime()
    {
        var page = Page();
        await page.LastWork;
        _bench.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = page.LabelNowAsync();
        Assert.True(page.IsRunning);
        Assert.False(page.LabelNowCommand.CanExecute(null));
        await page.LabelNowAsync(); // refused: a run is active
        Assert.Equal(1, _bench.RunCount);

        _bench.Hold.SetResult();
        await first;
        Assert.False(page.IsRunning);
        Assert.Equal(1, _bench.RunCount);
    }

    [Fact]
    public async Task TheButton_RunsNow_ShowsProgress_AndTellsWhatHappened()
    {
        _bench.Report = new StudyRunReport(Labelled: 2, Failed: 0, ReviewFilesMade: 2, StudyProblem.None);
        _bench.OnRunEnd = () => _bench.Recordings.Add(FakeWorkbench.Status("2026-10-24_19-30-12", RecordingStage.SpotCheckReady, input: 100, output: 5));
        var page = Page();
        await page.LastWork;
        var announced = 0;
        page.SpotCheckReady += (_, _) => announced++;

        await page.LabelNowAsync();

        Assert.Contains("part 1 of 1", string.Join('|', _bench.ProgressSeen), StringComparison.Ordinal);
        Assert.Equal("Labelled 2 recordings. Spot-check ready (about 2 minutes).", page.ResultText);
        Assert.Null(page.ProblemText);
        Assert.Null(page.ProgressText);
        Assert.Single(page.Recordings);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task ARunThatFindsNothingNew_SaysSo_AndDoesNotAnnounceASpotCheck()
    {
        var page = Page();
        await page.LastWork;
        var announced = 0;
        page.SpotCheckReady += (_, _) => announced++;

        await page.LabelNowAsync();

        Assert.Equal("Nothing new to label.", page.ResultText);
        Assert.Equal(0, announced);
    }

    [Theory]
    [InlineData(StudyProblem.NotSetUp, "Automatic labelling is not set up on your service yet. See step 8a of the Cloudflare guide.")]
    [InlineData(StudyProblem.DailyCap, "Today's automatic labelling limit is used up; it continues tomorrow.")]
    [InlineData(StudyProblem.Busy, "The labelling service is busy; try again later.")]
    [InlineData(StudyProblem.Other, "Some recordings could not be labelled. The list says why.")]
    public async Task EachProblem_IsToldInItsOwnPlainSentence(StudyProblem problem, string sentence)
    {
        _bench.Report = new StudyRunReport(0, 1, 0, problem);
        var page = Page();
        await page.LastWork;

        await page.LabelNowAsync();

        Assert.Equal(sentence, page.ProblemText);
        Assert.Null(page.ResultText);
        Assert.True(page.IsAvailable);
    }

    [Fact]
    public async Task WhenTheServiceSaysThisIsNotTheOwnersAccount_ThePageIsHidden()
    {
        _bench.Report = new StudyRunReport(0, 1, 0, StudyProblem.NotAllowed);
        var page = Page();
        using var kit = new AppKit(_host, s_app, study: page);
        await page.LastWork;
        var changes = 0;
        page.AvailabilityChanged += (_, _) => changes++;

        await page.LabelNowAsync();

        Assert.False(page.IsAvailable);
        Assert.False(_owner.IsOwner);
        Assert.DoesNotContain(kit.Shell.Pages, p => p.Key == "study");
        Assert.Equal(1, changes);
        Assert.Empty(page.Recordings);
        Assert.Null(page.ProblemText);
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_IsToldInPlainWords_NotAsAnError()
    {
        _bench.OnRunEnd = () => throw new IOException("disk full");
        var page = Page();
        await page.LastWork;

        await page.LabelNowAsync();

        Assert.Equal("Automatic labelling stopped: the study folder could not be read or written.", page.ProblemText);
        Assert.False(page.IsRunning);
        Assert.True(page.LabelNowCommand.CanExecute(null));
    }

    // ----- Confirmation, switch, lifecycle (security review M2, QA review M1 to M3 and section 5) -----

    private void SetSwitch(bool on) => _ = _host.Settings.Update(s => s with { Study = s.Study with { LabelAfterPlay = on } });

    private void SetConfirmed(bool on) => _ = _host.Settings.Update(s => s with { Study = s.Study with { LabellingConfirmed = on } });

    private static GameLifecycle Launching => new() { State = GameState.Launching };

    [Fact]
    public async Task UntilTheOwnerConfirmsOnce_NothingIsSent_NotAfterPlayAndNotFromTheButton_AndTheConfirmationIsShown()
    {
        var page = Page(confirmed: false);
        await page.LastWork;
        Assert.False(page.LabellingConfirmed);
        Assert.True(page.IsConfirmationNeeded);
        Assert.False(page.IsLabellingOn);
        Assert.False(page.LabelNowCommand.CanExecute(null));
        Assert.DoesNotContain("claude", string.Join(' ', page.ConfirmParagraphs) + page.Intro + page.PrivacyNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other players' names", string.Join(' ', page.ConfirmParagraphs), StringComparison.Ordinal);

        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay + StudyViewModel.RecorderPoll);
        await page.LastWork;
        await page.LabelNowAsync();

        Assert.Equal(0, _bench.RunCount);
        Assert.Equal(StudyViewModel.NotConfirmedText, page.ProblemText);
    }

    [Fact]
    public async Task Confirming_IsStoredInTheSettings_EnablesTheButtonAndTheAutomaticRun_AndCanBeWithdrawn()
    {
        var page = Page(confirmed: false);
        await page.LastWork;
        var seen = new List<string?>();
        page.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        page.ConfirmLabellingCommand.Execute(null);

        Assert.True(_host.Settings.Current.Study.LabellingConfirmed);
        Assert.True(page.IsLabellingOn);
        Assert.False(page.IsConfirmationNeeded);
        Assert.Contains(nameof(StudyViewModel.IsConfirmationNeeded), seen);
        Assert.True(page.LabelNowCommand.CanExecute(null));
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        await page.LastWork;
        Assert.Equal(1, _bench.RunCount);

        page.WithdrawLabellingCommand.Execute(null);
        Assert.False(_host.Settings.Current.Study.LabellingConfirmed);
        Assert.False(page.LabelNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task TurningTheSwitchOff_WhileTheRunWaitsForTheSettleDelay_CancelsIt()
    {
        var page = Page();
        await page.LastWork;
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay - TimeSpan.FromSeconds(1));

        SetSwitch(false);
        _host.Time.Advance(TimeSpan.FromSeconds(1));
        await page.LastWork;

        Assert.Equal(0, _bench.RunCount);
    }

    [Fact]
    public async Task TurningTheSwitchOff_WhileWaitingForTheRecorder_CancelsIt()
    {
        var page = Page();
        await page.LastWork;
        _recorderRunning = true;
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        _host.Time.Advance(StudyViewModel.RecorderPoll);

        SetSwitch(false);
        _recorderRunning = false;
        _host.Time.Advance(StudyViewModel.RecorderPoll);
        await page.LastWork;

        Assert.Equal(0, _bench.RunCount);
    }

    [Fact]
    public async Task WithdrawingTheConfirmation_WhileTheRunWaits_CancelsIt()
    {
        var page = Page();
        await page.LastWork;
        PlayAndClose();

        SetConfirmed(false);
        _host.Time.Advance(StudyViewModel.SettleDelay);
        await page.LastWork;

        Assert.Equal(0, _bench.RunCount);
    }

    [Fact]
    public async Task TurningTheSwitchOff_CancelsARunThatIsGoing_ButNotOneTheOwnerStartedWithTheButton()
    {
        var page = Page();
        await page.LastWork;
        _bench.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        Assert.Equal([false], _bench.ManualRuns);
        SetSwitch(false);
        Assert.True(_bench.Tokens[0].IsCancellationRequested);
        await page.LastWork;
        Assert.False(page.IsRunning);

        _bench.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = page.LabelNowAsync();
        SetSwitch(true);
        SetSwitch(false);
        Assert.False(_bench.Tokens[1].IsCancellationRequested);
        SetConfirmed(false);
        Assert.True(_bench.Tokens[1].IsCancellationRequested);
        await manual;
    }

    [Fact]
    public async Task WhenTheGameStarts_ARunThatIsGoing_IsCancelled_AndALauncherAloneCountsAsTheGame()
    {
        var page = Page();
        await page.LastWork;
        _bench.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = page.LabelNowAsync();
        Assert.True(page.IsRunning);

        _host.Game.Set(Launching);

        Assert.True(_bench.Tokens[0].IsCancellationRequested);
        await run;
        Assert.False(page.IsRunning);
        Assert.False(page.LabelNowCommand.CanExecute(null));
        await page.LabelNowAsync();
        Assert.Equal(1, _bench.RunCount);
        Assert.Equal(StudyViewModel.GameRunningText, page.ProblemText);
    }

    [Fact]
    public async Task CloseStartClose_RunsOnce_AfterTheFullDelayFromTheLastClose()
    {
        var page = Page();
        await page.LastWork;
        PlayAndClose(); // t0
        _host.Time.Advance(TimeSpan.FromSeconds(5));
        _host.Game.SetRunning(_host.Time.GetUtcNow()); // t0+5
        _host.Time.Advance(TimeSpan.FromSeconds(5));
        _host.Game.Set(Closed); // t0+10

        _host.Time.Advance(StudyViewModel.SettleDelay - TimeSpan.FromSeconds(10)); // t0+20: the first wait would have ended here
        Assert.Equal(0, _bench.RunCount);
        _host.Time.Advance(TimeSpan.FromSeconds(9)); // t0+29
        Assert.Equal(0, _bench.RunCount);
        _host.Time.Advance(TimeSpan.FromSeconds(1)); // t0+30 = the last close + 20 s
        await page.LastWork;
        Assert.Equal(1, _bench.RunCount);
        _host.Time.Advance(StudyViewModel.SettleDelay * 3);
        await page.LastWork;
        Assert.Equal(1, _bench.RunCount);
    }

    [Fact]
    public async Task OddLifecycleSequences_NeverRunDuringLaunchingOrRunning_AndRunOnceAfterTheFinalClose()
    {
        var page = Page();
        await page.LastWork;

        _host.Game.Set(Launching);
        _host.Game.Set(Closed); // a launcher that never became a game is not a close
        _host.Time.Advance(StudyViewModel.SettleDelay * 2);
        await page.LastWork;
        Assert.Equal(0, _bench.RunCount);

        _host.Game.SetRunning(_host.Time.GetUtcNow());
        _host.Game.SetRunning(_host.Time.GetUtcNow()); // duplicate Running event
        _host.Game.Set(Launching);
        _host.Game.SetRunning(_host.Time.GetUtcNow());
        _host.Time.Advance(StudyViewModel.SettleDelay * 2);
        Assert.Equal(0, _bench.RunCount);

        _host.Game.Set(Closed);
        _host.Time.Advance(StudyViewModel.SettleDelay);
        await page.LastWork;
        Assert.Equal(1, _bench.RunCount);
    }

    [Fact]
    public async Task TheButton_RefusesWhileTheStudyRecorderRuns_WithAPlainSentence_AndSendsNothing()
    {
        var page = Page();
        await page.LastWork;
        _recorderRunning = true;

        await page.LabelNowAsync();

        Assert.Equal(0, _bench.RunCount);
        Assert.Equal(StudyViewModel.RecorderRunningText, page.ProblemText);
        _recorderRunning = false;
        await page.LabelNowAsync();
        Assert.Equal(1, _bench.RunCount);
        Assert.Equal([true], _bench.ManualRuns);
    }

    [Fact]
    public async Task TheAutomaticRun_IsNotManual_AndTheButtonIs_SoTheWorkbenchCanTellThemApart()
    {
        var page = Page();
        await page.LastWork;
        PlayAndClose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        await page.LastWork;
        await page.LabelNowAsync();

        Assert.Equal([false, true], _bench.ManualRuns);
    }

    [Fact]
    public async Task TheRunsRequestsAndTokens_AreShown_AndTheRunLimitHasItsOwnSentence()
    {
        _bench.Report = new StudyRunReport(1, 0, 1, StudyProblem.None, Requests: 12, InputTokens: 345_678, OutputTokens: 9_000);
        var page = Page();
        await page.LastWork;

        await page.LabelNowAsync();

        Assert.Equal("Labelled 1 recording. Spot-check ready (about 2 minutes). Last run: 12 requests, 345,678 tokens read, 9,000 written.", page.ResultText);

        _bench.Report = new StudyRunReport(0, 1, 0, StudyProblem.RunLimit, Requests: 200, InputTokens: 10, OutputTokens: 2);
        await page.LabelNowAsync();
        Assert.Equal(StudyViewModel.RunLimitText, page.ProblemText);
        Assert.Equal("Last run: 200 requests, 10 tokens read, 2 written.", page.ResultText);
    }

    [Fact]
    public async Task ARecordingThatFailedTwice_SaysItIsNotTriedAgainAutomatically()
    {
        _bench.Recordings.Add(FakeWorkbench.Status("2026-10-25_10-00-00", RecordingStage.Failed, failure: "The labeller's answer could not be used twice in a row.", retryStopped: true));
        var page = Page();
        await page.LastWork;

        Assert.Contains("not tried again automatically", page.Recordings[0].StateText, StringComparison.Ordinal);
        Assert.Contains("Label new recordings now", page.Recordings[0].StateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheOwnerFlagIsLostDuringARun_TheRunIsCancelled_AndThePageStaysHidden()
    {
        var page = Page();
        await page.LastWork;
        _bench.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = page.LabelNowAsync();

        _owner.Note(false);

        Assert.True(_bench.Tokens[0].IsCancellationRequested);
        await run;
        Assert.False(page.IsAvailable);
        Assert.Empty(page.Recordings);
    }

    [Theory]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(IOException))]
    public async Task ASpotCheckWhoseFilesCannotBeRead_IsASentenceOnThePage_NotAnException(Type kind)
    {
        _bench.Recordings.Add(FakeWorkbench.Status("2026-10-24_19-30-12", RecordingStage.SpotCheckReady));
        _bench.OpenThrows = (Exception)Activator.CreateInstance(kind, "C:\\secret\\path")!;
        var page = Page();
        await page.LastWork;

        await page.OpenSpotCheckAsync(page.Recordings[0]);

        Assert.Equal("The spot-check could not be prepared: its pictures could not be read.", page.ProblemText);
        Assert.Null(page.SpotCheck);
    }

    [Fact]
    public async Task ClosingTheApp_StopsAWaitingRun()
    {
        var page = Page();
        await page.LastWork;
        PlayAndClose();

        page.Dispose();
        _host.Time.Advance(StudyViewModel.SettleDelay);
        await page.LastWork;

        Assert.Equal(0, _bench.RunCount);
        Assert.Equal(0, _host.Game.SubscriberCount);
    }

    // ----- The spot-check -----

    private async Task<StudyViewModel> OpenedSpotCheck()
    {
        _bench.Recordings.Add(FakeWorkbench.Status("2026-10-24_19-30-12", RecordingStage.SpotCheckReady));
        _bench.Sheet = FakeWorkbench.OneOfEach();
        var page = Page();
        await page.LastWork;
        await page.OpenSpotCheckAsync(page.Recordings[0]);
        return page;
    }

    [Fact]
    public async Task TheSpotCheck_ShowsPicturesAndChoices_AndNoEarlierAnswerAndNoSuggestion()
    {
        var page = await OpenedSpotCheck();

        var spot = Assert.IsType<SpotCheckViewModel>(page.SpotCheck);
        Assert.True(page.IsSpotCheckOpen);
        Assert.Equal(2, spot.Segments.Count);
        Assert.Single(spot.Matches);
        Assert.All(spot.Segments, r =>
        {
            Assert.Null(r.SelectedScene);
            Assert.NotEmpty(r.Pictures);
            Assert.Equal(2, r.Scenes.Count);
        });
        var match = spot.Matches[0];
        Assert.Null(match.SelectedRole);
        Assert.Null(match.SelectedCharacter);
        Assert.Null(match.SelectedMap);
        Assert.Null(match.SelectedResult);
        Assert.Equal(string.Empty, match.SelectedKiller?.Value);
        Assert.Equal(string.Empty, match.SelectedQueue?.Value);
        Assert.False(match.ShowResult);
        Assert.Contains("Nothing here shows what the automatic labelling said", spot.Intro, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpotCheckModels_HaveNoMemberThatCouldCarryAnEarlierAnswerOrASuggestion()
    {
        string[] banned = ["Earlier", "Previous", "Original", "Suggest", "Prefill", "Labelled", "Expected", "Automatic"];
        var types = new[] { typeof(SpotCheckSheet), typeof(SpotCheckSegment), typeof(SpotCheckMatch), typeof(SpotCheckViewModel), typeof(SpotCheckSegmentRowViewModel), typeof(SpotCheckMatchRowViewModel) };

        var offending = types.SelectMany(t => t.GetMembers().Select(m => $"{t.Name}.{m.Name}")).Where(n => banned.Any(b => n.Contains(b, StringComparison.OrdinalIgnoreCase))).ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public async Task OnlyARecordingWhoseSpotCheckIsDue_CanBeOpened()
    {
        _bench.Recordings.Add(FakeWorkbench.Status("2026-10-24_19-30-12", RecordingStage.Disputed));
        _bench.Sheet = FakeWorkbench.OneOfEach();
        var page = Page();
        await page.LastWork;

        await page.OpenSpotCheckAsync(page.Recordings[0]);

        Assert.Null(page.SpotCheck);
    }

    [Fact]
    public async Task SavingWithRowsUnanswered_SavesNothing_AndMarksWhatIsMissing()
    {
        var page = await OpenedSpotCheck();
        var spot = page.SpotCheck!;
        spot.Segments[0].SelectedScene = spot.Segments[0].Scenes[0];

        await page.SaveSpotCheckAsync();

        Assert.Null(_bench.Saved);
        Assert.Same(spot, page.SpotCheck);
        Assert.Equal("2 things still need an answer (marked “Needs an answer”).", spot.Message);
        Assert.Null(spot.Segments[0].Missing);
        Assert.Equal("Needs an answer", spot.Segments[1].Missing);
        Assert.Equal("Needs an answer", spot.Matches[0].Missing);
    }

    private static void AnswerAll(SpotCheckViewModel spot, string role = "Survivor")
    {
        spot.Segments[0].SelectedScene = spot.Segments[0].Scenes[0];
        spot.Segments[1].SelectedScene = spot.Segments[1].Scenes[1];
        var match = spot.Matches[0];
        match.SelectedRole = match.Roles.Single(c => c.Value == role);
        match.SelectedCharacter = match.Characters.Single(c => c.Value == "dwight-fairfield");
        match.SelectedMap = match.Maps.Single(c => c.Value == "ormond/mount-ormond-resort");
        if (role == "Survivor")
        {
            match.SelectedResult = match.Results.Single(c => c.Value == "Escaped");
        }
    }

    [Fact]
    public async Task ASurvivorMatch_NeedsAResult_AKillerMatchDoesNot()
    {
        var page = await OpenedSpotCheck();
        var spot = page.SpotCheck!;
        AnswerAll(spot, role: "Killer");
        Assert.False(spot.Matches[0].ShowResult);
        Assert.NotNull(spot.CollectAnswers());

        spot.Matches[0].SelectedRole = spot.Matches[0].Roles.Single(c => c.Value == "Survivor");
        Assert.True(spot.Matches[0].ShowResult);
        Assert.Null(spot.CollectAnswers());
        Assert.Equal("1 thing still needs an answer (marked “Needs an answer”).", spot.Message);
    }

    [Fact]
    public async Task SavingCompleteAnswers_GradesThemThroughTheLibrary_AndShowsThePlainOutcome()
    {
        var page = await OpenedSpotCheck();
        AnswerAll(page.SpotCheck!);
        _bench.Recordings[0] = FakeWorkbench.Status("2026-10-24_19-30-12", RecordingStage.SpotCheckDone);

        await page.SaveSpotCheckAsync();

        var saved = Assert.IsType<SpotCheckAnswers>(_bench.Saved);
        Assert.Equal([(0, "MainMenu"), (2, "Lobby")], saved.Segments.Select(s => (s.Index, s.Scene)).ToArray());
        var match = Assert.Single(saved.Matches);
        Assert.Equal(new SpotCheckMatchAnswer(0, "Survivor", "dwight-fairfield", "ormond/mount-ormond-resort", "Escaped", string.Empty, string.Empty), match);
        Assert.Null(page.SpotCheck);
        Assert.Equal("Spot-check of 2026-10-24_19-30-12 done: your answers agree with the automatic labels, so the recording now counts.", page.ResultText);
        Assert.Equal("Spot-check done", page.Recordings[0].StateText);
    }

    [Fact]
    public async Task ADisputedSpotCheck_SendsTheOwnerToTheStudyTool()
    {
        var page = await OpenedSpotCheck();
        AnswerAll(page.SpotCheck!);
        _bench.SaveResult = new SpotCheckSaveResult(new SpotCheckVerdict(false, 3, 0.62, 1, ["character", "map", "character"]), null);

        await page.SaveSpotCheckAsync();

        Assert.Equal("Spot-check of 2026-10-24_19-30-12: disputed — rework it in the study tool. Agreement on the screens: 0.62 (0.90 is needed). Different: character, map.", page.ResultText);
    }

    [Fact]
    public async Task WhenTheAnswersCannotBeSaved_TheSpotCheckStaysOpenWithTheReason()
    {
        var page = await OpenedSpotCheck();
        AnswerAll(page.SpotCheck!);
        _bench.SaveResult = new SpotCheckSaveResult(null, "The answers could not be saved: disk full Nothing was changed.");

        await page.SaveSpotCheckAsync();

        Assert.NotNull(page.SpotCheck);
        Assert.Equal("The answers could not be saved: disk full Nothing was changed.", page.SpotCheck!.Message);
    }

    [Fact]
    public async Task ClosingTheSpotCheck_KeepsItDue()
    {
        var page = await OpenedSpotCheck();

        page.CloseSpotCheckCommand.Execute(null);

        Assert.Null(page.SpotCheck);
        Assert.True(page.Recordings[0].CanSpotCheck);
        Assert.Null(_bench.Saved);
    }

    [Fact]
    public async Task ARecordingWithNoSpotCheckDue_SaysSoInsteadOfOpeningAnEmptyPage()
    {
        _bench.Recordings.Add(FakeWorkbench.Status("2026-10-24_19-30-12", RecordingStage.SpotCheckReady));
        _bench.Sheet = null;
        var page = Page();
        await page.LastWork;

        await page.OpenSpotCheckAsync(page.Recordings[0]);

        Assert.Null(page.SpotCheck);
        Assert.Equal("This recording has no spot-check due.", page.ProblemText);
    }
}
