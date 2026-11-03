// Covers: local CI round 4, red-team F3 and QA M-2 (the isolation probe starts a session of the account's Linux machine, which begins with wsl --shutdown: next to a running loop it would end a running
// check. So with the loop running the probe command asks GitHub whether a check runs on this PC and refuses if one does, or if that cannot be told; otherwise it stops the loop like set-up does and
// starts it again however the probe ends; and a second program cannot begin a session next to a running one at all: the machine-wide session lock). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ProbeCommandLoopTests
{
    private static FakeScheduler RunningLoop() => new() { TaskExists = true, Running = true };

    private static RunnerInfo Runner(string name, bool busy) => new(1, name, "online", busy);

    [Fact]
    public async Task WithTheLoopRunningAndNoCheckBusy_TheLoopIsStopped_TheProbeRuns_AndTheLoopIsStartedAgain()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        scheduler.Calls.Journal = rig.Journal;
        scheduler.Calls.Prefix = "task ";
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);
        api.Runners.Add(Runner("dbd-linux-20261029110000", busy: false));

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["task stop", "wsl shutdown"], rig.Journal.Take(2).ToArray());
        Assert.Equal("task start", rig.Journal[^1]);
        Assert.True(scheduler.Running);
        Assert.Single(rig.Wsl.ProbeScripts);
        Assert.Contains("stopping it for the time the probe takes (it starts again afterwards)", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("The background loop that the probe stopped was started again.", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Equal("dbd-linux", api.Variables["LINUX_RUNNER"]);
    }

    [Fact]
    public async Task WhileACheckRunsOnThisPc_TheProbeIsNotStarted_NothingIsStopped_AndNothingIsChanged()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);
        api.Runners.Add(Runner("dbd-linux-20261029110000", busy: true));

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(scheduler.Calls);
        Assert.True(scheduler.Running);
        Assert.Empty(rig.Wsl.ProbeScripts);
        Assert.Empty(rig.Journal);
        Assert.Null(rig.State.Load().Probe);
        Assert.Equal("dbd-linux", api.Variables["LINUX_RUNNER"]);
        Assert.Contains("A check is running on this PC right now", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("would end that check, so it was not started and nothing was changed", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunnerThatIsNotThisProgramsAndBusy_DoesNotKeepTheProbeFromRunning()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);
        api.Runners.Add(Runner("my-own-runner", busy: true));
        api.Runners.Add(Runner("dbd-linux-20261029110000", busy: false));

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["stop", "start"], scheduler.Calls.ToArray());
    }

    [Fact]
    public async Task WhenGitHubCannotBeAsked_TheProbeIsNotStarted_BecauseItCannotBeToldWhetherACheckRuns()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);
        api.RunnersFailure = new GitHubApiException("GitHub could not be reached: no route", 0, isTransient: true);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(scheduler.Calls);
        Assert.Empty(rig.Wsl.ProbeScripts);
        Assert.Contains("GitHub could not be asked whether a check is running on this PC right now (GitHub could not be reached: no route)", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("stop the task yourself first", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("could not ask GitHub whether a check is running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoopThatCannotBeStopped_MeansNoProbe_AndNoRestart()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        scheduler.StopLeavesRunning = true;
        var (command, _, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(["stop"], scheduler.Calls.ToArray());
        Assert.Empty(rig.Wsl.ProbeScripts);
        Assert.Contains("The background loop is still running, so the probe was not started and nothing was changed", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenStoppingTheLoopThrows_TheReasonIsSaid_AndNoProbeRuns()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        scheduler.StopFailure = new InvalidOperationException("Windows refused to end the task");
        var (command, _, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(rig.Wsl.ProbeScripts);
        Assert.Contains("The background loop could not be stopped (Windows refused to end the task)", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("could not stop the background loop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheOwnerDoesNotAccept_TheVariableGoes_AndTheLoopIsStartedAgainAnyway()
    {
        using var rig = ProbeReviewTests.Rig("no");
        var scheduler = RunningLoop();
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Equal(["stop", "start"], scheduler.Calls.ToArray());
        Assert.True(scheduler.Running);
    }

    [Fact]
    public async Task WhenTheProbeIsCancelled_TheLoopIsStartedAgain()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        var (command, _, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);
        using var cts = new CancellationTokenSource();
        rig.Wsl.ProbeHang.On = true;
        var running = command.RunAsync(cts.Token);
        await rig.Wsl.ProbeHang.StartedAsync();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        Assert.Equal(["stop", "start"], scheduler.Calls.ToArray());
        Assert.True(scheduler.Running);
    }

    [Fact]
    public async Task ALoopThatCannotBeStartedAgain_IsSaid_AndTheVariableIsTakenAway_EvenAfterAYes()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = RunningLoop();
        scheduler.StartFailure = new InvalidOperationException("the task is gone");
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(command.Accepted);
        Assert.False(api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("could not be started again (the task is gone)", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("the background loop is not running", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoLoopRunning_NothingIsStoppedOrStarted_AndGitHubIsNotAsked()
    {
        using var rig = ProbeReviewTests.Rig("yes");
        var scheduler = new FakeScheduler { TaskExists = true, Running = false };
        var (command, api, _) = ProbeReviewTests.Command(rig, scheduler: scheduler);
        api.RunnersFailure = new GitHubApiException("must not be asked", 500, isTransient: false);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Empty(scheduler.Calls);
        Assert.DoesNotContain("stopping it", rig.Console.AllText, StringComparison.Ordinal);
    }

    // ---- the machine-wide session lock

    private static (WslSandbox Sandbox, FakeSessionLock Lock, FakeWsl Wsl, FakeWslAccount Account, FakeWslSettingsFile Settings) Sandbox()
    {
        var wsl = new FakeWsl();
        var account = new FakeWslAccount();
        var settings = new FakeWslSettingsFile();
        var locker = new FakeSessionLock();
        var redactor = new Redactor();
        var sandbox = Sandboxes.New(wsl, account, settings, new MemoryCiLog(redactor), redactor, TestTime.New(), sessionLock: locker);
        return (sandbox, locker, wsl, account, settings);
    }

    [Fact]
    public async Task AnotherProgramThatHoldsTheLock_KeepsASessionFromStarting_BeforeAnythingIsDoneToTheMachine()
    {
        var (sandbox, locker, wsl, account, settings) = Sandbox();
        locker.HeldByAnotherProgram = true;

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("Another DBD Local CI program (the background loop, set-up, the isolation probe or the test check) is using the Linux machine of the account right now", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was done to the machine", ex.Message, StringComparison.Ordinal);
        Assert.Empty(wsl.Calls);
        Assert.Empty(account.Calls);
        Assert.Empty(settings.Calls);
        Assert.Equal(0, account.Checks);

        // The refusal leaves nothing behind: once the other program lets go, a session starts.
        locker.HeldByAnotherProgram = false;
        await using var session = await sandbox.BeginAsync(CancellationToken.None);
        Assert.Equal(1, locker.Held);
    }

    [Fact]
    public async Task TheLockIsTakenBeforeTheMachineIsTouched_AndLetGoOnlyAfterItWasShutDownAndItsProgramsWereEnded()
    {
        var (sandbox, locker, wsl, account, settings) = Sandbox();
        var journal = new List<string>();
        locker.Calls.Journal = journal;
        wsl.Calls.Journal = journal;
        wsl.Calls.Prefix = "wsl ";
        account.Calls.Journal = journal;
        account.Calls.Prefix = "account ";
        settings.Calls.Journal = journal;
        settings.Calls.Prefix = "settings ";

        var session = await sandbox.BeginAsync(CancellationToken.None);
        Assert.Equal(1, locker.Held);
        await session.DisposeAsync();

        Assert.Equal("lock taken", journal[0]);
        Assert.True(journal.IndexOf("lock taken") < journal.IndexOf("wsl shutdown"));
        Assert.Equal("lock released", journal[^1]);
        Assert.True(journal.LastIndexOf("wsl shutdown") < journal.IndexOf("lock released"));
        Assert.True(journal.LastIndexOf("account end-programs") < journal.IndexOf("lock released"));
        Assert.True(journal.IndexOf("settings release") < journal.IndexOf("lock released"));
        Assert.Equal(0, locker.Held);
    }

    [Fact]
    public async Task ASessionThatFailsToBegin_LetsTheLockGo_AndTheSandboxIsFreeAgain()
    {
        var (sandbox, locker, _, account, _) = Sandbox();
        account.Problems.Add("the account is disabled");

        await Assert.ThrowsAsync<JobEnvironmentException>(() => sandbox.BeginAsync(CancellationToken.None));

        Assert.Equal(1, locker.Taken);
        Assert.Equal(0, locker.Held);
        account.Problems.Clear();
        await using var session = await sandbox.BeginAsync(CancellationToken.None);
        Assert.Equal(1, locker.Held);
    }

    [Fact]
    public async Task ASecondSessionInTheSameProgram_IsRefusedByTheOldGuard_WithoutTakingTheLockAgain()
    {
        var (sandbox, locker, _, _, _) = Sandbox();
        await using var first = await sandbox.BeginAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("Another task is using the Linux machine right now", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, locker.Taken);
    }

    [Fact]
    public async Task DisposingASessionTwice_LetsTheLockGoOnce()
    {
        var (sandbox, locker, _, _, _) = Sandbox();
        var session = await sandbox.BeginAsync(CancellationToken.None);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, locker.Taken);
        Assert.Equal(0, locker.Held);
        Assert.Single(locker.Calls, c => c == "lock released");
    }

    [Fact]
    public void TheRealLock_IsAFileLockInTheDataFolder_OneHolderAtATime()
    {
        using var folder = new TempFolder();
        var locker = new FileSessionLock(folder.Paths.SessionLockFile);

        using (var first = locker.TryAcquire())
        {
            Assert.NotNull(first);
            Assert.Null(locker.TryAcquire());
            Assert.Null(new FileSessionLock(folder.Paths.SessionLockFile).TryAcquire());
        }

        using var again = locker.TryAcquire();
        Assert.NotNull(again);
        Assert.Equal(Path.Combine(folder.Paths.Machine, "session.lock"), folder.Paths.SessionLockFile);
        Assert.NotEqual(folder.Paths.LockFile, folder.Paths.SessionLockFile);
    }

    [Fact]
    public void TheRealLock_NeverCreatesTheDataFolder_AndAnEmptyNameIsRefused()
    {
        using var folder = new TempFolder();
        var nowhere = Path.Combine(folder.Path, "nowhere", "session.lock");

        Assert.Throws<DirectoryNotFoundException>(() => new FileSessionLock(nowhere).TryAcquire());
        Assert.False(Directory.Exists(Path.GetDirectoryName(nowhere)));
        Assert.Throws<ArgumentException>(() => new FileSessionLock(string.Empty));
    }

    [Fact]
    public void ASandboxIsNeverMadeWithoutALock()
    {
        var redactor = new Redactor();

        Assert.Throws<ArgumentNullException>(() => new WslSandbox(new FakeWsl(), new FakeWslAccount(), new FakeWslSettingsFile(), Sandboxes.Facts, new FakeMachineRecord(), new MemoryCiLog(redactor), redactor, TestTime.New(), null!));
    }
}
