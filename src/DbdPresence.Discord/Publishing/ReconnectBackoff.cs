namespace DbdPresence.Discord;

/// <summary>
/// Exponential reconnect delays (1 s, 2 s, 4 s … capped at 60 s) with random spread (FR-RPC-02). The spread
/// keeps many clients from reconnecting in lockstep; the result is never below the first delay and never
/// exceeds the cap. Not thread-safe.
/// </summary>
internal sealed class ReconnectBackoff
{
    private readonly TimeSpan _min;
    private readonly TimeSpan _max;
    private readonly double _jitter;
    private readonly Random _random;
    private int _attempt;

    public ReconnectBackoff(TimeSpan min, TimeSpan max, double jitter, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(min, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1.0);
        ArgumentNullException.ThrowIfNull(random);
        _min = min;
        _max = max;
        _jitter = jitter;
        _random = random;
    }

    /// <summary>Returns the delay before the next attempt and advances the sequence.</summary>
    public TimeSpan Next()
    {
        var seconds = Math.Min(_min.TotalSeconds * Math.Pow(2, _attempt), _max.TotalSeconds);
        if (seconds < _max.TotalSeconds)
        {
            _attempt++;
        }

        // Uniform in [1 - jitter, 1 + jitter].
        var factor = 1.0 - _jitter + (2.0 * _jitter * _random.NextDouble());
        return TimeSpan.FromSeconds(Math.Clamp(seconds * factor, _min.TotalSeconds, _max.TotalSeconds));
    }

    /// <summary>Starts the sequence over once a connection has proved healthy (see <see cref="PresencePublisherOptions.HealthySessionDuration"/>).</summary>
    public void Reset() => _attempt = 0;
}
