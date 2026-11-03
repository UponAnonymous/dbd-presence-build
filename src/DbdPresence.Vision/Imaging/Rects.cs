namespace DbdPresence.Vision.Imaging;

/// <summary>A rectangle in pixels. Width and height are at least 1.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Gets the exclusive right edge.</summary>
    public int Right => X + Width;

    /// <summary>Gets the exclusive bottom edge.</summary>
    public int Bottom => Y + Height;
}

/// <summary>
/// A rectangle in normalised coordinates: fractions of the client area, with the origin at the top left and every value
/// in [0, 1]. Regions are stored this way so a profile is independent of the resolution and display mode (PLAN_V2 D17).
/// </summary>
public readonly record struct NormalizedRect
{
    /// <summary>Initializes a new instance of the <see cref="NormalizedRect"/> struct.</summary>
    /// <param name="x">Left edge as a fraction of the width.</param>
    /// <param name="y">Top edge as a fraction of the height.</param>
    /// <param name="width">Width as a fraction of the width.</param>
    /// <param name="height">Height as a fraction of the height.</param>
    public NormalizedRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Rectangle values must be finite.");
        }

        const double Tolerance = 1e-9;
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1 + Tolerance || y + height > 1 + Tolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "A normalised rectangle must lie inside the unit square and have a positive size.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Gets the left edge.</summary>
    public double X { get; }

    /// <summary>Gets the top edge.</summary>
    public double Y { get; }

    /// <summary>Gets the width.</summary>
    public double Width { get; }

    /// <summary>Gets the height.</summary>
    public double Height { get; }

    /// <summary>
    /// Maps the rectangle onto an image of the given size. Edges are rounded to the nearest pixel (half away from
    /// zero) so the same region selects the same content at every resolution; the result has at least one pixel.
    /// </summary>
    public PixelRect ToPixels(int imageWidth, int imageHeight)
    {
        if (imageWidth < 1 || imageHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "The image must have at least one pixel.");
        }

        var (x0, x1) = Span(X, Width, imageWidth);
        var (y0, y1) = Span(Y, Height, imageHeight);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static (int Start, int End) Span(double start, double size, int length)
    {
        var a = Math.Clamp((int)Math.Round(start * length, MidpointRounding.AwayFromZero), 0, length - 1);
        var b = Math.Clamp((int)Math.Round((start + size) * length, MidpointRounding.AwayFromZero), a + 1, length);
        return (a, b);
    }
}
