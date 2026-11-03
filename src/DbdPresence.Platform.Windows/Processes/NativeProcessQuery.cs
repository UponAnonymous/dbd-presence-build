using System.Runtime.InteropServices;
using DbdPresence.Core.Game;

namespace DbdPresence.Platform.Windows.Processes;

/// <summary>
/// The only code that opens a handle to another process (SAFETY_AND_PRIVACY §2.1): one short-lived
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> handle, used for the start time and image path, then closed.
/// The architecture tests enforce the literal access mask and the absence of any other process access.
/// </summary>
internal static partial class NativeProcessQuery
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxPath = 32768;

    /// <summary>Returns start time and image path, or null when the process cannot be queried.</summary>
    public static ProcessDetails? Query(int processId)
    {
        var handle = OpenLimited((uint)processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            DateTimeOffset? started = GetProcessTimes(handle, out var creation, out _, out _, out _)
                ? DateTimeOffset.FromFileTime(creation).ToUniversalTime()
                : null;

            return new ProcessDetails(started, ImagePath(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static unsafe string? ImagePath(nint handle)
    {
        var buffer = new char[MaxPath];
        var length = (uint)buffer.Length;
        fixed (char* start = buffer)
        {
            return QueryFullProcessImageName(handle, 0, start, ref length) ? new string(start, 0, (int)length) : null;
        }
    }

    // Kept tiny and literal on purpose: the architecture test requires `ldc.i4 0x1000; ldc.i4.0; ldarg; call`.
    private static nint OpenLimited(uint processId) => OpenProcess(ProcessQueryLimitedInformation, false, processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* exeName, ref uint size);
}
