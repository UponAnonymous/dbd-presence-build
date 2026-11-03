namespace DbdPresence.LocalCi;

/// <summary>Tries a GitHub call or a download again, with growing waits, while the failure can pass (no answer, 5xx, rate limit). Other failures surface at once.</summary>
public sealed class Retry
{
    private readonly TimeProvider _time;
    private readonly int _attempts;
    private readonly TimeSpan _firstWait;
    private readonly Redactor _redactor;
    private readonly ICiLog _log;

    public Retry(TimeProvider time, Redactor redactor, ICiLog log, int attempts = 4, TimeSpan? firstWait = null)
    {
        _time = time;
        _redactor = redactor;
        _log = log;
        _attempts = attempts;
        _firstWait = firstWait ?? TimeSpan.FromSeconds(5);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var wait = _firstWait;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubApiException ex) when (ex.IsTransient && attempt < _attempts)
            {
                var delay = ex.RetryAfter is { } asked && asked > wait ? asked : wait;
                await WaitAsync(what, ex, delay, attempt, cancellationToken).ConfigureAwait(false);
                wait = TimeSpan.FromTicks(wait.Ticks * 2);
            }
            catch (HttpRequestException ex) when (attempt < _attempts)
            {
                await WaitAsync(what, ex, wait, attempt, cancellationToken).ConfigureAwait(false);
                wait = TimeSpan.FromTicks(wait.Ticks * 2);
            }
        }
    }

    private async Task WaitAsync(string what, Exception ex, TimeSpan delay, int attempt, CancellationToken cancellationToken)
    {
        _log.Warn($"{what} failed ({_redactor.Describe(ex)}); trying again in {delay.TotalSeconds:0} seconds ({attempt}/{_attempts}).");
        await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
    }

    public Task RunAsync(Func<CancellationToken, Task> action, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(
            async ct =>
            {
                await action(ct).ConfigureAwait(false);
                return true;
            },
            what,
            cancellationToken);
    }
}
