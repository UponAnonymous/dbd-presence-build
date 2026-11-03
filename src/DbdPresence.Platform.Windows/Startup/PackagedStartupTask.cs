using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace DbdPresence.Platform.Windows.Startup;

/// <summary>
/// "Start with Windows" for the Microsoft Store package through the manifest's startup task (FR-LIFE-01). Used instead of the HKCU Run key
/// when <see cref="PackageIdentity.IsPackaged"/>. A task the user turned off in Windows Settings (<see cref="StartupTaskState.DisabledByUser"/>)
/// cannot be turned on from code. Windows behaviour is PENDING-ENV until the MSIX smoke run and a manual check.
/// </summary>
public static class PackagedStartupTask
{
    /// <summary>The task id declared in <c>build/msix/AppxManifest.xml</c>.</summary>
    public const string TaskId = "DbdPresenceStartup";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    /// <summary>Reads the task's state, or null when Windows did not answer or the task is not declared.</summary>
    /// <returns>The state, or null.</returns>
    public static StartupTaskState? GetState() => Run(async () => (await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false)).State);

    /// <summary>Asks Windows to turn the task on or off and returns the state afterwards, or null when Windows did not answer.</summary>
    /// <param name="enable">True to enable, false to disable.</param>
    /// <returns>The state after the request, or null.</returns>
    public static StartupTaskState? Set(bool enable) => Run(async () =>
    {
        var task = await StartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);
        if (enable)
        {
            return await task.RequestEnableAsync().AsTask().ConfigureAwait(false);
        }

        task.Disable();
        return task.State;
    });

    // Runs on a thread-pool thread so a call from the UI thread never waits on the UI thread's own message pump.
    private static StartupTaskState? Run(Func<Task<StartupTaskState>> work)
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(s_timeout) ? task.Result : null;
        }
        catch (AggregateException ex) when (ex.InnerException is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            // The task is not declared in this package, or Windows refused: reported as "unknown" and shown to the user as a plain message.
            return null;
        }
    }
}
