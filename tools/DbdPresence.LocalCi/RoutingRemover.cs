namespace DbdPresence.LocalCi;

/// <summary>
/// Takes the repository variable that sends the Linux checks to this PC away again (local CI round 3, QA M-1; red-team H1), used by set-up when it stops for a reason that leaves nothing to serve checks with
/// and by the <c>probe</c> command when the owner does not accept what the probe found. A variable that holds this program's label is deleted; one with another value is the owner's and stays. Needs the saved token.
/// </summary>
public sealed class RoutingRemover
{
    private readonly IConsoleUi _ui;
    private readonly IGitHubApi _api;
    private readonly ISecretStore _secrets;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;

    public RoutingRemover(IConsoleUi ui, IGitHubApi api, ISecretStore secrets, ICiLog log, Redactor redactor)
    {
        _ui = ui;
        _api = api;
        _secrets = secrets;
        _log = log;
        _redactor = redactor;
    }

    /// <summary>Looks at each variable this program sets and deletes the ones that still hold its label; says what it did in plain words. Never throws for a GitHub failure.</summary>
    /// <param name="why">The reason, for the message ("the isolation probe was not accepted").</param>
    public async Task RemoveAsync(string why)
    {
        if (!_secrets.Exists())
        {
            _ui.WriteLine($"No GitHub token is saved, so I could not check whether the variable {string.Join(", ", LocalCiDefaults.Variables.Keys)} is set. If it is, delete it on GitHub by hand (Settings, Secrets and variables, Actions, Variables), or checks wait for this PC.");
            return;
        }

        foreach (var (name, label) in LocalCiDefaults.Variables)
        {
            try
            {
                var value = await _api.GetVariableAsync(name, CancellationToken.None).ConfigureAwait(false);
                if (value is null)
                {
                    continue;
                }

                if (!string.Equals(value, label, StringComparison.Ordinal))
                {
                    _ui.WriteLine($"{name} is set to '{value}', not to this program's '{label}', so it was left alone.");
                    continue;
                }

                await _api.DeleteVariableAsync(name, CancellationToken.None).ConfigureAwait(false);
                _ui.WriteLine($"I turned {name} off ({why}): checks run on GitHub's machines again and nothing waits for this PC.");
            }
            catch (GitHubApiException ex)
            {
                _log.Failure($"Could not look at or take away the variable {name}: {_redactor.Describe(ex)}");
                _ui.WriteLine($"I could not check or turn {name} off ({why}). Delete it on GitHub by hand now if it is set (Settings, Secrets and variables, Actions, Variables), or checks wait for this PC.");
            }
        }
    }
}

/// <summary>
/// The <c>probe</c> command (local CI round 3, red-team H1): runs the isolation probe again, for example after WSL was updated, shows what it found and asks for the owner's acceptance. When the result is not
/// accepted the variable that sends checks to this PC is taken away. The exit code is 0 only when the owner accepted.
/// </summary>
/// <remarks>
/// The probe starts a session of the account's Linux machine, which begins with <c>wsl --shutdown</c>, so it must never run next to the loop (local CI round 4, red-team F3, QA M-2): when the loop is running
/// the command asks GitHub whether a check is running on this PC and refuses if one is (or if it cannot be told); otherwise it stops the loop like set-up does, runs the probe and starts the loop again, however
/// the probe ends. The machine-wide session lock (<see cref="ISessionLock"/>) is the second guard: even a loop that was started in between cannot begin a session next to the probe.
/// </remarks>
public sealed class ProbeCommand
{
    private readonly ProbeReview _review;
    private readonly RoutingRemover _routing;
    private readonly IConsoleUi _ui;
    private readonly IScheduler _scheduler;
    private readonly IGitHubApi _api;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;

    public ProbeCommand(ProbeReview review, RoutingRemover routing, IConsoleUi ui, IScheduler scheduler, IGitHubApi api, ICiLog log, Redactor redactor)
    {
        _review = review;
        _routing = routing;
        _ui = ui;
        _scheduler = scheduler;
        _api = api;
        _log = log;
        _redactor = redactor;
    }

