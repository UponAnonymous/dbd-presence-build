using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The program's own installed copy (security review 2026-10-30, C1): <c>C:\Program Files\DbdLocalCi\DbdLocalCi.exe</c>, in a folder where the system and Administrators have full control and every
/// user may only read and run. The scheduled task runs that copy and no other. A copy someone else could replace would be code run with administrator rights.
/// PENDING-ENV: not run on a Windows PC yet.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProgramInstaller : IProgramInstaller
{
    private const string FolderName = "DbdLocalCi";
    private const string FileName = "DbdLocalCi.exe";
    private const int MoveAttempts = 5;

    private readonly string _folder;
    private readonly IFileSecurityProbe _probe;
    private readonly ICiLog _log;

    public WindowsProgramInstaller(string programFiles, IFileSecurityProbe probe, ICiLog log)
    {
        _folder = Path.Combine(programFiles, FolderName);
        _probe = probe;
        _log = log;
        InstalledPath = Path.Combine(_folder, FileName);
        ProgramPaths.Approve(InstalledPath);
    }

    public string InstalledPath { get; }

    public bool IsInstalled => File.Exists(InstalledPath);

    public bool IsInstalledCopy(string runningPath) =>
        string.Equals(PathTrust.Normalize(runningPath), PathTrust.Normalize(InstalledPath), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> CheckInstalledLocation()
    {
        try
        {
            return new PathTrust(_probe).CheckProgramLocation(InstalledPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return [$"The permissions of {InstalledPath} cannot be read ({ex.Message})."];
        }
        catch (IOException ex)
        {
            return [$"{InstalledPath} cannot be checked ({ex.Message})."];
        }
    }

    /// <summary>
    /// Copies the program that is running into the protected folder. The file set-up was started from lives where the owner's own programs can write, so it is read through one handle that follows
    /// no link and that nobody can write to, rename or delete while it is held (<see cref="VerifiedUserFile"/>): a link put at that path could otherwise make this elevated copy read any file (the
    /// saved token) and put it where every user can read it (security review 2026-10-30, the same confused-deputy class as the clean-up of a job's folder). What is hashed, compared and copied is
    /// that one open file. A program a different process swapped in before set-up started is the owner's own start, as with any installer, and is documented as such.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(string runningPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runningPath);
        EnsureFolder();
        var source = VerifiedUserFile.OpenRead(runningPath) ?? throw new FileNotFoundException($"{runningPath} is not there any more.");
        await using (source.ConfigureAwait(false))
        {
            var runningHash = await Hashing.Sha256OfStreamAsync(source, cancellationToken).ConfigureAwait(false);
            source.Position = 0;
            var existed = File.Exists(InstalledPath);
            if (existed && Hashing.SameHash(runningHash, await Hashing.Sha256OfFileAsync(InstalledPath, cancellationToken).ConfigureAwait(false)))
            {
                return InstallOutcome.Unchanged;
            }

            // Copied next to the target first and checked byte for byte, then moved over it: the installed file is never half written. The folder is closed to ordinary users.
            var temporary = InstalledPath + ".new";
            var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            if (!Hashing.SameHash(runningHash, await Hashing.Sha256OfFileAsync(temporary, cancellationToken).ConfigureAwait(false)))
            {
                File.Delete(temporary);
                throw new IOException("The copy of the program in Program Files does not match the original; it was deleted.");
            }

            await MoveOverAsync(temporary, cancellationToken).ConfigureAwait(false);
            _log.Info(existed ? $"Updated {InstalledPath}." : $"Installed {InstalledPath}.");
            return existed ? InstallOutcome.Updated : InstallOutcome.Installed;
        }
    }

    /// <summary>The stopped loop may need a moment to let go of the old file.</summary>
    private async Task MoveOverAsync(string temporary, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, InstalledPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public string Remove()
    {
        if (!Directory.Exists(_folder))
        {
            return "The program was not installed in Program Files.";
        }

        try
        {
            SafeTree.Delete(_folder);
            return "The program was removed from Program Files.";
        }
        catch (IOException)
        {
            return ScheduleRemovalAtRestart();
        }
        catch (UnauthorizedAccessException)
        {
            return ScheduleRemovalAtRestart();
        }
    }

    /// <summary>What cannot be deleted while it runs (this very program) is deleted by Windows at the next restart: the files first, then the folder.</summary>
    private string ScheduleRemovalAtRestart()
    {
        var scheduled = true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_folder, "*", SearchOption.AllDirectories).OrderByDescending(e => e.Length))
        {
            scheduled &= NativeMethods.MoveFileEx(entry, null, NativeMethods.DelayUntilReboot);
        }

        scheduled &= NativeMethods.MoveFileEx(_folder, null, NativeMethods.DelayUntilReboot);
        return scheduled
            ? $"The program in {_folder} is in use right now; Windows will delete it at the next restart."
            : $"The program in {_folder} is in use and could not be scheduled for removal; delete that folder yourself after closing this window.";
    }

    private void EnsureFolder()
    {
        if (Directory.Exists(_folder))
        {
            var problems = AccessRules.ProgramFolderProblems(_probe.Describe(_folder));
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"{_folder} already exists but is not closed to ordinary users: {problems[0]} It is not used. Delete or repair it, then run setup again.");
            }

            return;
        }

        ProtectedCreation.FolderDescriptor([ProtectedCreation.System, ProtectedCreation.Administrators], ProtectedCreation.Users).CreateDirectory(_folder);
        var created = AccessRules.ProgramFolderProblems(_probe.Describe(_folder));
        if (created.Count > 0)
        {
            throw new InvalidOperationException($"{_folder} was created but is not closed as it must be: {created[0]}");
        }
    }
}
