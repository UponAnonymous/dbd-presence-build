namespace DbdPresence.LocalCi;

/// <summary>Jobs the loop does between runners: the token check, the daily runner download, the weekly base image.</summary>
public interface IMaintenance
{
    /// <param name="linuxIdle">True when no Linux environment is in use (the base image is only rebuilt then).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task RunDueAsync(bool linuxIdle, CancellationToken cancellationToken);

    string? LatestPackage();
}

/// <summary>
/// The maintenance of the loop. Downloads and the image build are gated like a job (never while the game runs: local CI round 2, QA M10) and each is time-boxed, so one wedged transfer or
/// build cannot stop the loop for ever (QA M5). The owner sees a notice once a day while the token is about to end or GitHub refuses it (QA M9); the log and the status always say it.
/// </summary>
public sealed class Maintenance : IMaintenance
{
    private static readonly TimeSpan s_retryAfterFailure = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_tokenCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_noticeInterval = TimeSpan.FromHours(24);

    private readonly RunnerCache _runners;
    private readonly BaseImageBuilder _image;
    private readonly IGitHubApi _api;
    private readonly StateStore _state;
    private readonly JobGate _gate;
    private readonly OwnerNotices _notices;
    private readonly TimeProvider _time;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly TimeSpan _tokenCheckLimit;
    private readonly TimeSpan _runnerDownloadLimit;
    private readonly TimeSpan _imageBuildLimit;
    private string? _lastGateReason;
    private readonly WeeklyWslUpdate? _wslUpdate;
    private readonly ProbeRenewal? _renewal;
    private readonly ProbeGate? _probeGate;
    private DateTimeOffset? _residueLoggedAt;
    private DateTimeOffset? _probeLoggedAt;
    private string? _probeLoggedFor;

    /// <param name="runners">The runner package cache.</param>
    /// <param name="image">The base image builder.</param>
    /// <param name="api">GitHub.</param>
    /// <param name="state">What the program remembers.</param>
    /// <param name="gate">Decides whether heavy work may run now.</param>
    /// <param name="notifier">Shows the owner a short notice.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The log.</param>
    /// <param name="redactor">Removes secrets from text.</param>
    /// <param name="tokenCheckLimit">How long one token check may take (default 2 minutes).</param>
    /// <param name="runnerDownloadLimit">How long the runner download may take (default 30 minutes).</param>
    /// <param name="imageBuildLimit">How long the image build may take (default 90 minutes).</param>
    /// <param name="wslUpdate">The weekly update of WSL, done before the weekly image build when nobody uses WSL; null when the program does not update WSL (tests, status).</param>
    /// <param name="renewal">Repeats the isolation probe by itself after the program changed WSL (round 4, F5); null when the program does not (tests).</param>
    /// <param name="probeGate">Says whether the accepted probe still holds, for the daily notice that no Linux check starts (round 4, F5); null when not asked (tests).</param>
    public Maintenance(
        RunnerCache runners,
        BaseImageBuilder image,
        IGitHubApi api,
        StateStore state,
        JobGate gate,
        INotifier notifier,
        TimeProvider time,
        ICiLog log,
        Redactor redactor,
        TimeSpan? tokenCheckLimit = null,
        TimeSpan? runnerDownloadLimit = null,
        TimeSpan? imageBuildLimit = null,
        WeeklyWslUpdate? wslUpdate = null,
        ProbeRenewal? renewal = null,
        ProbeGate? probeGate = null)
    {
        _wslUpdate = wslUpdate;
        _renewal = renewal;
        _probeGate = probeGate;
        _runners = runners;
        _image = image;
        _api = api;
        _state = state;
        _gate = gate;
        _notices = new OwnerNotices(notifier, state, time);
        _time = time;
        _log = log;
        _redactor = redactor;
        _tokenCheckLimit = tokenCheckLimit ?? TimeSpan.FromMinutes(2);
        _runnerDownloadLimit = runnerDownloadLimit ?? TimeSpan.FromMinutes(30);
        _imageBuildLimit = imageBuildLimit ?? TimeSpan.FromMinutes(90);
    }

    public string? LatestPackage() => _runners.LatestPackage();

