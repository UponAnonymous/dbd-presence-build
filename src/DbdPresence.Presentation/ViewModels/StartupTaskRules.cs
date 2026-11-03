namespace DbdPresence.Presentation.ViewModels;

/// <summary>The state of the Microsoft Store package's startup task, as Windows reports it (mirrors <c>Windows.ApplicationModel.StartupTaskState</c>).</summary>
public enum StartupTaskStatus
{
    /// <summary>Off, and the app may turn it on.</summary>
    Disabled,

    /// <summary>The user turned it off in Windows Settings; only the user can turn it on again.</summary>
    DisabledByUser,

    /// <summary>Policy turned it off.</summary>
    DisabledByPolicy,

    /// <summary>On.</summary>
    Enabled,

    /// <summary>Policy turned it on.</summary>
    EnabledByPolicy,

    /// <summary>The task is not declared or Windows did not answer.</summary>
    Unavailable,
}

/// <summary>
/// "Start with Windows" for the Microsoft Store package (FR-LIFE-01). A packaged app cannot use the HKCU Run key (writes are redirected into
/// the package and removed on uninstall), so it uses the manifest's startup task, which the user can also switch in Windows Settings.
/// </summary>
public static class StartupTaskRules
{
    /// <summary>Said under the switch for the Microsoft Store package.</summary>
    public const string Caption = "You can also change this in Windows Settings, under Apps, then Startup.";

    /// <summary>Said when the user turned the startup task off in Windows.</summary>
    public const string DisabledByUserText = "Windows has start-up turned off for DBD Presence. Turn it on in Windows Settings, under Apps, then Startup.";

    /// <summary>Said when policy controls the startup task.</summary>
    public const string PolicyText = "Your organisation's policy controls whether DBD Presence starts with Windows.";

    /// <summary>Said when Windows did not allow the change.</summary>
    public const string UnavailableText = "Windows did not allow the startup setting to be changed.";

    /// <summary>Whether the app starts with Windows in this state.</summary>
    /// <param name="status">The task state.</param>
    /// <returns>True when enabled by the user or by policy.</returns>
    public static bool IsEnabled(StartupTaskStatus status) => status is StartupTaskStatus.Enabled or StartupTaskStatus.EnabledByPolicy;

    /// <summary>Whether the app may ask Windows to turn the task on (or off) from code.</summary>
    /// <param name="status">The task state.</param>
    /// <returns>True when the app may request a change.</returns>
    public static bool CanRequestChange(StartupTaskStatus status) => status is StartupTaskStatus.Disabled or StartupTaskStatus.Enabled;

    /// <summary>The sentence to show when a change to <paramref name="wanted"/> did not happen, or null when the state is what was asked for.</summary>
    /// <param name="status">The task state after the attempt.</param>
    /// <param name="wanted">What the user asked for.</param>
    /// <returns>The message, or null on success.</returns>
    public static string? ErrorFor(StartupTaskStatus status, bool wanted)
    {
        if (IsEnabled(status) == wanted)
        {
            return null;
        }

        return status switch
        {
            StartupTaskStatus.DisabledByUser => DisabledByUserText,
            StartupTaskStatus.DisabledByPolicy or StartupTaskStatus.EnabledByPolicy => PolicyText,
            _ => UnavailableText,
        };
    }
}
