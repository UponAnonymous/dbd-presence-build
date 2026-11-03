namespace DbdPresence.LocalCi;

public enum SelfTestOutcome
{
    /// <summary>The test ran on this PC (a runner of this program took every job) and passed.</summary>
    PassedOnThisPc,

    /// <summary>It passed, but on one of GitHub's machines: the variable is not set or the loop is not running.</summary>
    PassedOnGitHub,

    /// <summary>It ran and failed.</summary>
    Failed,

    /// <summary>
    /// It failed before any step ran and no runner took it (GitHub's refusal because of the payment or spending-limit block looks like this; the reason cannot be read with a fine-grained
    /// token, so the message says where to read it).
    /// </summary>
    FailedToStart,

    /// <summary>Nothing picked it up, or no run appeared, within the time limit.</summary>
    NotPickedUp,

    /// <summary>
    /// The run was cancelled before it ended (a newer push cancels the older run of CI (Linux) on the same branch, or somebody cancelled it). Nothing was measured: it is neither a pass nor a failure of the
    /// PC (local CI round 4, QA M-1).
    /// </summary>
    Cancelled,
}

public sealed record SelfTestResult(SelfTestOutcome Outcome, string Message);

/// <summary>
/// The test check (FR-CI-07): starts the tiny <c>local-ci-probe.yml</c> by hand on the default branch, waits up to 30 minutes and says where its one job ran: on a runner of this program (label
/// <c>dbd-linux</c>, name <c>dbd-linux-&lt;time&gt;</c>), or on one of GitHub's machines, and whether it passed. GitHub starts a workflow by hand only when the file is on the default branch, so the pull
/// request has to be merged first; until then the test instead waits (up to 30 minutes) for the owner to push a commit and judges that pushed run of <c>ci-linux.yml</c>, which has several jobs that go
/// through the one runner one after the other (it is given <see cref="LocalCiDefaults.PassiveRunLimit"/> for its job count). Whether GitHub's billing block also stops jobs on the owner's own runner is the
/// one thing nobody knows until this has run once (UNKNOWN in the status text until then).
/// </summary>
public sealed class SelfTest
{
    private static readonly TimeSpan s_pollEvery = TimeSpan.FromSeconds(15);

    /// <summary>What to push while the test workflow is not on the default branch. It is part of the log, of the message at the end and of the guide.</summary>
    public const string PushAdvice = "push a commit that changes a file outside docs/ (CI (Linux) ignores a push that changes only files under docs/, and GitHub may skip a commit that changes no file at all): "
                                     + "for example add a blank line at the end of tools/DbdPresence.LocalCi/README.txt, then git commit and git push";

    private readonly IGitHubApi _api;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SelfTest(IGitHubApi api, StateStore state, TimeProvider time, ICiLog log, Redactor redactor, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _state = state;
        _time = time;
        _log = log;
        _redactor = redactor;
        _delay = delay ?? ((span, token) => Task.Delay(span, time, token));
    }

