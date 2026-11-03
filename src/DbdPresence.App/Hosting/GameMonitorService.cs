using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbdPresence.App.Hosting;

/// <summary>Runs the process monitor (FR-PROC-01..05) and keeps its executable list in sync with settings (FR-PROC-03).</summary>
internal sealed partial class GameMonitorService(GameMonitor monitor, ISettingsService settings, TimeProvider time, ILogger<GameMonitorService> logger) : IHostedService
{
    private static readonly TimeSpan s_failureLogInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset _lastFailureLog = DateTimeOffset.MinValue;
    private int _suppressedFailures;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        monitor.UpdateMatcher(new GameProcessMatcher(settings.Current.Lifecycle.ExtraProcessNames));
        settings.Changed += OnSettingsChanged;
        monitor.LifecycleChanged += OnLifecycleChanged;
        monitor.PollFailed += OnPollFailed;
        monitor.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        monitor.LifecycleChanged -= OnLifecycleChanged;
        monitor.PollFailed -= OnPollFailed;
        await monitor.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!e.Previous.Lifecycle.ExtraProcessNames.SequenceEqual(e.Current.Lifecycle.ExtraProcessNames, StringComparer.OrdinalIgnoreCase))
        {
            monitor.UpdateMatcher(new GameProcessMatcher(e.Current.Lifecycle.ExtraProcessNames));
            LogMatcherUpdated(logger, e.Current.Lifecycle.ExtraProcessNames.Count);
        }
    }

    private void OnLifecycleChanged(object? sender, GameLifecycle e) =>
        LogLifecycle(logger, e.State, e.Process?.ExecutableName, e.StartIsApproximate);

    private void OnPollFailed(object? sender, Exception e)
    {
        // A failing poll repeats every few seconds; log the first, then at most once a minute with a count.
        var now = time.GetUtcNow();
        if (now - _lastFailureLog < s_failureLogInterval)
        {
            _suppressedFailures++;
            return;
        }

        LogPollFailed(logger, e.GetType().Name, _suppressedFailures);
        _lastFailureLog = now;
        _suppressedFailures = 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Game process state {State} ({Executable}; start approximate: {Approximate})")]
    private static partial void LogLifecycle(ILogger logger, GameState state, string? executable, bool approximate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Game executable list updated ({ExtraCount} extra names)")]
    private static partial void LogMatcherUpdated(ILogger logger, int extraCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Process poll failed: {ExceptionType} ({Suppressed} similar failures not logged since the last entry)")]
    private static partial void LogPollFailed(ILogger logger, string exceptionType, int suppressed);
}
