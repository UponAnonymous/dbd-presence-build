namespace DbdPresence.LocalCi;

/// <summary>
/// The access rules of the program as pure functions over <see cref="NodeSecurity"/> (security review 2026-10-30, C1 H1 H3 M2; local CI round 2, F2). Each returns the problems it found in
/// plain words; an empty list means the object is as the program needs it. Nothing here touches the file system, so every rule is tested without Windows.
/// </summary>
/// <remarks>
/// Why the owner matters: the owner of a file or folder can always change its permissions, whatever the permissions say. A file owned by an ordinary
/// user can therefore be re-opened by any program running as that user. Everything the elevated loop trusts must be owned by Administrators or the system.
/// Why links matter: a junction or symbolic link in a path lets a program without rights send the elevated loop to another place.
/// </remarks>
public static class AccessRules
{
    private static readonly HashSet<string> s_systemAndAdministrators = new(StringComparer.Ordinal)
    {
        WellKnownSids.LocalSystem,
        WellKnownSids.Administrators,
    };

    private static readonly HashSet<string> s_trusted = new(StringComparer.Ordinal)
    {
        WellKnownSids.LocalSystem,
        WellKnownSids.Administrators,
        WellKnownSids.TrustedInstaller,
    };

    /// <summary>True for Administrators, the system or the Windows installer: owners whose files the elevated loop may trust.</summary>
    public static bool IsTrustedOwner(string? sid) => sid is not null && s_trusted.Contains(sid);

    /// <summary>True when the rights let the holder write to a file or add, change or delete entries of a folder, or change who may.</summary>
    public static bool MayChangeContent(uint mask) =>
        (mask & (FileRights.WriteData | FileRights.AppendData | FileRights.WriteExtendedAttributes | FileRights.DeleteChild | FileRights.WriteAttributes
                 | FileRights.Delete | FileRights.WriteDac | FileRights.WriteOwner | FileRights.GenericAll | FileRights.GenericWrite | FileRights.MaximumAllowed)) != 0;

    /// <summary>
    /// True when the rights let the holder of a folder remove or rename what is inside it (or take the folder over). Adding a new entry is not enough:
    /// it cannot replace an entry that is already there.
    /// </summary>
    public static bool MayReplaceChildren(uint mask) =>
        (mask & (FileRights.DeleteChild | FileRights.Delete | FileRights.WriteDac | FileRights.WriteOwner | FileRights.GenericAll | FileRights.GenericWrite | FileRights.MaximumAllowed)) != 0;

    /// <summary>
    /// A folder only the system and Administrators may use (the program's data folder): a real folder, owned by them, with its own
    /// permissions that give them full control and give nobody else anything. Entries that apply only to children count too, because the children inherit them.
    /// </summary>
    public static IReadOnlyList<string> AdminOnlyFolderProblems(NodeSecurity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var problems = new List<string>();
        if (!RealFolder(node, problems))
        {
            return problems;
        }

        RequireOwner(node, s_systemAndAdministrators, problems);
        if (!node.DaclProtected)
        {
            problems.Add($"{node.Path} takes its permissions from the folder above it, so other users may get access. It must have its own permissions.");
        }

        foreach (var sid in StrangersWithAccess(node, includeInheritOnly: true, s_systemAndAdministrators))
        {
            problems.Add($"{node.Path} gives access to {Describe(sid)}. Only the system and Administrators may have any.");
        }

        RequireFullControl(node, WellKnownSids.LocalSystem, "the system", problems);
        RequireFullControl(node, WellKnownSids.Administrators, "Administrators", problems);
        return problems;
    }

    /// <summary>A file or folder inside the data folder: not a link, owned by Administrators or the system, and no access for anyone else.</summary>
    public static IReadOnlyList<string> AdminOnlyEntryProblems(NodeSecurity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var problems = new List<string>();
        if (!node.Exists)
        {
            problems.Add($"{node.Path} does not exist.");
            return problems;
        }

        if (node.IsReparsePoint)
        {
            problems.Add($"{node.Path} is a link (a junction or symbolic link), not a real file or folder.");
        }

        RequireOwner(node, s_systemAndAdministrators, problems);
        foreach (var sid in StrangersWithAccess(node, includeInheritOnly: true, s_systemAndAdministrators))
        {
            problems.Add($"{node.Path} gives access to {Describe(sid)}. Only the system and Administrators may have any.");
        }

        return problems;
    }

