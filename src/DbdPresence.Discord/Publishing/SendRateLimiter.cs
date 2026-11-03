namespace DbdPresence.Discord;

/// <summary>
/// Sliding-window limiter: at most <c>limit</c> sends in any window of the given length (FR-RPC-03). It
/// remembers the time of each recent send, so unlike a refilling token bucket it can never allow a burst
/// at a window boundary that exceeds the limit. Times are monotonic offsets; the class is not thread-safe.
/// </summary>
internal sealed class SendRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Queue<TimeSpan> _sends = new();

    public SendRateLimiter(int limit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _limit = limit;
        _window = window;
    }

    /// <summary>Returns the number of sends within the window that ends at <paramref name="now"/>.</summary>
    public int CountInWindow(TimeSpan now)
    {
        Prune(now);
        return _sends.Count;
    }

    /// <summary>Returns the earliest time a send is allowed: <paramref name="now"/> when a slot is free.</summary>
    public TimeSpan NextAllowedAt(TimeSpan now)
    {
        Prune(now);
        return _sends.Count < _limit ? now : _sends.Peek() + _window;
    }

    /// <summary>Records a send at <paramref name="now"/>.</summary>
    public void Record(TimeSpan now)
    {
        Prune(now);
        _sends.Enqueue(now);
    }

    private void Prune(TimeSpan now)
    {
        while (_sends.Count > 0 && _sends.Peek() + _window <= now)
        {
            _sends.Dequeue();
        }
    }
}
