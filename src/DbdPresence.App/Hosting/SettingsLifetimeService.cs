using System.IO;
using DbdPresence.App.Composition;
using DbdPresence.Configuration;
using DbdPresence.Core.Diagnostics;
using DbdPresence.Platform.Windows.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbdPresence.App.Hosting;

/// <summary>
/// Logs how settings were loaded, repairs the startup entry (ARCHITECTURE §8 self-heal), reports save failures,
/// and writes pending changes on shutdown.
/// </summary>
internal sealed partial class SettingsLifetimeService(
    SettingsService settings, AutostartRegistry autostart, string executablePath, bool packaged, ILogger<SettingsLifetimeService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var load = settings.LoadResult;
        LogLoaded(logger, load.Source, load.Issues.Count, settings.IsReadOnly);
        foreach (var issue in load.Issues)
        {
            LogIssue(logger, LogRedaction.Current.Redact(issue.Path), LogRedaction.Current.Redact(issue.Message));
        }

        settings.SaveFailed += OnSaveFailed;
        if (packaged)
        {
            // The Microsoft Store package: Windows owns the startup task and the user can change it in Windows Settings, so the setting follows it.
            var wanted = settings.Current.Lifecycle.StartWithWindows;
            var actual = StartupTaskAutostart.Reconcile(wanted);
            if (actual != wanted)
            {
                _ = settings.Update(s => s with { Lifecycle = s.Lifecycle with { StartWithWindows = actual } });
                LogStartupFollowsWindows(logger, actual);
            }

            return Task.CompletedTask;
        }

        try
        {
            if (autostart.Reconcile(settings.Current.Lifecycle.StartWithWindows, executablePath))
            {
                LogStaleAutostart(logger);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LogAutostartFailed(logger, ex.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Flush();
        settings.SaveFailed -= OnSaveFailed;
        return Task.CompletedTask;
    }

    private void OnSaveFailed(object? sender, SettingsSaveFailedEventArgs e) => LogSaveFailed(logger, e.Error.GetType().Name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings loaded from {Source} with {IssueCount} corrections (read-only: {ReadOnly})")]
    private static partial void LogLoaded(ILogger logger, SettingsSource source, int issueCount, bool readOnly);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings correction at {Path}: {Message}")]
    private static partial void LogIssue(ILogger logger, string path, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed a startup entry that pointed to a missing file")]
    private static partial void LogStaleAutostart(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start with Windows now follows the Windows startup task: {StartWithWindows}")]
    private static partial void LogStartupFollowsWindows(ILogger logger, bool startWithWindows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the startup entry: {ExceptionType}")]
    private static partial void LogAutostartFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving settings failed: {ExceptionType}")]
    private static partial void LogSaveFailed(ILogger logger, string exceptionType);
}
