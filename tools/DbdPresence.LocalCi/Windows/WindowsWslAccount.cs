using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The dedicated Windows account that owns the Linux machine (local CI round 2, red-team F2): a persistent standard user (<see cref="LocalCiDefaults.WslAccountName"/>) in no group but Users, hidden from
/// the sign-in screen, with a random password the program keeps under Windows data protection (this computer's key plus an extra value) in the closed data folder, and a work folder of its own where its
/// Linux machines keep their disks. WSL, its virtual machine, its distributions and its <c>.wslconfig</c> are per Windows user (microsoft/WSL source and Microsoft Learn, read 2026-10-30), so everything
/// the Linux jobs do is that account's, not the owner's. The account is never a leftover to sweep: only uninstall removes it, completely and on purpose, and only an account that carries this program's
/// comment and is in no other group is ever adopted, reset or removed. PENDING-ENV: no account has been created on a Windows PC by this code yet.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWslAccount : IWslAccount
{
    private const uint LgIncludeIndirect = 0x1;
    private const uint UserPrivUser = 1;
    private const uint NerrUserNotFound = 2221;
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\";
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int EndProgramsAttempts = 6;

    private static readonly TimeSpan s_passwordCheckEvery = TimeSpan.FromMinutes(5);

    private readonly LocalCiPaths _paths;
    private readonly ISecretStore _password;
    private readonly IFileSecurityProbe _probe;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly TimeProvider _time;
    private readonly string _machine = Environment.MachineName;
    private string? _sid;
    private DateTimeOffset _passwordCheckedAt = DateTimeOffset.MinValue;
    private string? _passwordProblem;

    public WindowsWslAccount(LocalCiPaths paths, ISecretStore password, IFileSecurityProbe probe, ICiLog log, Redactor redactor, TimeProvider time)
    {
        _paths = paths;
        _password = password;
        _probe = probe;
        _log = log;
        _redactor = redactor;
        _time = time;
    }

    public string UserName => LocalCiDefaults.WslAccountName;

    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();
        AccountInfo? info;
        try
        {
            info = GetInfo();
        }
        catch (Win32Exception ex)
        {
            return [$"Windows would not describe the account {UserName}: {ex.Message}"];
        }

        if (info is null)
        {
            problems.Add($"the account {UserName} does not exist (run set-up)");
            return problems;
        }

        try
        {
            problems.AddRange(WslAccountPolicy.AccountProblems(UserName, info.Flags, info.Comment, GroupSids()));
        }
        catch (Win32Exception ex)
        {
            problems.Add($"Windows would not list the groups of the account {UserName}: {ex.Message}");
        }

        if (PasswordProblem() is { } password)
        {
            problems.Add(password);
        }

        if (!IsHiddenFromSignIn())
        {
            problems.Add($"the account {UserName} is not hidden from the sign-in screen (run set-up)");
        }

        problems.AddRange(WorkFolderProblems());
        if (SecondaryLogonDisabled())
        {
            problems.Add(WslAccountPolicy.SecondaryLogonDisabledProblem);
        }

        return problems;
    }

    public Task EnsureAsync(CancellationToken cancellationToken) => Task.Run(Ensure, cancellationToken);

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var exists = false;
        string? sid = null;
        try
        {
            exists = GetInfo() is not null;
            sid = exists ? Sid() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IdentityNotMappedException)
        {
            _log.Failure($"Uninstall could not look the account {UserName} up: {_redactor.Describe(ex)}");
            failures.Add($"look the account {UserName} up ({ex.Message})");
        }

        if (exists && Foreign() is { } foreign)
        {
            throw new InvalidOperationException($"{foreign}, so it was not removed. Nothing of it was touched.");
        }

        if (exists)
        {
            await AttemptAsync(failures, "end the programs of the account", () => EndProgramsAsync(cancellationToken)).ConfigureAwait(false);
            if (sid is not null)
            {
                await AttemptAsync(failures, "delete the profile of the account", () => DeleteProfileAsync(sid, cancellationToken)).ConfigureAwait(false);
            }

            await AttemptAsync(failures, "delete the account", () =>
            {
                DeleteAccount();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        await AttemptAsync(failures, "show the account on the sign-in screen again (remove its entry)", () =>
        {
            ShowOnSignInScreenAgain();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await AttemptAsync(failures, "remove the work folder " + _paths.WslWorkFolder, () =>
        {
            RemoveWorkFolder();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await AttemptAsync(failures, "delete the saved password of the account", () =>
        {
            _password.Delete();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        _sid = null;
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("the account " + UserName + " is only partly removed. Not done: " + string.Join("; ", failures));
        }
    }

    /// <summary>The account, its SID and its password, for starting a program as it. Throws when any of them is missing.</summary>
    /// <exception cref="JobEnvironmentException">The account does not exist or its password record cannot be read.</exception>
    public AccountCredentials Credentials()
    {
        var password = _password.Load()
                       ?? throw new JobEnvironmentException($"The saved password of the Windows account {UserName} is missing or cannot be read, so no Linux machine can be started. Run set-up again: it sets a new password.");
        _redactor.Register(password);
        try
        {
            return new AccountCredentials(UserName, Sid(), password);
        }
        catch (IdentityNotMappedException ex)
        {
            throw new JobEnvironmentException($"The Windows account {UserName} does not exist, so no Linux machine can be started. Run set-up again.", ex);
        }
    }

    /// <summary>The account's profile folder, as Windows records it. It exists only after the account has signed in once (the first WSL command as the account does that).</summary>
    /// <exception cref="JobEnvironmentException">The account has no profile folder yet.</exception>
    public string ProfilePath()
    {
        string sid;
        try
        {
            sid = Sid();
        }
        catch (IdentityNotMappedException ex)
        {
            throw new JobEnvironmentException($"The Windows account {UserName} does not exist. Run set-up again.", ex);
        }

        using var key = Registry.LocalMachine.OpenSubKey(ProfileListKey + sid);
        var recorded = key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrEmpty(recorded))
        {
            throw new JobEnvironmentException($"Windows has no profile folder for the account {UserName} yet: it is made when the account first runs a program (the first WSL command). If this stays, run set-up again.");
        }

        var path = Environment.ExpandEnvironmentVariables(recorded);
        if (!Directory.Exists(path) || SafeTree.IsLink(path))
        {
            throw new JobEnvironmentException($"The profile folder {path} of the account {UserName} is not a normal folder, so the WSL settings file is not written there.");
        }

        return path;
    }

    private static bool SecondaryLogonDisabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WslAccountPolicy.SecondaryLogonKey);
        return key?.GetValue("Start") is int start && start == WslAccountPolicy.ServiceDisabled;
    }

    private static async Task AttemptAsync(List<string> failures, string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Every removal step is tried even when an earlier one failed; each failure is kept and reported together at the end.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add($"{what} ({LogText.Clean(ex.Message, 200)})");
        }
    }

    private static string SidOfGroup(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        try
        {
            return new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return string.Empty;
        }
    }

    private void Ensure()
    {
        if (SecondaryLogonDisabled())
        {
            throw new InvalidOperationException("The Windows service 'Secondary Logon' is switched off, and this program needs it to start the Linux machine as its own account. Open Services, set 'Secondary Logon' to Manual, then run set-up again.");
        }

        var existing = GetInfo();
        if (existing is not null && WslAccountPolicy.IdentityProblems(UserName, existing.Comment, GroupSids()) is { Count: > 0 } foreign)
        {
            throw new InvalidOperationException($"An account named {UserName} already exists but {foreign[0]}. Nothing of it was changed. Rename or delete it yourself, then run set-up again.");
        }

        // A new random password at every set-up: nobody has to remember it, the record is replaced in one step, and a changed or lost one is repaired by running set-up again. The record is written first so a
        // stop after it leaves a record that the next run replaces, never an account whose password nobody has.
        var password = new SecretText(WslAccountPassword.New());
        _redactor.Register(password);
        _password.Save(password);
        if (existing is null)
        {
            CreateAccount(password);
            _log.Info($"Created the Windows account {UserName} (standard user, in no group but Users).");
        }
        else
        {
            SetPassword(password);
            _log.Info($"Set a new password for the Windows account {UserName}.");
        }

        _sid = null;
        var current = GetInfo() ?? throw new InvalidOperationException($"Windows did not keep the account {UserName}.");
        SetFlags(WslAccountPolicy.RepairedFlags(current.Flags));
        HideFromSignInScreen();
        CreateOrRepairWorkFolder();
        var problems = Check();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"The account {UserName} is not as it must be after set-up: {problems[0]}.");
        }
    }

    /// <summary>The reason this account is not this program's (no marker comment, another group), or null.</summary>
    private string? Foreign()
    {
        var info = GetInfo();
        return info is null ? null : WslAccountPolicy.IdentityProblems(UserName, info.Comment, GroupSids()) is { Count: > 0 } problems ? $"The account {UserName} is not this program's: {problems[0]}" : null;
    }

    private string? PasswordProblem()
    {
        var now = _time.GetUtcNow();
        if (now - _passwordCheckedAt < s_passwordCheckEvery && _passwordCheckedAt != DateTimeOffset.MinValue)
        {
            return _passwordProblem;
        }

        _passwordCheckedAt = now;
        _passwordProblem = !_password.Exists()
            ? $"the saved password of the account {UserName} is missing (run set-up)"
            : _password.Load() is null ? $"the saved password of the account {UserName} cannot be read (run set-up)" : null;
        return _passwordProblem;
    }

    private List<string> WorkFolderProblems()
    {
        string sid;
        try
        {
            sid = Sid();
        }
        catch (IdentityNotMappedException)
        {
            return [];
        }

        try
        {
            var problems = new PathTrust(_probe).CheckAccountWorkFolder(_paths.WslWorkFolder, sid).ToList();
            if (problems.Count == 0)
            {
                // The account may make and delete what is in the work folder (Modify), so it could swap the folder for its Linux machines for a link to somewhere else (round 3, L4): it must be a real folder.
                if (SafeTree.IsLink(_paths.WslDistrosFolder))
                {
                    problems.Add($"{_paths.WslDistrosFolder} is a link (a junction or symbolic link), not a real folder (run set-up, which moves it aside)");
                }
                else if (!Directory.Exists(_paths.WslDistrosFolder))
                {
                    problems.Add($"{_paths.WslDistrosFolder} does not exist (run set-up)");
                }
            }

            return problems;
        }
        catch (UnauthorizedAccessException ex)
        {
            return [$"The permissions of {_paths.WslWorkFolder} cannot be read ({ex.Message})."];
        }
        catch (IOException ex)
        {
            return [$"{_paths.WslWorkFolder} cannot be checked ({ex.Message})."];
        }
    }

    /// <summary>Makes the work folder with permissions for the system, Administrators and the account only; a folder that is there but not so is moved aside, never repaired in place or trusted.</summary>
    private void CreateOrRepairWorkFolder()
    {
        var sid = new SecurityIdentifier(Sid());
        var folder = _paths.WslWorkFolder;
        if (Directory.Exists(folder) || File.Exists(folder) || SafeTree.IsLink(folder))
        {
            if (Directory.Exists(folder) && new PathTrust(_probe).CheckAccountWorkFolder(folder, sid.Value).Count == 0)
            {
                if (SafeTree.IsLink(_paths.WslDistrosFolder))
                {
                    FolderQuarantine.MoveAside(_paths.WslDistrosFolder, _time, _log);
                }

                DataFolder.CreateChild(_paths.WslDistrosFolder);
                return;
            }

            FolderQuarantine.MoveAside(folder, _time, _log);
        }

        Directory.CreateDirectory(_paths.ProgramData);
        ProtectedCreation.FolderDescriptor([ProtectedCreation.System, ProtectedCreation.Administrators], modify: sid).CreateDirectory(folder);
        var problems = new PathTrust(_probe).CheckAccountWorkFolder(folder, sid.Value);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The work folder was created but is not closed as it must be: " + problems[0]);
        }

        DataFolder.CreateChild(_paths.WslDistrosFolder);
    }

    /// <summary>Removes the work folder only when it is empty (its Linux machines were removed by WSL, which deletes their disks); anything left in it is the owner's to look at, never deleted from here.</summary>
    private void RemoveWorkFolder()
    {
        if (!Directory.Exists(_paths.WslWorkFolder))
        {
            return;
        }

        if (Directory.Exists(_paths.WslDistrosFolder) && !Directory.EnumerateFileSystemEntries(_paths.WslDistrosFolder).Any())
        {
            Directory.Delete(_paths.WslDistrosFolder, recursive: false);
        }

        if (Directory.EnumerateFileSystemEntries(_paths.WslWorkFolder).Any())
        {
            throw new IOException($"{_paths.WslWorkFolder} still holds files (a Linux machine's disk that WSL did not remove?). Look at it and delete it yourself");
        }

        Directory.Delete(_paths.WslWorkFolder, recursive: false);
    }

    /// <summary>
    /// Ends the programs of the account and checks that none is left: taskkill is asked again until it finds nothing (exit code 128), up to six times a second apart, because the programs take a moment to
    /// disappear after they are ended. The filter names the dedicated account and no other (<see cref="TaskkillArguments"/>).
    /// </summary>
    public async Task EndProgramsAsync(CancellationToken cancellationToken)
    {
        ProcessResult? last = null;
        for (var attempt = 1; attempt <= EndProgramsAttempts; attempt++)
        {
            last = await ProcessRunner.RunAsync(ProgramPaths.Taskkill, TaskkillArguments.ForTheAccount(_machine, UserName), null, null, null, cancellationToken).ConfigureAwait(false);
            if (last.ExitCode == TaskkillArguments.NothingFound)
            {
                return;
            }

            if (last.ExitCode != 0)
            {
                throw new InvalidOperationException($"taskkill ended with exit code {last.ExitCode}: {WslOutput.LastLine(last.Output)}");
            }

            if (attempt < EndProgramsAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"programs of the account {UserName} are still running after they were ended {EndProgramsAttempts} times: {WslOutput.LastLine(last?.Output ?? string.Empty)}");
    }

    public string? AccountSid()
    {
        try
        {
            return Sid();
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    public bool ClearLockout()
    {
        try
        {
            var info = GetInfo();
            if (info is null || !WslAccountPolicy.IsLockedOut(info.Flags) || Foreign() is not null)
            {
                return false;
            }

            SetFlags(WslAccountPolicy.WithoutLockout(info.Flags));
            return true;
        }
        catch (Win32Exception ex)
        {
            _log.Failure($"Could not clear the lock-out of the account {UserName}: {_redactor.Describe(ex)}");
            return false;
        }
        catch (IdentityNotMappedException ex)
        {
            _log.Failure($"Could not clear the lock-out of the account {UserName}: {_redactor.Describe(ex)}");
            return false;
        }
    }

    private async Task DeleteProfileAsync(string sid, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (NativeMethods.DeleteProfile(sid, null, null))
            {
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return;
            }

            if (attempt >= 10)
            {
                throw new Win32Exception(error, $"Windows would not delete the profile of {UserName} (it may still be in use; restart the PC and run uninstall again)");
            }

            // The profile is unloaded a moment after the last program of the account ended.
            await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void DeleteAccount()
    {
        var status = NativeMethods.NetUserDel(null, UserName);
        if (status is not (0 or NerrUserNotFound))
        {
            throw new Win32Exception((int)status, $"Windows would not delete the account {UserName} (error {status})");
        }
    }

    private bool IsHiddenFromSignIn()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WslAccountPolicy.HiddenAccountsKey);
        return key?.GetValue(UserName) is int value && value == 0;
    }

    private void HideFromSignInScreen()
    {
        using var key = Registry.LocalMachine.CreateSubKey(WslAccountPolicy.HiddenAccountsKey, writable: true);
        key.SetValue(UserName, 0, RegistryValueKind.DWord);
    }

    private void ShowOnSignInScreenAgain()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WslAccountPolicy.HiddenAccountsKey, writable: true);
        key?.DeleteValue(UserName, throwOnMissingValue: false);
    }

    private string Sid()
    {
        if (_sid is { } known)
        {
            return known;
        }

        var sid = new NTAccount(_machine, UserName).Translate(typeof(SecurityIdentifier)).Value;
        _sid = sid;
        return sid;
    }

    private AccountInfo? GetInfo()
    {
        var status = NativeMethods.NetUserGetInfo(null, UserName, 1, out var buffer);
        if (status == NerrUserNotFound)
        {
            return null;
        }

        if (status != 0)
        {
            throw new Win32Exception((int)status, $"NetUserGetInfo failed with error {status}");
        }

        try
        {
            var info = Marshal.PtrToStructure<UserInfo1>(buffer);
            return new AccountInfo(info.Flags, info.Comment == 0 ? null : Marshal.PtrToStringUni(info.Comment));
        }
        finally
        {
            _ = NativeMethods.NetApiBufferFree(buffer);
        }
    }

    private List<string> GroupSids()
    {
        var status = NativeMethods.NetUserGetLocalGroups(null, UserName, 0, LgIncludeIndirect, out var buffer, NativeMethods.MaxPreferredLength, out var read, out _);
        if (status != 0)
        {
            throw new Win32Exception((int)status, $"NetUserGetLocalGroups failed with error {status}");
        }

        try
        {
            var sids = new List<string>();
            var size = Marshal.SizeOf<LocalGroupUsersInfo0>();
            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupUsersInfo0>(buffer + (i * size));
                sids.Add(SidOfGroup(Marshal.PtrToStringUni(entry.Name)));
            }

            return sids;
        }
        finally
        {
            _ = NativeMethods.NetApiBufferFree(buffer);
        }
    }

    private void CreateAccount(SecretText password)
    {
        var passwordPointer = Marshal.StringToCoTaskMemUni(password.Reveal());
        var namePointer = Marshal.StringToCoTaskMemUni(UserName);
        var commentPointer = Marshal.StringToCoTaskMemUni(WslAccountPolicy.Comment);
        try
        {
            var info = new UserInfo1 { Name = namePointer, Password = passwordPointer, Privilege = UserPrivUser, Comment = commentPointer, Flags = WslAccountPolicy.FlagScript };
            var status = NativeMethods.NetUserAdd(null, 1, ref info, out var parameter);
            if (status != 0)
            {
                throw new Win32Exception((int)status, $"Windows would not create the account {UserName} (error {status}, field {parameter}; 2245 means the password rules of this PC refused the generated password)");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(passwordPointer);
            Marshal.FreeCoTaskMem(namePointer);
            Marshal.FreeCoTaskMem(commentPointer);
        }
    }

    private void SetPassword(SecretText password)
    {
        var passwordPointer = Marshal.StringToCoTaskMemUni(password.Reveal());
        try
        {
            var info = new UserInfo1003 { Password = passwordPointer };
            var status = NativeMethods.NetUserSetPassword(null, UserName, 1003, ref info, out var parameter);
            if (status != 0)
            {
                throw new Win32Exception((int)status, $"Windows would not set the password of the account {UserName} (error {status}, field {parameter})");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(passwordPointer);
        }
    }

    private void SetFlags(uint flags)
    {
        var info = new UserInfo1008 { Flags = flags };
        var status = NativeMethods.NetUserSetFlags(null, UserName, 1008, ref info, out var parameter);
        if (status != 0)
        {
            throw new Win32Exception((int)status, $"Windows would not set the flags of the account {UserName} (error {status}, field {parameter})");
        }
    }

    private sealed record AccountInfo(uint Flags, string? Comment);
}
