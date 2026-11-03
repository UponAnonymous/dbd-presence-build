using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The backup right (<c>SeBackupPrivilege</c>) of the elevated program's own token, switched on for the length of one read and switched off again when this is disposed (local CI round 4, red-team F1). An
/// administrator's token holds the right but has it off. Switched on, it lets the program read the permissions of a folder that refuses even administrators (<c>C:\System Volume Information</c>), which the audit of
/// the places the dedicated account may write to needs in order to say something about such a folder instead of "unknown". Only <see cref="WindowsFileSecurityProbe.DescribeWithBackupRights"/> uses this, and only
/// for reading permissions (the architecture tests pin both). PENDING-ENV: not run on a Windows PC yet; whether the read then succeeds is one of the things the first run measures.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BackupPrivilege : IDisposable
{
    private const string Name = "SeBackupPrivilege";

    private readonly nint _token;
    private readonly Luid _luid;
    private bool _disposed;
    private bool _off;

    private BackupPrivilege(nint token, Luid luid)
    {
        _token = token;
        _luid = luid;
    }

    /// <summary>Switches the right on. Null when the program's token does not hold it (it does not run as an administrator) or Windows refuses to switch it on.</summary>
    public static BackupPrivilege? TryEnable()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery, out var token))
        {
            return null;
        }

        if (!NativeMethods.LookupPrivilegeValue(null, Name, out var luid))
        {
            _ = NativeMethods.CloseHandle(token);
            return null;
        }

        var on = new TokenPrivileges { PrivilegeCount = 1, Privilege = luid, Attributes = NativeMethods.PrivilegeEnabled };

        // AdjustTokenPrivileges succeeds even when the token does not hold the privilege; the last error then says so.
        if (!NativeMethods.AdjustTokenPrivileges(token, false, ref on, 0, 0, 0) || Marshal.GetLastWin32Error() == NativeMethods.ErrorNotAllAssigned)
        {
            _ = NativeMethods.CloseHandle(token);
            return null;
        }

        return new BackupPrivilege(token, luid);
    }

    /// <summary>Switches the right off again and closes the token. True when Windows confirmed that the right is off.</summary>
    public bool SwitchOff()
    {
        if (_disposed)
        {
            return _off;
        }

        _disposed = true;
        var off = new TokenPrivileges { PrivilegeCount = 1, Privilege = _luid, Attributes = 0 };
        _off = NativeMethods.AdjustTokenPrivileges(_token, false, ref off, 0, 0, 0);
        _ = NativeMethods.CloseHandle(_token);
        return _off;
    }

    public void Dispose() => _ = SwitchOff();
}
