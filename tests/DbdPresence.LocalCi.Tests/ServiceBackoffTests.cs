// Covers: FR-CI-10 and FR-CI-02, local CI round 4 QA M-4 (the survivors of the round 3 mutation run, adopted from the reviewer's scratch tests D1 to D4 and widened): the maintenance is told whether a
// runner slot is in use (the weekly image build and the WSL update run only when none is); a host that throws, or a runner that ends with an error code, makes the lane wait exactly one minute (not yet at
// 59 seconds, due at 60); the waits double up to 30 minutes; a good job starts the count over; a runner is replaced after exactly 12 hours without a job and not a tick before; while the environment of a
// stopped loop still exists the lane does not spin. No sleeps, no wall clock: the injected clock moves only when the test moves it. docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class ServiceBackoffTests : IDisposable
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
    private int _delaysAsked;

    public ServiceBackoffTests()
    {
        _log = new MemoryCiLog(_redactor);
        _state = new StateStore(_folder.Paths.StateFile);
        var retry = new Retry(_time, _redactor, _log, attempts: 1);
        _service = new LocalCiService(_api, _linux, _maintenance, new JobGate(_processes), _state, _log, _redactor, _time, retry, new RepositoryGuard(_api, _state, _time, _log, _redactor), new OwnerNotices(_notifier, _state, _time), CountedDelay);
    }

    public void Dispose() => _folder.Dispose();

    private Task CountedDelay(TimeSpan span, CancellationToken token)
    {
        Interlocked.Increment(ref _delaysAsked);
        return _delay.Delay(span, token);
    }

    private async Task TickAndWaitAsync()
    {
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
    }

    /// <summary>Mutant 720 (idle = _slot is not null): the weekly image build and the WSL update are told "a Linux machine is in use" exactly when one is.</summary>
    [Fact]
    public async Task TheMaintenanceIsToldWhetherARunnerSlotIsInUse()
    {
        await _service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();
        await _service.TickAsync(CancellationToken.None);

        Assert.Equal([true, false], _maintenance.Rounds);

        _linux.Release();
        await _service.WhenIdleAsync().OrFail();
        await _service.TickAsync(CancellationToken.None);
        Assert.Equal([true, false, true], _maintenance.Rounds);
        _linux.Release();
        await _service.WhenIdleAsync().OrFail();
    }

    /// <summary>Mutants 690 and 699: a host that throws (a broken environment) makes the lane wait one minute, not retry at the next round. Exactly: not yet at 59 seconds, due at 60.</summary>
    [Fact]
    public async Task AHostThatThrows_MakesTheLaneWaitExactlyOneMinute()
    {
        _linux.BlockUntilReleased = false;
        _linux.Failure = new InvalidOperationException("environment broke");
        await TickAndWaitAsync();
        var before = _api.JitRequests.Count;

        _time.Advance(TimeSpan.FromSeconds(59));
        await TickAndWaitAsync();
        Assert.Equal(before, _api.JitRequests.Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        await TickAndWaitAsync();
        Assert.Equal(before + 1, _api.JitRequests.Count);
    }

    /// <summary>Mutants 690 and 699, the other kind of failure: the runner program ends with an error code before it took a job. Exactly one minute again.</summary>
    [Fact]
    public async Task ARunnerThatEndsWithAnErrorCode_MakesTheLaneWaitExactlyOneMinute()
    {
        _linux.BlockUntilReleased = false;
        _linux.ExitCode = 1;
        await TickAndWaitAsync();
        var before = _api.JitRequests.Count;

        _time.Advance(TimeSpan.FromSeconds(59));
        await TickAndWaitAsync();
        Assert.Equal(before, _api.JitRequests.Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        await TickAndWaitAsync();
        Assert.Equal(before + 1, _api.JitRequests.Count);
    }

    [Fact]
    public async Task AHostThatKeepsFailing_MakesTheLaneWaitOneTwoFourEightSixteenAndThenThirtyMinutes_ExactlyEachTime()
    {
        _linux.BlockUntilReleased = false;
        _linux.Failure = new InvalidOperationException("environment broke");
        await TickAndWaitAsync();

        foreach (var minutes in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            var wait = TimeSpan.FromMinutes(minutes);
            var before = _api.JitRequests.Count;
            _time.Advance(wait - TimeSpan.FromSeconds(1));
            await TickAndWaitAsync();
            Assert.Equal(before, _api.JitRequests.Count);

            _time.Advance(TimeSpan.FromSeconds(1));
            await TickAndWaitAsync();
            Assert.Equal(before + 1, _api.JitRequests.Count);
        }
    }

    /// <summary>Mutant 713 (streak reset to 1): after a good job the next failure waits exactly one minute (not yet at 59 seconds, due at 60).</summary>
    [Fact]
    public async Task AfterAGoodJob_TheNextFailureWaitsExactlyOneMinute()
    {
        _api.JitFailure = new GitHubApiException("down", 500, isTransient: false);
        for (var i = 0; i < 3; i++)
        {
            await TickAndWaitAsync();
            _time.Advance(TimeSpan.FromMinutes(40));
        }

        _api.JitFailure = null;
        _linux.BlockUntilReleased = false;
        await TickAndWaitAsync();
        _time.Advance(TimeSpan.FromMinutes(40));

        _api.JitFailure = new GitHubApiException("down", 500, isTransient: false);
        await TickAndWaitAsync();
        var before = _api.JitRequests.Count;
        _api.JitFailure = null;
        _time.Advance(TimeSpan.FromSeconds(59));
        await TickAndWaitAsync();
        Assert.Equal(before, _api.JitRequests.Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        await TickAndWaitAsync();
        Assert.Equal(before + 1, _api.JitRequests.Count);
    }

    // ---- a runner is replaced after exactly twelve hours without a job

    [Fact]
    public async Task AWaitingRunner_IsNotReplacedAtExactlyTwelveHours_ButOneSecondAfter()
    {
        await _service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));

        _time.Advance(TimeSpan.FromHours(12));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
        Assert.False(_linux.WasCancelled);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("waited a long time", StringComparison.Ordinal));

        _time.Advance(TimeSpan.FromSeconds(1));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
        Assert.Contains(1L, _api.DeletedRunners);
        Assert.Contains(_log.Lines, l => l.Contains("waited a long time without a job and is being replaced", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTwelveHours_AndTheOtherLimitsOfTheLane_AreTheNumbersTheGuideNames()
    {
        Assert.Equal(TimeSpan.FromHours(12), LocalCiDefaults.MaximumWait);
        Assert.Equal(TimeSpan.FromMinutes(120), LocalCiDefaults.JobTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), LocalCiDefaults.PollInterval);
    }

    // ---- the lane does not spin while the environment of a stopped loop still exists

    [Fact]
    public async Task WhileTheEnvironmentOfAStoppedLoopStillExists_TheLaneWaitsForIt_AndAsksNothingMore()
    {
        using var cts = new CancellationTokenSource();
        _linux.IgnoreCancellationUntilReleased = true;
        var running = _service.RunAsync(cts.Token);
        await _linux.WaitStartedAsync();
        await cts.CancelAsync();
        await WaitUntilAsync(() => _linux.WasCancelled);
        var asked = Volatile.Read(ref _delaysAsked);

        for (var i = 0; i < 200; i++)
        {
            await Task.Yield();
        }

        Assert.False(running.IsCompleted, "the loop returned while the environment still existed");
        Assert.Equal(asked, Volatile.Read(ref _delaysAsked));
        Assert.True(_service.IsRunning);

        _linux.Release();
        await running.OrFail();
        Assert.False(_service.IsRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(limit.Elapsed < TestWait.Safety, "A condition that must come true without any time passing did not.");
            await Task.Yield();
        }
    }
}