    /// <summary>True when the owner typed "yes" to what the probe found in the last run.</summary>
    public bool Accepted { get; private set; }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var loopStopped = false;
        if (_scheduler.LoopIsRunning())
        {
            if (await WhyTheLoopMustStayAsync(cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                _ui.WriteLine(refusal);
                return ExitCodes.Failed;
            }

            _ui.WriteLine("The background loop is running; stopping it for the time the probe takes (it starts again afterwards). A runner it had waiting at GitHub stays registered but offline; GitHub removes it by itself.");
            if (!await StopTheLoopAsync(cancellationToken).ConfigureAwait(false))
            {
                return ExitCodes.Failed;
            }

            loopStopped = true;
        }

        try
        {
            var outcome = await _review.RunAsync(cancellationToken).ConfigureAwait(false);
            Accepted = outcome == ProbeOutcome.Accepted;
            if (!Accepted)
            {
                await _routing.RemoveAsync(outcome switch
                {
                    ProbeOutcome.Declined => "you did not accept what the isolation probe found",
                    ProbeOutcome.Superseded => "another window of this program saved a newer probe result while you were reading this one",
                    _ => "the isolation probe could not give a result",
                }).ConfigureAwait(false);
            }
            else
            {
                _ui.WriteLine("Nothing was changed on GitHub or on this PC. If checks are not sent to this PC yet, run set-up (it skips what is done).");
            }
        }
        finally
        {
            if (loopStopped && !await StartTheLoopAgainAsync().ConfigureAwait(false))
            {
                // Whatever the owner decided, nothing serves the checks now.
                Accepted = false;
                await _routing.RemoveAsync("the background loop is not running").ConfigureAwait(false);
            }
        }

        return ExitCodes.ForProbe(Accepted);
    }

    /// <summary>Null when the loop may be stopped; otherwise the plain reason it may not (a check is running on this PC, or GitHub could not be asked), which ends the command before anything is changed.</summary>
    private async Task<string?> WhyTheLoopMustStayAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunnerInfo> runners;
        try
        {
            runners = await _api.ListRunnersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException ex)
        {
            _log.Failure($"The probe could not ask GitHub whether a check is running: {_redactor.Describe(ex)}");
            return "The background loop is running, and GitHub could not be asked whether a check is running on this PC right now (" + _redactor.Redact(ex.Message) + "). "
                   + "The probe shuts down the Linux machine, which would end a running check, so it was not started and nothing was changed. Try again in a minute, "
                   + "or stop the task yourself first (Task Scheduler, \"" + LocalCiDefaults.TaskName + "\", End) when you know that no check is running.";
        }

        return runners.Any(r => LocalCiDefaults.IsRunnerName(r.Name) && r.Busy)
            ? "A check is running on this PC right now (GitHub lists a runner of this program as busy). The probe shuts down the Linux machine, which would end that check, so it was not started and nothing was changed. "
              + "Try again when the check has finished (Status says \"busy\" while one runs)."
            : null;
    }

    private async Task<bool> StopTheLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _scheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Whatever stops the stop (Windows refuses, the task is gone) is shown in plain words; the probe is not started next to a loop that may still run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"The probe could not stop the background loop: {_redactor.Describe(ex)}");
            _ui.WriteLine("The background loop could not be stopped (" + _redactor.Redact(ex.Message) + "), so the probe was not started and nothing was changed. Stop it yourself (Task Scheduler, \"" + LocalCiDefaults.TaskName + "\", End) and run the probe again.");
            return false;
        }

        if (_scheduler.LoopIsRunning())
        {
            _ui.WriteLine("The background loop is still running, so the probe was not started and nothing was changed. Stop it (Task Scheduler, task \"" + LocalCiDefaults.TaskName + "\", End) and run the probe again.");
            return false;
        }

        return true;
    }

    /// <summary>Starts the loop that the probe stopped. False (and said in plain words) when it cannot be started.</summary>
    private async Task<bool> StartTheLoopAgainAsync()
    {
        try
        {
            await _scheduler.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever stops the restart is logged and answered by taking the routing away: the probe never leaves a variable pointing at a loop that is not running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"The probe could not start the background loop again: {_redactor.Describe(ex)}");
            _ui.WriteLine("The background loop that the probe stopped could not be started again (" + _redactor.Redact(ex.Message) + "). Start it yourself (Task Scheduler, \"" + LocalCiDefaults.TaskName + "\", Run) or sign out and in again.");
            return false;
        }

        _ui.WriteLine("The background loop that the probe stopped was started again.");
        return true;
    }
}
