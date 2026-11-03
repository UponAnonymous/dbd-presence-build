namespace DbdPresence.ImageHelper;

/// <summary>
/// Shrinks BGRA32 pixels with exact area averaging (a box filter over the covered source area, weighted by overlap, on premultiplied
/// colour), which keeps edges clean without ringing. It never enlarges (FR-PACK-12).
/// </summary>
internal static class ImageResampler
{
    /// <summary>The output size: the source size when both sides fit, otherwise scaled so the longer side is exactly <paramref name="maxEdge"/>.</summary>
    public static (int Width, int Height) FitSize(int width, int height, int maxEdge)
    {
        if (width <= maxEdge && height <= maxEdge)
        {
            return (width, height);
        }

        return width >= height
            ? (maxEdge, Math.Max(1, (int)Math.Round((double)height * maxEdge / width)))
            : (Math.Max(1, (int)Math.Round((double)width * maxEdge / height)), maxEdge);
    }

    /// <summary>Resamples to a size no larger than the source in either direction.</summary>
    public static byte[] Resample(byte[] bgra, int width, int height, int outWidth, int outHeight)
    {
        if (outWidth > width || outHeight > height)
        {
            throw new ArgumentOutOfRangeException(nameof(outWidth), "Enlarging is not supported.");
        }

        // Pass 1 (horizontal) keeps the full source height; pass 2 (vertical) brings it to the output height.
        // Values are premultiplied (colour * alpha) on a 0..1 scale.
        var horizontal = new float[outWidth * height * 4];
        var xScale = (double)width / outWidth;
        var sums = new double[4];
        for (var ox = 0; ox < outWidth; ox++)
        {
            var start = ox * xScale;
            var end = Math.Min(width, start + xScale);
            for (var y = 0; y < height; y++)
            {
                Array.Clear(sums);
                var total = 0.0;
                var last = Math.Min((int)Math.Ceiling(end) - 1, width - 1);
                for (var i = (int)Math.Floor(start); i <= last; i++)
                {
                    var weight = Math.Min(end, i + 1) - Math.Max(start, i);
                    if (weight <= 0)
                    {
                        continue;
                    }

                    var p = ((y * width) + i) * 4;
                    var alpha = bgra[p + 3] / 255.0;
                    sums[0] += bgra[p] / 255.0 * alpha * weight;
                    sums[1] += bgra[p + 1] / 255.0 * alpha * weight;
                    sums[2] += bgra[p + 2] / 255.0 * alpha * weight;
                    sums[3] += alpha * weight;
                    total += weight;
                }

                var target = ((y * outWidth) + ox) * 4;
                for (var c = 0; c < 4; c++)
                {
                    horizontal[target + c] = (float)(sums[c] / total);
                }
            }
        }

        var result = new byte[outWidth * outHeight * 4];
        var yScale = (double)height / outHeight;
        for (var oy = 0; oy < outHeight; oy++)
        {
            var start = oy * yScale;
            var end = Math.Min(height, start + yScale);
            var last = Math.Min((int)Math.Ceiling(end) - 1, height - 1);
            for (var x = 0; x < outWidth; x++)
            {
                Array.Clear(sums);
                var total = 0.0;
                for (var i = (int)Math.Floor(start); i <= last; i++)
                {
                    var weight = Math.Min(end, i + 1) - Math.Max(start, i);
                    if (weight <= 0)
                    {
                        continue;
                    }

                    var p = ((i * outWidth) + x) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        sums[c] += horizontal[p + c] * weight;
                    }

                    total += weight;
                }

                var target = ((oy * outWidth) + x) * 4;
                var a = sums[3] / total;
                result[target + 3] = ToByte(a);
                for (var c = 0; c < 3; c++)
                {
                    result[target + c] = a <= 0 ? (byte)0 : ToByte(sums[c] / total / a);
                }
            }
        }

        return result;
    }

    private static byte ToByte(double unit) => (byte)Math.Clamp((int)Math.Round(unit * 255.0), 0, 255);
}
