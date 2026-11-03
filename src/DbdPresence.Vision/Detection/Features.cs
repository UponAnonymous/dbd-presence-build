using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Matching;
using DbdPresence.Vision.Statistics;

namespace DbdPresence.Vision.Detection;

/// <summary>A measurement on a region crop. The score is in [0, 1]; it is a raw score, not a probability.</summary>
public interface IFeature
{
    /// <summary>Scores a region crop.</summary>
    double Score(ImageBuffer crop);
}

/// <summary>
/// Looks for a template inside the region at several scales. The crop is first brought to a canonical width, so the
/// same profile works at any resolution and display mode (PLAN_V2 D17); the scales cover HUD-scale differences. The
/// template is learned from the user's own calibration capture with <see cref="FromSample"/>. This repository contains no templates.
/// </summary>
public sealed class TemplateFeature : IFeature
{
    private static readonly double[] s_defaultScales = [0.8, 1.0, 1.25];

    private readonly ImageBuffer _template;
    private readonly IReadOnlyList<double> _scales;

    /// <summary>Initializes a new instance of the <see cref="TemplateFeature"/> class.</summary>
    /// <param name="template">A template at canonical scale (not flat).</param>
    /// <param name="canonicalWidth">Width the region crop is resized to before matching.</param>
    /// <param name="scales">Template scales to try; null uses 0.8, 1.0 and 1.25 (an initial guess for the HUD-scale range).</param>
    public TemplateFeature(ImageBuffer template, int canonicalWidth, IReadOnlyList<double>? scales = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfLessThan(canonicalWidth, TemplateMatcher.MinimumTemplateSide);

        if (template.Width > canonicalWidth)
        {
            throw new ArgumentException("The template must not be wider than the canonical region width.", nameof(template));
        }

        _template = template.ToGray();
        CanonicalWidth = canonicalWidth;
        _scales = scales ?? s_defaultScales;
        if (ColorStatistics.Summarize(_template).StdLuma < 1.0)
        {
            throw new ArgumentException("The template is flat, so it cannot identify anything.", nameof(template));
        }
    }

    /// <summary>Gets the width the region crop is resized to before matching.</summary>
    public int CanonicalWidth { get; }

    /// <summary>
    /// Learns a template from a calibration sample: the sample region is brought to the canonical width and
    /// <paramref name="anchor"/> (a rectangle inside the region, normalised to the region) is cut out of it.
    /// </summary>
    public static TemplateFeature FromSample(ImageBuffer sampleRegion, NormalizedRect anchor, int canonicalWidth = 96, IReadOnlyList<double>? scales = null)
    {
        ArgumentNullException.ThrowIfNull(sampleRegion);
        var canonical = sampleRegion.ToGray().ResizeToWidth(canonicalWidth);
        return new TemplateFeature(canonical.Crop(anchor), canonicalWidth, scales);
    }

    /// <inheritdoc />
    public double Score(ImageBuffer crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        var canonical = crop.ToGray().ResizeToWidth(CanonicalWidth);
        var match = TemplateMatcher.MatchMultiScale(canonical, _template, _scales);
        return match is { } m ? Math.Clamp(m.Score, 0.0, 1.0) : 0.0;
    }
}

/// <summary>Scores the share of pixels near a colour, relative to the share expected when the element is present.</summary>
public sealed class ColorFractionFeature : IFeature
{
    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;
    private readonly int _tolerance;
    private readonly double _expectedFraction;

    /// <summary>Initializes a new instance of the <see cref="ColorFractionFeature"/> class.</summary>
    /// <param name="r">Target red.</param>
    /// <param name="g">Target green.</param>
    /// <param name="b">Target blue.</param>
    /// <param name="tolerance">Per-channel tolerance.</param>
    /// <param name="expectedFraction">The share of matching pixels that counts as fully present, in (0, 1].</param>
    public ColorFractionFeature(byte r, byte g, byte b, int tolerance, double expectedFraction)
    {
        if (tolerance is < 0 or > 255 || expectedFraction is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        (_r, _g, _b, _tolerance, _expectedFraction) = (r, g, b, tolerance, expectedFraction);
    }

    /// <inheritdoc />
    public double Score(ImageBuffer crop) => Math.Min(1.0, ColorStatistics.FractionNear(crop, _r, _g, _b, _tolerance) / _expectedFraction);
}

/// <summary>Scores how closely the region's brightness histogram follows a reference histogram from calibration.</summary>
public sealed class HistogramFeature : IFeature
{
    private readonly Histogram _reference;

    /// <summary>Initializes a new instance of the <see cref="HistogramFeature"/> class.</summary>
    public HistogramFeature(Histogram reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _reference = reference;
    }

    /// <summary>Learns the reference from a calibration sample.</summary>
    public static HistogramFeature FromSample(ImageBuffer sampleRegion, int bins = 32) => new(Histogram.FromImage(sampleRegion, bins));

    /// <inheritdoc />
    public double Score(ImageBuffer crop) => Math.Clamp(Histogram.Similarity(Histogram.FromImage(crop, _reference.BinCount), _reference), 0.0, 1.0);
}

/// <summary>Scores perceptual-hash similarity to a reference hash from calibration.</summary>
public sealed class HashFeature : IFeature
{
    private readonly ulong _reference;
    private readonly Func<ImageBuffer, ulong> _hash;

    private HashFeature(ulong reference, Func<ImageBuffer, ulong> hash)
    {
        (_reference, _hash) = (reference, hash);
    }

    /// <summary>Learns a difference-hash reference from a calibration sample.</summary>
    public static HashFeature FromSample(ImageBuffer sampleRegion) => new(PerceptualHash.Difference(sampleRegion), PerceptualHash.Difference);

    /// <inheritdoc />
    public double Score(ImageBuffer crop) => PerceptualHash.Similarity(_hash(crop), _reference);
}
