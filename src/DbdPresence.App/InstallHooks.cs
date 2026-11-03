using System.IO;
using System.Security;
using DbdPresence.App.Composition;
using DbdPresence.Platform.Windows.Startup;

namespace DbdPresence.App;

/// <summary>
/// Work done while the uninstaller runs this program with a Velopack switch (SAFETY R-12). Hooks must finish
/// within 30 seconds, never ask for elevation and never throw.
/// </summary>
internal static class InstallHooks
{
    /// <summary>
    /// Removes the per-user "start with Windows" entry so an uninstalled app leaves nothing in the Run key.
    /// Reuses the same adapter as the Settings page. A failure is logged, not thrown, so the uninstall goes on.
    /// </summary>
    internal static void RemoveAutostartEntry()
    {
        using var logger = Program.CreateLogger(AppPaths.Resolve());
        try
        {
            new AutostartRegistry().Disable();
            logger.Information("Uninstall hook: start-with-Windows entry removed");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            logger.Warning("Uninstall hook: the start-with-Windows entry could not be removed ({ExceptionType})", ex.GetType().Name);
        }
    }
}
