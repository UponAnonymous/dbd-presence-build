// Covers: QA minors and missing tests (a jump of the PC's clock neither stretches nor shortens a pause or a job limit: the service measures them on the monotonic clock, and a time written down in the
// future counts as due), FR-CI-10. docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class ClockJumpTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _real = TestTime.New();
    private readonly SkewedTime _time;
    private readonly Redactor _redactor = new();
    private readonly MemoryCiLog _log;
    private readonly FakeGitHubApi _api = new();
    private readonly ManualDelay _delay = new();
    private readonly FakeJobHost _linux = new() { BlockUntilReleased = true };
    private readonly LocalCiService _service;

    public ClockJumpTests()
    {
        _time = new SkewedTime(_real);
        _log = new MemoryCiLog(_redactor);
        var retry = new Retry(_time, _redactor, _log, attempts: 1);
        var state = new StateStore(_folder.Paths.StateFile);
        _service = new LocalCiService(_api, _linux, new FakeMaintenance(), new JobGate(new FakeProcesses()), state, _log, _redactor, _time, retry, new RepositoryGuard(_api, state, _time, _log, _redactor), delay: _delay.Delay);
    }

    public void Dispose() => _folder.Dispose();

    private async Task StartBusyJobAsync()
    {
        await _service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: true));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();
    }

    [Fact]
    public async Task AJobIsStoppedAfter120MinutesOfRealTime_EvenWhenTheClockWasSetBackFiveHoursMeanwhile()
    {
        await StartBusyJobAsync();
        Assert.False(_linux.WasCancelled);

        _time.Skew = TimeSpan.FromHours(-5);
        _real.Advance(TimeSpan.FromMinutes(121));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
    }

    [Fact]
    public async Task AJobIsNotStoppedEarly_WhenTheClockJumpsForwardADay()
    {
        await StartBusyJobAsync();

        _time.Skew = TimeSpan.FromDays(1);
        _real.Advance(TimeSpan.FromMinutes(10));
        await _delay.ReleaseNextAsync();
        await _delay.WaitForRequestAsync();

        Assert.False(_linux.WasCancelled);
        _linux.Release();
        await _service.WhenIdleAsync().OrFail();
    }

    [Fact]
    public async Task AWaitingRunnerIsReplacedAfterTwelveHoursOfRealTime_WhateverTheClockSays()
    {
        await _service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();
        _api.Runners.Add(new RunnerInfo(1, _api.JitRequests[0].Name, "online", Busy: false));

        _time.Skew = TimeSpan.FromDays(-3);
        _real.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        await _delay.ReleaseNextAsync();
        await _service.WhenIdleAsync().OrFail();

        Assert.True(_linux.WasCancelled);
    }

    [Fact]
    public async Task AFailurePause_IsNotStretchedByAClockSetBack()
    {
        _api.JitFailure = new GitHubApiException("down", 503, isTransient: true);
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        _api.JitFailure = null;
        Assert.Single(_api.JitRequests);

        _time.Skew = TimeSpan.FromDays(-3);
        _real.Advance(TimeSpan.FromSeconds(61));
        await _service.TickAsync(CancellationToken.None);
        await _linux.WaitStartedAsync();

        Assert.Equal(2, _api.JitRequests.Count);
        _linux.Release();
        await _service.WhenIdleAsync().OrFail();
    }

    [Fact]
    public async Task AFailurePause_IsNotShortenedByAClockJumpForward()
    {
        _api.JitFailure = new GitHubApiException("down", 503, isTransient: true);
        await _service.TickAsync(CancellationToken.None);
        await _service.WhenIdleAsync().OrFail();
        _api.JitFailure = null;

        _time.Skew = TimeSpan.FromDays(2);
        _real.Advance(TimeSpan.FromSeconds(30));
        await _service.TickAsync(CancellationToken.None);

        Assert.Single(_api.JitRequests);
        Assert.False(_service.IsRunning);
    }
}
