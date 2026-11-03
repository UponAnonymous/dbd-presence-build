using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DbdPresence.ImageHelper;

/// <summary>
/// Decodes one untrusted image with the OS codecs and re-encodes it as a clean PNG (FR-PACK-10, FR-ART-03).
/// Order: magic bytes and file size, header dimensions, full decode ignoring metadata and colour profiles, scale to fit,
/// PNG without metadata. Only the re-encoded pixels leave this process.
/// </summary>
internal static class ImageSanitizer
{
    public const long MaxFileBytes = 8L * 1024 * 1024;
    public const int MaxSourceDimension = 4096;
    public const int MaxOutputDimension = 1024;

    /// <summary>The standard picture size in fit mode.</summary>
    public const int FitEdgeStandard = 512;

    /// <summary>The large picture size in fit mode.</summary>
    public const int FitEdgeLarge = 1024;

    /// <summary>A fitted picture must be smaller than this (FR-PACK-12).</summary>
    public const int MaxFitBytes = 2 * 1024 * 1024;

    private static readonly byte[] s_pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] s_jpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] s_pngEnd = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    public static int Run(string inputPath, string outputPath, TextWriter error, int? fitEdge = null)
    {
        byte[] data;
        try
        {
            var info = new FileInfo(inputPath);
            if (!info.Exists)
            {
                error.WriteLine("input file not found");
                return ExitCodes.BadInput;
            }

            if (info.Length > MaxFileBytes)
            {
                error.WriteLine("file larger than 8 MB");
                return ExitCodes.TooLarge;
            }

            data = ReadCapped(inputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"input unreadable: {ex.GetType().Name}");
            return ExitCodes.BadInput;
        }

        if (data.Length > MaxFileBytes)
        {
            error.WriteLine("file larger than 8 MB");
            return ExitCodes.TooLarge;
        }

        if (!HasImageMagic(data))
        {
            error.WriteLine("not a PNG, JPEG or WebP file");
            return ExitCodes.BadInput;
        }

        // WIC decodes truncated files into a partial image without an error, so completeness is checked first.
        if (!IsComplete(data))
        {
            error.WriteLine("the file is incomplete (truncated)");
            return ExitCodes.DecodeFailed;
        }

        try
        {
            if (!TryReadDimensions(data, out var width, out var height))
            {
                error.WriteLine("image header could not be read");
                return ExitCodes.DecodeFailed;
            }

            if (width > MaxSourceDimension || height > MaxSourceDimension)
            {
                error.WriteLine($"image {width}x{height} exceeds {MaxSourceDimension}x{MaxSourceDimension}");
                return ExitCodes.TooLarge;
            }

            // Exactly one frame, checked without decoding pixels, so an animation or multi-image file never reaches the full decode.
            if (!HasExactlyOneFrame(data))
            {
                error.WriteLine("the file does not hold exactly one image frame");
                return ExitCodes.DecodeFailed;
            }

            var png = fitEdge is { } edge ? DecodeAndFit(data, edge) : DecodeAndEncode(data);
            if (fitEdge is not null && png.Length >= MaxFitBytes)
            {
                error.WriteLine("the fitted picture is 2 MB or more");
                return ExitCodes.OutputTooLarge;
            }

            File.WriteAllBytes(outputPath, png);
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
            or COMException or ArgumentException or OutOfMemoryException or OverflowException or InvalidDataException)
        {
            error.WriteLine($"decode failed: {ex.GetType().Name}");
            return ExitCodes.DecodeFailed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"output could not be written: {ex.GetType().Name}");
            return ExitCodes.DecodeFailed;
        }
    }

    private static byte[] ReadCapped(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (buffer.Length <= MaxFileBytes)
        {
            var n = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxFileBytes + 1 - buffer.Length));
            if (n == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }

    // PNG must end with its IEND chunk, JPEG with the EOI marker, WebP's RIFF size must match the file length.
    private static bool IsComplete(byte[] data)
    {
        var span = data.AsSpan();
        if (span.StartsWith(s_pngMagic))
        {
            return span.EndsWith(s_pngEnd);
        }

        if (span.StartsWith(s_jpegMagic))
        {
            return span.EndsWith((ReadOnlySpan<byte>)[0xFF, 0xD9]);
        }

        return data.Length >= 12 && (long)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4)) + 8 == data.Length;
    }

    private static bool HasImageMagic(byte[] data) =>
        data.AsSpan().StartsWith(s_pngMagic)
        || data.AsSpan().StartsWith(s_jpegMagic)
        || (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8));

    // Header only: a PNG's IHDR is read directly; other formats use BitmapCacheOption.None, which never decodes
    // pixel data, so a hostile large image cannot cost memory here.
    private static bool TryReadDimensions(byte[] data, out int width, out int height)
    {
        if (data.AsSpan().StartsWith(s_pngMagic))
        {
            return TryReadPngHeader(data, out width, out height);
        }

        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = decoder.Frames[0].PixelWidth;
        height = decoder.Frames[0].PixelHeight;
        return width > 0 && height > 0;
    }

    private static bool HasExactlyOneFrame(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        return decoder.Frames.Count == 1;
    }

    private static bool TryReadPngHeader(byte[] data, out int width, out int height)
    {
        // 8 magic bytes, 4 length, "IHDR", then big-endian width and height.
        width = 0;
        height = 0;
        if (data.Length < 24 || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
        width = (int)Math.Min(w, int.MaxValue);
        height = (int)Math.Min(h, int.MaxValue);
        return width > 0 && height > 0;
    }

    private static byte[] DecodeAndEncode(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("The image does not hold exactly one frame.");
        }

        BitmapSource source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

        if (source.PixelWidth > MaxOutputDimension || source.PixelHeight > MaxOutputDimension)
        {
            var scale = Math.Min((double)MaxOutputDimension / source.PixelWidth, (double)MaxOutputDimension / source.PixelHeight);
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        source.Freeze();

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var bgra = new byte[stride * height];
        source.CopyPixels(bgra, stride, 0);
        return MinimalPng.Encode(width, height, bgra);
    }

    // Fit mode (FR-PACK-12): never enlarges, resamples with exact area averaging, and writes the smallest PNG it can.
    private static byte[] DecodeAndFit(byte[] data, int maxEdge)
    {
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("The image does not hold exactly one frame.");
        }

        BitmapSource source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var bgra = new byte[checked(width * 4 * height)];
        source.CopyPixels(bgra, width * 4, 0);

        var (outWidth, outHeight) = ImageResampler.FitSize(width, height, maxEdge);
        if (outWidth != width || outHeight != height)
        {
            bgra = ImageResampler.Resample(bgra, width, height, outWidth, outHeight);
        }

        return MinimalPng.EncodeSmallest(outWidth, outHeight, bgra);
    }
}
