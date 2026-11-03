// Covers: FR-CI-01 and local CI round 3, QA M-1 (re-running set-up stops the loop; however set-up then ends, the loop is started again or the variable is taken away, so no check waits for a PC whose
// loop is stopped), M-5 (the safety rules of set-up are tested with failures injected: the running loop is stopped first, a loop that cannot be stopped stops set-up before any command as the
// account, the variable is set only after the task exists), ADV1, ADV7, ADV9, ADV10, ADV12, ADV17. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class SetupSafetyTests
{
    // ---- M-5: the loop is stopped first (S8), and a loop that cannot be stopped stops set-up (S11)

    /// <summary>ADV9 / S8: a running loop is ended before set-up does anything with the account's Linux machine (wsl --shutdown would end a job).</summary>
    [Fact]
    public async Task ARunningLoop_IsStoppedBeforeAnyCommandAsTheAccount()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        var journal = new List<string>();
        rig.Scheduler.Calls.Journal = journal;
        rig.Scheduler.Calls.Prefix = "task ";
        rig.Wsl.Calls.Journal = journal;
        rig.Wsl.Calls.Prefix = "wsl ";
        rig.Account.Calls.Journal = journal;
        rig.Account.Calls.Prefix = "account ";

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal("task stop", journal[0]);
        Assert.Contains("stopping it for the time set-up takes", rig.Console.AllText, StringComparison.Ordinal);
    }

    /// <summary>ADV12 / S11: a loop that cannot be stopped stops set-up before any command as the account.</summary>
    [Fact]
    public async Task ALoopThatCannotBeStopped_StopsSetupBeforeAnyWslCommand_AndSaysHowToStopIt()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        rig.Scheduler.StopLeavesRunning = true;

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Wsl.Calls);
        Assert.Empty(rig.Account.Calls);
        Assert.Empty(rig.Installer.Calls);
        Assert.Contains("still running", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("Task Scheduler", rig.Console.AllText, StringComparison.Ordinal);

        // It is still running, so nothing was stopped by set-up that it would have to restart, and the variable is left as it was.
        Assert.Equal(["stop"], rig.Scheduler.Calls);
        Assert.Equal(LocalCiDefaults.LinuxLabel, rig.Api.Variables["LINUX_RUNNER"]);
    }

    [Fact]
    public async Task ATaskThatCannotBeEnded_StopsSetupBeforeAnyWslCommand()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        rig.Scheduler.StopFailure = new InvalidOperationException("Access is denied.");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Wsl.Calls);
        Assert.Contains("Stopped while stopping the background task", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNoLoopIsRunning_NothingIsStopped_AndNothingIsStartedByTheRestore()
    {
        using var rig = new SetupRig();
        rig.Account.EnsureFailure = new InvalidOperationException("boom at step 4");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Scheduler.Calls);
        Assert.DoesNotContain(rig.Api.Calls, c => c.StartsWith("delete-variable", StringComparison.Ordinal));
    }

    // ---- M-1: however set-up ends after it stopped the loop, nothing is left pointing at a stopped loop

    /// <summary>ADV1: a re-run that stops early restarts the loop it stopped, so the variable that is still set is served.</summary>
    [Theory]
    [InlineData("check")]
    [InlineData("install")]
    [InlineData("data-folder")]
    [InlineData("account")]
    [InlineData("wsl")]
    [InlineData("settings")]
    [InlineData("image")]
    [InlineData("download")]
    [InlineData("token")]
    public async Task AReRunThatStopsEarly_StartsTheLoopItStoppedAgain_AndLeavesTheVariableAsItWas(string stopAt)
    {
        using var rig = new SetupRig(secrets: []).WithAWorkingEarlierSetup();
        rig.Store.Save(new SecretText(SetupRig.GoodToken));
        switch (stopAt)
        {
            case "check":
                rig.Machine.WindowsBuild = 18000;
                break;
            case "install":
                rig.Installer.LocationProblems.Add("the folder is open to all users");
                break;
            case "data-folder":
                rig.Storage.DataFolderProblems.Add("the data folder is open to all users");
                break;
            case "account":
                rig.Account.EnsureFailure = new InvalidOperationException("boom at step 4");
                break;
            case "wsl":
                rig.Wsl.Usable = false;
                break;
            case "settings":
                rig.Settings.HeldTextOverride = "[wsl2]\nnetworkingMode=mirrored\n";
                break;
            case "image":
                rig.Wsl.ImportFailure = new InvalidOperationException("import failed");
                break;
            case "download":
                rig.Api.Release = null;
                break;
            default:
                rig.Api.RepoFailure = new GitHubApiException("down", 503, isTransient: true);
                break;
        }

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.True(rig.Scheduler.Running, "set-up stopped the loop and ended early, but the loop is not running again");
        Assert.Equal(LocalCiDefaults.LinuxLabel, rig.Api.Variables["LINUX_RUNNER"]);
        Assert.Equal("start", rig.Scheduler.Calls[^1]);
        Assert.Contains("was started again", rig.Console.AllText, StringComparison.Ordinal);
        Assert.False(rig.Wizard.ProvenOnThisPc);
    }

    [Fact]
    public async Task AReRunThatNeedsARestartOfThePc_StartsTheLoopAgain()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        rig.Wsl.Usable = false;
        rig.WslInstaller.Restart = true;

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.RestartNeeded, outcome);
        Assert.True(rig.Scheduler.Running);
    }

    /// <summary>ADV1, the other branch: when the loop cannot be started again, the variable goes, so no check waits for a runner nobody serves.</summary>
    [Fact]
    public async Task IfTheLoopCannotBeStartedAgain_TheVariableIsDeleted_AndTheOwnerIsToldWhy()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        rig.Store.Save(new SecretText(SetupRig.GoodToken));
        rig.Account.EnsureFailure = new InvalidOperationException("boom at step 4");
        rig.Scheduler.StartFailure = new InvalidOperationException("Windows would not start the scheduled task");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(rig.Scheduler.Running);
        Assert.Empty(rig.Api.Variables);
        Assert.Contains("could not be started again", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("I turned LINUX_RUNNER off (the background loop is not running)", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains(rig.Log.Lines, l => l.Contains("could not start the background loop again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVariableWithAnotherValue_IsTheOwnersOwn_AndIsNeverDeletedByTheRestore()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        rig.Store.Save(new SecretText(SetupRig.GoodToken));
        rig.Api.Variables["LINUX_RUNNER"] = "my-own-runner";
        rig.Account.EnsureFailure = new InvalidOperationException("boom at step 4");
        rig.Scheduler.StartFailure = new InvalidOperationException("no");

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal("my-own-runner", rig.Api.Variables["LINUX_RUNNER"]);
        Assert.DoesNotContain(rig.Api.Calls, c => c.StartsWith("delete-variable", StringComparison.Ordinal));
        Assert.Contains("left alone", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfTheVariableCannotBeDeletedEither_TheOwnerIsToldToDeleteItByHand()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        rig.Store.Save(new SecretText(SetupRig.GoodToken));
        rig.Account.EnsureFailure = new InvalidOperationException("boom at step 4");
        rig.Scheduler.StartFailure = new InvalidOperationException("no");
        rig.Api.DeleteVariableFailure = new GitHubApiException("Resource not accessible", 403, isTransient: false);

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Contains("Delete it on GitHub by hand now", rig.Console.AllText, StringComparison.Ordinal);
    }

    /// <summary>A refusal (a repository that is not private) takes the routing away even though the loop was running and could have been restarted.</summary>
    [Fact]
    public async Task ARepositoryThatIsNotPrivate_TakesTheVariableAway_AndDoesNotStartTheLoopAgain()
    {
        using var rig = new SetupRig(secrets: []).WithAWorkingEarlierSetup();
        rig.Store.Save(new SecretText(SetupRig.GoodToken));
        rig.Api.Repo = new RepoInfo("main", null, IsPrivate: false);

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Api.Variables);
        Assert.DoesNotContain("start", rig.Scheduler.Calls);
        Assert.Contains("I turned LINUX_RUNNER off (set-up refused to send checks to this PC)", rig.Console.AllText, StringComparison.Ordinal);
    }

    /// <summary>ADV17: Ctrl+C in the middle of the test check takes the variable away (the unproven end), and the loop that set-up started stays.</summary>
    [Fact]
    public async Task ACancelDuringTheTestCheck_TakesTheVariableAway()
    {
        using var rig = new SetupRig();
        using var cts = new CancellationTokenSource();
        rig.Api.RunStates.Clear();
        rig.Api.RunStates.Add(new WorkflowRunInfo(77, "in_progress", null, TestTime.New().GetUtcNow()));
        rig.Api.OnRunPolled = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Wizard.RunAsync(cts.Token));

        Assert.Empty(rig.Api.Variables);
        Assert.True(rig.Scheduler.Running);
    }

    /// <summary>A cancel before the loop is started again still starts it: nothing set-up stopped stays stopped.</summary>
    [Fact]
    public async Task ACancelInTheMiddleOfAReRun_StartsTheLoopAgain()
    {
        using var rig = new SetupRig().WithAWorkingEarlierSetup();
        using var cts = new CancellationTokenSource();
        rig.Account.OnEnsure = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Wizard.RunAsync(cts.Token));

        Assert.True(rig.Scheduler.Running);
        Assert.Equal("start", rig.Scheduler.Calls[^1]);
    }

    // ---- M-5: the variable is set only after the task exists (S12 / ADV7)

    [Fact]
    public async Task ATaskThatCannotBeCreated_LeavesNoVariable_AndNoTestIsDispatched()
    {
        using var rig = new SetupRig();
        rig.Scheduler.InstallFailure = new InvalidOperationException("Windows would not create the scheduled task");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Api.Variables);
        Assert.DoesNotContain(rig.Api.Calls, c => c.StartsWith("set ", StringComparison.Ordinal) || c.StartsWith("dispatch", StringComparison.Ordinal));
        Assert.Contains("Stopped while creating the sign-in task", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskThatCannotBeStarted_LeavesNoVariable()
    {
        using var rig = new SetupRig();
        rig.Scheduler.StartFailure = new InvalidOperationException("Windows would not start the scheduled task");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Api.Variables);
    }

    [Fact]
    public async Task TheVariableIsSetAfterTheTaskIsCreatedAndStarted()
    {
        using var rig = new SetupRig();
        var journal = new List<string>();
        rig.Scheduler.Calls.Journal = journal;
        rig.Scheduler.Calls.Prefix = "task ";
        rig.Api.Calls.Journal = journal;

        await rig.Wizard.RunAsync(CancellationToken.None);

        var set = journal.IndexOf("set LINUX_RUNNER");
        Assert.True(set > journal.FindIndex(c => c.StartsWith("task install", StringComparison.Ordinal)));
        Assert.True(set > journal.IndexOf("task start"));
        Assert.True(rig.Scheduler.Running);
    }

    [Fact]
    public async Task ASetupThatEndsWell_DoesNotStartTheLoopASecondTime()
    {
        using var rig = new SetupRig();

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Single(rig.Scheduler.Calls, c => c == "start");
    }

    // ---- ADV10: the edges of the first check

    [Theory]
    [InlineData(19040, true)]
    [InlineData(19041, false)]
    public async Task TheWindowsBuildEdge(int build, bool stops)
    {
        using var rig = new SetupRig();
        rig.Machine.WindowsBuild = build;

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(stops, outcome == SetupOutcome.Stopped && rig.Console.AllText.Contains("too old", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(25L * 1024 * 1024 * 1024 - 1, true)]
    [InlineData(25L * 1024 * 1024 * 1024, false)]
    public async Task TheFreeDiskEdge_ForAFirstSetup(long free, bool stops)
    {
        using var rig = new SetupRig();
        rig.Machine.FreeBytes = free;

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(stops, outcome == SetupOutcome.Stopped && rig.Console.AllText.Contains("are needed for a first set-up", StringComparison.Ordinal));
    }

    /// <summary>A loop that set-up started itself in step 10 needs a moment to show as running; when set-up then ends, it is not started a second time.</summary>
    [Fact]
    public async Task ALoopThatSetUpStartedItself_ButThatDoesNotYetShowAsRunning_IsNotStartedASecondTime()
    {
        using var rig = new SetupRig();
        rig.Scheduler.TaskExists = true;
        rig.Scheduler.Running = true;
        rig.Scheduler.StartShowsAsRunning = false;

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Contains("stop", rig.Scheduler.Calls);
        Assert.Single(rig.Scheduler.Calls, c => c == "start");
    }
}