    /// <summary>
    /// A folder above the program or the data folder (the drive, ProgramData, Program Files): not a link, a trusted owner, and nobody without rights may remove,
    /// rename or take over what is inside it. Creating new entries is allowed (ProgramData lets every user do that).
    /// </summary>
    public static IReadOnlyList<string> AncestorFolderProblems(NodeSecurity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var problems = new List<string>();
        if (!RealFolder(node, problems))
        {
            return problems;
        }

        RequireOwner(node, s_trusted, problems);
        foreach (var ace in node.Aces.Where(a => a.Allow && !a.InheritOnly && !IsCreatorPlaceholder(a.Sid) && !s_trusted.Contains(a.Sid) && MayReplaceChildren(a.Mask)))
        {
            problems.Add($"{node.Path} lets {Describe(ace.Sid)} remove, rename or take over what is inside it.");
        }

        return problems;
    }

    /// <summary>
    /// The program's own folder (C:\Program Files\DbdLocalCi): a real folder with its own permissions where the system and Administrators have full control,
    /// every user may only read and run, and nobody else has anything.
    /// </summary>
    public static IReadOnlyList<string> ProgramFolderProblems(NodeSecurity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var problems = new List<string>();
        if (!RealFolder(node, problems))
        {
            return problems;
        }

        RequireOwner(node, s_trusted, problems);
        if (!node.DaclProtected)
        {
            problems.Add($"{node.Path} takes its permissions from the folder above it. It must have its own permissions.");
        }

        var allowed = new HashSet<string>(s_trusted, StringComparer.Ordinal) { WellKnownSids.Users };
        foreach (var sid in StrangersWithAccess(node, includeInheritOnly: true, allowed))
        {
            problems.Add($"{node.Path} gives access to {Describe(sid)}. Only the system and Administrators may change the program, and users may only read and run it.");
        }

        foreach (var ace in node.Aces.Where(a => a.Allow && a.Sid == WellKnownSids.Users && MayChangeContent(a.Mask)))
        {
            problems.Add($"{node.Path} lets every user change the program. Users may only read and run it.");
        }

        RequireFullControl(node, WellKnownSids.LocalSystem, "the system", problems);
        RequireFullControl(node, WellKnownSids.Administrators, "Administrators", problems);
        return problems;
    }

    /// <summary>The program file itself: a real file with a trusted owner that nobody without rights may change.</summary>
    public static IReadOnlyList<string> ProgramFileProblems(NodeSecurity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var problems = new List<string>();
        if (!node.Exists)
        {
            problems.Add($"{node.Path} does not exist.");
            return problems;
        }

        if (node.IsDirectory)
        {
            problems.Add($"{node.Path} is a folder, not the program.");
        }

        if (node.IsReparsePoint)
        {
            problems.Add($"{node.Path} is a link, not the program itself.");
        }

        RequireOwner(node, s_trusted, problems);
        foreach (var ace in node.Aces.Where(a => a.Allow && !a.InheritOnly && !IsCreatorPlaceholder(a.Sid) && !s_trusted.Contains(a.Sid) && MayChangeContent(a.Mask)))
        {
            problems.Add($"{node.Path} can be changed by {Describe(ace.Sid)}. Only the system and Administrators may change the program.");
        }

        return problems;
    }

    /// <summary>
    /// The work folder of the dedicated WSL account (<c>C:\ProgramData\DbdLocalCi-wsl</c>): a real folder, owned by Administrators or the system, with its own permissions for the system, Administrators
    /// and that account only. The account puts the disks of its Linux machines there; nobody else may have any access. The account has Modify on it and not full control (local CI round 3, L4): it can
    /// make, change and delete what is in the folder, but it cannot change the permissions or the owner of the folder itself, so it cannot open the folder to others or take it over. An account that
    /// has full control (an earlier version's folder) is refused: the folder is moved aside and made again.
    /// </summary>
    public static IReadOnlyList<string> WslWorkFolderProblems(NodeSecurity node, string accountSid)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrEmpty(accountSid);
        var problems = new List<string>();
        if (!RealFolder(node, problems))
        {
            return problems;
        }

        RequireOwner(node, s_systemAndAdministrators, problems);
        if (!node.DaclProtected)
        {
            problems.Add($"{node.Path} takes its permissions from the folder above it. It must have its own permissions.");
        }

        var allowed = new HashSet<string>(s_systemAndAdministrators, StringComparer.Ordinal) { accountSid };
        foreach (var sid in StrangersWithAccess(node, includeInheritOnly: true, allowed))
        {
            problems.Add($"{node.Path} gives access to {Describe(sid)}. Only the system, Administrators and the account for the Linux machine may have any.");
        }

