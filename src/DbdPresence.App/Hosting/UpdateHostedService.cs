using DbdPresence.App.Updates;
using Microsoft.Extensions.Hosting;

namespace DbdPresence.App.Hosting;

/// <summary>Starts the signed updater's schedule (FR-ONL-10): 2 minutes after start, then every 6 hours. A copy that was not installed never checks, and the Microsoft Store package has no updater (null): the Store delivers updates.</summary>
internal sealed class UpdateHostedService(AppUpdater? updater) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        updater?.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        updater?.Dispose();
        return Task.CompletedTask;
    }
}
