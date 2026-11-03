using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct DataBlob
{
    public int Size;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UserInfo1
{
    public nint Name;
    public nint Password;
    public uint PasswordAge;
    public uint Privilege;
    public nint HomeDirectory;
    public nint Comment;
    public uint Flags;
    public nint ScriptPath;
}

/// <summary>USER_INFO_1003 (lmaccess.h): a new password for an account (set by an administrator, no old password needed).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UserInfo1003
{
    public nint Password;
}

/// <summary>USER_INFO_1008 (lmaccess.h): the flags of an account.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UserInfo1008
{
    public uint Flags;
}

/// <summary>LOCALGROUP_USERS_INFO_0 (lmaccess.h): the name of one group an account is in.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LocalGroupUsersInfo0
{
    public nint Name;
}

/// <summary>STARTUPINFOW (winbase.h). 104 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    public uint Size;
    public nint Reserved;
    public nint Desktop;
    public nint Title;
    public uint X;
    public uint Y;
    public uint XSize;
    public uint YSize;
    public uint XCountChars;
    public uint YCountChars;
    public uint FillAttribute;
    public uint Flags;
    public ushort ShowWindow;
    public ushort Reserved2Size;
    public nint Reserved2;
    public nint StandardInput;
    public nint StandardOutput;
    public nint StandardError;
}

/// <summary>PROCESS_INFORMATION (processthreadsapi.h). 24 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public uint ProcessId;
    public uint ThreadId;
}

/// <summary>SECURITY_ATTRIBUTES (wtypesbase.h). 24 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public uint Length;
    public nint SecurityDescriptor;
    public int InheritHandle;
}

/// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION (winnt.h). 64 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

/// <summary>IO_COUNTERS (winnt.h). 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION (winnt.h). 144 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>JOBOBJECT_BASIC_UI_RESTRICTIONS (winnt.h). 4 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicUiRestrictions
{
    public uint UiRestrictionsClass;
}

/// <summary>JOBOBJECT_BASIC_ACCOUNTING_INFORMATION (winnt.h). 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicAccountingInformation
{
    public long TotalUserTime;
    public long TotalKernelTime;
    public long ThisPeriodTotalUserTime;
    public long ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount;
    public uint TotalProcesses;
    public uint ActiveProcesses;
    public uint TotalTerminatedProcesses;
}

/// <summary>LUID (winnt.h): a locally unique identifier, here the number Windows gives a privilege. 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>TOKEN_PRIVILEGES (winnt.h) with exactly one privilege: how many, which one and whether it is switched on. 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    public uint PrivilegeCount;
    public Luid Privilege;
    public uint Attributes;
}

/// <summary>BY_HANDLE_FILE_INFORMATION (fileapi.h). 52 bytes. The three FILETIME fields are kept as pairs of 32-bit numbers: nothing here reads them.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

