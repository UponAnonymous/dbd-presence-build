// Covers: FR-CI-02 (at most one just-in-time runner, named dbd-linux-<timestamp>, labelled self-hosted + dbd-linux, runner group 1; the lane is freed only when its environment is gone),
// FR-CI-03 (gating; a waiting runner is stopped when the gate closes; a running job is never interrupted by the gate but is stopped after 120 minutes), FR-CI-07 (job history),
// local CI round 2 QA B1 (a failed, damaged or unreadable answer from GitHub never orphans a running job), red-team round 2 (decision D41: one Linux lane). docs/owner/free-ci.md.
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class ServiceTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly Redactor _redactor = new();
    private readonly MemoryCiLog _log;
    private readonly FakeGitHubApi _api = new();
    private readonly FakeProcesses _processes = new();
    private readonly FakeMaintenance _maintenance = new();
    private readonly ManualDelay _delay = new();
    private readonly FakeJobHost _linux = new() { BlockUntilReleased = true };
    private readonly FakeNotifier _notifier = new();
    private readonly StateStore _state;
    private readonly LocalCiService _service;

    public ServiceTests()
    {
        _log = new MemoryCiLog(_redactor);
        _state = new StateStore(_folder.Paths.StateFile);
        var retry = new Retry(_time, _redactor, _log, attempts: 1);
        _service = new LocalCiService(_api, _linux, _maintenance, new JobGate(_processes), _state, _log, _redactor, _time, retry, new RepositoryGuard(_api, _state, _time, _log, _redactor), new OwnerNotices(_notifier, _state, _time), _delay.Delay);
    }

    public void Dispose() => _folder.Dispose();

    private async Task FinishAsync()
    {
        _linux.Release();
        await _service.WhenIdleAsync().OrFail();
    }

    private async Task StartAsync()
    {
        await _service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();
    }

    // ---- round 3, M1: a job stopped because the drive was almost full

    [Fact]
    public async Task AJobStoppedForRoom_IsRecordedAsAFailure_AndTheOwnerIsToldOncePerDay_NotWhileTheGameRuns()
    {
        _linux.BlockUntilReleased = false;
        const string Text = "The drive that holds the Linux machines has 2.0 GB free, less than 3 GB, so the job was stopped.";

        // The game starts while the job is being stopped: the window is not shown over it.
        _linux.FailureFactory = _ =>
        {
            _processes.Running.Add("DeadByDaylight");
            return new LowDiskException(Text);
        };
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Empty(_notifier.Messages);
        Assert.Contains(_state.Load().Jobs, j => j.Result == "error: LowDiskException: " + Text);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains(Text, StringComparison.Ordinal));

        _processes.Running.Clear();
        _linux.FailureFactory = _ => new LowDiskException(Text);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Equal(Text, Assert.Single(_notifier.Messages));

        _time.Advance(TimeSpan.FromMinutes(10));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        Assert.Single(_notifier.Messages);

        _time.Advance(TimeSpan.FromHours(25));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        Assert.Equal(2, _notifier.Messages.Count);
    }

    [Fact]
    public async Task AnotherFailureOfTheEnvironment_GivesNoDiskNotice()
    {
        _linux.BlockUntilReleased = false;
        _linux.Failure = new InvalidOperationException("environment broke");

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Empty(_notifier.Messages);
    }

    [Fact]
    public async Task OneRunner_EvenAfterManyRounds()
    {
        await StartAsync();
        await _service.TickAsync(CancellationToken.None);
        await _service.TickAsync(CancellationToken.None);

        Assert.Single(_api.JitRequests);
        Assert.Equal("dbd-linux-20261029120000", _api.JitRequests[0].Name);
        Assert.True(_service.IsRunning);
        await FinishAsync();
    }

    [Fact]
    public async Task TheRequestNamesTheLabels_AndTheRunnerGroup()
    {
        await StartAsync();

        Assert.Equal(["self-hosted", "dbd-linux"], _api.JitRequests.Single().Labels);
        await FinishAsync();
    }

    [Fact]
    public async Task AFinishedRunner_IsReplacedOnTheNextRound_AndTheJobIsRecorded()
    {
        _linux.BlockUntilReleased = false;

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        _time.Advance(TimeSpan.FromSeconds(30));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Equal(2, _linux.Launches.Count);
        Assert.Equal(2, _state.Load().Jobs.Count(j => j.Os == "Linux" && j.Result == "job finished"));
    }

    [Fact]
    public async Task WhileTheGameRuns_NoRunnerStarts()
    {
        _processes.Running.Add("DeadByDaylight-Win64-Shipping");

        await _service.TickAsync(CancellationToken.None);

        Assert.Empty(_api.JitRequests);
        Assert.False(_service.IsRunning);
        Assert.Contains(_log.Lines, l => l.Contains("Dead by Daylight is running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunnerIsNotStarted_WhileTheDownloadsOrTheBaseImageAreMissing()
    {
        _maintenance.Package = null;
        await _service.TickAsync(CancellationToken.None);
        Assert.Empty(_api.JitRequests);

        _maintenance.Package = "linux.tar.gz";
        _linux.NotReady = "something is missing";
        await _service.TickAsync(CancellationToken.None);
        Assert.Empty(_api.JitRequests);
        Assert.Contains(_log.Lines, l => l.Contains("something is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenGitHubRefusesTheRegistration_NothingStarts_AndItIsRetriedOnlyAfterAPause()
    {
        _api.JitFailure = new GitHubApiException("no", 403, isTransient: false);

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Single(_api.JitRequests);
        Assert.Empty(_linux.Launches);
        _time.Advance(TimeSpan.FromMinutes(2));
        _api.JitFailure = null;
        await StartAsync();
        Assert.Equal(2, _api.JitRequests.Count);
        await FinishAsync();
    }

    [Fact]
    public async Task ARepeatedFailure_MakesTheLaneWaitTwiceAsLongEachTime_UpToThirtyMinutes()
    {
        _api.JitFailure = new GitHubApiException("no", 403, isTransient: false);

        async Task<bool> TryOnceAsync()
        {
            var before = _api.JitRequests.Count;
            await _service.TickAsync(CancellationToken.None);
            await _service.WhenIdleAsync().OrFail();
            return _api.JitRequests.Count > before;
        }

        Assert.True(await TryOnceAsync());
        var waits = new List<int>();
        for (var failure = 1; failure <= 7; failure++)
        {
            var minutes = 0;
            while (!await TryOnceAsync())
            {
                minutes++;
                _time.Advance(TimeSpan.FromMinutes(1));
                Assert.True(minutes < 40, "the lane never tried again");
            }

            waits.Add(minutes);
        }

        Assert.Equal([1, 2, 4, 8, 16, 30, 30], waits);
    }

    [Fact]
    public async Task AWaitingRunner_IsStoppedAndForgotten_WhenTheGameStarts()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));
        _processes.Running.Add("DeadByDaylight");

        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.Contains(1L, _api.DeletedRunners);
        Assert.Empty(_state.Load().Jobs);
    }

    [Fact]
    public async Task ARunningJob_IsNotInterruptedByTheGate_ButIsStoppedAfterTheTimeout()
    {
        await StartAsync();
        var name = _api.JitRequests[0].Name;
        _api.Runners.Add(new RunnerInfo(1, name, "online", Busy: true));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        _processes.Running.Add("DeadByDaylight");
        _time.Advance(TimeSpan.FromMinutes(60));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.False(_linux.WasCancelled);

        _time.Advance(TimeSpan.FromMinutes(61));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.DoesNotContain(1L, _api.DeletedRunners);
        Assert.Contains(_state.Load().Jobs, j => j.Os == "Linux" && j.Result.StartsWith("stopped after 120 minutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJobWhoseTokenEndsWhileItRuns_IsStillStoppedAfterTheTimeout_BecauseItIsJudgedByTheClock()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: true));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.False(_linux.WasCancelled);

        // GitHub refuses every question from now on (the token ended); the job started long ago, so the limit still ends it.
        _api.RunnersFailure = new GitHubApiException("GitHub refused the token (wrong, expired or revoked).", 401, isTransient: false);
        _time.Advance(TimeSpan.FromMinutes(130));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.Contains(_state.Load().Jobs, j => j.Result.StartsWith("stopped after 120 minutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEnvironmentThatGitHubCouldNeverBeAskedAbout_IsEndedAfterFourteenHours()
    {
        await StartAsync();
        _api.RunnersFailure = new GitHubApiException("down", 503, isTransient: true);

        _time.Advance(TimeSpan.FromHours(13));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.False(_linux.WasCancelled);

        _time.Advance(TimeSpan.FromHours(2));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.Contains(_log.Lines, l => l.Contains("could not be asked about it for 14 hours", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWaitingRunner_IsReplacedAfterTwelveHours()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));

        _time.Advance(TimeSpan.FromHours(12.5));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.Contains(1L, _api.DeletedRunners);
    }

    [Fact]
    public async Task AHostThatThrows_IsRecordedAsAnError_WithoutTheJitConfiguration()
    {
        _linux.BlockUntilReleased = false;
        _linux.Failure = new InvalidOperationException("environment broke");

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        var job = Assert.Single(_state.Load().Jobs);
        Assert.StartsWith("error:", job.Result, StringComparison.Ordinal);
        Assert.Contains(1L, _api.DeletedRunners);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("JITCONFIG", StringComparison.Ordinal));
        Assert.DoesNotContain(job.Result, "JITCONFIG", StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatThrowsTheJitConfigurationItself_NeverPutsItInTheLogOrTheStateFile()
    {
        _linux.BlockUntilReleased = false;
        _linux.FailureFactory = launch => new InvalidOperationException("the runner failed with " + launch.JitConfig.Reveal());

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Contains(_state.Load().Jobs, j => j.Result.StartsWith("error:", StringComparison.Ordinal) && j.Result.Contains("[hidden]", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("JITCONFIG", StringComparison.Ordinal));
        Assert.DoesNotContain("JITCONFIG", File.ReadAllText(_folder.Paths.StateFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(HttpRequestException))]
    public async Task AnyFailureWhileRegisteringARunner_IsLogged_NeverLost_AndTheLaneWaitsAMinute(Type failure)
    {
        _api.JitFailure = (Exception)Activator.CreateInstance(failure, "damaged answer")!;

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Single(_api.JitRequests);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("Could not register a Linux runner", StringComparison.Ordinal) && l.Contains("damaged answer", StringComparison.Ordinal));
        Assert.Empty(_linux.Launches);

        _time.Advance(TimeSpan.FromMinutes(2));
        _api.JitFailure = null;
        await StartAsync();
        Assert.Equal(2, _api.JitRequests.Count);
        await FinishAsync();
    }

    [Fact]
    public async Task AWaitingRunner_WhoseStateCannotBeReadFromGitHub_IsNotStopped()
    {
        await StartAsync();
        _processes.Running.Add("DeadByDaylight");
        _api.RunnersFailure = new GitHubApiException("rate limited", 429, isTransient: true);

        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        Assert.False(_linux.WasCancelled);
        Assert.Empty(_api.DeletedRunners);
        _api.RunnersFailure = null;
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();
        Assert.True(_linux.WasCancelled);
    }

    // ---- a failed or damaged answer while a job runs never orphans it (QA B1: T2, T2b, T10, T11)

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public async Task ADamagedAnswerWhilePolling_DoesNotOrphanTheRunningJob(Type failure)
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: true));
        _api.RunnersFailure = (Exception)Activator.CreateInstance(failure, "garbled answer")!;

        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        // The host is still blocked (running). The lane must still be in use, and no second runner may start next to it.
        Assert.True(_service.IsRunning, "the lane was released while its host is still running");
        _api.RunnersFailure = null;
        await _service.TickAsync(CancellationToken.None);
        await _service.TickAsync(CancellationToken.None);
        Assert.Single(_linux.Launches);
        Assert.Single(_api.JitRequests);
        await FinishAsync();
    }

    [Fact]
    public async Task ADamagedAnswerEveryRound_DoesNotLeaveOneMoreLiveEnvironmentEachTime()
    {
        await StartAsync();
        _api.RunnersFailure = new KeyNotFoundException("busy");
        for (var round = 0; round < 5; round++)
        {
            await _service.TickAsync(CancellationToken.None);
            await _delay.ReleaseNextAsync();
            await _delay.WaitForRequestAsync();
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.Single(_linux.Launches);
        Assert.True(_service.IsRunning);
        await FinishAsync();
    }

    [Fact]
    public async Task APollThatFails_IsLogged_AndTheNextPollWaitsLonger_UpToFiveMinutes()
    {
        await StartAsync();
        _api.RunnersFailure = new HttpRequestException("no route");
        var waits = new List<TimeSpan>();
        await _delay.WaitForRequestAsync();
        waits.Add(_delay.LastRequested);
        for (var round = 0; round < 6; round++)
        {
            await _delay.ReleaseNextAsync();
            await _delay.WaitForRequestAsync();
            waits.Add(_delay.LastRequested);
        }

        Assert.Equal(LocalCiDefaults.PollInterval, waits[0]);
        Assert.Equal([TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(160), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)], waits);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("whether the runner is busy", StringComparison.Ordinal));
        await FinishAsync();
    }

    [Fact]
    public async Task APollWhoseOwnCheckThrows_LeavesTheJobAlone_AndIsLogged()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));
        _processes.Throw = new InvalidOperationException("process list unavailable");

        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        Assert.False(_linux.WasCancelled);
        Assert.True(_service.IsRunning);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("process list unavailable", StringComparison.Ordinal));
        _processes.Throw = null;
        await FinishAsync();
    }

    [Fact]
    public async Task ASlotTaskThatEndsWithAnUnexpectedError_IsLogged_NeverDropped_AndTheLaneWaits()
    {
        // The state folder is gone, so recording the finished job throws out of the slot's task.
        var broken = new LocalCiPaths(Path.Combine(_folder.Path, "gone"));
        var brokenState = new StateStore(broken.StateFile);
        var service = new LocalCiService(
            _api, _linux, _maintenance, new JobGate(_processes), brokenState, _log, _redactor, _time, new Retry(_time, _redactor, _log, attempts: 1), new RepositoryGuard(_api, _state, _time, _log, _redactor), delay: _delay.Delay);
        _linux.BlockUntilReleased = false;

        await service.TickAsync(CancellationToken.None);
        await service.WhenIdleAsync().OrFail();
        await service.TickAsync(CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("unexpected error", StringComparison.Ordinal));
        Assert.Single(_api.JitRequests);
    }

    // ---- red-team M5: only a private repository is served

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ARepositoryThatIsNotPrivate_StartsNoRunner_IsNoticedOnceADay_AndTheLaneWaits(bool? isPrivate)
    {
        _api.Repo = new RepoInfo("main", null, isPrivate);

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Empty(_api.JitRequests);
        Assert.Empty(_linux.Launches);
        Assert.Single(_api.Calls, c => c == "repo");
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("Not starting a Linux runner", StringComparison.Ordinal));
        Assert.Equal(isPrivate is false ? 1 : 0, _notifier.Messages.Count);
        Assert.Equal(isPrivate, _state.Load().RepositoryPrivate);
    }

    [Fact]
    public async Task ARepositoryThatTurnsPrivateAgain_GetsItsRunnerWhenThePauseIsOver()
    {
        _api.Repo = new RepoInfo("main", null, IsPrivate: false);
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        _api.Repo = new RepoInfo("main", null, IsPrivate: true);
        _time.Advance(TimeSpan.FromMinutes(2));
        await StartAsync();

        Assert.Single(_api.JitRequests);
        Assert.True(_state.Load().RepositoryPrivate);
        await FinishAsync();
    }

    [Fact]
    public async Task ThePublicRepositoryNotice_IsOnceADay_AndNeverWhileTheGameRuns()
    {
        _api.Repo = new RepoInfo("main", null, IsPrivate: false);
        _processes.Running.Add("DeadByDaylight");
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        // The game stops a runner from being asked for at all, so nothing was even checked.
        Assert.Empty(_notifier.Messages);
        Assert.DoesNotContain("repo", _api.Calls);

        _processes.Running.Clear();
        _time.Advance(TimeSpan.FromMinutes(2));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        Assert.Single(_notifier.Messages);

        _time.Advance(TimeSpan.FromMinutes(40));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        Assert.Single(_notifier.Messages);

        _time.Advance(TimeSpan.FromHours(25));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        Assert.Equal(2, _notifier.Messages.Count);
    }

    [Fact]
    public async Task WhenGitHubCannotBeAskedWhetherTheRepositoryIsPrivate_NoRunnerStarts_AndNoNoticeIsShown()
    {
        _api.RepoFailure = new GitHubApiException("down", 503, isTransient: true);

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Empty(_api.JitRequests);
        Assert.Empty(_notifier.Messages);
        Assert.Contains(_log.Lines, l => l.Contains("could not be asked whether the repository is private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunnerWaitingForAJob_AsksAboutThePrivacyOnlyEveryFiveMinutes()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));
        Assert.Single(_api.Calls, c => c == "repo");

        // Three polls 90 seconds apart are 4.5 minutes after the start: not yet. The fourth, 6 minutes after the start, asks.
        for (var poll = 0; poll < 3; poll++)
        {
            _time.Advance(TimeSpan.FromSeconds(90));
            await _delay.ReleaseNextAsync();
            await _delay.WaitForRequestAsync();
        }

        Assert.Single(_api.Calls, c => c == "repo");

        _time.Advance(TimeSpan.FromSeconds(90));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.Equal(2, _api.Calls.Count(c => c == "repo"));
        Assert.False(_linux.WasCancelled);
        await FinishAsync();
    }

    [Fact]
    public async Task ARepositoryThatTurnsPublicWhileARunnerWaits_StopsTheRunner_ForgetsItsRegistration_AndTellsTheOwner()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));
        _api.Repo = new RepoInfo("main", null, IsPrivate: false);

        _time.Advance(TimeSpan.FromMinutes(5));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.Contains(1L, _api.DeletedRunners);
        Assert.Empty(_state.Load().Jobs);
        Assert.Single(_notifier.Messages);
        Assert.Contains("is not private", _notifier.Messages[0], StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.Contains("being stopped before it took a job", StringComparison.Ordinal) && l.Contains("is not private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepositoryThatCannotBeAskedAboutWhileARunnerWaits_LeavesTheRunnerAlone()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));
        _api.RepoFailure = new GitHubApiException("down", 503, isTransient: true);

        _time.Advance(TimeSpan.FromMinutes(6));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        Assert.False(_linux.WasCancelled);
        Assert.Empty(_api.DeletedRunners);
        await FinishAsync();
    }

    [Fact]
    public async Task ARunningJob_IsNeverStoppedBecauseTheRepositoryTurnedPublic()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: true));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        _api.Repo = new RepoInfo("main", null, IsPrivate: false);

        _time.Advance(TimeSpan.FromMinutes(30));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        Assert.False(_linux.WasCancelled);
        Assert.Single(_api.Calls, c => c == "repo");
        await FinishAsync();
    }

    // ---- QA minor (ADV11): a maintenance that throws never keeps the runner from starting

    [Fact]
    public async Task AMaintenanceThatThrows_IsLogged_AndDoesNotKeepARunnerFromStartingInThatRound()
    {
        var throwing = new ThrowingMaintenance();
        var service = new LocalCiService(_api, _linux, throwing, new JobGate(_processes), _state, _log, _redactor, _time, new Retry(_time, _redactor, _log, attempts: 1), new RepositoryGuard(_api, _state, _time, _log, _redactor), delay: _delay.Delay);

        await service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();

        Assert.Single(_api.JitRequests);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The maintenance of this round failed", StringComparison.Ordinal) && l.Contains("the state file cannot be written", StringComparison.Ordinal));
        await FinishThatAsync(service);
    }

    [Fact]
    public async Task TheLoopsOwnStop_DuringTheMaintenance_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var service = new LocalCiService(_api, _linux, new CancellingMaintenance(cts), new JobGate(_processes), _state, _log, _redactor, _time, new Retry(_time, _redactor, _log, attempts: 1), new RepositoryGuard(_api, _state, _time, _log, _redactor), delay: _delay.Delay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TickAsync(cts.Token));

        Assert.Empty(_api.JitRequests);
    }

    private async Task FinishThatAsync(LocalCiService service)
    {
        _linux.Release();
        await service.WhenIdleAsync().OrFail();
    }

    private sealed class ThrowingMaintenance : IMaintenance
    {
        public Task RunDueAsync(bool linuxIdle, CancellationToken cancellationToken) => throw new IOException("the state file cannot be written");

        public string? LatestPackage() => "linux.tar.gz";
    }

    private sealed class CancellingMaintenance : IMaintenance
    {
        private readonly CancellationTokenSource _source;

        public CancellingMaintenance(CancellationTokenSource source)
        {
            _source = source;
        }

        public async Task RunDueAsync(bool linuxIdle, CancellationToken cancellationToken)
        {
            await _source.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public string? LatestPackage() => "linux.tar.gz";
    }

    // ---- the limits of a job, pinned (QA M-6: P8b, P8, P18, P11)

    /// <summary>P8b: a runner that waited five hours for a job still gets its full 120 minutes, counted from the moment it took the job.</summary>
    [Fact]
    public async Task ARunnerThatWaitedHours_GetsTheFullJobLimitFromTheMomentItTookTheJob()
    {
        await StartAsync();
        var name = _api.JitRequests[0].Name;
        _api.Runners.Add(new RunnerInfo(1, name, "online", Busy: false));
        _time.Advance(TimeSpan.FromHours(5));
        _api.Calls.Clear();
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.False(_linux.WasCancelled);

        _api.Runners.Clear();
        _api.Runners.Add(new RunnerInfo(1, name, "online", Busy: true));
        _time.Advance(TimeSpan.FromMinutes(1));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        _time.Advance(TimeSpan.FromMinutes(60));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        Assert.False(_linux.WasCancelled, "a job that had run for 60 minutes was stopped because its runner had waited for hours first");
        await FinishAsync();
    }

    /// <summary>P8: exactly 120 minutes is not "longer than 120 minutes".</summary>
    [Fact]
    public async Task TheJobLimit_IsNotReachedAtExactlyTheLimit_ButOneSecondAfter()
    {
        await StartAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: true));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        _time.Advance(TimeSpan.FromMinutes(120));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.False(_linux.WasCancelled, "stopped at exactly 120 minutes");

        _time.Advance(TimeSpan.FromSeconds(1));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();
        Assert.True(_linux.WasCancelled);
    }

    /// <summary>P18: after a good job the failure streak starts over: the next failure waits one minute, not the stretched time.</summary>
    [Fact]
    public async Task AfterAGoodJob_TheNextFailureWaitsOneMinuteAgain()
    {
        _api.JitFailure = new GitHubApiException("down", 500, isTransient: false);
        for (var i = 0; i < 3; i++)
        {
            await _service.TickAsync(CancellationToken.None);
            await _service.WhenIdleAsync().OrFail();
            _time.Advance(TimeSpan.FromMinutes(40));
        }

        _api.JitFailure = null;
        _linux.BlockUntilReleased = false;
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        _time.Advance(TimeSpan.FromMinutes(40));
        Assert.True(_linux.Launches.Count >= 1);

        _api.JitFailure = new GitHubApiException("down", 500, isTransient: false);
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        var before = _api.JitRequests.Count;
        _api.JitFailure = null;
        _time.Advance(TimeSpan.FromMinutes(2));
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Equal(before + 1, _api.JitRequests.Count);
    }

    /// <summary>P11, without the race: a slot that faults makes the lane wait; the second round is awaited to its end before anything is asserted.</summary>
    [Fact]
    public async Task AFaultedSlot_MakesTheLaneWait_ForARealPause()
    {
        var brokenState = new StateStore(new LocalCiPaths(Path.Combine(_folder.Path, "gone")).StateFile);
        var service = new LocalCiService(_api, _linux, _maintenance, new JobGate(_processes), brokenState, _log, _redactor, _time, new Retry(_time, _redactor, _log, attempts: 1), new RepositoryGuard(_api, _state, _time, _log, _redactor), delay: _delay.Delay);
        _linux.BlockUntilReleased = false;

        await service.TickAsync(CancellationToken.None);
        await service.WhenIdleAsync().OrFail();
        await service.TickAsync(CancellationToken.None);
        await service.WhenIdleAsync().OrFail();

        Assert.Single(_api.JitRequests);

        _time.Advance(TimeSpan.FromMinutes(2));
        await service.TickAsync(CancellationToken.None);
        await service.WhenIdleAsync().OrFail();
        Assert.Equal(2, _api.JitRequests.Count);
    }

    /// <summary>Red-team round 3, L10: a job's runner configuration is hidden from every log line while the job runs, and the redactor forgets it when the slot has been reaped.</summary>
    [Fact]
    public async Task AJobsRunnerConfiguration_IsHiddenWhileItRuns_AndForgottenWhenItsSlotIsReaped()
    {
        await StartAsync();
        var configuration = "JITCONFIG-" + _api.JitRequests[0].Name;
        var whileRunning = _redactor.SecretCount;

        Assert.Equal("[hidden]", _redactor.Redact(configuration));

        await FinishAsync();

        Assert.Equal(configuration, _redactor.Redact(configuration));
        Assert.Equal(whileRunning - 1, _redactor.SecretCount);
    }

    [Fact]
    public async Task ManyJobsInARow_LeaveTheRedactorWithTheSecretsItStartedWith()
    {
        _linux.BlockUntilReleased = false;
        _redactor.Register("the-token");
        var before = _redactor.SecretCount;

        for (var job = 0; job < 12; job++)
        {
            await _service.TickAsync(CancellationToken.None);
            await _service.WhenIdleAsync().OrFail();
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(12, _linux.Launches.Count);
        Assert.Equal(before, _redactor.SecretCount);
    }

    [Fact]
    public async Task ARunnerThatFailsToStart_StillLetsGoOfItsConfiguration()
    {
        _linux.BlockUntilReleased = false;
        _linux.Failure = new InvalidOperationException("the environment broke");
        var before = _redactor.SecretCount;

        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();

        Assert.Equal(before, _redactor.SecretCount);
    }

    [Fact]
    public async Task TheLoop_CleansLeftoversFirst_AndStopsTheEnvironmentWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var running = _service.RunAsync(cts.Token);
        await _linux.WaitStartedAsync();

        await cts.CancelAsync();
        await running.OrFail();

        Assert.Equal(1, _linux.Cleanups);
        Assert.True(_linux.WasCancelled);
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task TheLoopStopsOnlyWhenTheHostHasEnded_EvenIfItTakesAWhile()
    {
        using var cts = new CancellationTokenSource();
        _linux.BlockUntilReleased = true;
        _linux.IgnoreCancellationUntilReleased = true;
        var running = _service.RunAsync(cts.Token);
        await _linux.WaitStartedAsync();

        await cts.CancelAsync();
        await Task.Yield();
        Assert.False(running.IsCompleted, "the loop returned while the environment still existed");

        _linux.Release();
        await running.OrFail();
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public void TheStateFile_KeepsOnlyTheLastTenJobs_AndNeverHoldsASecret()
    {
        for (var i = 0; i < 15; i++)
        {
            var at = _time.GetUtcNow().AddMinutes(i);
            _state.Update(s => s.Jobs.Add(new JobRecord { At = at, Os = "Linux", Result = "job finished" }));
        }

        var text = File.ReadAllText(_folder.Paths.StateFile);

        Assert.Equal(10, _state.Load().Jobs.Count);
        using var document = JsonDocument.Parse(text);
        Assert.DoesNotContain("github_pat_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("jit", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}
