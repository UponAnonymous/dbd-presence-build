namespace DbdPresence.LocalCi;

/// <summary>
/// Decides whether the isolation probe the owner accepted still holds for the Linux machine as it is now (local CI round 4, red-team F5, QA M-3). The probe is evidence about one setup (the Linux machine
/// settings and the base image recipe) on one version of WSL; a result for another setup or another WSL says nothing about this one. So no Linux check starts unless the last probe was accepted, for the
/// setup that is current and for the WSL that is installed now. Anything that cannot be shown is a "no": a probe that was never run, one that was not accepted, a WSL version that is not known.
/// </summary>
public static class ProbeValidity
{
    /// <summary>Null when the accepted probe holds; otherwise the plain-words reason no Linux check starts on this PC.</summary>
    /// <param name="state">What the program remembers: the last probe, and the WSL version read at the last use of the Linux machine.</param>
    /// <param name="currentRecipe">Names the setup a probe result would be about today.</param>
    public static string? Problem(CiState state, string currentRecipe)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(currentRecipe);
        if (state.Probe is not { } probe)
        {
            return "the isolation probe has not been run on this PC yet (choose 5 in DBD Local CI)";
        }

        if (!probe.Accepted)
        {
            return "you have not accepted the result of the isolation probe (choose 5 in DBD Local CI and decide)";
        }

        if (!string.Equals(probe.Recipe, currentRecipe, StringComparison.Ordinal))
        {
            return "the isolation probe was run for an older setup of the Linux machine (its settings or the base image recipe changed since); run it again (choose 5 in DBD Local CI)";
        }

        if (string.IsNullOrWhiteSpace(probe.WslVersion) || string.IsNullOrWhiteSpace(state.WslVersionText))
        {
            return "the WSL version is not known (the probe's, or the one installed now), so it cannot be shown that the isolation probe you accepted still holds; run it again (choose 5 in DBD Local CI)";
        }

        return string.Equals(probe.WslVersion.Trim(), state.WslVersionText.Trim(), StringComparison.Ordinal)
            ? null
            : $"WSL changed since the isolation probe you accepted ({FirstLine(probe.WslVersion)} then, {FirstLine(state.WslVersionText)} now); run the probe again (choose 5 in DBD Local CI)";
    }

    /// <summary>The first line of a <c>wsl --version</c> text, such as "WSL version: 2.4.8.0".</summary>
    public static string FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "unknown" : text.Split('\n', 2, StringSplitOptions.TrimEntries)[0];
}

/// <summary>The probe check the job host makes before a runner starts (and again once the session of a job has refreshed the WSL version), over the state file and the current setup.</summary>
public sealed class ProbeGate
{
    private readonly StateStore _state;
    private readonly Func<string> _currentRecipe;

    /// <param name="state">Where the last probe and the WSL version are kept.</param>
    /// <param name="currentRecipe">Names the setup a probe result would be about today (<see cref="IsolationProbe.RecipeIdFor"/>).</param>
    public ProbeGate(StateStore state, Func<string> currentRecipe)
    {
        _state = state;
        _currentRecipe = currentRecipe;
    }

    /// <summary>Null when a Linux check may start; otherwise why not.</summary>
    public string? Problem() => ProbeValidity.Problem(_state.Load(), _currentRecipe());
}

/// <summary>How a probe repeated by the program is compared with the result the owner accepted.</summary>
public static class ProbeComparison
{
    /// <summary>
    /// The attempts that are worse now than in the accepted result, each in a sentence: blocked is better than not tried is better than possible, and an attempt the accepted result does not have must be
    /// blocked. Empty means nothing got worse (every attempt that was blocked still is; what the owner knowingly accepted as open or untried is no worse).
    /// </summary>
    public static IReadOnlyList<string> WorseThanAccepted(ProbeRecord accepted, IReadOnlyList<ProbeAttempt> now)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(now);
        var worse = new List<string>();
        foreach (var attempt in now)
        {
            var before = accepted.Attempts.FirstOrDefault(a => string.Equals(a.Id, attempt.Id, StringComparison.Ordinal));
            var then = before is null ? 0 : Weight(before.Verdict);
            if (Weight(attempt.Verdict.ToString()) > then)
            {
                worse.Add($"{ProbeText.What(attempt.Id)}: {Say(then)} when you accepted it, {Say(Weight(attempt.Verdict.ToString()))} now ({attempt.Evidence})");
            }
        }

