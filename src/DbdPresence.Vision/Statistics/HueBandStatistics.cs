using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Statistics;

/// <summary>Colour measurements by hue. They need no template: a highlighted row is recognised by its colour alone.</summary>
public static class HueBandStatistics
{
    /// <summary>
    /// The share of pixels whose hue lies in the band and whose HSV saturation and value reach the minimums. Transparent pixels (alpha 0) and
    /// grey images count as outside.
    /// </summary>
    /// <param name="image">An RGBA image.</param>
    /// <param name="hueFrom">Start of the hue range in degrees, 0 to 360.</param>
    /// <param name="hueTo">End of the range; when smaller than <paramref name="hueFrom"/> the range wraps through 0.</param>
    /// <param name="minSaturation">Lowest saturation, 0 to 1.</param>
    /// <param name="minValue">Lowest value (brightness), 0 to 1.</param>
    public static double FractionInBand(ImageBuffer image, double hueFrom, double hueTo, double minSaturation, double minValue)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Format != PixelFormat.Rgba32)
        {
            return 0.0;
        }

        var pixels = image.Pixels;
        var inside = 0L;
        var total = image.Width * image.Height;
        for (var i = 0; i < total; i++)
        {
            if (pixels[(i * 4) + 3] == 0)
            {
                continue;
            }

            var (hue, saturation, value) = ToHsv(pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2]);
            if (saturation < minSaturation || value < minValue)
            {
                continue;
            }

            var within = hueFrom <= hueTo ? hue >= hueFrom && hue <= hueTo : hue >= hueFrom || hue <= hueTo;
            if (within)
            {
                inside++;
            }
        }

        return (double)inside / total;
    }

    /// <summary>Converts an RGB colour to hue (degrees, 0 to 360), saturation and value (both 0 to 1).</summary>
    public static (double Hue, double Saturation, double Value) ToHsv(byte r, byte g, byte b)
    {
        double red = r / 255.0;
        double green = g / 255.0;
        double blue = b / 255.0;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        if (delta <= 0)
        {
            return (0, 0, max);
        }

        double hue;
        if (max == red)
        {
            hue = 60.0 * (((green - blue) / delta) % 6.0);
        }
        else if (max == green)
        {
            hue = 60.0 * (((blue - red) / delta) + 2.0);
        }
        else
        {
            hue = 60.0 * (((red - green) / delta) + 4.0);
        }

        if (hue < 0)
        {
            hue += 360.0;
        }

        return (hue, delta / max, max);
    }
}