    public async Task<SelfTestResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        _state.Update(s => s.SelfTest = new SelfTestRecord { At = _time.GetUtcNow(), Outcome = result.Outcome.ToString() });
        _log.Info("Self-test: " + result.Message);
        return result;
    }

    private async Task<SelfTestResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        var repo = await _api.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);

        // The wall clock is only used to match the times GitHub gives its runs; every limit below runs on the monotonic clock, so a jump of the PC's clock cannot end the test early or keep it going
        // for ever (local CI round 3, QA minor).
        var startedAt = _time.GetUtcNow();
        var startedStamp = _time.GetTimestamp();
        long? runId = null;
        var passive = false;
        try
        {
            runId = await _api.DispatchWorkflowAsync(LocalCiDefaults.ProbeWorkflow, repo.DefaultBranch, cancellationToken).ConfigureAwait(false);
            runId ??= await FindRunAsync(LocalCiDefaults.ProbeWorkflow, "workflow_dispatch", startedAt, cancellationToken).ConfigureAwait(false);
            if (runId is null)
            {
                return new SelfTestResult(SelfTestOutcome.NotPickedUp, "GitHub did not show the test run. Look at the Actions page of the repository.");
            }
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 404)
        {
            passive = true;
            _log.Info($"GitHub cannot start {LocalCiDefaults.ProbeWorkflow} by hand: it only does that for a workflow that is on the repository's default branch ({repo.DefaultBranch}), and this one is not there yet. "
                      + "Merge the pull request first, so that the test workflow is on main (then this test needs no push and is expected to take a few minutes; UNMEASURED). "
                      + $"Until then this program waits up to {LocalCiDefaults.SelfTestTimeout.TotalMinutes:0} minutes for you to {PushAdvice}, and then judges that run of {LocalCiDefaults.PassiveWorkflow}: "
                      + $"it has several jobs that go through one runner one after the other, so it is given {LocalCiDefaults.PassiveRunBase.TotalMinutes:0} minutes plus {LocalCiDefaults.PassiveRunPerJob.TotalMinutes:0} minutes for each job.");
            runId = await WaitForPushedRunAsync(startedAt, startedStamp, cancellationToken).ConfigureAwait(false);
            if (runId is null)
            {
                return new SelfTestResult(
                    SelfTestOutcome.NotPickedUp,
                    $"No run of {LocalCiDefaults.PassiveWorkflow} started within {LocalCiDefaults.SelfTestTimeout.TotalMinutes:0} minutes, so nothing was measured. Merge the pull request first (then the test check starts its own workflow and needs no push), "
                    + $"or {PushAdvice}, and choose 3 again.");
            }
        }

        var limitStamp = passive ? _time.GetTimestamp() : startedStamp;
        var jobsSeen = 1;
        WorkflowRunInfo run;
        while (true)
        {
            run = await _api.GetWorkflowRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
            if (string.Equals(run.Status, "completed", StringComparison.Ordinal))
            {
                break;
            }

            if (passive)
            {
                // The run's own jobs set its allowance: every job needs the one runner in turn.
                jobsSeen = Math.Max(jobsSeen, (await _api.ListWorkflowJobsAsync(runId.Value, cancellationToken).ConfigureAwait(false)).Count);
            }

            var limit = passive ? LocalCiDefaults.PassiveRunLimit(jobsSeen) : LocalCiDefaults.SelfTestTimeout;
            if (_time.GetElapsedTime(limitStamp) >= limit)
            {
                if (!passive)
                {
                    await TryCancelAsync(runId.Value).ConfigureAwait(false);
                }

                return new SelfTestResult(
                    SelfTestOutcome.NotPickedUp,
                    passive
                        ? $"The run did not finish within {limit.TotalMinutes:0} minutes ({jobsSeen} {(jobsSeen == 1 ? "job" : "jobs")}, one runner). It was left running (it is your own CI run), so look at it on the Actions page: if its jobs are queued, check that 'DBD Local CI' is running (Status); if they ran on GitHub's machines, LINUX_RUNNER was not set."
                        : $"The test did not finish within {limit.TotalMinutes:0} minutes (it was cancelled). Check that 'DBD Local CI' is running (Status) and that the Linux machine is not blocked by the game or a restart.");
            }

            await _delay(s_pollEvery, cancellationToken).ConfigureAwait(false);
        }

        var jobs = await _api.ListWorkflowJobsAsync(runId.Value, cancellationToken).ConfigureAwait(false);
        return Judge(run, jobs, passive);
    }

    private static bool RanOnThisPc(WorkflowJobInfo job) => LocalCiDefaults.IsRunnerName(job.RunnerName) && (job.Labels is null || job.Labels.Contains(LocalCiDefaults.LinuxLabel, StringComparer.OrdinalIgnoreCase));

    private static bool AskedForThisPc(WorkflowJobInfo job) => job.Labels is not null && job.Labels.Contains(LocalCiDefaults.LinuxLabel, StringComparer.OrdinalIgnoreCase);

    private static bool NeverStarted(WorkflowJobInfo job) => job.StepCount == 0 && job.RunnerName is null;

    private static SelfTestResult Judge(WorkflowRunInfo run, IReadOnlyList<WorkflowJobInfo> jobs, bool passive)
    {
        // A cancelled run measured nothing: not a failure of the PC and not a payment block (its jobs usually never started because they were cancelled while they waited).
        if (string.Equals(run.Conclusion, "cancelled", StringComparison.Ordinal))
        {
            return new SelfTestResult(
                SelfTestOutcome.Cancelled,
                passive
                    ? $"The run of {LocalCiDefaults.PassiveWorkflow} was cancelled before it finished, so nothing was measured. CI (Linux) cancels the older run of a branch when a newer commit is pushed to it; if that was you, wait until your pushes are done and choose 3 again. "
                      + "Somebody may also have cancelled it on the Actions page."
                    : "The test run was cancelled before it finished, so nothing was measured. Somebody cancelled it on the Actions page (or a newer run took its place). Choose 3 again.");
        }

        var onPc = jobs.Count(RanOnThisPc);
        if (string.Equals(run.Conclusion, "success", StringComparison.Ordinal))
        {
            if (jobs.Count > 0 && onPc == jobs.Count)
            {
                return new SelfTestResult(
                    SelfTestOutcome.PassedOnThisPc,
                    passive ? $"All {jobs.Count} jobs of {LocalCiDefaults.PassiveWorkflow} ran on your PC and passed. The test ran on your PC and passed." : "The test ran on your PC and passed.");
            }

            if (onPc > 0)
            {
                return new SelfTestResult(
                    SelfTestOutcome.PassedOnGitHub,
                    $"The run passed, but only {onPc} of its {jobs.Count} jobs ran on your PC; the others ran on GitHub's machines. That is not what was asked for (LINUX_RUNNER must reach every Linux job).");
            }

            return new SelfTestResult(
                SelfTestOutcome.PassedOnGitHub,
                "The test passed, but on one of GitHub's machines, not yours. The repository variable LINUX_RUNNER may not be set or the loop is not running.");
        }

        if (jobs.Count > 0 && jobs.All(NeverStarted))
        {
            var asked = jobs.Any(AskedForThisPc);
            var where = asked
                ? "The test job asked for your PC (label dbd-linux) but did not start: no runner took it and no step ran. If the background task is running, GitHub itself stopped the job before it began."
                : "The test job did not start, and it asked for one of GitHub's own machines (LINUX_RUNNER is not set, so the job did not go to your PC): no runner took it and no step ran.";
            return new SelfTestResult(
                SelfTestOutcome.FailedToStart,
                where + " Open the run on the Actions page and read its message there: a payment or spending-limit block looks like this (GitHub does not let a token with Administration, Variables and Actions access read that message through its programming interface).");
        }

        return new SelfTestResult(
            SelfTestOutcome.Failed,
            onPc > 0 ? "The test ran on your PC and failed. The log is on the Actions page of the repository." : "The test failed on one of GitHub's machines. The log is on the Actions page of the repository.");
    }

    private async Task<long?> FindRunAsync(string workflow, string? eventName, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var runs = await _api.ListWorkflowRunsAsync(workflow, eventName, cancellationToken).ConfigureAwait(false);
            var mine = runs.Where(r => r.CreatedAt >= startedAt - TimeSpan.FromMinutes(1)).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            if (mine is not null)
            {
                return mine.Id;
            }

            await _delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Waits for a run of the passive workflow that started after the test did (the owner pushes a commit), up to the test's time limit.</summary>
    private async Task<long?> WaitForPushedRunAsync(DateTimeOffset startedAt, long startedStamp, CancellationToken cancellationToken)
    {
        while (true)
        {
            var runs = await _api.ListWorkflowRunsAsync(LocalCiDefaults.PassiveWorkflow, null, cancellationToken).ConfigureAwait(false);
            var mine = runs.Where(r => r.CreatedAt >= startedAt - TimeSpan.FromMinutes(1)).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            if (mine is not null)
            {
                return mine.Id;
            }

            if (_time.GetElapsedTime(startedStamp) >= LocalCiDefaults.SelfTestTimeout)
            {
                return null;
            }

            await _delay(s_pollEvery, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryCancelAsync(long runId)
    {
        try
        {
            await _api.CancelWorkflowRunAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GitHubApiException ex)
        {
            _log.Warn("Could not cancel the test run: " + _redactor.Describe(ex));
        }
    }
}