        return worse;
    }

    private static int Weight(string verdict) => verdict switch
    {
        nameof(ProbeVerdict.Blocked) => 0,
        nameof(ProbeVerdict.NotTried) => 1,
        _ => 2,
    };

    private static string Say(int weight) => weight switch
    {
        0 => "BLOCKED",
        1 => "NOT TRIED",
        _ => "POSSIBLE",
    };
}

/// <summary>Shows what a command would tell the owner as lines of the log, for the parts of the program that run in the background and have no console. It never asks anything: every question is a "no".</summary>
public sealed class LogConsoleUi : IConsoleUi
{
    private readonly ICiLog _log;

    public LogConsoleUi(ICiLog log)
    {
        _log = log;
    }

    public void WriteLine(string text) => _log.Info(text);

    public string? ReadLine() => null;

    public string? ReadSecret(string prompt) => null;

    public bool Confirm(string question) => false;
}

/// <summary>What came of repeating the probe after the program's own WSL update.</summary>
public enum RenewalResult
{
    /// <summary>There was no accepted probe for the current setup to renew (the gate already refuses); nothing was done.</summary>
    NothingToRenew,

    /// <summary>WSL's version did not change, so the accepted probe still holds and was not repeated.</summary>
    StillValid,

    /// <summary>The probe was repeated and nothing got worse: the acceptance stays, with the new time and version.</summary>
    Renewed,

    /// <summary>The probe was repeated and something is worse, or it could not run: the acceptance was withdrawn, the variable that sends checks here was deleted and the owner has a notice.</summary>
    Withdrawn,
}

/// <summary>
/// After the program updated WSL itself it repeats the isolation probe by itself, without the owner (local CI round 4, red-team F5, QA M-3): the update is the one change of the Linux machine's surroundings
/// that the program makes on its own authority, so it must also show that the machine is still as closed as the owner accepted. When nothing is worse the acceptance carries over (and says so: the count and
/// time of such renewals are kept); otherwise the acceptance is withdrawn, the variable that sends checks here is deleted so jobs go back to GitHub's machines, no runner starts (the gate sees the withdrawn
/// acceptance) and the owner gets a notice once a day. A probe that could not run counts as worse: fail closed.
/// </summary>
public sealed class ProbeRenewal
{
    private readonly IsolationProbe _probe;
    private readonly IWsl _wsl;
    private readonly StateStore _state;
    private readonly Func<string> _currentRecipe;
    private readonly RoutingRemover _routing;
    private readonly OwnerNotices _notices;
    private readonly JobGate _gate;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly TimeProvider _time;

    public ProbeRenewal(IsolationProbe probe, IWsl wsl, StateStore state, Func<string> currentRecipe, RoutingRemover routing, OwnerNotices notices, JobGate gate, ICiLog log, Redactor redactor, TimeProvider time)
    {
        _probe = probe;
        _wsl = wsl;
        _state = state;
        _currentRecipe = currentRecipe;
        _routing = routing;
        _notices = notices;
        _gate = gate;
        _log = log;
        _redactor = redactor;
        _time = time;
    }