/// <summary>
/// The only native calls of the program (FR-CI-05; LocalCiToolBoundaryTests lists them, each with the reason). They are for: the console window, the firmware virtualization flag, Windows data
/// protection (DPAPI) for the token and the account password, creating, changing and deleting the dedicated local user of the Linux machine and its profile, checking that user's groups, starting
/// the Linux machine's commands as that user on a private window station and desktop, the Job Object that holds and limits them (and the question whether the new program is already in one), reading
/// the program file that set-up was started from without following a link, switching the backup right on and off around one read of a folder's permissions (the audit of writable places), and a
/// timed message box. None of them reads or changes another program.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    public const uint VirtualizationFirmwareEnabled = 21;

    // CreateProcessWithLogonW flags and process creation flags (winbase.h).
    public const uint LogonWithProfile = 0x1;
    public const uint CreateSuspended = 0x4;
    public const uint CreateUnicodeEnvironment = 0x400;
    public const uint CreateNoWindow = 0x08000000;

    // STARTUPINFO.dwFlags.
    public const uint StartUseShowWindow = 0x1;
    public const uint StartUseStdHandles = 0x100;

    // CreateWindowStationW flags and access (winuser.h): fail if it exists; WINSTA_ALL_ACCESS (0x37F) with the standard rights DELETE, READ_CONTROL, WRITE_DAC, WRITE_OWNER.
    public const uint CreateOnly = 0x1;
    public const uint WindowStationAllAccess = 0x000F037F;

    // DESKTOP_* rights (0x1FF = READOBJECTS, CREATEWINDOW, CREATEMENU, HOOKCONTROL, JOURNALRECORD, JOURNALPLAYBACK, ENUMERATE, WRITEOBJECTS, SWITCHDESKTOP) with the standard rights.
    public const uint DesktopAllAccess = 0x000F01FF;

    // JOBOBJECTINFOCLASS values and limit flags (winnt.h).
    public const int JobObjectBasicAccountingInfo = 1;
    public const int JobObjectBasicUiRestrictionsInfo = 4;
    public const int JobObjectExtendedLimitInfo = 9;

    public const uint DelayUntilReboot = 0x4;
    public const uint FilterNormalAccount = 0x2;
    public const int MaxPreferredLength = -1;

    // ---- reading a file of the owner's without following a link (CreateFileW, GetFileInformationByHandle)

    /// <summary>The value CreateFileW returns when it fails (INVALID_HANDLE_VALUE).</summary>
    public const nint InvalidHandleValue = -1;

    // GENERIC_READ (winnt.h 0x80000000): reading a file of the owner's through a handle that follows no link.
    public const uint GenericRead = 0x80000000;

    // FILE_SHARE_READ only, never FILE_SHARE_WRITE or FILE_SHARE_DELETE: while the file is held open nobody can write to it, rename it or delete it.
    public const uint ShareRead = 0x1;
    public const uint OpenExisting = 3;

    // CreateFileW: FILE_FLAG_OPEN_REPARSE_POINT 0x00200000 (a link is opened itself, never what it leads to), CreateFileW documentation.
    public const uint FlagOpenReparsePoint = 0x00200000;

    // FILE_ATTRIBUTE_DIRECTORY and FILE_ATTRIBUTE_REPARSE_POINT (fileapi.h attribute constants).
    public const uint AttributeDirectory = 0x10;
    public const uint AttributeReparsePoint = 0x400;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;

    // ---- the console window, the notice window, firmware virtualization (user32, kernel32)

    // The first window call of a thread connects it to the desktop of the process's current window station. The thread that shows the notices makes this call once at the start, before any
    // private station exists, so a notice can never end up on a private desktop.
    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxTimeout(nint window, string text, string caption, uint type, ushort language, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessorFeaturePresent(uint feature);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    // ---- Windows data protection (DPAPI) for the token: machine scope with extra entropy

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptUnprotectData(ref DataBlob input, nint description, ref DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);

    // ---- the throwaway local user: create, look at, list, delete, check its groups; and its profile

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint NetUserAdd(string? server, uint level, ref UserInfo1 buffer, out uint parameterError);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint NetUserDel(string? server, string name);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint NetUserGetInfo(string? server, string name, uint level, out nint buffer);

    [LibraryImport("netapi32.dll", EntryPoint = "NetUserSetInfo", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint NetUserSetPassword(string? server, string name, uint level, ref UserInfo1003 buffer, out uint parameterError);

    [LibraryImport("netapi32.dll", EntryPoint = "NetUserSetInfo", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint NetUserSetFlags(string? server, string name, uint level, ref UserInfo1008 buffer, out uint parameterError);

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint NetUserGetLocalGroups(string? server, string userName, uint level, uint flags, out nint buffer, int preferredMaximumLength, out uint entriesRead, out uint totalEntries);

    [LibraryImport("netapi32.dll")]
    public static partial uint NetApiBufferFree(nint buffer);

    [LibraryImport("userenv.dll", EntryPoint = "DeleteProfileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteProfile(string sid, string? profilePath, string? computerName);

    // ---- start the job process as that user (secondary logon service), on a private window station and desktop

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessWithLogon(
        string userName,
        string? domain,
        nint password,
        uint logonFlags,
        string? applicationName,
        nint commandLine,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowStationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowStation(string name, uint flags, uint desiredAccess, ref SecurityAttributes attributes);

    [LibraryImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateDesktop(string name, nint device, nint deviceMode, uint flags, uint desiredAccess, ref SecurityAttributes attributes);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseWindowStation(nint station);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetProcessWindowStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWindowStation(nint station);

    // ---- the Job Object that holds the job process tree: limits, UI limits, end all, count what is left

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetJobExtendedLimits(nint job, int informationClass, ref JobObjectExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetJobUiRestrictions(nint job, int informationClass, ref JobObjectBasicUiRestrictions information, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryJobAccounting(nint job, int informationClass, out JobObjectBasicAccountingInformation information, uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(nint job, uint exitCode);

    // ---- handles of the job process: let it run, end it if it cannot be held, wait for it, read its result

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    // ---- read the program file that set-up was started from through a handle that follows no link

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(nint file, out ByHandleFileInformation information);

    // ---- the audit of writable places: the backup right, switched on for one read of a folder's permissions and switched off again (round 4, F1)

    // TOKEN_ADJUST_PRIVILEGES (0x20) and TOKEN_QUERY (0x8): what AdjustTokenPrivileges needs on the program's own token. SE_PRIVILEGE_ENABLED (0x2). ERROR_NOT_ALL_ASSIGNED (1300): the token does not hold the privilege.
    public const uint TokenAdjustPrivileges = 0x20;
    public const uint TokenQuery = 0x8;
    public const uint PrivilegeEnabled = 0x2;
    public const int ErrorNotAllAssigned = 1300;

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);

    // ---- uninstall: delete the program's own file at the next restart when it cannot be deleted while it runs

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
