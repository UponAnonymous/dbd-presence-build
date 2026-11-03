// Covers: FR-PROC-01 (debounced start/exit), FR-PROC-02 (launching), FR-PROC-04 (exit clears), FR-PROC-05 (start time).
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Game;

public sealed class GameLifecycleTrackerTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    private static ProcessScan Game(int seconds, int pid = 42, DateTimeOffset? started = null) =>
        new(s_t0.AddSeconds(seconds), new GameProcessInfo(pid, "DeadByDaylight-Win64-Shipping.exe", started ?? s_t0.AddMinutes(-1)), LauncherPresent: false);

    private static ProcessScan Nothing(int seconds) => new(s_t0.AddSeconds(seconds), null, false);

    private static ProcessScan Launcher(int seconds) => new(s_t0.AddSeconds(seconds), null, true);

    private static GameLifecycle Run(params ProcessScan[] scans) =>
        scans.Aggregate(GameLifecycle.Initial, GameLifecycleTracker.Next);

    [Fact]
    public void SingleGamePoll_DoesNotYetConfirmRunning()
    {
        Assert.Equal(GameState.NotRunning, Run(Game(0)).State);
    }

    [Fact]
    public void TwoConsecutiveGamePolls_ConfirmRunningWithProcessStartTime()
    {
        var state = Run(Game(0), Game(3));

        Assert.Equal(GameState.Running, state.State);
        Assert.Equal(42, state.Process!.ProcessId);
        Assert.Equal(s_t0.AddMinutes(-1), state.StartedUtc);
        Assert.False(state.StartIsApproximate);
    }

    [Fact]
    public void MissingStartTime_FallsBackToFirstConfirmationAndIsApproximate()
    {
        var unknownStart = new ProcessScan(s_t0, new GameProcessInfo(42, "x.exe", null), false);
        var state = Run(unknownStart, unknownStart with { ObservedUtc = s_t0.AddSeconds(3) });

        Assert.Equal(s_t0.AddSeconds(3), state.StartedUtc);
        Assert.True(state.StartIsApproximate);
    }

    [Fact]
    public void FlickeringPolls_NeverConfirmAChange()
    {
        var state = Run(Game(0), Nothing(3), Game(6), Nothing(9), Game(12), Nothing(15));

        Assert.Equal(GameState.NotRunning, state.State);
    }

    [Fact]
    public void SingleMissedPollWhileRunning_KeepsRunning()
    {
        var state = Run(Game(0), Game(2), Nothing(4), Game(6));

        Assert.Equal(GameState.Running, state.State);
        Assert.Equal(s_t0.AddMinutes(-1), state.StartedUtc);
    }

    [Fact]
    public void TwoMissedPolls_ClearsEverything()
    {
        var state = Run(Game(0), Game(2), Nothing(4), Nothing(6));

        Assert.Equal(GameLifecycle.Initial, state);
    }

    [Fact]
    public void LauncherOnly_ConfirmsLaunching_ThenGameConfirmsRunning()
    {
        Assert.Equal(GameState.Launching, Run(Launcher(0), Launcher(3)).State);
        Assert.Equal(GameState.Running, Run(Launcher(0), Launcher(3), Game(6), Game(8)).State);
    }

    [Fact]
    public void LauncherLeftBehindAfterExit_DoesNotShowLaunching()
    {
        var state = Run(Game(0), Game(2), Launcher(4), Launcher(6), Launcher(8));

        Assert.Equal(GameState.NotRunning, state.State);
    }

    [Fact]
    public void GameRestartedBetweenPolls_StartsNewSessionAfterConfirmation()
    {
        var restartedAt = s_t0.AddMinutes(30);
        var oneNewPoll = Run(Game(0), Game(2), Game(4, pid: 77, started: restartedAt));
        var twoNewPolls = Run(Game(0), Game(2), Game(4, pid: 77, started: restartedAt), Game(6, pid: 77, started: restartedAt));

        Assert.Equal(42, oneNewPoll.Process!.ProcessId);
        Assert.Equal(77, twoNewPolls.Process!.ProcessId);
        Assert.Equal(restartedAt, twoNewPolls.StartedUtc);
    }

    [Fact]
    public void RandomPollSequences_StateOnlyChangesAfterTwoAgreeingPolls()
    {
        var random = new Random(20261023);
        for (var run = 0; run < 500; run++)
        {
            var state = GameLifecycle.Initial;
            var previousObservations = new List<GameState>();
            for (var i = 0; i < 40; i++)
            {
                var roll = random.Next(3);
                var scan = roll switch { 0 => Game(i), 1 => Launcher(i), _ => Nothing(i) };

                // What this poll observes, by the documented rule (a launcher seen while running counts as "not running").
                var observed = roll == 0 ? GameState.Running
                    : roll == 1 && state.State != GameState.Running ? GameState.Launching : GameState.NotRunning;
                var next = GameLifecycleTracker.Next(state, scan);
                if (next.State != state.State)
                {
                    Assert.Equal(observed, next.State);
                    Assert.True(previousObservations.Count > 0 && previousObservations[^1] == observed, "a state change needs two consecutive agreeing polls");
                }

                previousObservations.Add(observed);
                state = next;
            }
        }
    }
}
