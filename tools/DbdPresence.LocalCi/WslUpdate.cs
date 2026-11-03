namespace DbdPresence.LocalCi;

/// <summary>What the weekly WSL update came to.</summary>
public enum WslUpdateResult
{
    /// <summary>It was not tried: the owner switched it off, someone is using WSL, or it is not due.</summary>
    Skipped,

    /// <summary><c>wsl --update</c> ran and ended well.</summary>
    Updated,

    /// <summary><c>wsl --update</c> was tried and failed or ran out of time.</summary>
    Failed,
}

/// <summary>Whether the owner allows the program to update WSL itself (round 4, F5): only a yes at set-up and no <c>no-wsl-update</c> file. Never asked counts as no.</summary>
public static class WslUpdatePolicy
{
    /// <summary>Null when the update may run; otherwise the plain reason it is off.</summary>
    /// <param name="allowed">The owner's answer at set-up (null: never asked).</param>
    /// <param name="switchFileThere">True when the file <c>no-wsl-update</c> is in the data folder.</param>
    public static string? OffBecause(bool? allowed, bool switchFileThere)
    {
        if (switchFileThere)
        {
            return "the file no-wsl-update is in the data folder";
        }

        return allowed == true ? null : allowed == false ? "you did not allow the weekly update at set-up (run set-up again to change that)" : "you have not been asked yet whether this program may update WSL (run set-up again)";
    }
}

/// <summary>
/// The weekly update of Windows Subsystem for Linux (local CI round 3, red-team H1: an old WSL kernel is how a job that gains root would get further, and WSL's kernel is updated by <c>wsl --update</c>).
/// WSL is installed once for the whole PC, so an update changes it for the owner too. It is therefore done only when the owner allowed it (the question at set-up, default no: round 4, F5) and has not
/// switched it off (a file named <c>no-wsl-update</c> in the data folder), no Linux machine of this program is up, and no WSL process of anybody is running (nobody is using WSL); otherwise the version is only
/// reported by the status. A failure is logged and never stops anything else.
/// </summary>
public sealed class WeeklyWslUpdate
{
    /// <summary>Names of the processes that show WSL is in use (without .exe): the virtual machine's memory, the host of a running distribution, a session, the relay and the window system.</summary>
    public static IReadOnlyList<string> ProcessesOfWsl { get; } = ["vmmemWSL", "vmmem", "wslhost", "wsl", "wslrelay", "wslg"];

    /// <summary>How often the update is tried.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromDays(7);

    private readonly IWslInstaller _installer;
    private readonly IProcessList _processes;
    private readonly Func<string?> _offBecause;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private string? _saidSkip;

    /// <param name="installer">Runs <c>wsl --update</c> under the administrator token (the one place that does).</param>
    /// <param name="processes">Only the names of the running programs are looked at.</param>
    /// <param name="offBecause">Null when the owner allows the update; otherwise the plain reason it is off ("the file no-wsl-update is in the data folder", "you did not allow it at set-up").</param>
    /// <param name="state">Where the last attempt is kept.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The log.</param>
    /// <param name="redactor">Cleans texts.</param>
    public WeeklyWslUpdate(IWslInstaller installer, IProcessList processes, Func<string?> offBecause, StateStore state, TimeProvider time, ICiLog log, Redactor redactor)
    {
        _installer = installer;
        _processes = processes;
        _offBecause = offBecause;
        _state = state;
        _time = time;
        _log = log;
        _redactor = redactor;
    }

    /// <summary>True when the update was last tried a week ago or more, or never (a time in the future counts as due: the PC's clock was set back).</summary>
    public bool Due(CiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var now = _time.GetUtcNow();
        return state.LastWslUpdateAttempt is not { } last || last > now || now - last >= Interval;
    }

    /// <summary>
    /// Tries the update when nothing speaks against it. The attempt is written down <b>before</b> the update starts (local CI round 4, red-team F2): an update that hangs, is ended by its time limit or takes
    /// the PC down is an attempt too, and is tried again after a week, not at every idle round. The time limit is the one of <see cref="LimitedWslInstaller"/> and is not repeated around this call, so its
    /// timeout reaches the handler below as an ordinary failure and is noted. A skip because WSL is in use is not an attempt, so it is tried again at the next round, and it is said once, not every round.
    /// </summary>
    public async Task<WslUpdateResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_offBecause() is { } off)
        {
            _saidSkip = null;
            Note(now, $"off: {off}, so WSL is not updated by this program", recordAttempt: true);
            _log.Info($"WSL is not updated: the update is off ({off}).");
            return WslUpdateResult.Skipped;
        }

        bool inUse;
        try
        {
            inUse = _processes.IsAnyRunning(ProcessesOfWsl);
        }
#pragma warning disable CA1031 // If the process list cannot be read it is not known that nobody uses WSL, so the update is not made; the reason is logged (once, until it works again).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (SayOnce("process list unreadable"))
            {
                _log.Warn($"WSL is not updated: it could not be seen whether anybody uses WSL ({_redactor.Describe(ex)}). It is tried again later.");
            }

            return WslUpdateResult.Skipped;
        }

        if (inUse)
        {
            if (SayOnce("WSL in use"))
            {
                _log.Info("WSL is not updated now: a WSL program is running (you may be using WSL). It is tried again later.");
                Note(now, "waiting: a WSL program is running, so WSL is not updated yet (it is tried again later)", recordAttempt: false);
            }

            return WslUpdateResult.Skipped;
        }

        _saidSkip = null;
        _log.Info("Updating Windows Subsystem for Linux (wsl --update).");
        Note(now, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"started at {now:yyyy-MM-dd HH:mm} UTC and has not reported an end (if it stays like this the update hung or the PC stopped)"), recordAttempt: true);
        try
        {
            var last = await _installer.UpdateAsync(cancellationToken).ConfigureAwait(false);
            Note(now, $"updated at {now:yyyy-MM-dd}: {last}", recordAttempt: true);
            _log.Info($"WSL update finished: {last}");
            return WslUpdateResult.Updated;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Note(now, "failed: the update was stopped", recordAttempt: true);
            _log.Failure("The WSL update was stopped.");
            return WslUpdateResult.Failed;
        }
#pragma warning disable CA1031 // Whatever stops the update (a refused download, a timeout, an installer error) is logged and tried again after a week; it never stops anything else.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var text = _redactor.Describe(ex);
            Note(now, $"failed at {now:yyyy-MM-dd}: {text}", recordAttempt: true);
            _log.Failure($"The WSL update failed: {text}");
            return WslUpdateResult.Failed;
        }
    }

    /// <summary>True the first time a reason for not updating is met, false while the same reason goes on (so a message is not repeated at every round of the loop).</summary>
    private bool SayOnce(string reason)
    {
        if (string.Equals(_saidSkip, reason, StringComparison.Ordinal))
        {
            return false;
        }

        _saidSkip = reason;
        return true;
    }

    private void Note(DateTimeOffset now, string note, bool recordAttempt) =>
        _state.Update(s =>
        {
            s.LastWslUpdateNote = note;
            if (recordAttempt)
            {
                s.LastWslUpdateAttempt = now;
            }
        });
}
