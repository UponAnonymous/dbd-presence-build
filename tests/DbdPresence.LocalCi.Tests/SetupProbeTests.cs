// Covers: FR-CI-01 and local CI round 3, red-team H1 (set-up has twelve steps; step 9 runs the isolation probe with the owner watching, after the token and the privacy check and before the sign-in task
// and the variable: the checks are sent to this PC only after the owner typed "yes" to what the probe found; a "no", Enter, "y" or a probe that could not run stops set-up and takes the variable away;
// a probe that found a way out recommends against accepting yet leaves the decision to the owner). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class SetupProbeTests
{
    [Fact]
    public async Task SetupHasTwelveSteps_AndTheProbeIsStepNine()
    {
        using var rig = new SetupRig();

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        var text = rig.Console.AllText;
        for (var step = 1; step <= 12; step++)
        {
            Assert.Contains($"Step {step} of 12:", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("of 11:", text, StringComparison.Ordinal);
        Assert.Contains("Step 9 of 12: the isolation probe", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeRunsAfterTheTokenAndBeforeTheSignInTaskAndTheVariable()
    {
        using var rig = new SetupRig();
        var journal = new List<string>();
        rig.Scheduler.Calls.Journal = journal;
        rig.Scheduler.Calls.Prefix = "task ";
        rig.Api.Calls.Journal = journal;
        rig.Api.Calls.Prefix = "api ";
        rig.Wsl.Calls.Journal = journal;
        rig.Wsl.Calls.Prefix = "wsl ";

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        var repository = journal.IndexOf("api repo");
        var probe = journal.IndexOf("wsl exec " + LocalCiDefaults.ProbeDistribution + " as root");
        var install = journal.FindIndex(j => j.StartsWith("task install", StringComparison.Ordinal));
        var variable = journal.IndexOf("api set LINUX_RUNNER");
        Assert.True(repository >= 0 && probe > repository, string.Join(" | ", journal));
        Assert.True(install > probe, string.Join(" | ", journal));
        Assert.True(variable > install, string.Join(" | ", journal));
        Assert.True(rig.State.Load().Probe!.Accepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("y")]
    [InlineData("no")]
    public async Task WithoutATypedYes_SetupStopsBeforeTheTaskAndTheVariable_AndTakesAnOldVariableAway(string typed)
    {
        using var rig = new SetupRig(typed: [typed]);
        rig.Api.Variables["LINUX_RUNNER"] = LocalCiDefaults.LinuxLabel;

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.DoesNotContain("install", rig.Scheduler.Calls);
        Assert.False(rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("Nothing was set to send checks to this PC.", rig.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Step 10 of 12", rig.Console.AllText, StringComparison.Ordinal);
        Assert.False(rig.State.Load().Probe!.Accepted);
        Assert.DoesNotContain(rig.Api.Calls, c => c.StartsWith("dispatch ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProbeThatCouldNotRun_StopsSetup_BecauseNothingIsShownToBeClosed()
    {
        using var rig = new SetupRig();
        rig.Wsl.ProbeFailure = new InvalidOperationException("wsl.exe stopped answering");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("because the probe could not give a result", rig.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("install", rig.Scheduler.Calls);
        Assert.Empty(rig.Api.Variables);
    }

    [Fact]
    public async Task AProbeThatFoundAWayOut_RecommendsNotAccepting_YetTheOwnerMayDecide()
    {
        using var rig = new SetupRig(typed: ["yes"]);
        rig.Wsl.ProbeLines.RemoveAll(l => l.StartsWith("ATTEMPT mount-windows-drive", StringComparison.Ordinal));
        rig.Wsl.ProbeLines.Add("ATTEMPT mount-windows-drive POSSIBLE root mounted C: and listed 3 entries in C:\\Users");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Contains("I recommend you do NOT accept this", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("I recommend NOT turning the Linux checks on for this PC", rig.Console.AllText, StringComparison.Ordinal);
        var record = rig.State.Load().Probe!;
        Assert.Equal("SomePossible", record.Outcome);
        Assert.True(record.Accepted);
    }

    [Fact]
    public async Task APublicRepository_StopsBeforeTheProbeEvenRuns()
    {
        using var rig = new SetupRig();
        rig.Api.Repo = new RepoInfo("main", null, IsPrivate: false);

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Wsl.ProbeScripts);
        Assert.Null(rig.State.Load().Probe);
    }

    [Fact]
    public async Task EveryRunOfSetup_RunsTheProbeAgain_AndKeepsOnlyTheLatestResult()
    {
        using var rig = new SetupRig(typed: ["yes", ""]);

        Assert.Equal(SetupOutcome.Done, await rig.Wizard.RunAsync(CancellationToken.None));
        Assert.True(rig.State.Load().Probe!.Accepted);

        var second = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, second);
        Assert.Equal(2, rig.Wsl.ProbeScripts.Count);
        Assert.False(rig.State.Load().Probe!.Accepted);
    }

    [Fact]
    public async Task StepTwelve_SaysHowLongTheTestTakesAndToMergeThePullRequestFirst_NotJustUpToThirtyMinutes()
    {
        using var rig = new SetupRig();

        await rig.Wizard.RunAsync(CancellationToken.None);

        var step = rig.Console.Lines.Single(l => l.StartsWith("Step 12 of 12:", StringComparison.Ordinal));
        Assert.Contains("Expected to take a few minutes if the pull request is merged", step, StringComparison.Ordinal);
        Assert.Contains("UNMEASURED", step, StringComparison.Ordinal);
        Assert.Contains("If it is not merged, GitHub cannot start the test by hand", step, StringComparison.Ordinal);
        Assert.Contains("waits up to 30 minutes for a push of yours and then for the whole CI (Linux) run", step, StringComparison.Ordinal);
        Assert.DoesNotContain("(up to 30 minutes)", step, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterTheOwnerAcceptsTheProbeAndAllowsTheUpdate_TheWeeklyWslUpdateIsNotDueUntilAWeekLater_SoItCannotChangeWslOnTheFirstIdleRound()
    {
        using var rig = new SetupRig(answers: [true]);

        Assert.Equal(SetupOutcome.Done, await rig.Wizard.RunAsync(CancellationToken.None));

        var state = rig.State.Load();
        var update = new WeeklyWslUpdate(new FakeWslInstaller(), new FakeProcesses(), () => null, rig.State, TestTime.New(), rig.Log, rig.Redactor);
        Assert.NotNull(state.LastWslUpdateAttempt);
        Assert.False(update.Due(state));
        Assert.True(state.WslUpdateAllowed);
        Assert.Equal("not tried yet: set-up looked at WSL on 2026-10-29, so the first update is due a week later", state.LastWslUpdateNote);
    }

    [Fact]
    public async Task TheWeeklyUpdateQuestion_ExplainsBothAnswers_AndEnterMeansNo()
    {
        using var rig = new SetupRig();

        Assert.Equal(SetupOutcome.Done, await rig.Wizard.RunAsync(CancellationToken.None));

        var text = rig.Console.AllText;
        Assert.Contains("One question: may DBD Local CI update WSL itself?", text, StringComparison.Ordinal);
        Assert.Contains("WSL is shared by the whole PC, so an update changes the WSL you use yourself too, at a time you did not pick.", text, StringComparison.Ordinal);
        Assert.Contains("If you say yes: once a week, only when no Linux of yours and none of this program is running", text, StringComparison.Ordinal);
        Assert.Contains("After each update the program repeats the isolation probe by itself. If the result is worse than the one you accepted, it turns LINUX_RUNNER off and tells you.", text, StringComparison.Ordinal);
        Assert.Contains("If you say no (the default): the program never changes WSL.", text, StringComparison.Ordinal);
        Assert.Equal("Update WSL once a week? (yes/no; Enter means no)", Assert.Single(rig.Console.Questions));
        Assert.Contains("The weekly WSL update is off.", text, StringComparison.Ordinal);
        var state = rig.State.Load();
        Assert.False(state.WslUpdateAllowed);
        Assert.StartsWith("off: you did not allow the weekly update at set-up (run set-up again to change that)", state.LastWslUpdateNote, StringComparison.Ordinal);
        Assert.NotNull(state.LastWslUpdateAttempt);
    }

    [Fact]
    public async Task AYesIsRecorded_AndALaterRunThatSaysNoTurnsItOffAgain_AndAYesAfterThatReplacesTheOffNote()
    {
        using var rig = new SetupRig(answers: [true, false, true], typed: ["yes", "yes", "yes"]);

        await rig.Wizard.RunAsync(CancellationToken.None);
        Assert.True(rig.State.Load().WslUpdateAllowed);
        Assert.Contains("The weekly WSL update is on.", rig.Console.AllText, StringComparison.Ordinal);

        await rig.Wizard.RunAsync(CancellationToken.None);
        Assert.False(rig.State.Load().WslUpdateAllowed);
        Assert.StartsWith("off: ", rig.State.Load().LastWslUpdateNote, StringComparison.Ordinal);

        await rig.Wizard.RunAsync(CancellationToken.None);
        Assert.True(rig.State.Load().WslUpdateAllowed);
        Assert.StartsWith("not tried yet: ", rig.State.Load().LastWslUpdateNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnAcceptedProbe_TheOwnerIsNotAskedAboutTheWeeklyUpdate()
    {
        using var rig = new SetupRig(typed: ["no"], answers: [true]);

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Empty(rig.Console.Questions);
        Assert.Null(rig.State.Load().WslUpdateAllowed);
    }

    [Fact]
    public async Task ALaterRunOfSetup_KeepsTheTimeOfAnEarlierUpdateAttempt_AndItsNote()
    {
        using var rig = new SetupRig(typed: ["yes"], answers: [true]);
        var earlier = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        rig.State.Update(s =>
        {
            s.LastWslUpdateAttempt = earlier;
            s.LastWslUpdateNote = "updated at 2026-10-01: WSL was updated to version 2.4.8.0.";
        });

        Assert.Equal(SetupOutcome.Done, await rig.Wizard.RunAsync(CancellationToken.None));

        Assert.Equal(earlier, rig.State.Load().LastWslUpdateAttempt);
        Assert.Equal("updated at 2026-10-01: WSL was updated to version 2.4.8.0.", rig.State.Load().LastWslUpdateNote);
    }

    [Fact]
    public async Task WhenTheProbeIsNotAccepted_SetupWritesNothingAboutTheWeeklyUpdate()
    {
        using var rig = new SetupRig(typed: ["no"]);

        Assert.Equal(SetupOutcome.Stopped, await rig.Wizard.RunAsync(CancellationToken.None));

        Assert.Null(rig.State.Load().LastWslUpdateAttempt);
        Assert.Null(rig.State.Load().LastWslUpdateNote);
    }

    // ---- round 4, QA minor 4: Ctrl+C in the middle of the request that sets the variable leaves nothing pointing at a PC that was not proven

    [Fact]
    public async Task ACtrlC_RightAfterTheVariableWasSet_TakesTheVariableAwayAgain()
    {
        using var rig = new SetupRig();
        using var cancel = new CancellationTokenSource();

        // GitHub has stored the variable when the request is cancelled: the owner pressed Ctrl+C while the answer was on its way.
        rig.Api.AfterVariableSet = _ => throw new OperationCanceledException(cancel.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Wizard.RunAsync(cancel.Token));

        Assert.False(rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("delete-variable LINUX_RUNNER", rig.Api.Calls);
        Assert.Contains("I turned LINUX_RUNNER off", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVariableThatCouldNotBeSet_LeavesNothingToTakeAwayButIsTriedAgainAtTheEnd()
    {
        using var rig = new SetupRig();
        rig.Api.SetVariableFailure = new GitHubApiException("GitHub refused the variable", 403, isTransient: false);

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
    }

    [Fact]
    public async Task ATestCheckThatPassedOnThisPc_KeepsTheVariable_SoNothingIsTakenAwayAtTheEnd()
    {
        using var rig = new SetupRig();

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.True(rig.Wizard.ProvenOnThisPc);
        Assert.Equal("dbd-linux", rig.Api.Variables["LINUX_RUNNER"]);
        Assert.DoesNotContain("delete-variable LINUX_RUNNER", rig.Api.Calls);
    }

    [Fact]
    public async Task ATestCheckThatDidNotPass_TakesTheVariableAwayOnce_NotTwice()
    {
        using var proven = new SetupRig();
        await proven.Wizard.RunAsync(CancellationToken.None);
        var readsWhenNothingIsTakenAway = proven.Api.VariableReads.GetValueOrDefault("LINUX_RUNNER");

        using var rig = new SetupRig();
        rig.Api.Jobs.Clear();
        rig.Api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "success", "GitHub Actions 1", 9, ["ubuntu-24.04"]));

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.False(rig.Wizard.ProvenOnThisPc);
        Assert.Single(rig.Api.Calls, c => c == "delete-variable LINUX_RUNNER");

        // Nothing at the end of set-up looks at the variable again: it was taken away and set-up knows it.
        Assert.Equal(readsWhenNothingIsTakenAway, rig.Api.VariableReads.GetValueOrDefault("LINUX_RUNNER"));
    }

    [Fact]
    public async Task AVariableThatCannotBeDeletedAfterATestThatDidNotPass_IsTriedOnceMoreAtTheEnd()
    {
        using var rig = new SetupRig();
        rig.Api.Jobs.Clear();
        rig.Api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "success", "GitHub Actions 1", 9, ["ubuntu-24.04"]));
        rig.Api.DeleteVariableFailure = new GitHubApiException("GitHub refused", 403, isTransient: false);

        await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(2, rig.Api.Calls.Count(c => c == "delete-variable LINUX_RUNNER"));
        Assert.True(rig.Api.VariableReads.GetValueOrDefault("LINUX_RUNNER") > 0);
    }

    [Fact]
    public async Task TheProbeStep_RunsOnlyAfterTheMachineIsSetUp_SoItHasAnImageToImport()
    {
        using var rig = new SetupRig();
        rig.Wsl.UsableFailure = new InvalidOperationException("wsl.exe cannot start");

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(rig.Wsl.ProbeScripts);
    }
}
