namespace DbdPresence.LocalCi;

/// <summary>
/// Checks every component of a path, from the drive down to the file, with <see cref="AccessRules"/> (security review 2026-10-30, H1, C1).
/// A path is only used by the elevated loop when none of its components is a link, each is owned by Administrators or the system, and nobody without
/// rights can change, replace or remove it.
/// </summary>
public sealed class PathTrust
{
    private readonly IFileSecurityProbe _probe;

    public PathTrust(IFileSecurityProbe probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// Problems with <paramref name="path"/> inside the data folder <paramref name="root"/>: the folders above the root, the root itself (admin-only, own
    /// permissions) and every component below it (admin-only, not a link). Empty means the path can be trusted.
    /// </summary>
    public IReadOnlyList<string> CheckInsideDataFolder(string root, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullRoot = Normalize(root);
        var fullPath = Normalize(path);
        if (!IsSameOrInside(fullRoot, fullPath))
        {
            return [$"{fullPath} is not inside the program's data folder {fullRoot}."];
        }

        var problems = new List<string>();
        foreach (var folder in FoldersAbove(fullRoot))
        {
            problems.AddRange(AccessRules.AncestorFolderProblems(_probe.Describe(folder)));
        }

        problems.AddRange(AccessRules.AdminOnlyFolderProblems(_probe.Describe(fullRoot)));
        foreach (var component in ComponentsBelow(fullRoot, fullPath))
        {
            problems.AddRange(AccessRules.AdminOnlyEntryProblems(_probe.Describe(component)));
        }

        return problems;
    }

    /// <summary>Problems with the data folder alone (checked at every start of the loop).</summary>
    public IReadOnlyList<string> CheckDataFolder(string root) => CheckInsideDataFolder(root, root);

    /// <summary>
    /// Problems with the place the elevated program runs from: every folder above it must be safe from tampering, its own folder must be closed to everyone but
    /// Administrators and the system (users may only read and run), and the file must be a real file nobody else can change.
    /// </summary>
    public IReadOnlyList<string> CheckProgramLocation(string programPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        var fullPath = Normalize(programPath);
        var folder = Path.GetDirectoryName(fullPath);
        if (folder is null)
        {
            return [$"{fullPath} is not a program file."];
        }

        var problems = new List<string>();
        foreach (var above in FoldersAbove(folder))
        {
            problems.AddRange(AccessRules.AncestorFolderProblems(_probe.Describe(above)));
        }

        problems.AddRange(AccessRules.ProgramFolderProblems(_probe.Describe(folder)));
        problems.AddRange(AccessRules.ProgramFileProblems(_probe.Describe(fullPath)));
        return problems;
    }

    /// <summary>
    /// Problems with the work folder of the dedicated WSL account: every folder above it must be safe from tampering and the folder itself must belong to the system and Administrators with its own
    /// permissions that give nobody but the system, Administrators and that account any access.
    /// </summary>
    public IReadOnlyList<string> CheckAccountWorkFolder(string folder, string accountSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(accountSid);
        var full = Normalize(folder);
        var problems = new List<string>();
        foreach (var above in FoldersAbove(full))
        {
            problems.AddRange(AccessRules.AncestorFolderProblems(_probe.Describe(above)));
        }

        problems.AddRange(AccessRules.WslWorkFolderProblems(_probe.Describe(full), accountSid));
        return problems;
    }

    /// <summary>The full path without a closing separator (the drive root keeps its own).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies below it. Windows compares names without regard to case.</summary>
    public static bool IsSameOrInside(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folders above <paramref name="path"/> from the drive down to its parent.</summary>
    private static List<string> FoldersAbove(string path)
    {
        var folders = new List<string>();
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            folders.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }

        folders.Reverse();
        return folders;
    }

    /// <summary>Every component after <paramref name="root"/> down to and including <paramref name="path"/>.</summary>
    private static List<string> ComponentsBelow(string root, string path)
    {
        var components = new List<string>();
        var current = path;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            components.Add(current);
            current = Path.GetDirectoryName(current) ?? root;
            if (components.Count > 255)
            {
                break;
            }
        }

        components.Reverse();
        return components;
    }
}
