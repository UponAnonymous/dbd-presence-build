// Covers: FR-CI-07 and local CI round 3, QA minor (the test check's own limits, 30 minutes for the probe and 45 for a pushed run, run on the monotonic clock: a jump of the PC's clock neither ends the
// wait early nor keeps it going for ever) and the advice printed while the probe is not on the default branch (a commit that changes a file the workflow watches). docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class SelfTestClockTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _real = TestTime.New();
    private readonly SkewedTime _time;
    private readonly FakeGitHubApi _api = new() { Repo = new RepoInfo("main", null, IsPrivate: true) };
    private readonly MemoryCiLog _log;
    private readonly SelfTest _selfTest;
    private int _polls;
    private Action<int>? _onPoll;

    public SelfTestClockTests()
    {
        _time = new SkewedTime(_real);
        var redactor = new Redactor();
        _log = new MemoryCiLog(redactor);
        _selfTest = new SelfTest(_api, new StateStore(_folder.Paths.StateFile), _time, _log, redactor, (span, _) =>
        {
            _polls++;
            _onPoll?.Invoke(_polls);
            _real.Advance(span);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _folder.Dispose();

    private void StillRunning() => _api.RunStates.Add(new WorkflowRunInfo(77, "in_progress", null, _real.GetUtcNow()));

    [Fact]
    public async Task AClockSetForwardFiveHours_DoesNotMakeTheTestGiveUpEarly()
    {
        StillRunning();
        _onPoll = poll =>
        {
            if (poll == 1)
            {
                _time.Skew = TimeSpan.FromHours(5);
            }

            if (poll == 8)
            {
                _api.RunStates.Clear();
                _api.RunStates.Add(new WorkflowRunInfo(77, "completed", "success", _real.GetUtcNow()));
                _api.Jobs.Add(new WorkflowJobInfo(1, "probe", "completed", "success", "dbd-linux-20261029120000", 9, ["self-hosted", "dbd-linux"]));
            }
        };

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.PassedOnThisPc, result.Outcome);
    }

    [Fact]
    public async Task AClockSetBackFiveHours_DoesNotKeepTheTestGoingForEver_ItEndsAfterThirtyRealMinutes()
    {
        StillRunning();
        _onPoll = poll =>
        {
            if (poll == 1)
            {
                _time.Skew = TimeSpan.FromHours(-5);
            }
        };

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("did not finish within 30 minutes", result.Message, StringComparison.Ordinal);
        Assert.Equal([77L], _api.CancelledRuns);

        // 30 minutes of 15-second polls (the last poll may be the one that finds the limit reached).
        Assert.InRange(_polls, 120, 121);
    }

    [Fact]
    public async Task APushedRunThatNeverComes_GivesUpAfterThirtyRealMinutes_EvenWithTheClockSetBack()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        _onPoll = poll =>
        {
            if (poll == 1)
            {
                _time.Skew = TimeSpan.FromHours(-5);
            }
        };

        var result = await _selfTest.RunAsync(CancellationToken.None).OrFail();

        Assert.Equal(SelfTestOutcome.NotPickedUp, result.Outcome);
        Assert.Contains("No run of ci-linux.yml started within 30 minutes", result.Message, StringComparison.Ordinal);
        Assert.InRange(_polls, 120, 121);
    }

    [Fact]
    public async Task WhileTheProbeIsNotOnTheDefaultBranch_TheAdviceNamesAFileTheWorkflowWatches_NotAnEmptyCommit()
    {
        _api.DispatchFailure = new GitHubApiException("Not Found", 404, isTransient: false);
        _onPoll = poll =>
        {
            if (poll == 1)
            {
                _api.RunStates.Add(new WorkflowRunInfo(5, "completed", "success", _real.GetUtcNow()));
                _api.Jobs.Add(new WorkflowJobInfo(1, "build-test", "completed", "success", "dbd-linux-20261029120000", 9, ["self-hosted", "dbd-linux"]));
            }
        };

        await _selfTest.RunAsync(CancellationToken.None).OrFail();

        var advice = string.Join("\n", _log.Lines);
        Assert.DoesNotContain("--allow-empty", advice, StringComparison.Ordinal);
        Assert.Contains("ignores a push that changes only files under docs/", advice, StringComparison.Ordinal);
        Assert.Contains("tools/DbdPresence.LocalCi/README.txt", advice, StringComparison.Ordinal);
    }
}
