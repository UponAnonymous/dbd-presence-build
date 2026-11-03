using DbdPresence.Core.Diagnostics;
using DbdPresence.Core.Online;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbdPresence.App.Hosting;

/// <summary>
/// Refreshes the session, licence and configuration at start and then hourly (the account service only calls the network once a day is
/// due; FR-ONL-04), and then the list of the user's uploads (FR-ONL-09) when it was never read since sign-in or once a day. A failure is
/// logged and never reaches presence: an outage never stops local presence (D8, FR-ONL-05).
/// </summary>
internal sealed partial class AccountRefreshService(IAccountService account, IUploadService uploads, TimeProvider time, ILogger<AccountRefreshService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan s_interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan s_uploadsInterval = TimeSpan.FromHours(24);
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private DateTimeOffset? _uploadsReadAt;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = time.CreateTimer(_ => _ = RunAsync(), null, TimeSpan.FromSeconds(5), s_interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        _stop.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
#pragma warning disable CA1031 // Background loop boundary: nothing may escape into the thread pool; the failure is logged and presence is unaffected.
        try
        {
            var result = await account.RefreshAsync(force: false, _stop.Token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LogRefreshFailed(logger, result.Failure);
            }

            var now = time.GetUtcNow();
            if (account.Current.OnlineFeaturesAllowed && (!uploads.Current.IsKnown || _uploadsReadAt is not { } last || now - last >= s_uploadsInterval))
            {
                var listed = await uploads.RefreshAsync(_stop.Token).ConfigureAwait(false);
                if (listed.Succeeded)
                {
                    _uploadsReadAt = now;
                }
                else
                {
                    LogUploadsFailed(logger, listed.Failure);
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogRefreshCancelled(logger);
        }
        catch (Exception ex)
        {
            LogRefreshCrashed(logger, LogRedaction.Current.Describe(ex));
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Account refresh did not complete: {Failure}")]
    private static partial void LogRefreshFailed(ILogger logger, AccountFailure failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload list refresh did not complete: {Failure}")]
    private static partial void LogUploadsFailed(ILogger logger, AccountFailure failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Account refresh cancelled by shutdown")]
    private static partial void LogRefreshCancelled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account refresh failed unexpectedly: {Detail}")]
    private static partial void LogRefreshCrashed(ILogger logger, string detail);
}
