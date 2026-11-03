using DbdPresence.Core.Diagnostics;
using DbdPresence.Presentation.Detection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbdPresence.App.Hosting;

/// <summary>
/// Runs the live screen-reading loop (FR-DET-25). The loop does nothing until the user turns screen reading on and the signed configuration allows it;
/// this service only starts and stops it and logs failures (the exception, never any text read from the screen). It is registered after the presence
/// service, so on shutdown it stops first: capture is released before Discord is cleared. Running it against the real game and Windows capture and text
/// recognition is PENDING-ENV.
/// </summary>
internal sealed partial class DetectionService(DetectionLoop loop, ILogger<DetectionService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        loop.CycleFailed += OnCycleFailed;
        loop.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        loop.CycleFailed -= OnCycleFailed;
        loop.Dispose();
        LogStopped(logger);
        return Task.CompletedTask;
    }

    private void OnCycleFailed(object? sender, Exception error) => LogCycleFailed(logger, LogRedaction.Current.Describe(error), loop.FailureCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "A screen-reading cycle failed ({Count} failures so far); the loop tries again on its next cycle: {Detail}")]
    private static partial void LogCycleFailed(ILogger logger, string detail, long count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen reading stopped and capture released")]
    private static partial void LogStopped(ILogger logger);
}
