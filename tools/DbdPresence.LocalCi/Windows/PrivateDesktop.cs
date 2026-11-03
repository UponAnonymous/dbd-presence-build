using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The permissions of a job's private window station and desktop, as a security descriptor (security review 2026-10-30, H2): the system, Administrators and that job's user get full access,
/// nobody else gets anything. Without a descriptor Windows would give GENERIC_ALL to all users ("If lpsa is NULL, the window station (and any desktops created within the window) gets a
/// security descriptor that grants GENERIC_ALL access to all users", CreateWindowStationW documentation). Pure managed code, so it is tested on any system.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PrivateDesktopSecurity
{
    /// <summary>The identities that get full access, in order: the system, Administrators, the job user.</summary>
    public static IReadOnlyList<string> Grantees(string jobUserSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobUserSid);
        return [WellKnownSids.LocalSystem, WellKnownSids.Administrators, jobUserSid];
    }

    /// <summary>A self-relative security descriptor: owner Administrators, one "allow <paramref name="mask"/>" entry per grantee.</summary>
    public static byte[] Descriptor(string jobUserSid, uint mask)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 3);
        foreach (var sid in Grantees(jobUserSid))
        {
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)mask), new SecurityIdentifier(sid), false, null));
        }

        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
            new SecurityIdentifier(WellKnownSids.Administrators),
            null,
            null,
            acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}

/// <summary>
/// A job's private, non-interactive window station with its own desktop (security review 2026-10-30, H2). The job process is started on it (<c>lpDesktop</c> = <c>station\desktop</c>), so it has
/// its own clipboard and its own atom table, receives no keyboard or mouse input, and has no display: "The interactive window station is the only window station that can display a user
/// interface or receive user input ... All other window stations are noninteractive" (learn.microsoft.com/windows/win32/winstation/window-stations, read 2026-10-29). Only the job's user, the
/// system and Administrators can open it. PENDING-ENV: not run on a Windows PC yet.
/// </summary>
/// <remarks>
/// <c>CreateDesktop</c> creates the desktop on the calling <em>process's</em> window station, so the process is switched to the new station for a moment (<c>SetProcessWindowStation</c>) and switched
/// back at once. That happens on a thread of its own, under <see cref="ProcessStationGate"/>, because <c>CreateDesktop</c> also assigns the new desktop to the calling thread for good, and
/// because a thread that makes its first window call while the process is switched would stay on the private station. Everything else that makes window calls takes the same lock.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PrivateDesktop : IDisposable
{
    private const string StationPrefix = "DbdCi-";
    private const string DesktopName = "Job";

    /// <summary>Held while the process is switched to a private station, and by every first window call of a thread.</summary>
    public static readonly object ProcessStationGate = new();

    private nint _station;
    private nint _desktop;

    private PrivateDesktop(nint station, nint desktop, string path)
    {
        _station = station;
        _desktop = desktop;
        Path = path;
    }

    /// <summary>The text for <c>lpDesktop</c>: window station name, a backslash, desktop name.</summary>
    public string Path { get; }

    public static PrivateDesktop Create(string userSid)
    {
        PrivateDesktop? created = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
#pragma warning disable CA1031 // The failure is handed to the calling thread and rethrown there; nothing is hidden.
            try
            {
                created = CreateOnThisThread(userSid);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
#pragma warning restore CA1031
        })
        {
            IsBackground = true,
            Name = "DBD Local CI private desktop",
        };
        worker.Start();
        worker.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return created ?? throw new InvalidOperationException("The private desktop was not created.");
    }

    public void Dispose()
    {
        // Close the desktop first, then the station that holds it. A handle that cannot be closed here is closed when the program ends.
        if (_desktop != 0)
        {
            _ = NativeMethods.CloseDesktop(_desktop);
            _desktop = 0;
        }

        if (_station != 0)
        {
            _ = NativeMethods.CloseWindowStation(_station);
            _station = 0;
        }
    }

    private static PrivateDesktop CreateOnThisThread(string userSid)
    {
        var stationName = StationPrefix + Guid.NewGuid().ToString("N")[..16];
        var stationDescriptor = GCHandle.Alloc(PrivateDesktopSecurity.Descriptor(userSid, NativeMethods.WindowStationAllAccess), GCHandleType.Pinned);
        var desktopDescriptor = GCHandle.Alloc(PrivateDesktopSecurity.Descriptor(userSid, NativeMethods.DesktopAllAccess), GCHandleType.Pinned);
        try
        {
            lock (ProcessStationGate)
            {
                var original = NativeMethods.GetProcessWindowStation();
                if (original == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not say which window station this program uses.");
                }

                var stationAttributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = stationDescriptor.AddrOfPinnedObject() };
                var station = NativeMethods.CreateWindowStation(stationName, NativeMethods.CreateOnly, NativeMethods.WindowStationAllAccess, ref stationAttributes);
                if (station == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not create the job's private window station.");
                }

                nint desktop = 0;
                string? problem = null;
                if (!NativeMethods.SetProcessWindowStation(station))
                {
                    problem = "Windows would not switch to the job's private window station.";
                }
                else
                {
                    var desktopAttributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = desktopDescriptor.AddrOfPinnedObject() };
                    desktop = NativeMethods.CreateDesktop(DesktopName, nint.Zero, nint.Zero, 0, NativeMethods.DesktopAllAccess, ref desktopAttributes);
                    var errorCode = Marshal.GetLastPInvokeError();
                    problem = desktop == 0 ? $"Windows would not create the job's private desktop (error {errorCode})." : null;

                    // Back to the owner's station at once, whatever happened above.
                    if (!NativeMethods.SetProcessWindowStation(original))
                    {
                        throw new InvalidOperationException("Windows would not switch this program back to the desktop it uses. The loop must be restarted.");
                    }
                }

                if (problem is not null)
                {
                    _ = NativeMethods.CloseWindowStation(station);
                    throw new InvalidOperationException(problem);
                }

                return new PrivateDesktop(station, desktop, stationName + "\\" + DesktopName);
            }
        }
        finally
        {
            stationDescriptor.Free();
            desktopDescriptor.Free();
        }
    }
}
