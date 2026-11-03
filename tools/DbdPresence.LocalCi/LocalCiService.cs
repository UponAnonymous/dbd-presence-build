using System.Globalization;

namespace DbdPresence.LocalCi;

/// <summary>
/// The loop (<c>serve</c>): keeps at most one just-in-time Linux runner waiting, in its own throwaway environment, and starts it only when the gate allows it (FR-CI-02, FR-CI-03). A runner that
/// has not taken a job is stopped when the gate closes (the game starts) or after 12 hours. A job that has started is never interrupted by the gate; it is stopped only after 120 minutes.
/// Decision D41: only Linux jobs run on the owner's PC, so there is one lane.
/// </summary>
/// <remarks>
/// The lane is freed only when its environment is gone: whatever GitHub answers (or fails to answer) while a job runs, the poll never ends the slot and never starts a second runner next to the
/// first (local CI round 2, QA B1). A failure of the poll is logged and the next answer is waited for longer; a failure of the runner or its registration makes the lane wait longer each time it repeats.
/// </remarks>
public sealed class LocalCiService
{
    private static readonly TimeSpan s_firstBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_longestBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_longestPollWait = TimeSpan.FromMinutes(5);

    /// <summary>A runner that waits for a job asks GitHub again whether the repository is still private this often.</summary>
    private static readonly TimeSpan s_privacyRecheck = TimeSpan.FromMinutes(5);

    private readonly IGitHubApi _api;
    private readonly IJobHost _host;
    private readonly IMaintenance _maintenance;
    private readonly JobGate _gate;
    private readonly StateStore _state;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly TimeProvider _time;
    private readonly Retry _retry;
    private readonly RepositoryGuard _repository;
    private readonly OwnerNotices? _notices;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gateLock = new();
    private Slot? _slot;
    private long? _backoffStamp;
    private TimeSpan _backoffLength;
    private int _failureStreak;
    private string? _lastBlockedReason;

    public LocalCiService(
        IGitHubApi api,
        IJobHost host,
        IMaintenance maintenance,
        JobGate gate,
        StateStore state,
        ICiLog log,
        Redactor redactor,
        TimeProvider time,
        Retry retry,
        RepositoryGuard repository,
        OwnerNotices? notices = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _host = host;
        _maintenance = maintenance;
        _gate = gate;
        _state = state;
        _log = log;
        _redactor = redactor;
        _time = time;
        _retry = retry;
        _repository = repository;
        _notices = notices;
        _delay = delay ?? ((span, token) => Task.Delay(span, time, token));
    }

    /// <summary>Runs until cancelled. The environment that is in use is cleaned up before this returns.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _host.CleanupLeftoversAsync(CancellationToken.None).ConfigureAwait(false);

