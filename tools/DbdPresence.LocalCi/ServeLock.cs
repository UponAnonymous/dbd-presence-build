namespace DbdPresence.LocalCi;

/// <summary>
/// Only one loop may run: the first <c>serve</c> holds an exclusive lock on <c>serve.lock</c> in the closed data folder (local CI round 2, red-team L3). It used to be a global named
/// mutex, which any ordinary program could create first under that predictable name and so keep the loop from ever starting. Nobody but Administrators and the system can create a file
/// in the data folder, so nobody else can take this lock; the loop checks that folder at its start.
/// </summary>
public static class ServeLock
{
    /// <summary>Takes the lock. Null when another loop holds it. The returned object holds the lock until it is disposed (or the program ends).</summary>
    /// <exception cref="DirectoryNotFoundException">The data folder does not exist (set-up has not been run).</exception>
    public static IDisposable? TryAcquire(string lockFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockFile);
        DataFolder.RequireExists(Path.GetDirectoryName(lockFile)!);
        try
        {
            return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // hygiene: allow-swallow another copy of the loop holds the lock, which is the answer
            return null;
        }
    }

    /// <summary>True while a loop holds the lock (any copy). Asking takes the lock for an instant when nobody holds it.</summary>
    public static bool IsHeld(string lockFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockFile);
        if (!Directory.Exists(Path.GetDirectoryName(lockFile)))
        {
            return false;
        }

        var taken = TryAcquire(lockFile);
        if (taken is null)
        {
            return true;
        }

        taken.Dispose();
        return false;
    }

    /// <summary>
    /// True for the exception a refused exclusive open raises: the Windows sharing violation (32) and lock violation (33) as HRESULTs, and EWOULDBLOCK (11) which .NET reports on Unix systems for a lock
    /// that another open file holds. Any other input/output failure is a real failure and is not hidden.
    /// </summary>
    public static bool IsSharingViolation(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or 11;
    }
}
