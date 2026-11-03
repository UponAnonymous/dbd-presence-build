namespace DbdPresence.LocalCi;

/// <summary>
/// The Linux machine of the dedicated account is used by one DBD Local CI program at a time, on the whole computer (local CI round 4, red-team F3, QA M-2). The account has one WSL virtual machine, and a
/// session begins with <c>wsl --shutdown</c> and ends the account's programs: a second program that began a session next to a running job (the isolation probe, set-up, a second loop) would kill that job. The
/// guard inside one program (<see cref="WslSandbox"/>) cannot see another program, so the session also takes this lock.
/// </summary>
public interface ISessionLock
{
    /// <summary>Takes the lock. Null when another program holds it. The returned object holds the lock until it is disposed (or the program ends).</summary>
    IDisposable? TryAcquire();
}

/// <summary>
/// <see cref="ISessionLock"/> as an exclusive lock on <c>session.lock</c> in the closed data folder, the way the loop's own lock works (<see cref="ServeLock"/>). A global named mutex would have been the
/// other choice; round 2 (L3) found that any ordinary program can create a mutex of a predictable name first and so keep the loop from ever starting, and a mutex must be released by the thread that took
/// it, which a session that spans awaits cannot promise. Nobody but Administrators and the system can create or open a file in the data folder. The operating system lets go of the lock when the program
/// that holds it ends, whatever way it ends.
/// </summary>
public sealed class FileSessionLock : ISessionLock
{
    private readonly string _lockFile;

    public FileSessionLock(string lockFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockFile);
        _lockFile = lockFile;
    }

    /// <exception cref="DirectoryNotFoundException">The data folder does not exist (set-up has not been run).</exception>
    public IDisposable? TryAcquire() => ServeLock.TryAcquire(_lockFile);
}
