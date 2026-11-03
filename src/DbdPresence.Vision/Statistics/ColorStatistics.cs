using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Statistics;

/// <summary>Per-channel and luma statistics of an image.</summary>
/// <param name="Pixels">Number of pixels.</param>
/// <param name="MeanR">Mean red.</param>
/// <param name="MeanG">Mean green.</param>
/// <param name="MeanB">Mean blue.</param>
/// <param name="MeanLuma">Mean luma.</param>
/// <param name="StdLuma">Population standard deviation of luma.</param>
/// <param name="MinLuma">Smallest luma.</param>
/// <param name="MaxLuma">Largest luma.</param>
public readonly record struct ColorSummary(int Pixels, double MeanR, double MeanG, double MeanB, double MeanLuma, double StdLuma, byte MinLuma, byte MaxLuma);

/// <summary>Colour and brightness measurements used as detector features. All results are in [0, 1] or in 0..255 as stated.</summary>
public static class ColorStatistics
{
    /// <summary>Computes channel means and luma statistics.</summary>
    public static ColorSummary Summarize(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        double r = 0;
        double g = 0;
        double b = 0;
        double luma = 0;
        double lumaSquares = 0;
        byte min = 255;
        byte max = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (pr, pg, pb) = image.RgbAt(x, y);
                var l = image.GrayAt(x, y);
                r += pr;
                g += pg;
                b += pb;
                luma += l;
                lumaSquares += (double)l * l;
                min = Math.Min(min, l);
                max = Math.Max(max, l);
            }
        }

        var n = (double)image.Width * image.Height;
        var mean = luma / n;
        return new ColorSummary(image.Width * image.Height, r / n, g / n, b / n, mean, Math.Sqrt(Math.Max(0, (lumaSquares / n) - (mean * mean))), min, max);
    }

    /// <summary>The fraction of pixels whose three channels are each within <paramref name="tolerance"/> of the target colour.</summary>
    public static double FractionNear(ImageBuffer image, byte r, byte g, byte b, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (tolerance is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        }

        var hits = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (pr, pg, pb) = image.RgbAt(x, y);
                if (Math.Abs(pr - r) <= tolerance && Math.Abs(pg - g) <= tolerance && Math.Abs(pb - b) <= tolerance)
                {
                    hits++;
                }
            }
        }

        return (double)hits / (image.Width * image.Height);
    }

    /// <summary>The fraction of pixels whose luma is at least <paramref name="threshold"/>.</summary>
    public static double FractionBrighterThan(ImageBuffer image, byte threshold)
    {
        ArgumentNullException.ThrowIfNull(image);
        var hits = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.GrayAt(x, y) >= threshold)
                {
                    hits++;
                }
            }
        }

        return (double)hits / (image.Width * image.Height);
    }

    /// <summary>
    /// The fraction of interior pixels whose luma gradient (|dx| + |dy|, central differences) is at least
    /// <paramref name="threshold"/>. A flat panel scores about 0; text, icons and borders score higher.
    /// Images smaller than 3 x 3 score 0.
    /// </summary>
    public static double EdgeDensity(ImageBuffer image, int threshold = 40)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 3 || image.Height < 3)
        {
            return 0;
        }

        var edges = 0;
        for (var y = 1; y < image.Height - 1; y++)
        {
            for (var x = 1; x < image.Width - 1; x++)
            {
                var dx = image.GrayAt(x + 1, y) - image.GrayAt(x - 1, y);
                var dy = image.GrayAt(x, y + 1) - image.GrayAt(x, y - 1);
                if (Math.Abs(dx) + Math.Abs(dy) >= threshold)
                {
                    edges++;
                }
            }
        }

        return (double)edges / ((image.Width - 2) * (image.Height - 2));
    }
}
