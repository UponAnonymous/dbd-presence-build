// Covers: FR-PROC-01 (start ≤ 6 s, exit ≤ 4 s), FR-PROC-02 (launcher), FR-PROC-03 (extra names), FR-PROC-05
// (start time queried once per PID), NFR-REL-01 (poll faults reported, monitoring continues).
using DbdPresence.Core.Game;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Core.Tests.Game;

public sealed class GameMonitorTests
{
    private readonly RecordingTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeProcesses _processes = new();

    private GameMonitor NewMonitor(params string[] extra) => new(_processes, _time, new GameProcessMatcher(extra));

    [Fact]
    public void Matcher_RecognisesSteamAndEpic_CaseInsensitive_AndExtraNames()
    {
        var matcher = new GameProcessMatcher(["DeadByDaylight-WinGDK-Shipping.exe"]);

        Assert.NotNull(matcher.FindGame([new(1, "deadbydaylight-win64-shipping")]));
        Assert.NotNull(matcher.FindGame([new(1, "DeadByDaylight-EGS-Shipping")]));
        Assert.NotNull(matcher.FindGame([new(1, "DeadByDaylight-WinGDK-Shipping")]));
        Assert.Null(matcher.FindGame([new(1, "DeadByDaylight")]));
        Assert.True(matcher.HasLauncher([new(1, "DeadByDaylight")]));
        Assert.Null(matcher.FindGame([new(1, "notepad")]));
    }

    [Fact]
    public void Matcher_PicksLowestPidDeterministically()
    {
        var game = new GameProcessMatcher().FindGame([new(90, "DeadByDaylight-Win64-Shipping"), new(12, "DeadByDaylight-Win64-Shipping")]);

        Assert.Equal(12, game!.Id);
    }

    [Fact]
    public async Task Start_IsConfirmedOnTheSecondPoll_OneIdleIntervalApart_AndStartTimeQueriedOnce()
    {
        // FR-PROC-01 "start ≤ 6 s": a game that appears just after a poll is seen at the next poll (≤ 3 s) and
        // confirmed one idle interval later (≤ 6 s). The test checks the two facts that guarantee it: confirmation on
        // the second poll, and the loop asking to wait exactly the idle interval between polls. It does not measure
        // elapsed virtual time: the test advances the fake clock while the loop runs, so a tick that lands between a
        // poll and the loop registering its delay shifts virtual time (seen as 7 s on Windows run 39880975572 and
        // reproduced locally under parallel load).
        await using var monitor = NewMonitor();
        var changes = new List<GameLifecycle>();
        var pollsAtConfirmation = 0;
        monitor.LifecycleChanged += (_, l) =>
        {
            changes.Add(l);
            pollsAtConfirmation = _processes.SnapshotCalls;
        };
        _processes.Running = [new(42, "DeadByDaylight-Win64-Shipping")];
        _processes.StartTimes[42] = _time.GetUtcNow().AddMinutes(-3);

        monitor.Start();
        await WaitForAsync(() => changes.Count == 1);

        var running = Assert.Single(changes);
        Assert.Equal(GameLifecycleTracker.ConfirmationPolls, pollsAtConfirmation);
        Assert.Equal(GameMonitor.IdleInterval, _time.RequestedDelays[0]);
        Assert.True(GameMonitor.IdleInterval * GameLifecycleTracker.ConfirmationPolls <= TimeSpan.FromSeconds(6));
        Assert.Equal(GameState.Running, running.State);
        Assert.Equal(_processes.StartTimes[42], running.StartedUtc);
        Assert.Equal(1, _processes.DetailCalls);
    }

    [Fact]
    public void Exit_IsConfirmedAfterTwoPolls()
    {
        var monitor = NewMonitor();
        _processes.Running = [new(42, "DeadByDaylight-Win64-Shipping")];
        monitor.PollOnce();
        monitor.PollOnce();
        _processes.Running = [];

        monitor.PollOnce();
        Assert.Equal(GameState.Running, monitor.Current.State);
        monitor.PollOnce();
        Assert.Equal(GameState.NotRunning, monitor.Current.State);
    }

    [Fact]
    public void UnreadableDetails_FallBackToApproximateStart()
    {
        var monitor = NewMonitor();
        _processes.Running = [new(42, "DeadByDaylight-Win64-Shipping")];

        monitor.PollOnce();
        monitor.PollOnce();

        Assert.True(monitor.Current.StartIsApproximate);
        Assert.Equal(_time.GetUtcNow(), monitor.Current.StartedUtc);
    }

    [Fact]
    public void UpdateMatcher_AppliesWithoutRestart()
    {
        var monitor = NewMonitor();
        _processes.Running = [new(7, "DeadByDaylight-WinGDK-Shipping")];
        monitor.PollOnce();
        monitor.PollOnce();
        Assert.Equal(GameState.NotRunning, monitor.Current.State);

        monitor.UpdateMatcher(new GameProcessMatcher(["DeadByDaylight-WinGDK-Shipping.exe"]));
        monitor.PollOnce();
        monitor.PollOnce();

        Assert.Equal(GameState.Running, monitor.Current.State);
    }

    [Fact]
    public async Task PollFault_IsReported_AndMonitoringContinues()
    {
        await using var monitor = NewMonitor();
        var faults = new List<Exception>();
        monitor.PollFailed += (_, e) => faults.Add(e);
        _processes.FailNext = new InvalidOperationException("snapshot failed");

        monitor.Start();
        await WaitForAsync(() => faults.Count == 1 && _processes.SnapshotCalls >= 2);

        Assert.IsType<InvalidOperationException>(Assert.Single(faults));
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        // The loop runs on the thread pool. Keep ticking the fake clock in 1 s steps until the condition holds:
        // a tick that lands before the loop registers its delay is simply absorbed by the next one, so the
        // outcome does not depend on thread scheduling. Bounded so a broken loop fails instead of hanging.
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "condition not reached");
    }

    /// <summary>A fake clock that also records each one-shot delay the code under test asks for.</summary>
    private sealed class RecordingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<TimeSpan> _delays = new();

        public IReadOnlyList<TimeSpan> RequestedDelays => [.. _delays];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period == Timeout.InfiniteTimeSpan)
            {
                _delays.Enqueue(dueTime);
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class FakeProcesses : IProcessSnapshotSource
    {
        private int _snapshotCalls;

        public IReadOnlyList<RunningProcess> Running { get; set; } = [];

        public Dictionary<int, DateTimeOffset> StartTimes { get; } = [];

        public Exception? FailNext { get; set; }

        public int SnapshotCalls => Volatile.Read(ref _snapshotCalls);

        public int DetailCalls { get; private set; }

        public IReadOnlyList<RunningProcess> Snapshot()
        {
            Interlocked.Increment(ref _snapshotCalls);
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw failure;
            }

            return Running;
        }

        public ProcessDetails? Details(int processId)
        {
            DetailCalls++;
            return StartTimes.TryGetValue(processId, out var start) ? new ProcessDetails(start, null) : null;
        }
    }
}
