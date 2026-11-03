namespace DbdPresence.LocalCi;

/// <summary>
/// The Linux machine (Windows Subsystem for Linux, through wsl.exe) as the dedicated Windows account sees it (local CI round 2, red-team F2): every method runs wsl.exe as that standard user, never
/// under the loop's own administrator token, so the virtual machine, the distributions and the settings are the account's and not the owner's.
/// </summary>
public interface IWsl
{
    /// <summary>True when wsl.exe works for the dedicated account (<c>wsl --version</c> answers). False is the answer "WSL is not usable for that account"; a command that cannot be run at all throws.</summary>
    Task<bool> IsUsableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What <c>wsl --version</c> prints for the account (the WSL version, its kernel version, the Windows version), cleaned and bounded in size; null when it gives no answer. Shown by the status (red-team
    /// round 3, L12); a missing answer is never a reason to stop.
    /// </summary>
    Task<string?> VersionAsync(CancellationToken cancellationToken);

    /// <summary>The names of every one of the account's distributions, also those that are being installed or removed (<c>--all</c>). A failure to ask throws: an unreadable answer is never an empty list (red-team L1).</summary>
    Task<IReadOnlyList<string>> ListDistributionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Imports a root-file-system archive as a WSL 2 distribution. The archive is piped into <c>wsl --import</c> through standard input (documented: "the filename can be <c>-</c> for standard
    /// input"), so the account never opens a file of the program's closed data folder and WSL reads exactly the bytes the caller verified.
    /// </summary>
    Task ImportAsync(string name, string installDirectory, Stream archive, CancellationToken cancellationToken);

    /// <summary>Exports a distribution as a tar archive into <paramref name="destination"/> (<c>wsl --export name -</c> writes it to standard output).</summary>
    Task ExportAsync(string name, Stream destination, CancellationToken cancellationToken);

    Task TerminateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Removes a distribution and its disk. A distribution that does not exist is not an error; a failure that leaves it there throws.</summary>
    Task UnregisterAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="script"/> (bash) in the distribution as <paramref name="user"/>; returns the exit code. <paramref name="standardInput"/> is piped in, every output line goes to
    /// <paramref name="onLine"/>. The step has its own time limit (<paramref name="limit"/>, see <see cref="WslLimits"/>): when it runs out the step is ended and a <see cref="TimeoutException"/> says so.
    /// </summary>
    Task<int> ExecuteAsync(string distribution, string user, string script, Stream? standardInput, Action<string>? onLine, TimeSpan limit, CancellationToken cancellationToken);

    /// <summary><c>wsl --shutdown</c> as the account: stops its WSL 2 machine and every distribution in it (its own; the owner's WSL is another user's and is not touched).</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>Installs and updates WSL itself (the Windows feature and its program). The only wsl.exe starts that run under the administrator's own token: the install from set-up, the update from set-up's weekly follow-up.</summary>
public interface IWslInstaller
{
    /// <summary><c>wsl --install --no-distribution</c>. Returns true when Windows needs a restart before WSL works.</summary>
    Task<bool> InstallWithoutDistributionAsync(CancellationToken cancellationToken);

    /// <summary><c>wsl --update</c> (round 3, H1): updates WSL and its Linux kernel for the whole PC. Returns the last line it printed. Only the weekly update calls this, and only when nobody uses WSL.</summary>
    Task<string> UpdateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The dedicated Windows account that owns the Linux machine (red-team F2): a persistent standard user in no group but Users, hidden from the sign-in screen, with a random password the program
/// keeps (Windows data protection for this computer plus an extra value, in the closed data folder). It is never a disposable leftover and is removed only by uninstall.
/// </summary>
public interface IWslAccount
{
    /// <summary>The account's name, <see cref="LocalCiDefaults.WslAccountName"/>.</summary>
    string UserName { get; }

    /// <summary>What is wrong with the account and its work folder as they are now (missing, disabled, in another group, password record lost, work folder open to others); empty when all is as it must be. Does not log anyone on.</summary>
    IReadOnlyList<string> Check();

    /// <summary>The account's security identifier (SID), or null when the account does not exist. Used to ask what the account may write.</summary>
    string? AccountSid();

    /// <summary>
    /// Clears the account's lock-out flag (wrong passwords locked it) and nothing else: a switched-off account stays off, and an account that is not this program's is not touched. True when the account was
    /// locked out and now is not. Never throws: when Windows cannot say or change it, the account check reports the lock-out and the Linux machine is not started (round 3, L11).
    /// </summary>
    bool ClearLockout();

    /// <summary>
    /// Ends every program of the account (taskkill with the account's filter and no other) and checks, by asking again, that none is left. Throws when one cannot be ended or is still there after a few
    /// seconds. Run after every use of the Linux machine and before the next (round 3, H1: nothing of a job outlives it on the PC).
    /// </summary>
    Task EndProgramsAsync(CancellationToken cancellationToken);

    /// <summary>Creates the account, its password record and its work folder, or repairs what is missing. Refuses an account that exists in other groups (it is not touched).</summary>
    Task EnsureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the account completely and on purpose: its programs are ended, then the account, its profile (and with it its WSL settings and crash files), its entry for the sign-in screen, its
    /// password record and its work folder. Only uninstall calls this.
    /// </summary>
    Task RemoveAsync(CancellationToken cancellationToken);
}

/// <summary>The account's <c>.wslconfig</c>, in the account's own profile folder. The file is handled as bytes: what is checked is exactly what WSL reads (red-team round 3, L3).</summary>
public interface IWslSettingsFile
{
    /// <summary>Where the file is (shown in messages).</summary>
    string Location { get; }

    /// <summary>The file's bytes, or null when there is no file. A link or a folder at that name, and a file larger than the program's own never is, are refused.</summary>
    byte[]? Read();

    /// <summary>Writes the file (a new file with an unguessable name is renamed over it, so nothing is written through an existing name).</summary>
    void Write(byte[] content);

    /// <summary>
    /// Opens the file for reading with nobody else allowed to write to it, rename it or delete it until the returned object is disposed, and gives its bytes: what is checked is what WSL reads when it
    /// starts the virtual machine.
    /// </summary>
    IDisposable OpenHeld(out byte[] content);
}

/// <summary>
/// One use of the account's Linux machine, from a fresh start to a fresh stop (local CI round 2, red-team F2, F8; round 3, H1, L11): a lock-out of the account is cleared, the machine is shut down first
/// (a new kernel and virtual machine), the programs of the account that are still running are ended (and checked gone), the settings file is written and read back and held (fail closed), and afterwards
/// the machine is shut down again and the programs of the account are ended and checked gone again. Only one session exists at a time.
/// </summary>
public sealed class WslSandbox
{
    private static readonly TimeSpan s_stepLimit = TimeSpan.FromMinutes(3);

    private readonly IWsl _wsl;
    private readonly IWslAccount _account;
    private readonly IWslSettingsFile _settings;
    private readonly WslHostFacts _facts;
    private readonly IMachineRecord _record;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly TimeProvider _time;
    private readonly ISessionLock _sessionLock;
    private readonly ResidueRecorder? _residue;
    private int _inUse;

    /// <summary>The subject of the entry on the leftovers list that says the last use of the Linux machine did not end cleanly (round 4, F14).</summary>
    public const string SessionEndSubject = "end-of-session";

    /// <summary>The sandbox over the Linux machine of the account. <paramref name="residue"/> is where a session that could not be ended cleanly is listed (so the status and the daily notice say it); null where nothing is kept.</summary>
    public WslSandbox(IWsl wsl, IWslAccount account, IWslSettingsFile settings, WslHostFacts facts, IMachineRecord record, ICiLog log, Redactor redactor, TimeProvider time, ISessionLock sessionLock, ResidueRecorder? residue = null)
    {
        ArgumentNullException.ThrowIfNull(sessionLock);
        _sessionLock = sessionLock;
        _residue = residue;
        _wsl = wsl;
        _account = account;
        _settings = settings;
        _facts = facts;
        _record = record;
        _log = log;
        _redactor = redactor;
        _time = time;
    }

    /// <summary>The settings the account's Linux machine is started with.</summary>
    public WslHostFacts Facts => _facts;

    /// <summary>What is remembered about the account's Linux machine (boot id, kernel, WSL version).</summary>
    public IMachineRecord Record => _record;

    /// <summary>Null when the account is as it must be; otherwise the plain reason no Linux machine can be started.</summary>
    public string? NotReadyReason()
    {
        var problems = _account.Check();
        return problems.Count == 0 ? null : $"the Windows account {_account.UserName} is not as it must be: {problems[0]}";
    }

    /// <summary>
    /// Starts a session: the machine-wide session lock is taken (no other DBD Local CI program uses the Linux machine at the same time), a lock-out of the account is cleared, the account is checked,
    /// <c>wsl --shutdown</c>, the programs of the account that are still running are ended and checked gone, the settings are written, verified and held. Disposing the session stops the machine again, ends
    /// and checks the account's programs again, lets the settings file go and then the session lock.
    /// </summary>
    /// <exception cref="JobEnvironmentException">The account or the settings are not as required, or another session is open (in this program, or in another one: nothing is done to the machine then).</exception>
    public async Task<WslSession> BeginAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _inUse, 1, 0) != 0)
        {
            throw new JobEnvironmentException("Another task is using the Linux machine right now, so this one was not started.");
        }

        IDisposable? machineWide = null;
        IDisposable? hold = null;
        try
        {
            // Before anything touches the machine: a second program must not shut down a virtual machine that a running job is in (round 4, F3).
            machineWide = _sessionLock.TryAcquire()
                          ?? throw new JobEnvironmentException("Another DBD Local CI program (the background loop, set-up, the isolation probe or the test check) is using the Linux machine of the account right now, so this one was not started and nothing was done to the machine. Wait until it has finished, or end it, and try again.");
            if (_account.ClearLockout())
            {
                _log.Warn($"The Windows account {_account.UserName} was locked out (wrong passwords were tried); the lock was cleared.");
            }

            if (NotReadyReason() is { } reason)
            {
                throw new JobEnvironmentException($"The Linux machine was not started: {reason}.");
            }

            await _wsl.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            await RequireNoProgramsOfTheAccountAsync(cancellationToken).ConfigureAwait(false);
            await RecordVersionAsync(cancellationToken).ConfigureAwait(false);
            if (WslSettingsPolicy.BytesProblems(_settings.Read(), _facts).Count > 0)
            {
                _settings.Write(WslSettingsPolicy.ExpectedBytes(_facts));
                _log.Info($"Wrote the WSL settings of the account {_account.UserName} ({_settings.Location}).");
            }

            hold = _settings.OpenHeld(out var held);
            var wrong = WslSettingsPolicy.BytesProblems(held, _facts);
            if (wrong.Count > 0)
            {
                throw new JobEnvironmentException($"The WSL settings of the account {_account.UserName} are not as required ({wrong[0]}), so the Linux machine was not started.");
            }

            return new WslSession(this, hold, machineWide);
        }
        catch
        {
            hold?.Dispose();
            machineWide?.Dispose();
            Volatile.Write(ref _inUse, 0);
            throw;
        }
    }

    /// <summary>Ends what is left of the account's programs from an earlier use and checks that none is; the Linux machine of a job does not start next to them.</summary>
    private async Task RequireNoProgramsOfTheAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _account.EndProgramsAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever stops the check is turned into the one plain refusal below; a cancel is passed on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            throw new JobEnvironmentException($"Programs of the Windows account {_account.UserName} from earlier are still running ({_redactor.Describe(ex)}), so no Linux machine was started.", ex);
        }
    }

    /// <summary>Asks WSL for its version and remembers the answer for the status. Never stops a start: a missing answer only means the status says "not known".</summary>
    private async Task RecordVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _wsl.VersionAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } text)
            {
                _record.RecordVersion(text);
            }
        }
