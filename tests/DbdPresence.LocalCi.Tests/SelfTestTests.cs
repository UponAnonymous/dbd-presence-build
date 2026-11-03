// Covers: FR-CI-07 (the test check starts the tiny local-ci-probe.yml by hand on the default branch, waits up to 30 minutes and says whether its job ran on a runner of this program and passed,
// ran there and failed, did not start at all, or ran on one of GitHub's machines; while the probe is not on the default branch yet it waits for the next push run of ci-linux.yml instead), QA M4.
// docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class SelfTestTests : IDisposable
{
    private static readonly string[] s_pc = ["self-hosted", "dbd-linux"];

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly FakeGitHubApi _api = new() { Repo = new RepoInfo("trunk", null, IsPrivate: true) };
    private readonly MemoryCiLog _log;
    private readonly StateStore _state;
    private readonly SelfTest _selfTest;

    public SelfTestTests()
    {
        var redactor = new Redactor();
        _log = new MemoryCiLog(redactor);
        _state = new StateStore(_folder.Paths.StateFile);
        _selfTest = new SelfTest(_api, _state, _time, _log, redactor, (span, _) =>
        {
            _time.Advance(span);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _folder.Dispose();

    private void Completed(string conclusion) =>
        _api.RunStates.Add(new WorkflowRunInfo(77, "completed", conclusion, _time.GetUtcNow()));

    [Fact]
    public async Task APassOnARunnerOfThisProgram_IsReportedAsRanHereAndPassed_AfterDispatchingTheProbeOnTheDefaultBranch()
    {
        _api.RunStates.Add(new WorkflowRunInfo(77, "queued", null, _time.GetUtcNow()));
        Completed("success");
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "success", "dbd-linux-20261029120000", 9, s_pc));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.PassedOnThisPc, result.Outcome);
        Assert.Contains("dispatch local-ci-probe.yml on trunk", _api.Calls);
        Assert.Equal(nameof(SelfTestOutcome.PassedOnThisPc), _state.Load().SelfTest!.Outcome);
        Assert.Equal("The test ran on your PC and passed.", result.Message);
    }

    [Fact]
    public async Task APassOnGitHubsOwnMachine_IsNotCountedAsPassingHere()
    {
        Completed("success");
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "success", "GitHub Actions 12", 9, ["ubuntu-24.04"]));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.PassedOnGitHub, result.Outcome);
        Assert.Contains("LINUX_RUNNER", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbd-linux-1")]
    [InlineData("dbd-linux-202610291200000")]
    [InlineData("dbd-windows-20261029120000")]
    [InlineData("my-dbd-linux-20261029120000")]
    public async Task ARunnerThatIsNotExactlyOneOfThisProgramsName_IsNotThisPc(string runner)
    {
        Completed("success");
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "success", runner, 9, s_pc));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.PassedOnGitHub, result.Outcome);
    }

    [Fact]
    public async Task AFailureThatRanOnTheRunnerOfThisProgram_IsReportedAsFailed()
    {
        Completed("failure");
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "failure", "dbd-linux-20261029120000", 9, s_pc));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.Failed, result.Outcome);
        Assert.Contains("your PC", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureOnGitHubsMachine_IsReportedAsFailedThere()
    {
        Completed("failure");
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "failure", "GitHub Actions 3", 9, ["ubuntu-24.04"]));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.Failed, result.Outcome);
        Assert.Contains("GitHub's machines", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobThatAskedForThisPcButNeverStarted_IsReportedAsFailedToStart_AndTheOwnerIsToldWhereToReadWhy()
    {
        Completed("failure");
        _api.Jobs.Add(new WorkflowJobInfo(5, "probe", "completed", "failure", null, 0, s_pc));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.FailedToStart, result.Outcome);
        Assert.Contains("asked for your PC", result.Message, StringComparison.Ordinal);
        Assert.Contains("Actions page", result.Message, StringComparison.Ordinal);
        Assert.Equal(nameof(SelfTestOutcome.FailedToStart), _state.Load().SelfTest!.Outcome);
    }

    [Fact]
    public async Task AJobThatAskedForGitHubsMachineAndNeverStarted_SaysTheVariableDidNotSendItToThisPc()
    {
        Completed("failure");
        _api.Jobs.Add(new WorkflowJobInfo(5, "probe", "completed", "failure", null, 0, ["ubuntu-24.04"]));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.FailedToStart, result.Outcome);
        Assert.Contains("LINUX_RUNNER is not set", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATestNobodyPicksUp_IsCancelledAfterThirtyMinutes()
    {
        _api.RunStates.Add(new WorkflowRunInfo(77, "queued", null, _time.GetUtcNow()));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Equal([77L], _api.CancelledRuns);
        Assert.True(_time.GetUtcNow() >= new DateTimeOffset(2026, 10, 29, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task WhenGitHubDoesNotReturnTheRunId_TheRunIsFoundByItsStartTime()
    {
        _api.DispatchedRun = null;
        _api.RunStates.Add(new WorkflowRunInfo(88, "completed", "success", _time.GetUtcNow().AddSeconds(3)));
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "success", "dbd-linux-20261029120000", 3, s_pc));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.PassedOnThisPc, result.Outcome);
    }

    [Fact]
    public async Task WhenNoRunAppearsAfterTheDispatch_ThatIsSaidAndNothingIsMeasured()
    {
        _api.DispatchedRun = null;

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("did not show the test run", result.Message, StringComparison.Ordinal);
    }

    /// <summary>GitHub starts a workflow by hand only when the file is on the default branch. Until the work is merged there, the test waits for the next pushed run of ci-linux.yml and judges that one.</summary>
    [Fact]
    public async Task WhileTheProbeIsNotOnTheDefaultBranch_TheTestWaitsForThePushedRunAndReportsWhereItRan()
    {
        _api.DispatchFailure = new GitHubApiException("Workflow does not have 'workflow_dispatch' trigger", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(91, "completed", "success", _time.GetUtcNow().AddMinutes(2)));
        _api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "success", "dbd-linux-20261029120500", 9, s_pc));
        _api.Jobs.Add(new WorkflowJobInfo(2, "test", "completed", "success", "dbd-linux-20261029121000", 9, s_pc));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.PassedOnThisPc, result.Outcome);
        Assert.Contains("All 2 jobs of ci-linux.yml ran on your PC and passed", result.Message, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.Contains("default branch", StringComparison.Ordinal) && l.Contains("push a commit", StringComparison.Ordinal));
        Assert.Empty(_api.CancelledRuns);
    }

    [Fact]
    public async Task APushedRunWhereOnlySomeJobsRanOnThisPc_IsNotAPass()
    {
        _api.DispatchFailure = new GitHubApiException("not found", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(91, "completed", "success", _time.GetUtcNow().AddMinutes(1)));
        _api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "success", "dbd-linux-20261029120500", 9, s_pc));
        _api.Jobs.Add(new WorkflowJobInfo(2, "windows", "completed", "success", "GitHub Actions 3", 9, ["windows-2025"]));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.PassedOnGitHub, result.Outcome);
        Assert.Contains("only 1 of its 2 jobs ran on your PC", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNoPushedRunComes_WithinThirtyMinutes_NothingWasMeasured_AndNoRunOfTheOwnersIsCancelled()
    {
        _api.DispatchFailure = new GitHubApiException("not found", 404, isTransient: false);

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("Merge the pull request", result.Message, StringComparison.Ordinal);
        Assert.Empty(_api.CancelledRuns);
        Assert.True(_time.GetUtcNow() >= new DateTimeOffset(2026, 10, 29, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task APushedRunThatNeverFinishes_IsLeftRunning_BecauseItIsTheOwnersOwnCiRun()
    {
        _api.DispatchFailure = new GitHubApiException("not found", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(91, "in_progress", null, _time.GetUtcNow().AddMinutes(1)));

        var result = await _selfTest.RunAsync(CancellationToken.None);

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("did not finish within 45 minutes", result.Message, StringComparison.Ordinal);
        Assert.Empty(_api.CancelledRuns);
    }

    [Fact]
    public async Task AnyOtherDispatchFailure_IsNotHidden()
    {
        _api.DispatchFailure = new GitHubApiException("Resource not accessible by personal access token", 403, isTransient: false);

        await Assert.ThrowsAsync<GitHubApiException>(() => _selfTest.RunAsync(CancellationToken.None));
    }
}
