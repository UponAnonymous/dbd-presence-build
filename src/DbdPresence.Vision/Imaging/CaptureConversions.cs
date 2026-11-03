using DbdPresence.Core.Capture;

namespace DbdPresence.Vision.Imaging;

/// <summary>
/// The bridge between the capture seam in Core (C3: <see cref="PixelBuffer"/>, <see cref="NormalizedRegion"/>) and the vision
/// types (C2: <see cref="ImageBuffer"/>, <see cref="NormalizedRect"/>). The capture adapter produces B8G8R8A8 crops with
/// premultiplied alpha; detectors read straight-alpha RGBA. Regions travel the other way: a detector's
/// <see cref="NormalizedRect"/> becomes the <see cref="NormalizedRegion"/> the capture adapter is asked for (FR-DET-13).
/// </summary>
public static class CaptureConversions
{
    /// <summary>Converts a captured crop to an RGBA image (channel order swapped, premultiplied alpha undone).</summary>
    /// <param name="buffer">A crop or frame from the capture adapter.</param>
    /// <returns>A new straight-alpha <see cref="PixelFormat.Rgba32"/> image of the same size.</returns>
    public static ImageBuffer ToImageBuffer(this PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var source = buffer.Bgra.Span;
        var rgba = new byte[source.Length];
        for (var i = 0; i < source.Length; i += PixelBuffer.BytesPerPixel)
        {
            var a = source[i + 3];
            rgba[i] = Unpremultiply(source[i + 2], a);
            rgba[i + 1] = Unpremultiply(source[i + 1], a);
            rgba[i + 2] = Unpremultiply(source[i], a);
            rgba[i + 3] = a;
        }

        return new ImageBuffer(buffer.Width, buffer.Height, PixelFormat.Rgba32, rgba);
    }

    /// <summary>
    /// Converts an image to the capture seam's B8G8R8A8 layout (opaque). The evaluator uses it to feed recorded frames through the same code the live
    /// capture feeds; grey images become grey-valued colour pixels.
    /// </summary>
    /// <param name="image">A decoded frame.</param>
    /// <returns>A new opaque pixel buffer of the same size.</returns>
    public static PixelBuffer ToPixelBuffer(this ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = image.Pixels;
        var bgra = new byte[image.Width * image.Height * PixelBuffer.BytesPerPixel];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            if (image.Format == PixelFormat.Gray8)
            {
                bgra[i * 4] = bgra[(i * 4) + 1] = bgra[(i * 4) + 2] = source[i];
            }
            else
            {
                bgra[i * 4] = source[(i * 4) + 2];
                bgra[(i * 4) + 1] = source[(i * 4) + 1];
                bgra[(i * 4) + 2] = source[i * 4];
            }

            bgra[(i * 4) + 3] = 255;
        }

        return new PixelBuffer(image.Width, image.Height, bgra);
    }

    /// <summary>Converts a detector region to the region type the capture adapter takes.</summary>
    /// <param name="rect">A region in fractions of the client area.</param>
    /// <returns>The same fractions as a capture region.</returns>
    public static NormalizedRegion ToCaptureRegion(this NormalizedRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Converts a capture region to the vision region type.</summary>
    /// <param name="region">A capture region.</param>
    /// <returns>The same fractions as a <see cref="NormalizedRect"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The region is not finite, empty or outside the unit square.</exception>
    public static NormalizedRect ToNormalizedRect(this NormalizedRegion region) => new(region.X, region.Y, region.Width, region.Height);

    private static byte Unpremultiply(byte channel, byte alpha) => alpha switch
    {
        0 => 0,
        255 => channel,
        _ => (byte)Math.Min(255, ((channel * 255) + (alpha / 2)) / alpha),
    };
}
