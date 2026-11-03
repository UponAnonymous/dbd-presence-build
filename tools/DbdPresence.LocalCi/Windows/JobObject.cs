using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The Job Object that holds a started program's whole process tree (security review 2026-10-30, H2 M1; local CI round 2, red-team F5). When the last handle to it is closed, every process in it is
/// ended (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: "Causes all processes associated with the job to terminate when the last handle to the job is closed"), so even a crash of the loop cannot leave a
/// program running. It also limits how many programs the job may hold (<c>ActiveProcessLimit</c>) and how much memory they may commit together (<c>JobMemoryLimit</c>), so a runaway program cannot
/// take the PC down. A child process of a process in the job is in the job as well and cannot leave it (breakaway is not allowed). The eight user-interface limits are added by
/// <see cref="RestrictUserInterface"/> only when the program is not already in another Job Object. PENDING-ENV: not run on a Windows PC yet.
/// </summary>
/// <remarks>learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_basic_ui_restrictions and ns-winnt-jobobject_basic_limit_information, read 2026-10-29 and 2026-10-30.</remarks>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
    private nint _handle;

    private JobObject(nint handle)
    {
        _handle = handle;
    }

    public static JobObject Create()
    {
        var handle = NativeMethods.CreateJobObject(nint.Zero, null);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not create the Job Object for the program.");
        }

        var job = new JobObject(handle);
        try
        {
            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = AccountJobLimits.LimitFlags;
            limits.BasicLimitInformation.ActiveProcessLimit = AccountJobLimits.ActiveProcesses;
            limits.JobMemoryLimit = (nuint)AccountJobLimits.MemoryBytes;
            if (!NativeMethods.SetJobExtendedLimits(handle, NativeMethods.JobObjectExtendedLimitInfo, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not set the Job Object's limits (end all on close, number of programs, memory).");
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }

        return job;
    }

    /// <summary>
    /// True when the process is in any Job Object, false when it is in none, null when Windows would not say. Asked about a program that was just started and is still held back.
    /// </summary>
    public static bool? IsInAnyJob(nint process)
    {
        if (!NativeMethods.IsProcessInJob(process, nint.Zero, out var inJob))
        {
            return null;
        }

        return inJob;
    }

    /// <summary>
    /// Switches on the eight user-interface limits (handles of other programs' windows, the clipboard, global atoms, system parameters, display settings, switching desktops, logging off). Only for
    /// a job no program has joined yet and only when the program is not already in another Job Object: a nested job cannot have them.
    /// </summary>
    public void RestrictUserInterface()
    {
        var ui = new JobObjectBasicUiRestrictions { UiRestrictionsClass = AccountJobLimits.UiLimitAll };
        if (!NativeMethods.SetJobUiRestrictions(_handle, NativeMethods.JobObjectBasicUiRestrictionsInfo, ref ui, (uint)Marshal.SizeOf<JobObjectBasicUiRestrictions>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not set the Job Object's user-interface limits.");
        }
    }

    /// <summary>Puts the (still suspended) process in the job. "Terminal Services: All processes within a job must run within the same session as the job" (AssignProcessToJobObject documentation).</summary>
    public void Assign(nint process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not put the program in its Job Object, so it was not started.");
        }
    }

    /// <summary>How many processes are in the job right now.</summary>
    public int ActiveProcesses()
    {
        if (!NativeMethods.QueryJobAccounting(_handle, NativeMethods.JobObjectBasicAccountingInfo, out var information, (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not say how many programs are left in the job.");
        }

        return (int)information.ActiveProcesses;
    }

    /// <summary>Ends every process in the job and waits until none is left; throws when some stay.</summary>
    public async Task EndAllAsync(TimeSpan limit)
    {
        _ = NativeMethods.TerminateJobObject(_handle, 1);
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(100);
        while (ActiveProcesses() > 0)
        {
            if (waited >= limit)
            {
                throw new InvalidOperationException($"{ActiveProcesses()} program(s) of the job did not end within {limit.TotalSeconds:0} seconds.");
            }

            await Task.Delay(step, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
            waited += step;
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            // Closing the last handle ends whatever is still in the job.
            _ = NativeMethods.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
