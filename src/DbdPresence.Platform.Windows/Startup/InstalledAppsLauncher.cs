using System.Runtime.InteropServices;
using Windows.System;

namespace DbdPresence.Platform.Windows.Startup;

/// <summary>
/// Opens Windows' Installed apps page, where the user uninstalls DBD Presence (any install kind). This is the one named user of the WinRT
/// <c>Launcher</c> (ADR-0016; the architecture test allows it only here and only with this fixed address): it never opens a file or any other
/// address. No process is started. Windows behaviour is PENDING-ENV.
/// </summary>
public static class InstalledAppsLauncher
{
    /// <summary>The only address this class opens.</summary>
    public const string SettingsUri = "ms-settings:appsfeatures";

    /// <summary>Opens the Installed apps page.</summary>
    /// <returns>True when Windows opened it; false when it did not.</returns>
    public static async Task<bool> OpenAsync()
    {
        try
        {
            return await Launcher.LaunchUriAsync(new Uri(SettingsUri)).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Windows refused or has no handler for the page: the caller shows "open it yourself" steps.
            return false;
        }
    }
}
