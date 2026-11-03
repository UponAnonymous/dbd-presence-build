using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Matching;

/// <summary>The best placement of a template inside an image.</summary>
/// <param name="X">Left edge of the match in the image.</param>
/// <param name="Y">Top edge of the match in the image.</param>
/// <param name="Scale">The template scale that produced the match (1 = as given).</param>
/// <param name="Score">Zero-mean normalised cross-correlation in [-1, 1]; 1 is a perfect match, 0 no correlation.</param>
public readonly record struct TemplateMatch(int X, int Y, double Scale, double Score);

/// <summary>
/// Zero-mean normalised cross-correlation (ZNCC) template matching on luma. The score does not change when the whole
/// image is brightened or its contrast is scaled, which is what makes a template learned in one lighting condition usable
/// in another. Multi-scale matching resizes the template to cope with different resolutions and HUD scales (PLAN_V2 D17).
/// Templates come from the user's own calibration captures; nothing here ships game artwork.
/// </summary>
public static class TemplateMatcher
{
    private const double FlatTolerance = 1e-9;

    /// <summary>Smallest template side (in pixels) the multi-scale search will try.</summary>
    public const int MinimumTemplateSide = 4;

    /// <summary>
    /// Finds the best placement of <paramref name="template"/> in <paramref name="image"/> at the template's own size.
    /// Returns null when the template does not fit. A flat window scores 0. A flat template carries no information and throws.
    /// </summary>
    public static TemplateMatch? Match(ImageBuffer image, ImageBuffer template)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        var source = image.ToGray();
        var pattern = template.ToGray();
        if (pattern.Width > source.Width || pattern.Height > source.Height)
        {
            return null;
        }

        var prepared = Prepare(pattern) ?? throw new ArgumentException("The template is flat (no contrast), so it cannot be matched.", nameof(template));
        return Search(source, prepared, scale: 1.0);
    }

    /// <summary>
    /// Matches the template at each scale and returns the best result (ties keep the first scale listed). Scales whose
    /// template is smaller than <see cref="MinimumTemplateSide"/>, larger than the image or flat are skipped; returns
    /// null when no scale could be tried.
    /// </summary>
    public static TemplateMatch? MatchMultiScale(ImageBuffer image, ImageBuffer template, IReadOnlyList<double> scales)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(scales);
        var source = image.ToGray();
        var pattern = template.ToGray();
        TemplateMatch? best = null;
        foreach (var scale in scales)
        {
            if (!double.IsFinite(scale) || scale <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(scales), "Scales must be positive and finite.");
            }

            var width = (int)Math.Round(pattern.Width * scale, MidpointRounding.AwayFromZero);
            var height = (int)Math.Round(pattern.Height * scale, MidpointRounding.AwayFromZero);
            if (width < MinimumTemplateSide || height < MinimumTemplateSide || width > source.Width || height > source.Height)
            {
                continue;
            }

            var scaled = pattern.Resize(width, height);
            if (Prepare(scaled) is not { } prepared)
            {
                continue; // A scale that flattens the template (extreme shrink) has nothing to match.
            }

            var candidate = Search(source, prepared, scale);
            if (candidate is { } c && (best is null || c.Score > best.Value.Score))
            {
                best = c;
            }
        }

        return best;
    }

    private static Prepared? Prepare(ImageBuffer pattern)
    {
        var n = pattern.Width * pattern.Height;
        double sum = 0;
        for (var y = 0; y < pattern.Height; y++)
        {
            for (var x = 0; x < pattern.Width; x++)
            {
                sum += pattern.GrayAt(x, y);
            }
        }

        var mean = sum / n;
        var deviations = new double[n];
        double norm = 0;
        for (var y = 0; y < pattern.Height; y++)
        {
            for (var x = 0; x < pattern.Width; x++)
            {
                var d = pattern.GrayAt(x, y) - mean;
                deviations[(y * pattern.Width) + x] = d;
                norm += d * d;
            }
        }

        if (norm < FlatTolerance)
        {
            return null;
        }

        return new Prepared(pattern.Width, pattern.Height, deviations, Math.Sqrt(norm));
    }

    private static TemplateMatch? Search(ImageBuffer image, Prepared t, double scale)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;
        var stride = width + 1;
        var sum = new long[stride * (height + 1)];
        var squares = new long[stride * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            long rowSum = 0;
            long rowSquares = 0;
            for (var x = 0; x < width; x++)
            {
                long v = pixels[(y * width) + x];
                rowSum += v;
                rowSquares += v * v;
                sum[((y + 1) * stride) + x + 1] = sum[(y * stride) + x + 1] + rowSum;
                squares[((y + 1) * stride) + x + 1] = squares[(y * stride) + x + 1] + rowSquares;
            }
        }

        var n = (double)(t.Width * t.Height);
        var best = new TemplateMatch(0, 0, scale, double.NegativeInfinity);
        for (var y = 0; y + t.Height <= height; y++)
        {
            for (var x = 0; x + t.Width <= width; x++)
            {
                var s = Window(sum, stride, x, y, t.Width, t.Height);
                var q = Window(squares, stride, x, y, t.Width, t.Height);
                var variance = q - (s * (double)s / n);
                double score;
                if (variance < FlatTolerance)
                {
                    score = 0;
                }
                else
                {
                    double numerator = 0;
                    for (var ty = 0; ty < t.Height; ty++)
                    {
                        var row = pixels.Slice(((y + ty) * width) + x, t.Width);
                        var offset = ty * t.Width;
                        for (var tx = 0; tx < row.Length; tx++)
                        {
                            numerator += t.Deviations[offset + tx] * row[tx];
                        }
                    }

                    score = Math.Clamp(numerator / (t.Norm * Math.Sqrt(variance)), -1.0, 1.0);
                }

                if (score > best.Score)
                {
                    best = new TemplateMatch(x, y, scale, score);
                }
            }
        }

        return double.IsNegativeInfinity(best.Score) ? null : best;
    }

    private static long Window(long[] table, int stride, int x, int y, int w, int h) =>
        table[((y + h) * stride) + x + w] - table[(y * stride) + x + w] - table[((y + h) * stride) + x] + table[(y * stride) + x];

    private readonly record struct Prepared(int Width, int Height, double[] Deviations, double Norm);
}
