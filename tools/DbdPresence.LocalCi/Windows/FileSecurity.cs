using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// Reads the real owner, permissions and link status of a file or folder. A link (junction, symbolic link or any other reparse point) is reported as one and its target is never looked at.
/// PENDING-ENV: not run on a Windows PC yet.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileSecurityProbe : IAuditSecurityProbe
{
    /// <summary>
    /// The read the audit of writable places makes after Describe was refused (round 4, F1): the same read with the backup right switched on for its length and off again afterwards. Throws when the program
    /// does not hold the right, when the read is still refused, or when the right could not be switched off again (the audit then names the place as one it could not check). PENDING-ENV.
    /// </summary>
    public NodeSecurity DescribeWithBackupRights(string path)
    {
        var right = BackupPrivilege.TryEnable() ?? throw new UnauthorizedAccessException("the program does not hold the backup right, or Windows would not switch it on");
        NodeSecurity node;
        try
        {
            node = Describe(path);
        }
        catch (Exception ex)
        {
            if (!right.SwitchOff())
            {
                throw new InvalidOperationException("the read was refused, and the backup right could not be switched off again afterwards", ex);
            }

            throw;
        }

        if (!right.SwitchOff())
        {
            throw new InvalidOperationException("the backup right could not be switched off again after the read");
        }

        return node;
    }

    public NodeSecurity Describe(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return NodeSecurity.Missing(path);
        }
        catch (DirectoryNotFoundException)
        {
            return NodeSecurity.Missing(path);
        }

        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new NodeSecurity(path, true, isDirectory, true, null, false, []);
        }

        // READ_CONTROL is all it takes to read an owner and a DACL; an administrator has it on everything this program looks at.
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var aces = new List<AceRecord>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            aces.Add(new AceRecord(
                ((SecurityIdentifier)rule.IdentityReference).Value,
                rule.AccessControlType == AccessControlType.Allow,
                unchecked((uint)(int)rule.FileSystemRights),
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        }

        return new NodeSecurity(path, true, isDirectory, false, owner?.Value, security.AreAccessRulesProtected, aces);
    }
}

/// <summary>Creates files and folders whose permissions are part of their creation (security review 2026-10-30, H1): there is never a moment in which a new entry has the permissions of its parent.</summary>
[SupportedOSPlatform("windows")]
internal static class ProtectedCreation
{
    public static SecurityIdentifier System { get; } = new(WellKnownSidType.LocalSystemSid, null);

    public static SecurityIdentifier Administrators { get; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static SecurityIdentifier Users { get; } = new(WellKnownSidType.BuiltinUsersSid, null);

    private const InheritanceFlags Everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// A folder security descriptor: owned by Administrators, not inheriting anything, with full control for the given identities, Modify (and no right to change the folder's permissions or owner) for
    /// <paramref name="modify"/> and read and run for <paramref name="readAndRun"/>.
    /// </summary>
    public static DirectorySecurity FolderDescriptor(IEnumerable<SecurityIdentifier> fullControl, SecurityIdentifier? readAndRun = null, SecurityIdentifier? modify = null)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in fullControl)
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, Everything, PropagationFlags.None, AccessControlType.Allow));
        }

        if (modify is not null)
        {
            // Modify with SYNCHRONIZE is what Explorer stores for "Modify"; without it a program cannot wait on its own file handles.
            security.AddAccessRule(new FileSystemAccessRule(modify, FileSystemRights.Modify | FileSystemRights.Synchronize, Everything, PropagationFlags.None, AccessControlType.Allow));
        }

        if (readAndRun is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(readAndRun, FileSystemRights.ReadAndExecute, Everything, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    public static FileSecurity FileDescriptor()
    {
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Creates a new file that only the system and Administrators can use and writes <paramref name="content"/> to it. Fails when the file exists.</summary>
    public static void WriteNewFile(string path, byte[] content)
    {
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Synchronize, FileShare.None, 4096, FileOptions.None, FileDescriptor());
        stream.Write(content);
    }
}

/// <summary>
/// The data folder and the other folders only Administrators may use (security review 2026-10-30, H1 M2). Checking uses <see cref="PathTrust"/> over the real probe; creating uses
/// <see cref="ProtectedCreation"/>. A folder that is already there but not as it must be is never repaired in place and never trusted: it is moved aside under a new name.
/// PENDING-ENV: not run on a Windows PC yet.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProtectedStorage : IProtectedStorage
{
    private readonly LocalCiPaths _paths;
    private readonly IFileSecurityProbe _probe;
    private readonly ICiLog _log;
    private readonly TimeProvider _time;

    public WindowsProtectedStorage(LocalCiPaths paths, IFileSecurityProbe probe, ICiLog log, TimeProvider time)
    {
        _paths = paths;
        _probe = probe;
        _log = log;
        _time = time;
    }

    public IReadOnlyList<string> CheckDataFolder() => Guarded(() => new PathTrust(_probe).CheckDataFolder(_paths.Machine), _paths.Machine);

    public IReadOnlyList<string> CheckInsideDataFolder(string path) => Guarded(() => new PathTrust(_probe).CheckInsideDataFolder(_paths.Machine, path), path);

    public void CreateOrRepairDataFolder()
    {
        if (Exists(_paths.Machine))
        {
            if (CheckDataFolder().Count == 0)
            {
                EnsureSubfolders();
                return;
            }

            MoveAside(_paths.Machine);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Machine)!);
        ProtectedCreation.FolderDescriptor([ProtectedCreation.System, ProtectedCreation.Administrators]).CreateDirectory(_paths.Machine);
        var problems = CheckDataFolder();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The data folder was created but is not closed as it must be: " + problems[0]);
        }

        EnsureSubfolders();
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path) || SafeTree.IsLink(path);

    private static IReadOnlyList<string> Guarded(Func<IReadOnlyList<string>> check, string what)
    {
        try
        {
            return check();
        }
        catch (UnauthorizedAccessException ex)
        {
            return [$"The permissions of {what} cannot be read ({ex.Message})."];
        }
        catch (IOException ex)
        {
            return [$"{what} cannot be checked ({ex.Message})."];
        }
    }

    private void EnsureSubfolders()
    {
        DataFolder.CreateChild(_paths.TemporaryDirectory);
        DataFolder.CreateChild(_paths.LogDirectory);
    }

    private void MoveAside(string path) => FolderQuarantine.MoveAside(path, _time, _log);
}

/// <summary>
/// A folder that must be closed to ordinary users but is not (or a link where a folder must be) is never repaired in place and never trusted (security review 2026-10-30, H1): a link is only unlinked (its
/// target is left alone), anything else is renamed to <c>name.untrusted-&lt;time&gt;</c> next to it, and the owner is told where it went.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FolderQuarantine
{
    public static void MoveAside(string path, TimeProvider time, ICiLog log)
    {
        if (SafeTree.IsLink(path))
        {
            log.Warn($"{path} was a link; the link was removed.");
            SafeTree.Delete(path);
            return;
        }

        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var aside = path + ".untrusted-" + stamp;
        if (Directory.Exists(path))
        {
            Directory.Move(path, aside);
        }
        else
        {
            File.Move(path, aside);
        }

        log.Warn($"{path} was not closed to ordinary users, so nothing in it can be trusted. It was moved to {aside}; delete that when you have looked at it.");
    }
}
