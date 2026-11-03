namespace DbdPresence.Core.Capture;

/// <summary>A rectangle in whole pixels of a frame.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// A region in normalised frame coordinates (0 to 1 from the top-left corner), so one profile works at every resolution
/// (PLAN_V2 D17, FR-CAP-02).
/// </summary>
public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height)
{
    /// <summary>Gets a value indicating whether the region is finite, non-empty and inside the unit square.</summary>
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1.000001 && Y + Height <= 1.000001;

    /// <summary>
    /// Maps the region to the smallest pixel rectangle that covers it, clamped to the frame (at least 1x1).
    /// Rounding outwards keeps thin regions from vanishing at low resolutions.
    /// </summary>
    public PixelRect ToPixels(int frameWidth, int frameHeight)
    {
        if (!IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth), this, "The region must be inside the unit square.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(frameWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameHeight, 1);

        var left = Math.Clamp((int)Math.Floor(X * frameWidth), 0, frameWidth - 1);
        var top = Math.Clamp((int)Math.Floor(Y * frameHeight), 0, frameHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(((X + Width) * frameWidth) - 1e-9), left + 1, frameWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(((Y + Height) * frameHeight) - 1e-9), top + 1, frameHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
