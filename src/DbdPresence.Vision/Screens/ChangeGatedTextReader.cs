using DbdPresence.Core.Capture;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// The change gate in front of text recognition (detection-speed-and-confidence §3.1: cheap checks every frame, expensive checks only on change).
/// A region is recognised once, and not again until its pixels change. Every figure is an initial guess until measured.
/// </summary>
/// <param name="ChangeThreshold">The mean absolute difference of the region's luma signature (0 to 1) above which the region counts as changed.</param>
/// <param name="CellThreshold">The largest difference in any one signature cell (0 to 1) above which the region counts as changed; it catches a small change such as one digit of a timer that barely moves the mean.</param>
/// <param name="MinInterval">The shortest time between two recognitions of the same region. A region that changes faster is read as unread until the interval has passed.</param>
/// <param name="MaxAge">After this time a region is recognised again even if it did not change, so a transient failure cannot stay cached.</param>
public sealed record ChangeGateOptions(double ChangeThreshold = 0.03, double CellThreshold = 0.15, TimeSpan? MinInterval = null, TimeSpan? MaxAge = null)
{
    /// <summary>Gets the shortest time between two recognitions of one region (default 250 ms).</summary>
    public TimeSpan EffectiveMinInterval => MinInterval ?? TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the refresh age (default 10 s).</summary>
    public TimeSpan EffectiveMaxAge => MaxAge ?? TimeSpan.FromSeconds(10);

    /// <summary>Throws when an option is unusable.</summary>
    public void Validate()
    {
        if (ChangeThreshold is <= 0 or >= 1 || CellThreshold is <= 0 or >= 1 || EffectiveMinInterval < TimeSpan.Zero || EffectiveMaxAge <= EffectiveMinInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(ChangeThreshold), "Thresholds must lie within (0, 1) and MaxAge must exceed MinInterval.");
        }
    }
}

/// <summary>What the gate did for one region.</summary>
/// <param name="Text">The recognised text, or null when the region is not read right now (recognition failed, or the region changed again inside the minimum interval).</param>
/// <param name="Recognized">True when text recognition ran for this call; false when the cached reading was reused or nothing was read.</param>
/// <param name="Status">The recognition status when it ran.</param>
public readonly record struct GatedText(string? Text, bool Recognized, TextRecognitionStatus? Status);

/// <summary>
/// Recognises text in region crops, but only when a region's pixels changed since it was last read: one OCR call per region per change.
/// An unchanged region reuses its cached text (text recognition is deterministic, so identical pixels give identical text); a region that changed
/// again inside <see cref="ChangeGateOptions.MinInterval"/> reports no text instead of an outdated one ("silence over error"). All times come
/// from the injected <see cref="TimeProvider"/>. The reader keeps no pixels, only a small luma signature per region. Not thread-safe: one
/// pipeline calls it for one frame at a time.
/// </summary>
public sealed class ChangeGatedTextReader
{
    private const int Columns = 48;
    private const int Rows = 12;

    private readonly ITextRecognizer _ocr;
    private readonly TimeProvider _time;
    private readonly ChangeGateOptions _options;
    private readonly Dictionary<string, RegionState> _states = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ChangeGatedTextReader"/> class.</summary>
    /// <param name="ocr">The recogniser (the Windows English adapter in the app, a fake in tests).</param>
    /// <param name="time">The clock.</param>
    /// <param name="options">Gate options; null uses the initial guesses.</param>
    public ChangeGatedTextReader(ITextRecognizer ocr, TimeProvider time, ChangeGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(time);
        _ocr = ocr;
        _time = time;
        _options = options ?? new ChangeGateOptions();
        _options.Validate();
    }

    /// <summary>Gets how many times text recognition ran.</summary>
    public long OcrCalls { get; private set; }

    /// <summary>Gets how many calls reused a cached reading because the region had not changed.</summary>
    public long CacheHits { get; private set; }

    /// <summary>Gets how many calls reported no text because the region changed again inside the minimum interval.</summary>
    public long Deferred { get; private set; }

