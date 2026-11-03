using DbdPresence.Core.Diagnostics;
using DbdPresence.Discord;
using DbdPresence.Presentation.Presence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbdPresence.App.Hosting;

/// <summary>
/// Starts the coordinator and, on shutdown, clears the Discord activity within its 500 ms budget before closing
/// the pipe (ARCHITECTURE §6 shutdown order; FR-RPC-05).
/// </summary>
internal sealed partial class PresenceService(PresenceCoordinator coordinator, PresencePublisher publisher, ILogger<PresenceService> logger) : IHostedService
{
    private RpcStatus? _lastStatus;
    private string? _lastError;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        publisher.HealthChanged += OnHealthChanged;
        coordinator.RecomputeFailed += OnRecomputeFailed;
        coordinator.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        coordinator.Dispose();
        await publisher.ShutdownAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        publisher.HealthChanged -= OnHealthChanged;
        coordinator.RecomputeFailed -= OnRecomputeFailed;
        LogStopped(logger);
    }

    private void OnHealthChanged(object? sender, RpcHealth health)
    {
        // Log transitions and new errors only; health changes on every update otherwise.
        if (health.Status != _lastStatus)
        {
            LogStatus(logger, health.Status, health.Endpoint);
            _lastStatus = health.Status;
        }

        if (health.LastError is { } error && !string.Equals(error, _lastError, StringComparison.Ordinal))
        {
            LogError(logger, LogRedaction.Current.Redact(error));
        }

        _lastError = health.LastError;
    }

    private void OnRecomputeFailed(object? sender, Exception error) => LogRecomputeFailed(logger, LogRedaction.Current.Describe(error), coordinator.FailureCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Composing the activity failed ({Count} failures so far); the previous activity stays until the next change: {Detail}")]
    private static partial void LogRecomputeFailed(ILogger logger, string detail, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord connection {Status} ({Endpoint})")]
    private static partial void LogStatus(ILogger logger, RpcStatus status, string? endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord: {Error}")]
    private static partial void LogError(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Presence stopped and cleared")]
    private static partial void LogStopped(ILogger logger);
}
