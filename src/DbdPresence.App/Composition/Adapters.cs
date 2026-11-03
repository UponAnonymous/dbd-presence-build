using System.IO;
using System.Security;
using System.Windows.Threading;
using DbdPresence.Platform.Windows.Startup;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.App.Composition;

/// <summary>Posts work to the WPF dispatcher.</summary>
internal sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>Start-with-Windows through the HKCU Run key (FR-LIFE-01).</summary>
internal sealed class RegistryAutostart(AutostartRegistry registry, string executablePath) : IAutostart
{
    public bool IsEnabled => registry.RegisteredExecutable() is not null;

    public string? SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                registry.Enable(executablePath);
            }
            else
            {
                registry.Disable();
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            Serilog.Log.Warning("Changing the startup entry failed: {ExceptionType}", ex.GetType().Name);
            return "Windows did not allow the startup entry to be changed. Your account may restrict startup programs.";
        }
    }
}

/// <summary>
/// Start-with-Windows for the Microsoft Store package through its manifest startup task (FR-LIFE-01). The HKCU Run key is not used there: writes
/// to it are redirected into the package. Windows behaviour is PENDING-ENV.
/// </summary>
internal sealed class StartupTaskAutostart : IAutostart
{
    public bool IsEnabled => StartupTaskRules.IsEnabled(Status(PackagedStartupTask.GetState()));

    public string? Caption => StartupTaskRules.Caption;

    public string? SetEnabled(bool enabled)
    {
        var before = Status(PackagedStartupTask.GetState());
        if (StartupTaskRules.IsEnabled(before) == enabled)
        {
            return null;
        }

        if (!StartupTaskRules.CanRequestChange(before))
        {
            return StartupTaskRules.ErrorFor(before, enabled);
        }

        var after = Status(PackagedStartupTask.Set(enabled));
        if (StartupTaskRules.ErrorFor(after, enabled) is { } problem)
        {
            Serilog.Log.Warning("Changing the startup task failed: state {State}", after);
            return problem;
        }

        return null;
    }

    /// <summary>
    /// Makes the setting match what Windows reports, because the user can change the task in Windows Settings at any time: a task Windows holds off
    /// (turned off by the user or by policy) turns the setting off. A setting that is on while the task is merely off is requested again.
    /// </summary>
    /// <param name="shouldStartWithWindows">The saved setting.</param>
    /// <returns>The value the setting should have.</returns>
    public static bool Reconcile(bool shouldStartWithWindows)
    {
        var state = Status(PackagedStartupTask.GetState());
        if (shouldStartWithWindows && state == StartupTaskStatus.Disabled)
        {
            return StartupTaskRules.ErrorFor(Status(PackagedStartupTask.Set(true)), true) is null;
        }

        return state == StartupTaskStatus.Unavailable ? shouldStartWithWindows : StartupTaskRules.IsEnabled(state);
    }

    private static StartupTaskStatus Status(Windows.ApplicationModel.StartupTaskState? state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Disabled => StartupTaskStatus.Disabled,
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => StartupTaskStatus.DisabledByUser,
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => StartupTaskStatus.DisabledByPolicy,
        Windows.ApplicationModel.StartupTaskState.Enabled => StartupTaskStatus.Enabled,
        Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => StartupTaskStatus.EnabledByPolicy,
        _ => StartupTaskStatus.Unavailable,
    };
}

/// <summary>Opens Windows' Installed apps page for the Uninstall button (all install kinds).</summary>
internal sealed class InstalledAppsAdapter : IInstalledAppsLauncher
{
    public Task<bool> OpenInstalledAppsAsync() => InstalledAppsLauncher.OpenAsync();
}

/// <summary>Writes the online layer's diagnostics to the log. The layer never passes tokens, codes or keys (ADR-0011).</summary>
internal sealed class SerilogOnlineLog : DbdPresence.Core.Online.IOnlineLog
{
    public void Warning(string message) => Serilog.Log.Warning("Online: {Message}", DbdPresence.Core.Diagnostics.LogRedaction.Current.Redact(message));
}