    public async Task<RenewalResult> AfterWslUpdateAsync(CancellationToken cancellationToken)
    {
        var before = _state.Load().Probe;
        if (before is not { Accepted: true } || !string.Equals(before.Recipe, _currentRecipe(), StringComparison.Ordinal))
        {
            _log.Info("The isolation probe is not repeated after the WSL update: there is no accepted result for the current setup to renew (no Linux check starts until you run the probe, choose 5).");
            return RenewalResult.NothingToRenew;
        }

        var installed = await AskVersionAsync(cancellationToken).ConfigureAwait(false);
        if (installed is not null && string.Equals(installed.Trim(), before.WslVersion?.Trim(), StringComparison.Ordinal))
        {
            _log.Info("WSL has the same version as when you accepted the isolation probe, so the probe is not repeated.");
            return RenewalResult.StillValid;
        }

        _log.Info($"WSL changed ({ProbeValidity.FirstLine(before.WslVersion)} then, {ProbeValidity.FirstLine(installed)} now). Repeating the isolation probe by itself, to see whether anything got worse than what you accepted.");
        var report = await _probe.RunAttemptsOnlyAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> worse = report.Failure is not null ? [report.Failure] : ProbeComparison.WorseThanAccepted(before, report.Attempts);
        var now = _time.GetUtcNow();
        if (worse.Count == 0)
        {
            var kept = Save(before, report, accepted: true, now);
            _log.Info($"The isolation probe, repeated after the WSL update, gave a result no worse than the one you accepted ({kept}); Linux checks stay on this PC.");
            return RenewalResult.Renewed;
        }

        _ = Save(before, report, accepted: false, now);
        var why = string.Join("; ", worse);
        _log.Failure($"The isolation probe, repeated after the WSL update, is worse than what you accepted, so the acceptance is withdrawn: {why}");
        await _routing.RemoveAsync("the isolation probe, repeated after the WSL update, is worse than what you accepted").ConfigureAwait(false);
        _notices.OncePerDay(
            NoticeTopic.ProbeInvalid,
            "DBD Local CI updated WSL and repeated the isolation probe by itself: " + why + ". It turned LINUX_RUNNER off, so checks run on GitHub's machines again, and it starts no Linux check on this PC. Choose 5 (Isolation probe) in DBD Local CI to look at it and decide; choose 1 (Set up) to turn the checks on again.",
            _gate.Check().Allowed);
        return RenewalResult.Withdrawn;
    }

    private async Task<string?> AskVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _wsl.VersionAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A version that cannot be read is an unknown version: the probe is then repeated, never skipped.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Warn($"Could not read the WSL version after the update: {_redactor.Describe(ex)}");
            return null;
        }
    }

    /// <summary>
    /// Keeps the result as the new last probe. A result that is no worse keeps the owner's acceptance (and its time), the audit of the result the owner looked at, and counts the renewal; a result that is worse
    /// is kept as it is, not accepted, so the status tells the truth.
    /// </summary>
    private string Save(ProbeRecord before, ProbeReport report, bool accepted, DateTimeOffset now)
    {
        // The word of the kept result: the attempts are today's, the audit is the one the owner looked at.
        var outcome = report.Failure is not null ? "Incomplete" : report.AnythingPossible ? "SomePossible" : report.AttemptsAllBlocked ? (before.AuditGapCount == 0 ? "AllBlocked" : "AuditIncomplete") : "Incomplete";
        _state.Update(s =>
        {
            s.Probe = new ProbeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                At = now,
                Outcome = outcome,
                Attempts = [.. report.Attempts.Select(a => new ProbeAttemptRecord { Id = a.Id, Verdict = a.Verdict.ToString(), Evidence = a.Evidence })],
                WritableLocations = [.. before.WritableLocations],
                AuditPlacesLooked = before.AuditPlacesLooked,
                AuditGapCount = before.AuditGapCount,
                AuditGaps = [.. before.AuditGaps],
                Recipe = before.Recipe,
                WslVersion = s.WslVersionText,
                Accepted = accepted,
                AcceptedAt = accepted ? before.AcceptedAt : null,
                Renewals = accepted ? before.Renewals + 1 : 0,
                LastRenewedAt = accepted ? now : null,
            };
        });
        return outcome;
    }
}
