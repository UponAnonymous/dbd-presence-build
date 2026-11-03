using System.IO;
using System.Reflection;
using DbdPresence.App.Composition;
using DbdPresence.App.Logging;
using DbdPresence.Configuration;
using DbdPresence.Platform.Windows.Startup;
using Serilog;
using Velopack;

namespace DbdPresence.App;

/// <summary>
/// Custom entry point: the Velopack startup hook (first call, ADR-0006), the single-instance check, logging,
/// then the WPF application with the composed services. The updater is off until V2-S6: no update check runs.
/// </summary>
public static class Program
{
    /// <summary>Application entry point.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Must be the first statement. (In the Microsoft Store package nobody passes these switches, so this returns at once and the hooks are harmless.) When Setup.exe or Update.exe starts this program with an install, update or
        // uninstall switch, Velopack runs the matching hook and exits before anything else happens. In a normal
        // launch it returns at once and contacts nothing (ADR-0016). Its apply-at-start is off: a downloaded package is
        // installed only through "Restart to update", after it was checked again against the newest verified feed
        // (security review 2026-10-25, M3 and L9).
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => InstallHooks.RemoveAutostartEntry())
            .Run();

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Another instance is running and has been asked to show its window (FR-LIFE-03).
            return 0;
        }

        var paths = AppPaths.Resolve();
        Log.Logger = CreateLogger(paths);

        try
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            // The "--autostart" switch only goes into this log line; no behaviour depends on it. The Microsoft Store package starts through its
            // manifest startup task, which passes no arguments, so the line then says "autostart: False" even after a start with Windows.
            Log.Information(
                "DBD Presence {Version} starting (autostart: {Autostart}, Microsoft Store package: {Packaged})",
                version,
                args.Contains(AutostartRegistry.AutostartArgument, StringComparer.OrdinalIgnoreCase),
                PackageIdentity.IsPackaged);

            var app = new App();
            app.InitializeComponent();
            using var root = new AppRoot(app, paths, instance, version);
            return root.Run();
        }
        finally
        {
            Log.Information("DBD Presence exited");
            Log.CloseAndFlush();
        }
    }

    internal static Serilog.Core.Logger CreateLogger(SettingsPaths paths) => new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Redacted(
            DbdPresence.Core.Diagnostics.LogRedaction.Current,
            sink => sink.File(
                Path.Combine(paths.LogDirectory, "dbd-presence-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture))
        .CreateLogger();
}
