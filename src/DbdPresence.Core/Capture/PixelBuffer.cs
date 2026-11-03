namespace DbdPresence.Core.Capture;

/// <summary>
/// A CPU image in B8G8R8A8 order (premultiplied alpha, top row first). This is the seam between the Windows capture adapter and
/// the platform-neutral recognition code (ADR-0003, ADR-0004): the adapter produces it, detectors and the OCR adapter consume it.
/// </summary>
public sealed class PixelBuffer
{
    /// <summary>Bytes per pixel in a <see cref="PixelBuffer"/>.</summary>
    public const int BytesPerPixel = 4;

    private readonly byte[] _bgra;

    /// <summary>Initializes a new instance of the <see cref="PixelBuffer"/> class.</summary>
    /// <param name="width">Width in pixels, at least 1.</param>
    /// <param name="height">Height in pixels, at least 1.</param>
    /// <param name="bgra">Tightly packed pixel rows (stride = width * 4); the buffer takes ownership.</param>
    public PixelBuffer(int width, int height, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != checked(width * height * BytesPerPixel))
        {
            throw new ArgumentException("The pixel data length must equal width * height * 4.", nameof(bgra));
        }

        Width = width;
        Height = height;
        _bgra = bgra;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the row stride in bytes.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>Gets the pixel bytes (B, G, R, A per pixel).</summary>
    public ReadOnlyMemory<byte> Bgra => _bgra;

    /// <summary>Reads one pixel.</summary>
    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        var i = (y * Stride) + (x * BytesPerPixel);
        return (_bgra[i + 2], _bgra[i + 1], _bgra[i], _bgra[i + 3]);
    }

    /// <summary>Copies a rectangle into a new buffer; the rectangle must lie inside this image.</summary>
    public PixelBuffer Crop(PixelRect rect)
    {
        if (rect.Width < 1 || rect.Height < 1 || rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > Width || rect.Y + rect.Height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "The crop rectangle must be non-empty and inside the image.");
        }

        var rowBytes = rect.Width * BytesPerPixel;
        var result = new byte[rowBytes * rect.Height];
        for (var row = 0; row < rect.Height; row++)
        {
            Buffer.BlockCopy(_bgra, ((rect.Y + row) * Stride) + (rect.X * BytesPerPixel), result, row * rowBytes, rowBytes);
        }

        return new PixelBuffer(rect.Width, rect.Height, result);
    }

    /// <summary>
    /// Whether every colour channel of every pixel is at most <paramref name="threshold"/>. A captured window that is
    /// entirely black usually means the surface could not be read (protected content, exclusive fullscreen), not a black screen.
    /// </summary>
    public bool IsEffectivelyBlack(byte threshold = 2)
    {
        for (var i = 0; i < _bgra.Length; i += BytesPerPixel)
        {
            if (_bgra[i] > threshold || _bgra[i + 1] > threshold || _bgra[i + 2] > threshold)
            {
                return false;
            }
        }

        return true;
    }
}