        RequireModifyOnly(node, accountSid, "the account for the Linux machine", problems);
        return problems;
    }

    /// <summary>
    /// What the holders of the given identities (the account and the groups its logon belongs to) may do in <paramref name="node"/>, from the entries that apply to the folder itself. Deny entries for those
    /// identities are taken off. The right to change what is in the folder is read from delete-child, or from delete together with write (what "Modify" gives, which is passed on to the entries in the folder).
    /// This reads permissions; it never changes any (round 3, H1 audit).
    /// </summary>
    public static WriteAccess AccountMayWrite(NodeSecurity node, IReadOnlySet<string> identities)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(identities);
        if (!node.Exists)
        {
            return WriteAccess.None;
        }

        var allowed = node.Aces.Where(a => a.Allow && !a.InheritOnly && identities.Contains(a.Sid)).Aggregate(0u, (all, a) => all | a.Mask);
        var denied = node.Aces.Where(a => !a.Allow && !a.InheritOnly && identities.Contains(a.Sid)).Aggregate(0u, (all, a) => all | a.Mask);
        var here = allowed & ~denied;
        var access = WriteAccess.None;
        if ((here & FileRights.WriteData) != 0)
        {
            access |= WriteAccess.CreateFiles;
        }

        if ((here & FileRights.AppendData) != 0)
        {
            access |= WriteAccess.CreateFolders;
        }

        if ((here & FileRights.DeleteChild) != 0 || ((here & FileRights.Delete) != 0 && (here & FileRights.WriteData) != 0))
        {
            access |= WriteAccess.ChangeEntries;
        }

        if ((here & (FileRights.WriteDac | FileRights.WriteOwner)) != 0)
        {
            access |= WriteAccess.ChangePermissions;
        }

        return access;
    }

    /// <summary>The users behind the permissions of <paramref name="node"/> that are not in <paramref name="allowed"/> (allow entries only).</summary>
    private static IEnumerable<string> StrangersWithAccess(NodeSecurity node, bool includeInheritOnly, HashSet<string> allowed) =>
        node.Aces
            .Where(a => a.Allow && (includeInheritOnly || !a.InheritOnly) && !IsCreatorPlaceholder(a.Sid) && !allowed.Contains(a.Sid) && a.Mask != 0)
            .Select(a => a.Sid)
            .Distinct(StringComparer.Ordinal);

    private static bool IsCreatorPlaceholder(string sid) => sid is WellKnownSids.CreatorOwner or WellKnownSids.CreatorGroup;

    private static bool RealFolder(NodeSecurity node, List<string> problems)
    {
        if (!node.Exists)
        {
            problems.Add($"{node.Path} does not exist.");
            return false;
        }

        if (!node.IsDirectory)
        {
            problems.Add($"{node.Path} is a file, not a folder.");
            return false;
        }

        if (node.IsReparsePoint)
        {
            problems.Add($"{node.Path} is a link (a junction or symbolic link), not a real folder.");
        }

        return true;
    }

    private static void RequireOwner(NodeSecurity node, HashSet<string> allowed, List<string> problems)
    {
        if (node.OwnerSid is null)
        {
            problems.Add($"{node.Path} has an owner that could not be read.");
        }
        else if (!allowed.Contains(node.OwnerSid))
        {
            problems.Add($"{node.Path} is owned by {Describe(node.OwnerSid)}. Its owner must be Administrators or the system, because an owner can always change who has access.");
        }
    }

    /// <summary>The identity has the right to change what is in the folder (Modify) and not the right to change the folder's own permissions or owner.</summary>
    private static void RequireModifyOnly(NodeSecurity node, string sid, string who, List<string> problems)
    {
        var rights = node.Aces.Where(a => a.Allow && a.Sid == sid && !a.InheritOnly).Aggregate(0u, (all, a) => all | a.Mask);
        if ((rights & FileRights.Modify) != FileRights.Modify)
        {
            problems.Add($"{node.Path} does not give {who} the right to change what is in it (Modify).");
        }

        if ((rights & (FileRights.WriteDac | FileRights.WriteOwner)) != 0)
        {
            problems.Add($"{node.Path} gives {who} the right to change its own permissions or its owner. It needs Modify, not full control.");
        }
    }

    private static void RequireFullControl(NodeSecurity node, string sid, string who, List<string> problems)
    {
        var full = node.Aces.Any(a => a.Allow && a.Sid == sid && !a.InheritOnly && (a.Mask & FileRights.FullControl) == FileRights.FullControl);
        if (!full)
        {
            problems.Add($"{node.Path} does not give {who} full control.");
        }
    }

    private static string Describe(string sid) => sid switch
    {
        WellKnownSids.LocalSystem => "the system",
        WellKnownSids.Administrators => "Administrators",
        WellKnownSids.Users => "all users",
        WellKnownSids.AuthenticatedUsers => "all signed-in users",
        WellKnownSids.Everyone => "everyone",
        WellKnownSids.TrustedInstaller => "the Windows installer",
        _ => "account " + sid,
    };
}