        _log.Info("The loop has started.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // hygiene: allow-swallow stopping the loop is the normal way it ends
                    break;
                }
#pragma warning disable CA1031 // The loop must survive any single failure; it is logged and the next round tries again.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log.Failure("A round of the loop failed: " + _redactor.Describe(ex));
                }

                try
                {
                    await _delay(LocalCiDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // hygiene: allow-swallow stopping the loop is the normal way it ends
                    break;
                }
            }
        }
        finally
        {
            await WhenIdleAsync().ConfigureAwait(false);
            _log.Info("The loop has stopped.");
        }
    }

    /// <summary>One round: maintenance, then start a runner when there is none and the gate allows one. Maintenance that throws never keeps the runner from starting (local CI round 3, QA minor).</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        Reap();
        bool idle;
        lock (_gateLock)
        {
            idle = _slot is null;
        }

        try
        {
            await _maintenance.RunDueAsync(idle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The maintenance (token check, downloads, image build) has its own handling; whatever still escapes (a state file that cannot be written, a bug) is logged and the round goes on to the runner.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure("The maintenance of this round failed: " + _redactor.Describe(ex));
        }

        TryStart(cancellationToken);
    }

    /// <summary>Completes when no runner is running or waiting.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task? running;
            lock (_gateLock)
            {
                running = _slot?.Task;
            }

            if (running is null)
            {
                return;
            }

            // Waits for the end of the slot without rethrowing its failure: Reap logs a task that ended in an unexpected error, and the loop's shutdown must go on.
            await Task.WhenAny(running).ConfigureAwait(false);
            Reap();
        }
    }

    /// <summary>True while the runner is waiting or working.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gateLock)
            {
                return _slot is not null;
            }
        }
    }

    private void TryStart(CancellationToken serviceToken)
    {
        lock (_gateLock)
        {
            if (_slot is not null || (_backoffStamp is { } stamp && _time.GetElapsedTime(stamp) < _backoffLength))
            {
                return;
            }
        }

        var package = _maintenance.LatestPackage();
        var notReady = package is null ? "the runner program has not been downloaded yet" : _host.NotReadyReason();
        if (notReady is not null)
        {
            SayOnce($"not starting a Linux runner: {notReady}");
            return;
        }

        var decision = _gate.Check();
        if (!decision.Allowed)
        {
            SayOnce($"not starting a Linux runner: {decision.Reason}");
            return;
        }

        lock (_gateLock)
        {
            _lastBlockedReason = null;
            var slot = new Slot();
            slot.Task = Task.Run(() => RunSlotAsync(slot, package!, serviceToken), CancellationToken.None);
            _slot = slot;
        }
    }

    private void SayOnce(string message)
    {
        lock (_gateLock)
        {
            if (string.Equals(_lastBlockedReason, message, StringComparison.Ordinal))
            {
                return;
            }

            _lastBlockedReason = message;
        }

        _log.Info(message);
    }

    /// <summary>Frees the lane once its task is done. A task that ended with an exception nobody expected is logged (it is never dropped silently).</summary>
    private void Reap()
    {
        Slot? finished = null;
        lock (_gateLock)
        {
            if (_slot is { Task.IsCompleted: true } slot)
            {
                finished = slot;
                _slot = null;
            }
        }

        if (finished is { Task.IsFaulted: true })
        {
            var failure = finished.Task.Exception?.GetBaseException();
            _log.Failure("The Linux runner's task ended with an unexpected error: " + (failure is null ? "no details" : _redactor.Describe(failure)));
            RecordFailure();
        }

        // The runner configuration is forgotten only now, after everything that could say something about the slot (even an unexpected failure) was cleaned.
        finished?.ForgetSecret();
    }

    private async Task RunSlotAsync(Slot slot, string package, CancellationToken serviceToken)
    {
        var startedStamp = _time.GetTimestamp();
        var name = LocalCiDefaults.RunnerName(_time.GetUtcNow());

        // Only a private repository is served: asked every time a runner is about to be registered (local CI round 3, red-team M5). Anything but a clear "private" starts nothing.
        PrivacyVerdict verdict;
        try
        {
            verdict = await _repository.AskAsync(serviceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
        {
            // hygiene: allow-swallow the loop is stopping, so no runner is wanted
            return;
        }

        if (!verdict.MayServe)
        {
            _log.Warn("Not starting a Linux runner: " + verdict.Reason);
            if (verdict.Privacy == RepositoryPrivacy.NotPrivate)
            {
                _notices?.OncePerDay(NoticeTopic.PublicRepository, verdict.Reason, _gate.Check().Allowed);
            }

            RecordFailure();
            return;
        }

        JitRunner jit;
        try
        {
            jit = await _retry.RunAsync(
                ct => _api.GenerateJitConfigAsync(name, ["self-hosted", LocalCiDefaults.LinuxLabel], ct),
                "Registering the Linux runner",
                serviceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
        {
            // hygiene: allow-swallow the loop is stopping, so no runner is wanted
            return;
        }
#pragma warning disable CA1031 // Whatever the registration threw (a refusal, a damaged answer, a bug) is logged and the lane waits: it must never crash this task silently or retry at full speed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"Could not register a Linux runner: {_redactor.Describe(ex)}");
            RecordFailure();
            return;
        }

        slot.Secret = _redactor.RegisterScoped(jit.EncodedConfig);
        _log.Info($"Linux runner {name} is starting.");
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        var hostTask = Task.Run(() => _host.RunAsync(new RunnerLaunch(name, jit.EncodedConfig, package), runCts.Token), CancellationToken.None);

        long? busyStamp = null;
        long privacyAskedStamp = _time.GetTimestamp();
        string? stopReason = null;
        var pollFailures = 0;
        try
        {
            while (!hostTask.IsCompleted)
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
                var poll = _delay(PollWait(pollFailures), pollCts.Token);
                await Task.WhenAny(hostTask, poll).ConfigureAwait(false);
                await pollCts.CancelAsync().ConfigureAwait(false);
                if (hostTask.IsCompleted)
                {
                    break;
                }

                if (serviceToken.IsCancellationRequested)
                {
                    // The host sees the same token and is ending; the slot stays occupied until it has.
                    await Task.WhenAny(hostTask).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var busy = await IsBusyAsync(name, serviceToken).ConfigureAwait(false);
                    pollFailures = busy is null ? pollFailures + 1 : 0;
                    if (busy == true && busyStamp is null)
                    {
                        busyStamp = _time.GetTimestamp();
                        _log.Info($"Linux runner {name} has taken a job.");
                    }

                    if (stopReason is null)
                    {
                        if (busyStamp is { } since)
                        {
                            // Judged by the monotonic clock alone: a token that ended in the middle of the job must not let it run for ever, and a jump of the PC's clock must not end it early or late.
                            if (_time.GetElapsedTime(since) > LocalCiDefaults.JobTimeout)
                            {
                                stopReason = $"stopped after {LocalCiDefaults.JobTimeout.TotalMinutes:0} minutes";
                                _log.Warn($"Linux runner {name}: the job ran longer than {LocalCiDefaults.JobTimeout.TotalMinutes:0} minutes and is being stopped.");
                                await runCts.CancelAsync().ConfigureAwait(false);
                            }
                        }
                        else if (busy == false)
                        {
                            if (_time.GetElapsedTime(privacyAskedStamp) >= s_privacyRecheck)
                            {
                                // A runner that waits for a job must not outlive the repository being private: a public repository lets anybody run code on it.
                                privacyAskedStamp = _time.GetTimestamp();
                                var again = await _repository.AskAsync(serviceToken).ConfigureAwait(false);
                                if (again.Privacy == RepositoryPrivacy.NotPrivate)
                                {
                                    stopReason = "recycled: the repository is not private";
                                    _log.Warn($"Linux runner {name} is being stopped before it took a job: {again.Reason}");
                                    _notices?.OncePerDay(NoticeTopic.PublicRepository, again.Reason, _gate.Check().Allowed);
                                    await runCts.CancelAsync().ConfigureAwait(false);
                                    continue;
                                }
                            }

                            if (_time.GetElapsedTime(startedStamp) > LocalCiDefaults.MaximumWait)
                            {
                                stopReason = "recycled";
                                _log.Info($"Linux runner {name} waited a long time without a job and is being replaced by a fresh one.");
                                await runCts.CancelAsync().ConfigureAwait(false);
                            }
                            else if (_gate.Check() is { Allowed: false } closed)
                            {
                                stopReason = "recycled";
                                _log.Info($"Linux runner {name} is being stopped before it took a job: {closed.Reason}.");
                                await runCts.CancelAsync().ConfigureAwait(false);
                            }
                        }
                        else if (_time.GetElapsedTime(startedStamp) > LocalCiDefaults.MaximumWait + LocalCiDefaults.JobTimeout)
                        {
                            // GitHub could not be asked for the longest time a runner may wait plus the longest a job may run: whatever this environment is doing, it is ended.
                            stopReason = "stopped: GitHub could not be asked for 14 hours";
                            _log.Warn($"Linux runner {name} is being stopped: GitHub could not be asked about it for 14 hours.");
                            await runCts.CancelAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                {
                    // hygiene: allow-swallow the loop is stopping; the next turn of the loop waits for the host
                }
#pragma warning disable CA1031 // Whatever the poll hit (a damaged answer, a failing process list, a bug), the running job is left alone: it is logged and the next poll waits longer.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    pollFailures++;
                    _log.Warn($"Checking on Linux runner {name} failed; its job is left alone: {_redactor.Describe(ex)}");
                }
            }
        }
        finally
        {
            // The lane is freed only when the environment is gone: a failure here must not leave a job running unobserved while a second runner starts next to it.
            if (!hostTask.IsCompleted)
            {
                await runCts.CancelAsync().ConfigureAwait(false);
            }

            await Task.WhenAny(hostTask).ConfigureAwait(false);
        }

        int? exitCode = null;
        string? failure = null;
        try
        {
            exitCode = await hostTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // hygiene: allow-swallow stopped on purpose (service stop, timeout, gate); the host has cleaned up and the reason is in the log
            stopReason ??= "stopped";
        }
#pragma warning disable CA1031 // Whatever the environment threw is recorded in the job history and the log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = _redactor.Describe(ex);
            _log.Failure($"Linux runner {name} failed: {failure}");
            if (ex is LowDiskException)
            {
                // The drive that holds the Linux machines nearly ran out of room (red-team round 3, M1): the owner is told once a day, never while the game runs.
                _notices?.OncePerDay(NoticeTopic.DiskFull, ex.Message, _gate.Check().Allowed);
            }
        }

        if (busyStamp is null && (stopReason is not null || failure is not null || exitCode is not (null or 0)))
        {
            await ForgetRunnerAsync(jit).ConfigureAwait(false);
        }

        var tookJob = busyStamp is not null || (exitCode == 0 && stopReason is null && failure is null);
        if (tookJob || failure is not null)
        {
            var result = failure is not null ? "error: " + failure
                : stopReason is not null ? stopReason
                : exitCode == 0 ? "job finished"
                : string.Create(CultureInfo.InvariantCulture, $"runner ended with exit code {exitCode}");
            _state.Update(s => s.Jobs.Add(new JobRecord { At = _time.GetUtcNow(), Os = "Linux", Result = result }));
        }

        if (failure is not null || (exitCode is not null and not 0 && stopReason is null))
        {
            RecordFailure();
        }
        else if (exitCode == 0 || stopReason is not null)
        {
            lock (_gateLock)
            {
                _failureStreak = 0;
            }
        }

        _log.Info($"Linux runner {name} is gone and its environment has been removed.");
    }

    /// <summary>How long to wait before asking GitHub about the runner again: the normal interval, or longer after answers that could not be read (never more than five minutes).</summary>
    private static TimeSpan PollWait(int failures)
    {
        if (failures <= 0)
        {
            return LocalCiDefaults.PollInterval;
        }

        var wait = TimeSpan.FromTicks(LocalCiDefaults.PollInterval.Ticks << Math.Min(failures, 4));
        return wait > s_longestPollWait ? s_longestPollWait : wait;
    }

    /// <summary>The lane waits one minute after a failure, and twice as long each time it repeats, up to 30 minutes: a broken environment must not register a new runner every minute for ever.</summary>
    private void RecordFailure()
    {
        lock (_gateLock)
        {
            _failureStreak++;
            var wait = TimeSpan.FromTicks(s_firstBackoff.Ticks << Math.Min(_failureStreak - 1, 5));

            // Measured on the monotonic clock: a jump of the PC's clock must neither shorten nor stretch a pause.
            _backoffStamp = _time.GetTimestamp();
            _backoffLength = wait > s_longestBackoff ? s_longestBackoff : wait;
        }
    }

    /// <summary>True when GitHub lists the runner as busy, false when it is listed idle or not listed, null when GitHub could not be asked or its answer could not be read.</summary>
    private async Task<bool?> IsBusyAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var runners = await _api.ListRunnersAsync(cancellationToken).ConfigureAwait(false);
            return runners.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))?.Busy ?? false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // An unreadable answer (network, damaged body, wrong shape) means "unknown", never "not busy": the caller leaves the job alone.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warn("Could not ask GitHub whether the runner is busy: " + _redactor.Describe(ex));
            return null;
        }
    }

    private async Task ForgetRunnerAsync(JitRunner jit)
    {
        try
        {
            await _api.DeleteRunnerAsync(jit.RunnerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GitHubApiException ex)
        {
            _log.Warn($"Could not remove the runner registration {jit.Name}: {_redactor.Describe(ex)}");
        }
    }

    /// <summary>The runner that is waiting or working: its task, and the redactor's hold on its runner configuration (kept until the slot is reaped, dropped then: local CI round 3, L10).</summary>
    private sealed class Slot
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public IDisposable? Secret { get; set; }

        public void ForgetSecret()
        {
            Secret?.Dispose();
            Secret = null;
        }
    }
}
