using DbdPresence.Online;
using Microsoft.Extensions.Hosting;

namespace DbdPresence.App.Hosting;

/// <summary>
/// Starts pack sync on the schedule (at start, every 6 hours, and when online features become available; FR-ONL-07). The schedule asks the
/// sync service itself whether online features are allowed, and a failed run is logged by the service and never reaches presence (D8).
/// </summary>
internal sealed class PackSyncHostedService(OnlineServices online) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        online.Scheduler.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        online.Scheduler.Dispose();
        return Task.CompletedTask;
    }
}
