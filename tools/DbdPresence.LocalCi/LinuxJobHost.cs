using System.Collections.Concurrent;
using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// Linux jobs: a fresh WSL 2 distribution imported from the base image for every job, inside a session of the dedicated account's Linux machine (its own virtual machine and kernel, started fresh
/// and stopped after the job; local CI round 2, red-team F2). The distribution is checked before the runner exists in it, and removed afterwards, also after an error, a timeout or a cancel; what
/// cannot be removed is listed as a leftover, shown in the status and announced to the owner (L2), and no job starts while a leftover Linux machine is still registered (QA M6).
/// </summary>
public sealed class LinuxJobHost : IJobHost
{
    /// <summary>Unpacks the verified runner package that arrives on standard input.</summary>
    public const string UnpackScript = "mkdir -p /home/runner/actions-runner && tar xzf - -C /home/runner/actions-runner";

    /// <summary>
    /// Starts the runner with its configuration, which arrives as the first line of standard input and goes into one environment variable of the runner (the runner reads
    /// <c>ACTIONS_RUNNER_INPUT_JITCONFIG</c>), so it is on no command line. The runner starts through <c>setpriv --no-new-privs</c> (round 3, H1): the flag is inherited by everything the job starts and
    /// cannot be taken back, so no program of the job gains privileges by running a set-user-id program (sudo, su, mount helpers). The isolation check proves the mechanism works in the same machine first.
    /// </summary>
    public const string RunScript = "IFS= read -r ACTIONS_RUNNER_INPUT_JITCONFIG && export ACTIONS_RUNNER_INPUT_JITCONFIG && cd /home/runner/actions-runner && exec setpriv --no-new-privs ./run.sh";

    /// <summary>
    /// A job needs room for its machine: the import of the base image takes some gigabytes before the job has done anything, and the room left afterwards must still be above
    /// <see cref="MinimumFreeBytesDuringAJob"/>, or the watch would stop a job that never got going (red-team round 4, F15). Below this the loop does not start one. The size of the import is not
    /// measured yet (PENDING-ENV: the log says what the import took).
    /// </summary>
    private const long MinimumFreeBytesForAJob = 8L * 1024 * 1024 * 1024;

    /// <summary>While a job runs, the drive that holds its machine is watched: below this the job is stopped and its machine removed, before the job can fill the drive (red-team round 3, M1).</summary>
    public const long MinimumFreeBytesDuringAJob = 3L * 1024 * 1024 * 1024;

    /// <summary>
    /// How often the free room is looked at while a machine of a job exists. A job that writes fast crosses the margin between two looks: at five seconds that was a write rate of about 0.6 GB a second
    /// (red-team round 4, F15), at one second it is five times that. Looking is one call that asks the drive for its free room.
    /// </summary>
    public static readonly TimeSpan DiskCheckEvery = TimeSpan.FromSeconds(1);

    private readonly IWsl _wsl;
    private readonly WslSandbox _sandbox;
    private readonly LocalCiPaths _paths;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly IntegrityStore _integrity;
    private readonly IProtectedStorage _storage;
    private readonly IDiskSpace _disk;
    private readonly ResidueRecorder _residue;
    private readonly TimeProvider _time;
    private readonly ProbeGate _probe;

    public LinuxJobHost(IWsl wsl, WslSandbox sandbox, LocalCiPaths paths, ICiLog log, Redactor redactor, IntegrityStore integrity, IProtectedStorage storage, IDiskSpace disk, ResidueRecorder residue, TimeProvider time, ProbeGate probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _time = time;
        _wsl = wsl;
        _sandbox = sandbox;
        _paths = paths;
        _log = log;
        _redactor = redactor;
        _integrity = integrity;
        _storage = storage;
        _disk = disk;
        _residue = residue;
    }

    /// <summary>The distribution name for a runner name: <c>dbd-ci-job-&lt;n&gt;</c> where n is the runner name's time stamp.</summary>
    public static string DistributionFor(string runnerName) => LocalCiDefaults.JobDistributionFor(runnerName);

