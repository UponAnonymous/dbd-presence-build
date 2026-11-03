// Covers: FR-CI-07 (the test check: how many times it looks for the run it started, the one-minute window in which a run counts as its own, the time it gives a pushed run (15 minutes and 30 for every job
// it has, local CI round 4, QA M-1), that a cancelled run is reported as cancelled and not as a failure, and that a run with no jobs at all is never "ran on your PC"). Written for the survivors of the round 3 mutation run (docs/reviews/2026-10-30-local-ci-round3-qa.md, Resolution). No sleeps, no wall clock: the
// injected clock moves only when the program waits. docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class SelfTestEdgeTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly FakeGitHubApi _api = new() { Repo = new RepoInfo("main", null, IsPrivate: true) };
    private readonly SelfTest _selfTest;
    private int _waits;

    public SelfTestEdgeTests()
    {
        var redactor = new Redactor();
        _selfTest = new SelfTest(_api, new StateStore(_folder.Paths.StateFile), _time, new MemoryCiLog(redactor), redactor, (span, _) =>
        {
            _waits++;
            _time.Advance(span);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _folder.Dispose();

    private static readonly string[] s_labels = ["self-hosted", "dbd-linux"];

    private static WorkflowJobInfo JobOnThisPc() => new(1, "probe", "completed", "success", "dbd-linux-20261029120000", 9, s_labels);

    [Fact]
    public async Task ARunThatPassedWithNoJobsAtAll_IsNotSaidToHaveRunOnThisPc()
    {
        _api.RunStates.Add(new WorkflowRunInfo(77, "completed", "success", _time.GetUtcNow()));

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.PassedOnGitHub, result.Outcome);
    }

    [Fact]
    public async Task TheRunIsLookedForEightTimes_FiveSecondsApart_AndThenGivenUp()
    {
        _api.DispatchedRun = null;

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("did not show the test run", result.Message, StringComparison.Ordinal);
        Assert.Equal(8, _waits);
    }

    [Fact]
    public async Task ARunStartedExactlyOneMinuteBeforeTheTestCounts_OneSecondEarlierDoesNot()
    {
        _api.DispatchedRun = null;
        _api.RunStates.Add(new WorkflowRunInfo(77, "completed", "success", _time.GetUtcNow() - TimeSpan.FromMinutes(1)));
        _api.Jobs.Add(JobOnThisPc());

        var found = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.PassedOnThisPc, found.Outcome);
        Assert.Equal(0, _waits);

        _api.RunStates.Clear();
        _api.RunStates.Add(new WorkflowRunInfo(78, "completed", "success", _time.GetUtcNow() - TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1)));

        var missed = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, missed.Outcome);
        Assert.Equal(8, _waits);
    }

    [Fact]
    public async Task APushedRunThatStaysUnfinished_IsGivenUpAfterExactlyFortyFiveMinutes_AndLeftRunning()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(5, "in_progress", null, _time.GetUtcNow()));

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("did not finish within 45 minutes", result.Message, StringComparison.Ordinal);
        Assert.Empty(_api.CancelledRuns);

        // The run was seen at once; it is looked at every 15 seconds; the 45th minute is reached at the 180th wait, and not one wait sooner or later.
        Assert.Equal(180, _waits);
    }

    [Theory]
    [InlineData(0, 45)]
    [InlineData(1, 45)]
    [InlineData(2, 75)]
    [InlineData(3, 105)]
    [InlineData(5, 165)]
    public void ThePushedRunIsGivenFifteenMinutesAndThirtyForEveryJob_AtLeastForOne(int jobs, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), LocalCiDefaults.PassiveRunLimit(jobs));

    [Fact]
    public async Task APushedRunWithThreeJobs_IsGivenOneHundredAndFiveMinutes_NotFortyFive()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(5, "in_progress", null, _time.GetUtcNow()));
        _api.Jobs.Add(new WorkflowJobInfo(1, "build-test", "in_progress", null, "dbd-linux-20261029120000", 4, s_labels));
        _api.Jobs.Add(new WorkflowJobInfo(2, "format", "queued", null, null, 0, s_labels));
        _api.Jobs.Add(new WorkflowJobInfo(3, "service", "queued", null, null, 0, s_labels));

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("did not finish within 105 minutes (3 jobs, one runner)", result.Message, StringComparison.Ordinal);
        Assert.Contains("LINUX_RUNNER was not set", result.Message, StringComparison.Ordinal);
        Assert.Empty(_api.CancelledRuns);

        // 105 minutes of 15-second polls, to the wait.
        Assert.Equal(420, _waits);
    }

    [Fact]
    public async Task TheAllowanceGrowsWhenMoreJobsAppear_AndNeverShrinksAgain()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(5, "in_progress", null, _time.GetUtcNow()));
        _api.Jobs.Add(new WorkflowJobInfo(1, "build-test", "in_progress", null, "dbd-linux-20261029120000", 4, s_labels));
        _api.OnRunPolled = () =>
        {
            // From the 100th poll on (25 minutes), two more jobs have appeared; from the 150th poll on, only one is listed again.
            if (_polls == 100)
            {
                _api.Jobs.Add(new WorkflowJobInfo(2, "format", "queued", null, null, 0, s_labels));
                _api.Jobs.Add(new WorkflowJobInfo(3, "service", "queued", null, null, 0, s_labels));
            }

            if (_polls == 150)
            {
                _api.Jobs.RemoveRange(1, 2);
            }

            _polls++;
        };

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Contains("did not finish within 105 minutes (3 jobs, one runner)", result.Message, StringComparison.Ordinal);
        Assert.Equal(420, _waits);
    }

    [Fact]
    public async Task AOneJobRun_SaysJobInTheSingular()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        _api.RunStates.Add(new WorkflowRunInfo(5, "in_progress", null, _time.GetUtcNow()));

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Contains("did not finish within 45 minutes (1 job, one runner)", result.Message, StringComparison.Ordinal);
    }

    private int _polls;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelledRun_IsReportedAsCancelled_NotAsAFailureOfThePcOrAPaymentBlock(bool pushedRun)
    {
        if (pushedRun)
        {
            _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        }

        _api.RunStates.Add(new WorkflowRunInfo(5, "completed", "cancelled", _time.GetUtcNow()));
        _api.Jobs.Add(new WorkflowJobInfo(1, "build-test", "completed", "cancelled", null, 0, s_labels));

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.Cancelled, result.Outcome);
        Assert.Contains("was cancelled before it finished, so nothing was measured", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not start", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pushedRun, result.Message.Contains("when a newer commit is pushed", StringComparison.Ordinal));
        Assert.Equal(nameof(SelfTestOutcome.Cancelled), new StateStore(_folder.Paths.StateFile).Load().SelfTest!.Outcome);
    }

    [Fact]
    public async Task AFailedRun_StaysFailed_AndOnlyTheWordCancelledMakesItCancelled()
    {
        _api.RunStates.Add(new WorkflowRunInfo(5, "completed", "failure", _time.GetUtcNow()));
        _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "failure", "dbd-linux-20261029120000", 4, s_labels));

        var failed = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.Failed, failed.Outcome);

        _api.RunStates.Clear();
        _api.RunStates.Add(new WorkflowRunInfo(6, "completed", "timed_out", _time.GetUtcNow()));
        Assert.Equal(SelfTestOutcome.Failed, (await _selfTest.RunAsync(CancellationToken.None).OrFail()).Outcome);
    }

    [Fact]
    public async Task WhenNoPushedRunComes_TheMessageNamesTheFileToChange_AndTheBestWay()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("Merge the pull request first (then the test check starts its own workflow and needs no push)", result.Message, StringComparison.Ordinal);
        Assert.Contains("a file outside docs/", result.Message, StringComparison.Ordinal);
        Assert.Contains("tools/DbdPresence.LocalCi/README.txt", result.Message, StringComparison.Ordinal);
        Assert.Contains("choose 3 again", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--allow-empty", result.Message, StringComparison.Ordinal);
    }
}