#pragma warning disable CA1031 // The version is only for the status; whatever goes wrong is logged and the start goes on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Warn($"Could not read the WSL version of the account {_account.UserName}: {_redactor.Describe(ex)}");
        }
    }

    /// <summary>
    /// Stops a distribution, removes it and checks that it is gone (the list is read again, and a list that cannot be read counts as "not gone"). Each step runs even when the one before failed and
    /// every failure is logged. True only when the distribution is verifiably not there any more.
    /// </summary>
    public async Task<bool> RemoveDistributionAsync(string name)
    {
        await StepAsync($"stop {name}", ct => _wsl.TerminateAsync(name, ct)).ConfigureAwait(false);
        await StepAsync($"remove {name}", ct => _wsl.UnregisterAsync(name, ct)).ConfigureAwait(false);
        using var limit = new CancellationTokenSource(s_stepLimit, _time);
        try
        {
            var remaining = await _wsl.ListDistributionsAsync(limit.Token).ConfigureAwait(false);
            if (remaining.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _log.Failure($"Clean-up: {name} is still registered after it was removed.");
                return false;
            }

            return true;
        }
#pragma warning disable CA1031 // Whatever stops the check is logged and means "not verified gone": the caller keeps the leftover on its list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"Clean-up could not check that {name} is gone: {_redactor.Describe(ex)}");
            return false;
        }
    }

    /// <summary>
    /// Removes a folder if it is empty. WSL deletes a distribution's disk and then its empty folder itself; this only finishes that when it did not. It is never recursive: a folder with anything in it
    /// stays, is logged, and counts as a leftover (a link at that name is only unlinked, never followed). True when the folder is gone or was never there.
    /// </summary>
    public bool RemoveFolderIfEmpty(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (IOException ex)
        {
            _log.Failure($"Clean-up could not remove {path}: {_redactor.Describe(ex)}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Failure($"Clean-up could not remove {path}: {_redactor.Describe(ex)}");
            return false;
        }
    }

    internal async ValueTask EndAsync(IDisposable hold, IDisposable machineWide)
    {
        try
        {
            var shutDown = await StepAsync("shut down the Linux machine", ct => _wsl.ShutdownAsync(ct)).ConfigureAwait(false);
            var ended = await StepAsync("end the programs of the account", ct => _account.EndProgramsAsync(ct)).ConfigureAwait(false);
            RecordHowTheSessionEnded(shutDown, ended);
        }
        finally
        {
            hold.Dispose();

            // The machine-wide lock goes last: another program may begin a session only when this one's machine is shut down and its programs are ended.
            machineWide.Dispose();
            Volatile.Write(ref _inUse, 0);
        }
    }

    /// <summary>
    /// A session that could not shut the machine down or end the account's programs is put on the leftovers list, so the status says so and the owner gets the daily notice: the next start of a Linux
    /// machine refuses while programs of the account from earlier are running, and a log line alone would leave the owner with checks that silently do not run (round 4, F14). A session that ended
    /// cleanly takes the entry off again. Never throws: the state file being busy must not turn the end of a session into a failure.
    /// </summary>
    private void RecordHowTheSessionEnded(bool shutDown, bool ended)
    {
        if (_residue is null)
        {
            return;
        }

        try
        {
            if (shutDown && ended)
            {
                _residue.Resolve(ResidueKinds.Session, SessionEndSubject);
                return;
            }

            var what = !shutDown && !ended
                ? $"the Linux machine of the account {_account.UserName} could not be shut down and its programs could not be ended after its last use"
                : !shutDown
                    ? $"the Linux machine of the account {_account.UserName} could not be shut down after its last use"
                    : $"the programs of the account {_account.UserName} could not be ended after the last use of its Linux machine";
            _residue.Add(ResidueKinds.Session, SessionEndSubject, $"{what} (see the log). The next Linux check will not start until that works; the program tries again before each check. If it keeps failing, sign out the Windows account {_account.UserName} or restart Windows");
        }
#pragma warning disable CA1031 // Whatever goes wrong with the state file is logged; the end of the session goes on.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _log.Failure($"Could not record how the last use of the Linux machine ended: {_redactor.Describe(ex)}");
        }
    }

    /// <summary>Runs one clean-up step with a time limit; a failure is logged, never thrown, so the steps after it still run. True when the step worked.</summary>
    private async Task<bool> StepAsync(string what, Func<CancellationToken, Task> step)
    {
        using var limit = new CancellationTokenSource(s_stepLimit, _time);
        try
        {
            await step(limit.Token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Clean-up must go on with the next step whatever one step throws; the failure is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"Clean-up could not {what}: {_redactor.Describe(ex)}");
            return false;
        }
    }
}

/// <summary>An open use of the Linux machine (see <see cref="WslSandbox.BeginAsync"/>). Disposing it stops the machine and releases the settings file.</summary>
public sealed class WslSession : IAsyncDisposable
{
    private readonly WslSandbox _owner;
    private readonly IDisposable _machineWide;
    private IDisposable? _hold;

    internal WslSession(WslSandbox owner, IDisposable hold, IDisposable machineWide)
    {
        _owner = owner;
        _hold = hold;
        _machineWide = machineWide;
    }

    public async ValueTask DisposeAsync()
    {
        var hold = Interlocked.Exchange(ref _hold, null);
        if (hold is not null)
        {
            await _owner.EndAsync(hold, _machineWide).ConfigureAwait(false);
        }
    }
}