    /// <summary>Forgets every cached reading (the game closed, or the window changed size).</summary>
    public void Reset() => _states.Clear();

    /// <summary>
    /// Forgets the cached readings (text and signature) of the regions the predicate selects, so their text is read afresh the next time and no
    /// text read earlier stays in memory (used when the screen changes: the cached text of other players' names must not outlive its screen).
    /// </summary>
    /// <param name="select">Returns true for the region ids to forget.</param>
    public void Forget(Func<string, bool> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        foreach (var id in _states.Keys.Where(select).ToList())
        {
            _states.Remove(id);
        }
    }

    /// <summary>Gets the ids of the regions whose recognised text is held in memory right now (for tests of the cache rules).</summary>
    internal IReadOnlyCollection<string> CachedRegionIds => [.. _states.Where(s => s.Value.Text is not null).Select(s => s.Key)];

    /// <summary>Reads one region.</summary>
    public async Task<GatedText> ReadAsync(string regionId, PixelBuffer crop, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionId);
        ArgumentNullException.ThrowIfNull(crop);
        var now = _time.GetUtcNow();
        var signature = Signature(crop);
        if (_states.TryGetValue(regionId, out var state))
        {
            var changed = Changed(state.Signature, signature);
            // A clock that steps back (a changed system time, or the next recording session of an offline run) is treated as "long ago".
            var sinceLast = now >= state.LastRecognizedAt ? now - state.LastRecognizedAt : TimeSpan.MaxValue;
            if (state.Text is not null && !changed && sinceLast < _options.EffectiveMaxAge)
            {
                CacheHits++;
                return new GatedText(state.Text, false, null);
            }

            if (sinceLast < _options.EffectiveMinInterval)
            {
                Deferred++;
                return new GatedText(null, false, null);
            }
        }

        OcrCalls++;
        var result = await _ocr.RecognizeAsync(crop, cancellationToken).ConfigureAwait(false);
        if (result.Status != TextRecognitionStatus.Recognized)
        {
            _states[regionId] = new RegionState(signature, null, now); // retry after the minimum interval, never cache a failure
            return new GatedText(null, true, result.Status);
        }

        _states[regionId] = new RegionState(signature, result.Text, now);
        return new GatedText(result.Text, true, result.Status);
    }

    private bool Changed(byte[] previous, byte[] current)
    {
        var total = 0;
        var largest = 0;
        for (var i = 0; i < previous.Length; i++)
        {
            var diff = Math.Abs(previous[i] - current[i]);
            total += diff;
            largest = Math.Max(largest, diff);
        }

        return ((double)total / previous.Length / 255.0) > _options.ChangeThreshold || (largest / 255.0) > _options.CellThreshold;
    }

    /// <summary>A coarse grid of mean luma values: a few hundred bytes per region, enough to notice text appearing or changing.</summary>
    private static byte[] Signature(PixelBuffer crop)
    {
        var signature = new byte[Columns * Rows];
        var bgra = crop.Bgra.Span;
        for (var row = 0; row < Rows; row++)
        {
            var y0 = row * crop.Height / Rows;
            var y1 = Math.Max(y0 + 1, (row + 1) * crop.Height / Rows);
            for (var column = 0; column < Columns; column++)
            {
                var x0 = column * crop.Width / Columns;
                var x1 = Math.Max(x0 + 1, (column + 1) * crop.Width / Columns);
                long sum = 0;
                var count = 0;
                for (var y = y0; y < Math.Min(y1, crop.Height); y++)
                {
                    for (var x = x0; x < Math.Min(x1, crop.Width); x++)
                    {
                        var i = (y * crop.Stride) + (x * PixelBuffer.BytesPerPixel);
                        sum += ((bgra[i + 2] * 299) + (bgra[i + 1] * 587) + (bgra[i] * 114)) / 1000;
                        count++;
                    }
                }

                signature[(row * Columns) + column] = count == 0 ? (byte)0 : (byte)(sum / count);
            }
        }

        return signature;
    }

    private readonly record struct RegionState(byte[] Signature, string? Text, DateTimeOffset LastRecognizedAt);
}
