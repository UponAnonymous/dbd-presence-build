using System.Buffers;

namespace DbdPresence.Vision.Imaging;

/// <summary>Pixel layouts the vision code understands.</summary>
public enum PixelFormat
{
    /// <summary>One byte per pixel (luma).</summary>
    Gray8,

    /// <summary>Four bytes per pixel in R, G, B, A order, straight alpha.</summary>
    Rgba32,
}

/// <summary>
/// An immutable in-memory raster. It is the only image type in the vision code: no operating-system imaging API is used,
/// so everything built on it runs identically on Linux CI and on Windows. Pixels are row-major with no padding.
/// </summary>
public sealed class ImageBuffer
{
    private readonly byte[] _data;

    /// <summary>Initializes a new instance of the <see cref="ImageBuffer"/> class from pixel data (copied).</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The pixel layout.</param>
    /// <param name="pixels">The pixel bytes; the length must be width x height x bytes per pixel.</param>
    public ImageBuffer(int width, int height, PixelFormat format, ReadOnlySpan<byte> pixels)
        : this(width, height, format, pixels.ToArray())
    {
    }

    private ImageBuffer(int width, int height, PixelFormat format, byte[] data)
    {
        if (width < 1 || height < 1 || (long)width * height > 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive and at most 64 Mpixel.");
        }

        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        var expected = (long)width * height * (format == PixelFormat.Rgba32 ? 4 : 1);
        if (data.Length != expected)
        {
            throw new ArgumentException($"Expected {expected} bytes for a {width}x{height} {format} image but got {data.Length}.", nameof(data));
        }

        Width = width;
        Height = height;
        Format = format;
        _data = data;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixel layout.</summary>
    public PixelFormat Format { get; }

    /// <summary>Gets the number of bytes per pixel.</summary>
    public int BytesPerPixel => Format == PixelFormat.Rgba32 ? 4 : 1;

    /// <summary>Gets the raw pixel bytes.</summary>
    public ReadOnlySpan<byte> Pixels => _data;

    /// <summary>Creates a gray image filled with one value.</summary>
    public static ImageBuffer CreateGray(int width, int height, byte fill = 0)
    {
        var data = new byte[checked(width * height)];
        Array.Fill(data, fill);
        return new ImageBuffer(width, height, PixelFormat.Gray8, data);
    }

    /// <summary>Creates an opaque RGBA image filled with one colour.</summary>
    public static ImageBuffer CreateRgba(int width, int height, byte r, byte g, byte b)
    {
        var data = new byte[checked(width * height * 4)];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = 255;
        }

        return new ImageBuffer(width, height, PixelFormat.Rgba32, data);
    }

