using System.IO;
using DbdPresence.Core.Online;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Releases;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;

namespace DbdPresence.App.Updates;

/// <summary>
/// The signed updater (FR-ONL-10; PLAN_V2 §6.5): Velopack's <see cref="UpdateManager"/> over <see cref="SignedUpdateSource"/>, with downgrades
/// off. It checks 2 minutes after start and every 6 hours, and when asked; a verified newer version is downloaded in the background and
/// installed only when the user chooses "Restart to update". Velopack's own apply-at-start is switched off (Program.Main), because a package
/// is installed only after it was checked again against the newest verified feed (security review 2026-10-25, M3): its feed's key must still
/// be trusted, no newer feed may have been verified, and the file must match the signed size and SHA-256. A package that fails this is
/// deleted. Online features (a current licence) are required: when they are off, nothing is contacted. The installer is not code-signed
/// (D33); updates are safe without that because each package must match the release key's signed feed.
/// </summary>
internal sealed class AppUpdater : IAppUpdates, IDisposable
{
    /// <summary>How long after start the first check runs.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    /// <summary>The time between checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly SignedUpdateSource _source;
    private readonly UpdateService _service;
    private readonly UpdateManager _manager;
    private readonly IVelopackLocator _locator;
    private readonly TimeProvider _time;
    private readonly IOnlineLog _log;
    private readonly Action _shutdown;
    private readonly Action<VelopackAsset> _apply;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private AppUpdateStatus _status = AppUpdateStatus.Initial;
    private VelopackAsset? _ready;
    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="AppUpdater"/> class.</summary>
    /// <param name="service">The signed-feed service.</param>
    /// <param name="current">The running version, or null when it cannot be read as a release version (the updater then stays off).</param>
    /// <param name="locator">Velopack's view of the install; null uses the real one (the tests pass a test locator).</param>
    /// <param name="time">The clock and timers.</param>
    /// <param name="log">Diagnostics.</param>
    /// <param name="shutdown">Closes the app normally after the installer was told to wait for it.</param>
    /// <param name="apply">Hands a checked package to Velopack's installer; null uses <see cref="UpdateManager.WaitExitThenApplyUpdates"/> (tests record it).</param>
    public AppUpdater(UpdateService service, ReleaseVersion? current, IVelopackLocator? locator, TimeProvider time, IOnlineLog log, Action shutdown, Action<VelopackAsset>? apply = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(shutdown);
        _service = service;
        _source = new SignedUpdateSource(service, current ?? ReleaseVersion.Parse("0.0.0"));
        _locator = locator ?? VelopackLocator.Current;

        // AllowVersionDowngrade stays false: Velopack never offers a version lower than the installed one (ADR-0006 open item, now tested).
        _manager = new UpdateManager(_source, new UpdateOptions { AllowVersionDowngrade = false, ExplicitChannel = service.Channel }, _locator);
        _time = time;
        _log = log;
        _shutdown = shutdown;

        // Update.exe waits for this process to exit, installs the checked package and starts the new version.
        _apply = apply ?? (asset => _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true));
        if (current is null || !_manager.IsInstalled)
        {
            _status = _status with { State = AppUpdateState.NotInstalled };
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public AppUpdateStatus Current
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts the schedule (a development build that was not installed never checks).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_timer is not null || _status.State == AppUpdateState.NotInstalled)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => _ = RunScheduledAsync(), null, StartDelay, Interval);
        }
    }

    /// <inheritdoc />
    public Task CheckNowAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    /// <inheritdoc />
    public void RestartToUpdate()
    {
        VelopackAsset? ready;
        lock (_lock)
        {
            ready = _status.State == AppUpdateState.ReadyToRestart ? _ready : null;
        }

        if (ready is null)
        {
            return;
        }

        // Checked again right before it is handed to the installer (M3), against the last feed that offered it: a short outage since then
        // does not matter, but an expired feed, a revoked key, a newer feed or a changed file do.
        switch (Judge(ready, _service.LastOffer?.Candidate))
        {
            case PackageVerdict.Invalid:
                DeletePending(ready);
                Set(AppUpdateState.Problem, AppUpdateProblem.PackageRefused, null, 0, _time.GetUtcNow());
                return;
            case PackageVerdict.Unverifiable:
                // Nothing is wrong with the file, so it is kept; a later check decides. It is not installed on a guess.
                Set(AppUpdateState.Problem, AppUpdateProblem.Failed, null, 0, _time.GetUtcNow());
                return;
            default:
                break;
        }

#pragma warning disable CA1031 // Background boundary: whatever the installer hand-over throws is logged by type only and reported as Failed; the app keeps running.
        try
        {
            _apply(ready);
        }
        catch (Exception ex)
        {
            _log.Warning("Handing the update to the installer failed: " + ex.GetType().Name);
            Set(AppUpdateState.Problem, AppUpdateProblem.Failed, null, 0, _time.GetUtcNow());
            return;
        }
#pragma warning restore CA1031

        _log.Warning($"Restarting to install version {ready.Version}.");
        _shutdown();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _stop.Cancel();
        _stop.Dispose();
        _running.Dispose();
    }

    private static bool Matches(VelopackAsset asset, ReleasePackage package) =>
        asset.Version is not null
        && string.Equals(asset.Version.ToNormalizedString(), package.Version.Text, StringComparison.Ordinal)
        && string.Equals(asset.FileName, package.FileName, StringComparison.Ordinal);

    // Where Velopack keeps a downloaded package (its own GetLocalPackagePath is not public); null when the install has no packages folder.
    private string? PathOf(VelopackAsset asset) =>
        _locator.PackagesDir is { Length: > 0 } dir && Path.GetFileName(asset.FileName) is { Length: > 0 } name ? Path.Combine(dir, name) : null;

    private PackageVerdict Judge(VelopackAsset asset, ReleasePackage? offered)
    {
        if (offered is null)
        {
            return PackageVerdict.Unverifiable;
        }

        if (!Matches(asset, offered))
        {
            return PackageVerdict.Invalid;
        }

        return PathOf(asset) is { } path ? _service.Judge(offered, path, out _) : PackageVerdict.Unverifiable;
    }

    private async Task RunScheduledAsync()
    {
        try
        {
            await RunAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("The update check was stopped because the app is closing.");
        }
        catch (ObjectDisposedException)
        {
            _log.Warning("The update check was stopped because the app is closing.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Current.State == AppUpdateState.NotInstalled)
        {
            return;
        }

        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // A check is already running; its result is reported through Changed.
            return;
        }

        try
        {
            Set(AppUpdateState.Checking, AppUpdateProblem.None, null, 0);
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            var check = _source.LastCheck;

            // A package downloaded earlier (in this run or the last) is deleted when its feed was refused or no longer offers it, another
            // package is offered, its key was revoked, a newer feed came, or the file changed (M3). A check that only failed (offline,
            // rate-limited, signed out) deletes nothing: the verified package stays, and is offered again once a check can vouch for it.
            if (_manager.UpdatePendingRestart is { } pending)
            {
                switch (Judge(pending, _service.LastOffer?.Candidate))
                {
                    case PackageVerdict.Valid:
                        Ready(pending);
                        return;
                    case PackageVerdict.Unverifiable:
                        Finish(check);
                        return;
                    default:
                        DeletePending(pending);
                        break;
                }
            }

            if (info is null || info.IsDowngrade)
            {
                Finish(check);
                return;
            }

            var target = info.TargetFullRelease;

            // Velopack keeps a file that is already in the packages folder without downloading it again, so a damaged or changed one that
            // it could not read as a pending update is deleted first.
            if (PathOf(target) is { } existing && File.Exists(existing) && Judge(target, check?.Candidate) != PackageVerdict.Valid)
            {
                DeletePending(target);
            }

            Set(AppUpdateState.Downloading, AppUpdateProblem.None, target.Version.ToNormalizedString(), 0);
            await _manager.DownloadUpdatesAsync(info, p => Set(AppUpdateState.Downloading, AppUpdateProblem.None, target.Version.ToNormalizedString(), p), cancellationToken).ConfigureAwait(false);
            switch (Judge(target, _service.LastOffer?.Candidate))
            {
                case PackageVerdict.Invalid:
                    DeletePending(target);
                    Set(AppUpdateState.Problem, AppUpdateProblem.PackageRefused, null, 0, _time.GetUtcNow());
                    return;
                case PackageVerdict.Unverifiable:
                    Set(AppUpdateState.Problem, AppUpdateProblem.Failed, null, 0, _time.GetUtcNow());
                    return;
                default:
                    break;
            }

            Ready(target);
        }
        catch (UpdatePackageRefusedException ex)
        {
            _log.Warning("Update download refused: " + (ex.Outcome?.ToString() ?? DbdPresence.Core.Diagnostics.LogRedaction.Current.Redact(ex.Message)));
            var problem = ex.Outcome switch
            {
                UpdateDownloadOutcome.OnlineFeaturesOff => AppUpdateProblem.OnlineFeaturesOff,
                UpdateDownloadOutcome.Unreachable or UpdateDownloadOutcome.RateLimited => AppUpdateProblem.Unreachable,
                _ => AppUpdateProblem.PackageRefused,
            };
            Set(AppUpdateState.Problem, problem, null, 0, _time.GetUtcNow());
        }
        catch (ChecksumFailedException ex)
        {
            _log.Warning("Update package refused by Velopack's checksum: " + ex.GetType().Name);
            Set(AppUpdateState.Problem, AppUpdateProblem.PackageRefused, null, 0, _time.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Set(AppUpdateState.Idle, AppUpdateProblem.None, null, 0);
            throw;
        }
#pragma warning disable CA1031 // Background boundary (security review 2026-10-25, L9): any other failure is logged by type only and reported as Failed, never left at "Checking".
        catch (Exception ex)
        {
            _log.Warning("The update check failed: " + ex.GetType().Name);
            Set(AppUpdateState.Problem, AppUpdateProblem.Failed, null, 0, _time.GetUtcNow());
        }
#pragma warning restore CA1031
        finally
        {
            ReleaseRunning();
        }
    }

    private void ReleaseRunning()
    {
        try
        {
            _running.Release();
        }
        catch (ObjectDisposedException)
        {
            // The updater was disposed while a check was still finishing (the app is closing); there is nothing left to release.
            _log.Warning("The update check finished after the updater was closed.");
        }
    }

    private void DeletePending(VelopackAsset asset)
    {
        lock (_lock)
        {
            _ready = null;
        }

        if (PathOf(asset) is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
            _log.Warning($"A downloaded update ({asset.Version}) was deleted because the newest verified feed no longer vouches for it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It stays on disk but is never installed: installing needs a fresh check, and apply-at-start is off.
            _log.Warning("A downloaded update that is no longer valid could not be deleted: " + ex.GetType().Name);
        }
    }

    private void Finish(UpdateCheck? check)
    {
        var now = _time.GetUtcNow();
        var (state, problem) = check?.Outcome switch
        {
            UpdateCheckOutcome.UpToDate => (AppUpdateState.UpToDate, AppUpdateProblem.None),
            UpdateCheckOutcome.OnlineFeaturesOff => (AppUpdateState.Problem, AppUpdateProblem.OnlineFeaturesOff),
            UpdateCheckOutcome.NothingPublished => (AppUpdateState.Problem, AppUpdateProblem.NothingPublished),
            UpdateCheckOutcome.FeedRefused => (AppUpdateState.Problem, AppUpdateProblem.FeedRefused),
            UpdateCheckOutcome.Unreachable or UpdateCheckOutcome.RateLimited => (AppUpdateState.Problem, AppUpdateProblem.Unreachable),
            UpdateCheckOutcome.FloorUnavailable => (AppUpdateState.Problem, AppUpdateProblem.Failed),

            // A feed that offered a newer version which Velopack then did not take (it never takes a lower one): treated as up to date.
            UpdateCheckOutcome.UpdateAvailable => (AppUpdateState.UpToDate, AppUpdateProblem.None),
            _ => (AppUpdateState.Problem, AppUpdateProblem.Failed),
        };
        Set(state, problem, null, 0, now);
    }

    private void Ready(VelopackAsset asset)
    {
        lock (_lock)
        {
            _ready = asset;
        }

        Set(AppUpdateState.ReadyToRestart, AppUpdateProblem.None, asset.Version.ToNormalizedString(), 100, _time.GetUtcNow());
    }

    private void Set(AppUpdateState state, AppUpdateProblem problem, string? version, int progress, DateTimeOffset? checkedAt = null)
    {
        lock (_lock)
        {
            var next = new AppUpdateStatus(state, problem, version, Math.Clamp(progress, 0, 100), checkedAt ?? _status.LastChecked);
            if (next == _status)
            {
                return;
            }

            _status = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
