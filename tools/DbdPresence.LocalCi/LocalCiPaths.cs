namespace DbdPresence.LocalCi;

/// <summary>
/// Where the program keeps its files. Everything lives in the data folder <c>%ProgramData%\DbdLocalCi</c>, which only Administrators and the system can use
/// (security review 2026-10-30, H1, H3): downloads, the saved token, the state, the log and the checksums.
/// </summary>
public sealed class LocalCiPaths
{
    public LocalCiPaths(string programData)
    {
        ArgumentException.ThrowIfNullOrEmpty(programData);
        ProgramData = programData;
        Machine = Path.Combine(programData, "DbdLocalCi");
    }

    /// <summary>%ProgramData%, the parent of the data folder.</summary>
    public string ProgramData { get; }

    /// <summary>The data folder, closed to everyone but Administrators and the system.</summary>
    public string Machine { get; }

    public string BaseDirectory => Path.Combine(Machine, "base");

    public string BaseArchive => Path.Combine(BaseDirectory, "ubuntu-ci.tar");

    public string RunnerCache => Path.Combine(Machine, "runner");

    /// <summary>A temporary folder only Administrators and the system can write to (the programs the loop starts use it instead of the owner's own).</summary>
    public string TemporaryDirectory => Path.Combine(Machine, "tmp");

    /// <summary>The GitHub token, protected with Windows data protection for this computer.</summary>
    public string TokenFile => Path.Combine(Machine, "token.bin");

    /// <summary>The random value that goes with the token's encryption; it is useless without the token file and the other way round.</summary>
    public string TokenEntropyFile => Path.Combine(Machine, "token.entropy");

    public string StateFile => Path.Combine(Machine, "state.json");

    /// <summary>Extra program names that mean "the game runs" (one per line); made by the owner as an administrator. See <see cref="GameNames"/>.</summary>
    public string GameNamesFile => Path.Combine(Machine, "game-names.txt");

    /// <summary>A file the owner makes (as an administrator) to switch the weekly WSL update off; nothing in it is read, only whether it is there.</summary>
    public string WslUpdateOffFile => Path.Combine(Machine, "no-wsl-update");

    /// <summary>Held by the one running loop (<see cref="ServeLock"/>); only Administrators and the system can create a file in the data folder, so nobody else can take it.</summary>
    public string LockFile => Path.Combine(Machine, "serve.lock");

    /// <summary>The lock that lets one program at a time use the account's Linux machine (local CI round 4, F3; see <see cref="FileSessionLock"/>).</summary>
    public string SessionLockFile => Path.Combine(Machine, "session.lock");

    /// <summary>The checksums of every file the loop starts from.</summary>
    public string IntegrityFile => Path.Combine(Machine, "integrity.json");

    public string LogDirectory => Path.Combine(Machine, "logs");

    public string RunnerVersionDirectory(string version) => Path.Combine(RunnerCache, version);

    public string LinuxPackage(string version) => Path.Combine(RunnerVersionDirectory(version), $"actions-runner-linux-x64-{version}.tar.gz");

    /// <summary>
    /// The work folder of the dedicated WSL account, next to the data folder (<c>%ProgramData%\DbdLocalCi-wsl</c>): closed to everyone but the system, Administrators and that account, because the
    /// account's Linux machines keep their disks there. The program's own files are never in it.
    /// </summary>
    public string WslWorkFolder => Path.Combine(ProgramData, "DbdLocalCi-wsl");

    /// <summary>The folder inside the work folder that holds one folder per Linux machine (each job's, and the one the base image is built in).</summary>
    public string WslDistrosFolder => Path.Combine(WslWorkFolder, "distros");

    /// <summary>Where <c>wsl --import</c> puts the disk of the Linux machine with that folder name. WSL creates the folder itself (only its last name), so the folder above must exist.</summary>
    public string WslInstallDirectory(string folderName) => Path.Combine(WslDistrosFolder, folderName);

    /// <summary>The password of the dedicated account, protected with Windows data protection for this computer.</summary>
    public string WslAccountPasswordFile => Path.Combine(Machine, "wsl-account.bin");

    /// <summary>The random value that goes with the account password's encryption.</summary>
    public string WslAccountEntropyFile => Path.Combine(Machine, "wsl-account.entropy");

}

/// <summary>The rule that the data folder is only ever created by setup, with its closed permissions, never as a side effect of writing a file.</summary>
public static class DataFolder
{
    /// <summary>Throws when <paramref name="directory"/> does not exist (instead of creating it with the permissions of its parent).</summary>
    public static void RequireExists(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"{directory} does not exist. Run setup first.");
        }
    }

    /// <summary>Creates <paramref name="directory"/> itself, but only when its parent exists already.</summary>
    public static void CreateChild(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        var parent = Path.GetDirectoryName(directory);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"{parent} does not exist. Run setup first.");
        }

        Directory.CreateDirectory(directory);
    }
}