    /// <summary>Creates a gray image from a per-pixel function (used by tests and by calibration helpers).</summary>
    public static ImageBuffer FromFunction(int width, int height, Func<int, int, byte> luma)
    {
        ArgumentNullException.ThrowIfNull(luma);
        var data = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[(y * width) + x] = luma(x, y);
            }
        }

        return new ImageBuffer(width, height, PixelFormat.Gray8, data);
    }

    /// <summary>Returns the luma of a pixel (BT.601 integer weights for RGBA).</summary>
    public byte GrayAt(int x, int y)
    {
        CheckBounds(x, y);
        var i = (y * Width) + x;
        return Format == PixelFormat.Gray8 ? _data[i] : Luma(_data[i * 4], _data[(i * 4) + 1], _data[(i * 4) + 2]);
    }

    /// <summary>Returns the colour of a pixel; a gray image reports its luma on all three channels.</summary>
    public (byte R, byte G, byte B) RgbAt(int x, int y)
    {
        CheckBounds(x, y);
        var i = (y * Width) + x;
        if (Format == PixelFormat.Gray8)
        {
            return (_data[i], _data[i], _data[i]);
        }

        return (_data[i * 4], _data[(i * 4) + 1], _data[(i * 4) + 2]);
    }

    /// <summary>Converts to a gray image; a gray image returns itself.</summary>
    public ImageBuffer ToGray()
    {
        if (Format == PixelFormat.Gray8)
        {
            return this;
        }

        var gray = new byte[Width * Height];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = Luma(_data[i * 4], _data[(i * 4) + 1], _data[(i * 4) + 2]);
        }

        return new ImageBuffer(Width, Height, PixelFormat.Gray8, gray);
    }

    /// <summary>Copies a pixel rectangle. The rectangle must lie inside the image.</summary>
    public ImageBuffer Crop(PixelRect rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width < 1 || rect.Height < 1 || rect.Right > Width || rect.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "The crop rectangle must lie inside the image.");
        }

        var bpp = BytesPerPixel;
        var data = new byte[rect.Width * rect.Height * bpp];
        for (var row = 0; row < rect.Height; row++)
        {
            _data.AsSpan((((rect.Y + row) * Width) + rect.X) * bpp, rect.Width * bpp).CopyTo(data.AsSpan(row * rect.Width * bpp));
        }

        return new ImageBuffer(rect.Width, rect.Height, Format, data);
    }

    /// <summary>Copies a region given in normalised coordinates, so the same call selects the same content at any resolution.</summary>
    public ImageBuffer Crop(NormalizedRect region) => Crop(region.ToPixels(Width, Height));

    /// <summary>
    /// Resizes with area averaging when shrinking and bilinear interpolation when enlarging. The result is deterministic
    /// (double arithmetic, round half away from zero).
    /// </summary>
    public ImageBuffer Resize(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The target size must be positive.");
        }

        if (width == Width && height == Height)
        {
            return this;
        }

        var xWeights = AxisWeights(Width, width);
        var yWeights = AxisWeights(Height, height);
        var bpp = BytesPerPixel;
        var horizontal = ArrayPool<double>.Shared.Rent(width * Height * bpp);
        try
        {
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (start, weights) = xWeights[x];
                    for (var c = 0; c < bpp; c++)
                    {
                        double sum = 0;
                        for (var k = 0; k < weights.Length; k++)
                        {
                            sum += weights[k] * _data[((((y * Width) + start + k)) * bpp) + c];
                        }

                        horizontal[((((y * width) + x)) * bpp) + c] = sum;
                    }
                }
            }

            var result = new byte[width * height * bpp];
            for (var y = 0; y < height; y++)
            {
                var (start, weights) = yWeights[y];
                for (var x = 0; x < width; x++)
                {
                    for (var c = 0; c < bpp; c++)
                    {
                        double sum = 0;
                        for (var k = 0; k < weights.Length; k++)
                        {
                            sum += weights[k] * horizontal[((((start + k) * width) + x) * bpp) + c];
                        }

                        result[((((y * width) + x)) * bpp) + c] = (byte)Math.Clamp((int)Math.Round(sum, MidpointRounding.AwayFromZero), 0, 255);
                    }
                }
            }

            return new ImageBuffer(width, height, Format, result);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(horizontal);
        }
    }

    /// <summary>Resizes to a width, keeping the aspect ratio (height rounded, at least 1).</summary>
    public ImageBuffer ResizeToWidth(int width)
    {
        var height = Math.Max(1, (int)Math.Round((double)Height * width / Width, MidpointRounding.AwayFromZero));
        return Resize(width, height);
    }

    private static byte Luma(byte r, byte g, byte b) => (byte)(((299 * r) + (587 * g) + (114 * b) + 500) / 1000);

    private void CheckBounds(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Pixel coordinates are outside the image.");
        }
    }

    private static (int Start, double[] Weights)[] AxisWeights(int source, int target)
    {
        var result = new (int, double[])[target];
        var scale = (double)source / target;
        for (var d = 0; d < target; d++)
        {
            if (target >= source)
            {
                // Enlarging: bilinear, pixel centres aligned.
                var position = Math.Clamp(((d + 0.5) * scale) - 0.5, 0, source - 1);
                var i0 = (int)Math.Floor(position);
                var i1 = Math.Min(i0 + 1, source - 1);
                var frac = position - i0;
                result[d] = i0 == i1 ? (i0, [1.0]) : (i0, [1 - frac, frac]);
            }
            else
            {
                // Shrinking: exact area coverage of [d * scale, (d + 1) * scale).
                var from = d * scale;
                var to = (d + 1) * scale;
                var first = (int)Math.Floor(from);
                var last = Math.Min((int)Math.Ceiling(to) - 1, source - 1);
                var weights = new double[last - first + 1];
                for (var i = first; i <= last; i++)
                {
                    weights[i - first] = (Math.Min(to, i + 1) - Math.Max(from, i)) / scale;
                }

                result[d] = (first, weights);
            }
        }

        return result;
    }
}
