namespace DbdPresence.LocalCi;

/// <summary>
/// Well-known security identifiers in string form (S-1-...). The access rules compare identifiers as text so they can be tested on any system
/// without Windows (security review 2026-10-30, C1, H1).
/// </summary>
public static class WellKnownSids
{
    public const string LocalSystem = "S-1-5-18";

    public const string Administrators = "S-1-5-32-544";

    public const string Users = "S-1-5-32-545";

    public const string AuthenticatedUsers = "S-1-5-11";

    public const string Everyone = "S-1-1-0";

    /// <summary>CREATOR OWNER: only ever meaningful in an ACE that is inherited by new children; it gives nothing on the object that carries it.</summary>
    public const string CreatorOwner = "S-1-3-0";

    public const string CreatorGroup = "S-1-3-1";

    /// <summary>NT SERVICE\TrustedInstaller, the owner of the Windows folders (Program Files and the like).</summary>
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
}

/// <summary>File and folder access rights as the numbers Windows stores in an access-control entry (winnt.h).</summary>
public static class FileRights
{
    /// <summary>FILE_READ_DATA / FILE_LIST_DIRECTORY.</summary>
    public const uint ReadData = 0x1;

    /// <summary>FILE_WRITE_DATA / FILE_ADD_FILE.</summary>
    public const uint WriteData = 0x2;

    /// <summary>FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY.</summary>
    public const uint AppendData = 0x4;

    public const uint ReadExtendedAttributes = 0x8;

    public const uint WriteExtendedAttributes = 0x10;

    /// <summary>FILE_EXECUTE / FILE_TRAVERSE.</summary>
    public const uint Execute = 0x20;

    /// <summary>FILE_DELETE_CHILD: delete or rename an entry of a folder whatever the entry's own permissions say.</summary>
    public const uint DeleteChild = 0x40;

    public const uint ReadAttributes = 0x80;

    public const uint WriteAttributes = 0x100;

    public const uint Delete = 0x10000;

    public const uint ReadControl = 0x20000;

    public const uint WriteDac = 0x40000;

    public const uint WriteOwner = 0x80000;

    public const uint Synchronize = 0x100000;

    public const uint FullControl = 0x1F01FF;

    /// <summary>
    /// "Modify" as Explorer stores it: read, write, append, attributes, execute, delete and read the permissions, plus the standard SYNCHRONIZE bit. It has neither WRITE_DAC nor WRITE_OWNER, so an account
    /// that has only this on a folder cannot change who may use the folder (local CI round 3, L4).
    /// </summary>
    public const uint Modify = ReadData | WriteData | AppendData | ReadExtendedAttributes | WriteExtendedAttributes | Execute | ReadAttributes | WriteAttributes | Delete | ReadControl | Synchronize;

    /// <summary>Read and execute, with the standard SYNCHRONIZE bit: what "Users: Read &amp; execute" stores.</summary>
    public const uint ReadAndExecute = ReadData | ReadExtendedAttributes | Execute | ReadAttributes | ReadControl | Synchronize;

    public const uint MaximumAllowed = 0x02000000;

    public const uint GenericAll = 0x10000000;

    public const uint GenericWrite = 0x40000000;
}

/// <summary>One access-control entry of a file or folder, reduced to what the rules need.</summary>
/// <param name="Sid">Who the entry is for.</param>
/// <param name="Allow">True for an "allow" entry, false for "deny".</param>
/// <param name="Mask">The access rights (see <see cref="FileRights"/>).</param>
/// <param name="InheritOnly">True when the entry applies to children only, not to the object that carries it.</param>
public sealed record AceRecord(string Sid, bool Allow, uint Mask, bool InheritOnly = false);

/// <summary>What the program learns about one file or folder: whether it exists, whether it is a link, who owns it and who may do what with it.</summary>
/// <param name="Path">The path asked about.</param>
/// <param name="Exists">False when there is nothing at that path (the rest is then meaningless).</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="IsReparsePoint">True for a junction, a symbolic link or any other reparse point: a name that can lead somewhere else.</param>
/// <param name="OwnerSid">The owner, or null when it could not be read.</param>
/// <param name="DaclProtected">True when the permissions are not inherited from the parent folder.</param>
/// <param name="Aces">The access-control entries.</param>
public sealed record NodeSecurity(
    string Path,
    bool Exists,
    bool IsDirectory,
    bool IsReparsePoint,
    string? OwnerSid,
    bool DaclProtected,
    IReadOnlyList<AceRecord> Aces)
{
    public static NodeSecurity Missing(string path) => new(path, false, false, false, null, false, []);
}

/// <summary>What an account may do in a folder (see <see cref="AccessRules.AccountMayWrite"/>).</summary>
[Flags]
public enum WriteAccess
{
    None = 0,

    /// <summary>Make new files in the folder.</summary>
    CreateFiles = 1,

    /// <summary>Make new folders in the folder.</summary>
    CreateFolders = 2,

    /// <summary>Change or delete what is already in the folder.</summary>
    ChangeEntries = 4,

    /// <summary>Change who may use the folder, or take it over.</summary>
    ChangePermissions = 8,
}

/// <summary>Reads the owner, the permissions and the link status of a file or folder. The Windows program reads the real ones; tests use a fake.</summary>
public interface IFileSecurityProbe
{
    NodeSecurity Describe(string path);
}

/// <summary>
/// The read the audit of writable places makes (local CI round 4, red-team F1). It is <see cref="IFileSecurityProbe"/> and one more way to read: some folders (<c>C:\System Volume Information</c>) refuse even
/// an administrator the right to read their permissions, which is what the backup right of an elevated program is for. Only the audit asks for it, and only after the ordinary read was refused;
/// everything that decides whether the program trusts a folder keeps using <see cref="IFileSecurityProbe.Describe"/>.
/// </summary>
public interface IAuditSecurityProbe : IFileSecurityProbe
{
    /// <summary>
    /// Reads the permissions as <see cref="IFileSecurityProbe.Describe"/> does, with the backup right switched on for the length of the read when the program holds it (it only reads; the right is switched off
    /// again at once). Throws when the program does not hold the right or the read is still refused.
    /// </summary>
    NodeSecurity DescribeWithBackupRights(string path);
}
