namespace DbdPresence.LocalCi;

/// <summary>Runs one piece of work with a time limit of its own on top of the caller's cancellation (local CI round 3, L2). The limit is measured on the injected clock.</summary>
public static class TimeBox
{
    /// <summary>
    /// Runs <paramref name="work"/>. When the limit runs out first, the work is cancelled and a <see cref="TimeoutException"/> says in plain words what did not finish; when the caller cancels, the
    /// cancellation passes through unchanged.
    /// </summary>
    /// <param name="limit">How long the work may take.</param>
    /// <param name="time">The clock the limit is measured on.</param>
    /// <param name="what">What is done, for the message: "Installing WSL".</param>
    /// <param name="work">The work; it must end when its token is cancelled.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static async Task<T> RunAsync<T>(TimeSpan limit, TimeProvider time, string what, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrEmpty(what);
        ArgumentNullException.ThrowIfNull(work);
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A time limit must be above zero.");
        }

        using var timer = new CancellationTokenSource(limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timer.IsCancellationRequested)
        {
            throw new TimeoutException($"{what} did not finish within {Minutes(limit)}, so it was ended.");
        }
    }

    /// <summary>The same for work that gives no result.</summary>
    public static Task RunAsync(TimeSpan limit, TimeProvider time, string what, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(
            limit,
            time,
            what,
            async ct =>
            {
                await work(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    private static string Minutes(TimeSpan limit) => limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0} minutes" : $"{limit.TotalSeconds:0} seconds";
}
