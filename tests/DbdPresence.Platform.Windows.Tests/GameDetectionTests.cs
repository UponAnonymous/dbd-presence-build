// Covers: FR-PROC-01 (game detected by executable name), FR-PROC-02 (launcher only → Launching), FR-PROC-04 (exit returns
// to NotRunning), FR-PROC-05 (start time read through the least-privilege query) — QUALITY_GATES B3, on real Windows
// with a harmless stand-in process: a copy of cmd.exe named like the game, kept alive by an open stdin.
using System.Diagnostics;
using DbdPresence.Core.Game;
using DbdPresence.Platform.Windows.Processes;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class GameDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbdp-proc", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public GameDetectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var process in _started)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }

            process.Dispose();
        }

        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RenamedStandIn_IsDetectedAsRunning_WithAStartTime_ThenNotRunningAfterExit()
    {
        var monitor = new GameMonitor(new WindowsProcessSnapshotSource(), TimeProvider.System, new GameProcessMatcher());
        var game = StartStandIn("DeadByDaylight-Win64-Shipping.exe");

        PollUntil(monitor, GameState.Running);

        Assert.Equal(game.Id, monitor.Current.Process?.ProcessId);
        Assert.NotNull(monitor.Current.StartedUtc);
        Assert.False(monitor.Current.StartIsApproximate);
        Assert.True(Math.Abs((monitor.Current.StartedUtc!.Value - DateTimeOffset.UtcNow).TotalMinutes) < 5);

        game.Kill();
        game.WaitForExit(5000);
        PollUntil(monitor, GameState.NotRunning);
        Assert.Null(monitor.Current.Process);
    }

    [Fact]
    public void LauncherOnly_IsLaunching()
    {
        var monitor = new GameMonitor(new WindowsProcessSnapshotSource(), TimeProvider.System, new GameProcessMatcher());
        StartStandIn("DeadByDaylight.exe");

        PollUntil(monitor, GameState.Launching);
    }

    [Fact]
    public void ExtraExecutableName_IsDetected()
    {
        var monitor = new GameMonitor(new WindowsProcessSnapshotSource(), TimeProvider.System, new GameProcessMatcher(["DBD-StoreBuild.exe"]));
        StartStandIn("DBD-StoreBuild.exe");

        PollUntil(monitor, GameState.Running);
    }

    private static void PollUntil(GameMonitor monitor, GameState expected)
    {
        // Two agreeing polls confirm a change (GameLifecycleTracker.ConfirmationPolls); allow a few more for start-up.
        for (var i = 0; i < 20 && monitor.Current.State != expected; i++)
        {
            monitor.PollOnce();
            Thread.Sleep(100);
        }

        Assert.Equal(expected, monitor.Current.State);
    }

    private Process StartStandIn(string executableName)
    {
        var path = Path.Combine(_dir, executableName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // cmd waits for input, so the stand-in stays alive until killed
        })!;
        _started.Add(process);
        return process;
    }
}