    public async Task RunDueAsync(bool linuxIdle, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var state = _state.Load();
        var gate = _gate.Check();

        if (Due(state.LastTokenCheck, s_tokenCheckInterval, now))
        {
            await CheckTokenAsync(now, gate, cancellationToken).ConfigureAwait(false);
        }

        NoticeResidueOncePerDay(now, gate, _state.Load());
        NoticeProbeProblemOncePerDay(gate);

        var runnersMissing = _runners.LatestPackage() is null;
        var runnersDue = (runnersMissing || Due(state.LastRunnerCheck, LocalCiDefaults.RunnerCheckInterval, now)) && Due(state.LastRunnerAttempt, s_retryAfterFailure, now);
        var imageDue = linuxIdle && (!_image.Exists || Due(state.LastBaseImageBuild, LocalCiDefaults.BaseImageMaximumAge, now)) && Due(state.LastBaseImageAttempt, s_retryAfterFailure, now);
        var updateDue = linuxIdle && _wslUpdate is not null && _wslUpdate.Due(state);
        if (!runnersDue && !imageDue && !updateDue)
        {
            return;
        }

        if (!gate.Allowed)
        {
            SayGateOnce($"waiting with the downloads and the Linux image: {gate.Reason}");
            return;
        }

        _lastGateReason = null;
        if (runnersDue)
        {
            await RefreshRunnersAsync(now, cancellationToken).ConfigureAwait(false);
        }

        if (updateDue)
        {
            // Whenever the installer ran (it may have changed WSL even when it failed halfway), the program shows that the machine is still as closed as the owner accepted.
            if (await UpdateWslAsync(cancellationToken).ConfigureAwait(false) is WslUpdateResult.Updated or WslUpdateResult.Failed)
            {
                await RenewTheProbeAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (imageDue)
        {
            await BuildImageAsync(now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The weekly WSL update: before the image build of the same round, so the image is built on the WSL it will run on. It never keeps the build or a runner from starting. It has one time limit, the one of
    /// <see cref="LimitedWslInstaller"/> inside the update; a second limit around it would end the update before the first one could be noted as an attempt (red-team round 4, F2).
    /// </summary>
    private async Task<WslUpdateResult?> UpdateWslAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _wslUpdate!.RunAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The update reports its own failures; whatever else stops it (a state file that cannot be written) is logged here and never keeps the round's other work from running.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Failure("The WSL update stopped: " + _redactor.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// After the program's own WSL update: the isolation probe is repeated by the program and compared with what the owner accepted (round 4, F5). A failure of the repeat itself is logged and never keeps the
    /// round's other work from running; the gate in front of every runner sees the changed WSL version and refuses until the owner has looked.
    /// </summary>
    private async Task RenewTheProbeAsync(CancellationToken cancellationToken)
    {
        if (_renewal is null)
        {
            return;
        }

        try
        {
            _ = await _renewal.AfterWslUpdateAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever stops the repeat (a state file that cannot be written, a bug) is logged; no runner starts on a changed WSL anyway.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Failure("Repeating the isolation probe after the WSL update stopped: " + _redactor.Describe(ex));
        }
    }

    /// <summary>
    /// When the accepted isolation probe no longer holds (WSL or the setup changed since, or it was never accepted) no Linux check starts on this PC; the owner is told once a day, never while the game runs
    /// (round 4, F5). The log and the status say it every time.
    /// </summary>
    private void NoticeProbeProblemOncePerDay(GateDecision gate)
    {
        if (_probeGate?.Problem() is not { } problem)
        {
            return;
        }

        var text = "DBD Local CI sends no Linux check to this PC: " + problem + ". Until then checks that were sent here wait for a runner nobody starts (turn LINUX_RUNNER off on GitHub, or run the probe and decide).";
        var now = _time.GetUtcNow();
        if (!string.Equals(_probeLoggedFor, problem, StringComparison.Ordinal) || _probeLoggedAt is null || now - _probeLoggedAt.Value >= s_noticeInterval)
        {
            _probeLoggedFor = problem;
            _probeLoggedAt = now;
            _log.Warn(text);
        }

        _notices.OncePerDay(NoticeTopic.ProbeInvalid, text, gate.Allowed);
    }

    /// <summary>Due when never done, when the interval has passed, or when the recorded time is in the future (the PC's clock was set back, or the file is not to be believed): waiting for the clock to catch up could take days.</summary>
    private static bool Due(DateTimeOffset? last, TimeSpan interval, DateTimeOffset now) =>
        last is null || last.Value > now || now - last.Value >= interval;

    private void SayGateOnce(string message)
    {
        if (string.Equals(_lastGateReason, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastGateReason = message;
        _log.Info(message);
    }

    /// <summary>Runs <paramref name="work"/> with a time limit. Returns true when it ended inside the limit; false (logged) when the limit stopped it. A stop of the loop itself is passed on.</summary>
    private async Task<bool> BoxedAsync(string what, TimeSpan limit, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        using var box = new CancellationTokenSource(limit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, box.Token);
        try
        {
            await work(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && box.IsCancellationRequested)
        {
            _log.Failure($"{what} took longer than {limit.TotalMinutes:0} minutes and was stopped; it is tried again later.");
            return false;
        }
    }

    private async Task CheckTokenAsync(DateTimeOffset now, GateDecision gate, CancellationToken cancellationToken)
    {
        RepoInfo? repo = null;
        Exception? failure = null;
        var finished = await BoxedAsync(
            "The token check",
            _tokenCheckLimit,
            async ct =>
            {
                try
                {
                    repo = await _api.GetRepositoryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Whatever the check hit (a refusal, a network failure, a damaged answer) is logged below; a failing check never stops the loop.
                    failure = ex;
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!finished)
        {
            _state.Update(s => s.LastTokenCheck = now);
            return;
        }

        if (repo is not null)
        {
            _state.Update(s =>
            {
                s.LastTokenCheck = now;
                s.TokenExpiresAt = repo.TokenExpiresAt ?? s.TokenExpiresAt;
                s.RepositoryPrivate = repo.IsPrivate;
                s.RepositoryCheckedAt = now;
            });
            if (repo.TokenExpiresAt is { } expires && expires - now <= LocalCiDefaults.TokenWarning)
            {
                var text = $"The GitHub token ends on {expires:yyyy-MM-dd}. Make a new token and choose 1 (Set up) in DBD Local CI before then; after that day GitHub refuses it and CI runs wait for a runner nobody serves.";
                _log.Warn(text);
                _notices.OncePerDay(NoticeTopic.Token, text, gate.Allowed);
            }

            if (repo.IsPrivate != true)
            {
                var text = RepositoryGuard.Judge(repo.IsPrivate).Reason;
                _log.Warn(text);
                _notices.OncePerDay(NoticeTopic.PublicRepository, text, gate.Allowed);
            }

            return;
        }

        _state.Update(s => s.LastTokenCheck = now);
        _log.Failure("The GitHub token check failed: " + _redactor.Describe(failure!));
        if (failure is GitHubApiException { IsClassicToken: true })
        {
            _notices.OncePerDay(NoticeTopic.Token, "GitHub says the saved token is a classic token, which reaches every repository of your account, so DBD Local CI refuses it and starts no runner. Make a fine-grained token (starts with github_pat_) and choose 1 (Set up) in DBD Local CI.", gate.Allowed);
        }
        else if (failure is GitHubApiException { IsAuthenticationFailure: true })
        {
            _notices.OncePerDay(NoticeTopic.Token, "GitHub refuses the saved token (it ended, was revoked or lacks a permission), so CI runs that were sent to this PC wait for a runner nobody serves. Make a new token and choose 1 (Set up) in DBD Local CI.", gate.Allowed);
        }
    }

    /// <summary>
    /// What a clean-up could not remove (a Linux machine that would not unregister, a folder that is not empty) stays in the state file and in the status, and the owner also gets a notice once a day
    /// (local CI round 2, red-team L2), never while the game runs.
    /// </summary>
    private void NoticeResidueOncePerDay(DateTimeOffset now, GateDecision gate, CiState state)
    {
        if (state.ResidueItems.Count == 0)
        {
            return;
        }

        var text = "DBD Local CI could not remove everything after a Linux job: " + string.Join("; ", state.Residue) + ". The loop tries again at its next start; if it stays, see docs/owner/free-ci.md, \"If something is left behind\".";
        if (_residueLoggedAt is null || now - _residueLoggedAt.Value >= s_noticeInterval)
        {
            _residueLoggedAt = now;
            _log.Warn(text);
        }

        _notices.OncePerDay(NoticeTopic.Residue, text, gate.Allowed);
    }

    private async Task RefreshRunnersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _state.Update(s => s.LastRunnerAttempt = now);
        try
        {
            string? version = null;
            if (await BoxedAsync("The runner download", _runnerDownloadLimit, async ct => version = await _runners.RefreshAsync(ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
            {
                _state.Update(s => s.LastRunnerCheck = now);
                _log.Info($"Runner packages are up to date (version {version}).");
            }
        }
#pragma warning disable CA1031 // Whatever stops the refresh (a refusal, a damaged answer, a full disk, a bug) is logged and tried again after the pause; it never keeps a runner from starting in the same round.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Failure("Could not update the runner packages: " + _redactor.Describe(ex));
        }
    }

    private async Task BuildImageAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _state.Update(s => s.LastBaseImageAttempt = now);
        try
        {
            if (await BoxedAsync("The Linux image build", _imageBuildLimit, ct => _image.BuildAsync(ct), cancellationToken).ConfigureAwait(false))
            {
                _state.Update(s =>
                {
                    s.LastBaseImageBuild = now;
                    s.BaseImageRecipe = _image.RecipeId;
                });
            }
        }
#pragma warning disable CA1031 // Whatever stops the build (a timeout of a wsl.exe command, a program that cannot be started as the account, a failed download, a bug) is logged and tried again after the pause; it never keeps a runner from starting in the same round.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Failure("Could not build the Linux base image: " + _redactor.Describe(ex));
        }
    }
}
