namespace DbdPresence.LocalCi;

/// <summary>A secret (token, runner configuration, throwaway password). It never prints: <see cref="ToString"/> hides it.</summary>
public sealed class SecretText
{
    private readonly string _value;

    public SecretText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    /// <summary>The secret itself. Call only where it must be sent (a request header, a child process).</summary>
    public string Reveal() => _value;

    public override string ToString() => "[hidden]";
}

/// <summary>The program's log. Every implementation removes registered secrets before writing.</summary>
public interface ICiLog
{
    void Info(string message);

    void Warn(string message);

    void Failure(string message);
}

/// <summary>The Windows Scheduled Task that starts the loop at the owner's sign-in.</summary>
public interface IScheduler
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>True while a <c>serve</c> loop is running (any copy).</summary>
    bool LoopIsRunning();

    Task InstallAsync(string programPath, CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task RemoveAsync(CancellationToken cancellationToken);
}

/// <summary>Holds the GitHub token encrypted for the current Windows user.</summary>
public interface ISecretStore
{
    bool Exists();

    SecretText? Load();

    void Save(SecretText secret);

    void Delete();
}

public interface IProcessList
{
    /// <summary>True when a process with any of these names (without .exe) is running. Only the names are looked at.</summary>
    bool IsAnyRunning(IReadOnlyCollection<string> processNames);
}

/// <summary>Tells the owner something in a window or notice (the log is always written as well).</summary>
public interface INotifier
{
    void Notify(string message);
}

/// <summary>Downloads over HTTPS from the approved hosts only.</summary>
public interface IDownloads
{
    Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>Downloads to a file; stops and throws when the answer is larger than <paramref name="maximumBytes"/>.</summary>
    Task DownloadToFileAsync(Uri url, string destinationPath, long maximumBytes, CancellationToken cancellationToken);
}

/// <summary>How much room a drive has left.</summary>
public interface IDiskSpace
{
    long FreeBytes(string path);
}

/// <summary>The questions setup asks of the machine, and the things it installs.</summary>
public interface IMachine
{
    int WindowsBuild { get; }

    long FreeDiskBytes(string path);

    /// <summary>True, false, or null when it cannot be told.</summary>
    bool? VirtualizationEnabled();

    bool IsAdministrator();

    /// <summary>Starts <paramref name="program"/> again with the administrator prompt and waits; returns its exit code.</summary>
    int RelaunchElevated(string program, string arguments);
}

/// <summary>
/// The program's data folder and the other folders setup closes to ordinary users (security review 2026-10-30, H1, M2): creating them with the right permissions,
/// and checking them again later.
/// </summary>
public interface IProtectedStorage
{
    /// <summary>The problems with the data folder (empty when it is as it must be). Checked at every start of the loop.</summary>
    IReadOnlyList<string> CheckDataFolder();

    /// <summary>The problems with a file or folder inside the data folder and every folder above it (empty when it can be trusted).</summary>
    IReadOnlyList<string> CheckInsideDataFolder(string path);

    /// <summary>Creates the data folder with permissions for the system and Administrators only. A folder that is already there but not as it must be is moved aside, never trusted.</summary>
    void CreateOrRepairDataFolder();
}

/// <summary>Whether setup replaced the installed copy of the program.</summary>
public enum InstallOutcome
{
    Installed,
    Updated,
    Unchanged,
}

/// <summary>The program's own installed copy in Program Files (security review 2026-10-30, C1): the scheduled task runs that copy and no other.</summary>
public interface IProgramInstaller
{
    /// <summary>Where the installed copy is (or will be).</summary>
    string InstalledPath { get; }

    bool IsInstalled { get; }

    /// <summary>True when <paramref name="runningPath"/> is the installed copy.</summary>
    bool IsInstalledCopy(string runningPath);

    /// <summary>The problems with the installed copy's place (empty when only Administrators and the system can change it).</summary>
    IReadOnlyList<string> CheckInstalledLocation();

    /// <summary>Copies the running program into its protected folder, replacing an older or different copy after checking the copy byte for byte.</summary>
    Task<InstallOutcome> InstallAsync(string runningPath, CancellationToken cancellationToken);

    /// <summary>Removes the installed copy (what cannot be removed while it runs is removed at the next restart); returns a plain-words note.</summary>
    string Remove();
}

/// <summary>The console, as setup and the menu use it.</summary>
public interface IConsoleUi
{
    void WriteLine(string text);

    string? ReadLine();

    /// <summary>Reads a line without showing it.</summary>
    string? ReadSecret(string prompt);

    bool Confirm(string question);
}