    public string? NotReadyReason()
    {
        if (!File.Exists(_paths.BaseArchive) || _integrity.Recorded(_paths.BaseArchive) is null)
        {
            return "the Linux base image has not been built yet";
        }

        var problems = _storage.CheckInsideDataFolder(_paths.BaseArchive);
        if (problems.Count > 0)
        {
            return "the Linux base image cannot be trusted: " + problems[0];
        }

        if (!_integrity.IsIntact(_paths.BaseArchive))
        {
            return "the Linux base image does not match the checksum recorded for it, so it is built again";
        }

        if (_sandbox.NotReadyReason() is { } account)
        {
            return account;
        }

        // No Linux check starts unless the isolation probe was accepted for this setup and this WSL (local CI round 4, red-team F5, QA M-3).
        if (_probe.Problem() is { } probeProblem)
        {
            return "no Linux check starts on this PC: " + probeProblem;
        }

        return _disk.FreeBytes(_paths.WslWorkFolder) < MinimumFreeBytesForAJob
            ? "less than 8 GB are free on the drive that holds the Linux machines"
            : null;
    }

    public async Task<int> RunAsync(RunnerLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var distribution = DistributionFor(launch.Name);
        var directory = _paths.WslInstallDirectory(distribution);
        var session = await _sandbox.BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                // The session has just read the WSL version again: a WSL that was changed since the last look (the owner, Windows or the Store updated it) is caught here, before anything of the job exists.
                if (_probe.Problem() is { } probeProblem)
                {
                    throw new JobEnvironmentException($"The isolation probe no longer allows a job: {probeProblem}. No job was started.");
                }

                // From the moment the job's machine is imported, its disk can grow, and the import itself takes room: the room left on the drive is watched from before the import to the end of the job, and the
                // job is stopped before it fills the drive (red-team round 3, M1; round 4, F15: the watch used to begin only after the import, the hardening and the isolation check).
                return await WatchingTheDiskAsync(
                    async watched =>
                    {
                        await RequireNoLeftoverAsync(watched).ConfigureAwait(false);
                        var before = FreeRoom();

                        // The base image is hashed again now and kept open (others may only read it) while WSL reads it from standard input: WSL gets the bytes that were just checked.
                        var baseImage = await _integrity.OpenVerifiedAsync(_paths.BaseArchive, watched).ConfigureAwait(false);
                        await using (baseImage.ConfigureAwait(false))
                        {
                            await _wsl.ImportAsync(distribution, directory, baseImage, watched).ConfigureAwait(false);
                        }

                        // What the import took is measured, not guessed (PENDING-ENV: no real import has been seen yet); the first run's log says it.
                        _log.Info($"Free room on the drive that holds the Linux machines: {before} before the machine {distribution} was imported, {FreeRoom()} after.");

                        await HardenAsync(distribution, watched).ConfigureAwait(false);
                        await RequireIsolationAsync(distribution, watched).ConfigureAwait(false);

                        var package = await _integrity.OpenVerifiedAsync(launch.PackagePath, watched).ConfigureAwait(false);
                        await using (package.ConfigureAwait(false))
                        {
                            var unpack = await _wsl.ExecuteAsync(distribution, LocalCiDefaults.LinuxJobUser, UnpackScript, package, null, WslLimits.Unpack, watched).ConfigureAwait(false);
                            if (unpack != 0)
                            {
                                throw new InvalidOperationException($"Unpacking the runner in {distribution} failed (exit code {unpack}).");
                            }
                        }

                        var configuration = new MemoryStream(Encoding.UTF8.GetBytes(launch.JitConfig.Reveal() + "\n"));
                        await using (configuration.ConfigureAwait(false))
                        {
                            var output = new RunnerOutput(_log);
                            return await _wsl.ExecuteAsync(distribution, LocalCiDefaults.LinuxJobUser, RunScript, configuration, output.Line, WslLimits.Run, watched).ConfigureAwait(false);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await RemoveAsync(distribution, directory).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The sweep at every start of the loop: every Linux machine and folder with the exact shape of the ones this program makes is removed (a job's, or the one a base image build uses), and what is
    /// left afterwards is recorded. Anything with another name is left alone, in particular nothing a person made and nothing of the dedicated account itself. Never throws.
    /// </summary>
    public async Task CleanupLeftoversAsync(CancellationToken cancellationToken)
    {
        var remaining = new List<ResidueItem>();
        try
        {
            var session = await _sandbox.BeginAsync(cancellationToken).ConfigureAwait(false);
            await using (session.ConfigureAwait(false))
            {
                var present = await _wsl.ListDistributionsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var name in present.Where(IsOurs))
                {
                    _log.Warn($"Removing the leftover Linux machine {name}.");
                    if (!await _sandbox.RemoveDistributionAsync(name).ConfigureAwait(false))
                    {
                        remaining.Add(new ResidueItem { Kind = ResidueKinds.LinuxMachine, Subject = name, Text = $"the Linux machine {name} could not be removed" });
                    }
                }

                if (Directory.Exists(_paths.WslDistrosFolder))
                {
                    foreach (var folder in Directory.EnumerateDirectories(_paths.WslDistrosFolder).Where(f => IsOursFolder(Path.GetFileName(f))))
                    {
                        if (!_sandbox.RemoveFolderIfEmpty(folder))
                        {
                            remaining.Add(new ResidueItem { Kind = ResidueKinds.Folder, Subject = folder, Text = $"the folder {folder} is not empty (delete it by hand when no Linux machine uses it)" });
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The sweep never stops the loop: whatever it hit is logged and listed as something that could not be checked.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure("Could not look for leftover Linux machines: " + _redactor.Describe(ex));
            remaining.Add(new ResidueItem { Kind = ResidueKinds.Check, Subject = "leftover-linux-machines", Text = "the check for leftover Linux machines could not be done (see the log)" });
        }

        _residue.Sweep(remaining);
        _residue.RecheckMarkerFiles();
    }

    private static bool IsOurs(string distribution) =>
        LocalCiDefaults.IsJobDistributionName(distribution) || LocalCiDefaults.IsBaseDistributionName(distribution) || LocalCiDefaults.IsProbeDistributionName(distribution);

    private static bool IsOursFolder(string folder) =>
        LocalCiDefaults.IsJobDistributionName(folder) || LocalCiDefaults.IsBuildFolderName(folder) || LocalCiDefaults.IsProbeFolderName(folder);

    /// <summary>
    /// No job starts while a Linux machine of an earlier job or build is still registered: it is removed first, and if that does not work the job is refused. The isolation of a new job must not rest on
    /// the hope that the last clean-up worked.
    /// </summary>
    private async Task RequireNoLeftoverAsync(CancellationToken cancellationToken)
    {
        var present = await _wsl.ListDistributionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var name in present.Where(IsOurs))
        {
            _log.Warn($"The Linux machine {name} of an earlier job is still there; removing it before the new job starts.");
            if (!await _sandbox.RemoveDistributionAsync(name).ConfigureAwait(false))
            {
                _residue.Add(ResidueKinds.LinuxMachine, name, $"the Linux machine {name} could not be removed");
                throw new JobEnvironmentException($"The Linux machine {name} of an earlier job could not be removed, so no new job was started.");
            }

            _residue.Resolve(ResidueKinds.LinuxMachine, name);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> while the free room on the drive that holds the Linux machines is looked at every <see cref="DiskCheckEvery"/>. When it falls below
    /// <see cref="MinimumFreeBytesDuringAJob"/> the work is cancelled and a <see cref="LowDiskException"/> says so in plain words; the caller's own cancel stays a cancel. A look at the disk that fails is
    /// logged once and the job goes on (a missing reading is not a reason to kill a job).
    /// </summary>
    private async Task<int> WatchingTheDiskAsync(Func<CancellationToken, Task<int>> work, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long low = -1;
        var watcher = WatchAsync(
            free =>
            {
                Interlocked.Exchange(ref low, free);
                stop.Cancel();
            },
            stop.Token);
        try
        {
            return await work(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Interlocked.Read(ref low) >= 0)
        {
            var free = Interlocked.Read(ref low);
            var message = $"The drive that holds the Linux machines has {free / (1024.0 * 1024 * 1024):0.0} GB free, less than {MinimumFreeBytesDuringAJob / (1024 * 1024 * 1024)} GB, so the job was stopped and its Linux machine removed before it could fill the drive. Free some space on that drive ({_paths.WslWorkFolder}).";
            _log.Failure(message);
            throw new LowDiskException(message);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
        }
    }

    private async Task WatchAsync(Action<long> tooLittle, CancellationToken stopWatching)
    {
        var warned = false;
        try
        {
            using var timer = new PeriodicTimer(DiskCheckEvery, _time);
            while (await timer.WaitForNextTickAsync(stopWatching).ConfigureAwait(false))
            {
                long free;
                try
                {
                    free = _disk.FreeBytes(_paths.WslWorkFolder);
                }
                catch (IOException ex)
                {
                    warned = WarnOnce(warned, ex);
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    warned = WarnOnce(warned, ex);
                    continue;
                }

                if (free < MinimumFreeBytesDuringAJob)
                {
                    tooLittle(free);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // hygiene: allow-swallow the job ended (or was stopped), which is what ends the watching
        }
    }

    /// <summary>The free room of the drive that holds the Linux machines in words, for the log; a reading that fails is said, never thrown (it is not a reason to stop a job).</summary>
    private string FreeRoom()
    {
        string problem;
        try
        {
            return $"{_disk.FreeBytes(_paths.WslWorkFolder) / (1024.0 * 1024 * 1024):0.0} GB";
        }
        catch (IOException ex)
        {
            problem = _redactor.Describe(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            problem = _redactor.Describe(ex);
        }

        return $"unknown ({problem})";
    }

    private bool WarnOnce(bool already, Exception ex)
    {
        if (!already)
        {
            _log.Warn($"Could not look at the free room of the drive that holds the Linux machines; the job goes on: {_redactor.Describe(ex)}");
        }

        return true;
    }

    /// <summary>
    /// Closes user namespaces in the new environment, as root, before anything of the job exists there (round 3, H1). It is the only step of a job that runs as root; its text is fixed
    /// (<see cref="WslHardening.Script"/>). A machine where it fails is refused.
    /// </summary>
    private async Task HardenAsync(string distribution, CancellationToken cancellationToken)
    {
        var lines = new ConcurrentQueue<string>();
        var exit = await _wsl.ExecuteAsync(distribution, "root", WslHardening.Script, null, lines.Enqueue, WslLimits.Harden, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            _log.Failure($"Closing user namespaces in {distribution} failed (exit code {exit}): {string.Join(" / ", lines.Take(5))}");
            throw new JobEnvironmentException($"User namespaces could not be closed in the Linux environment {distribution}, so no job was started in it.");
        }
    }

    /// <summary>
    /// Runs the isolation check inside the new environment. Anything but a clean pass stops the job before the runner exists there. The boot id and kernel the machine reports are remembered: the next
    /// job's machine must have another boot id (and a check that reports none fails).
    /// </summary>
    private async Task RequireIsolationAsync(string distribution, CancellationToken cancellationToken)
    {
        var lines = new ConcurrentQueue<string>();
        var script = WslPreflight.ScriptFor(_sandbox.Facts, _sandbox.Record.LastBootId);
        var exit = await _wsl.ExecuteAsync(distribution, LocalCiDefaults.LinuxJobUser, script, null, lines.Enqueue, WslLimits.Preflight, cancellationToken).ConfigureAwait(false);
        var verdict = WslPreflight.Parse(lines, exit);
        if (verdict.BootId is { } bootId)
        {
            _sandbox.Record.RecordBoot(bootId, verdict.KernelRelease, verdict.KernelBuilt);
        }

        if (!verdict.Passed)
        {
            foreach (var failure in verdict.Failures)
            {
                _log.Failure($"Isolation check of {distribution} failed: {failure}");
            }

            throw new JobEnvironmentException($"The Linux environment {distribution} is not isolated as required ({verdict.Failures[0]}), so no job was started in it.");
        }
    }

    /// <summary>Stops and unregisters the job's machine, checks that it is gone and finishes removing its (empty) folder; what stays is recorded as a leftover.</summary>
    private async Task RemoveAsync(string distribution, string directory)
    {
        var removed = await _sandbox.RemoveDistributionAsync(distribution).ConfigureAwait(false);
        if (!removed)
        {
            _residue.Add(ResidueKinds.LinuxMachine, distribution, $"the Linux machine {distribution} could not be removed");
            return;
        }

        _residue.Resolve(ResidueKinds.LinuxMachine, distribution);
        if (!_sandbox.RemoveFolderIfEmpty(directory))
        {
            _residue.Add(ResidueKinds.Folder, directory, $"the folder {directory} is not empty (delete it by hand when no Linux machine uses it)");
            return;
        }

        _residue.Resolve(ResidueKinds.Folder, directory);
    }
}
