namespace DbdPresence.Core.Detection;

/// <summary>
/// Rolling health of one detector (FR-DET-10, DETECTION_VALIDATION §8): a detector is Degraded when its unreadable
/// rate over the health window exceeds the validation baseline by the configured factor, or when its context no
/// longer matches what it was validated for (language, game version, HUD profile). It is Stale when capture runs but
/// no observation arrives.
/// </summary>
internal sealed class DetectorHealthTracker
{
    private readonly Queue<(DateTimeOffset At, bool Unreadable)> _samples = new();
    private int _unreadable;
    private DateTimeOffset? _last;
    private string? _contextProblem;

    public void Reset()
    {
        _samples.Clear();
        _unreadable = 0;
        _last = null;
        _contextProblem = null;
    }

    public void Record(DateTimeOffset at, bool unreadable, ContextKey context, DetectorSettings settings)
    {
        _last = at;
        _samples.Enqueue((at, unreadable));
        if (unreadable)
        {
            _unreadable++;
        }

        _contextProblem = ContextProblem(context, settings);
    }

    public DetectorHealth Evaluate(DateTimeOffset now, EngineOptions options, DetectorSettings settings, bool expectingData)
    {
        while (_samples.TryPeek(out var oldest) && now - oldest.At > options.HealthWindow)
        {
            _samples.Dequeue();
            if (oldest.Unreadable)
            {
                _unreadable--;
            }
        }

        if (_last is null)
        {
            return new DetectorHealth(HealthState.NoData, null, null, null, 0);
        }

        var samples = _samples.Count;
        double? rate = samples >= options.HealthMinSamples ? (double)_unreadable / samples : null;

        if (_contextProblem is not null)
        {
            return new DetectorHealth(HealthState.Degraded, _contextProblem, _last, rate, samples);
        }

        if (rate is { } r && settings.BaselineUnreadableRate is { } baseline)
        {
            var limit = Math.Max(options.DegradedFactor * baseline, options.MinDegradedUnreadableRate);
            if (r > limit)
            {
                return new DetectorHealth(
                    HealthState.Degraded,
                    FormattableString.Invariant($"unreadable rate {r:P0} exceeds {options.DegradedFactor:0.#}x the validation baseline"),
                    _last,
                    rate,
                    samples);
            }
        }

        if (expectingData && now - _last.Value > options.HealthSilence)
        {
            return new DetectorHealth(HealthState.Stale, "no observations while capture is active", _last, rate, samples);
        }

        return new DetectorHealth(HealthState.Healthy, null, _last, rate, samples);
    }

    private static string? ContextProblem(ContextKey context, DetectorSettings settings)
    {
        if (!context.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "unsupported game language (English only)";
        }

        if (settings.ValidatedGameVersion is { } version && !string.Equals(version, context.GameVersion, StringComparison.Ordinal))
        {
            return "game version differs from the validated version";
        }

        if (settings.ValidatedProfileId is { } profile && !string.Equals(profile, context.ProfileId, StringComparison.Ordinal))
        {
            return "HUD profile differs from the validated profile";
        }

        return null;
    }
}
