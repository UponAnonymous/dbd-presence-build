using System.Buffers.Binary;
using System.IO.Compression;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Evaluation;

/// <summary>
/// Decodes the PNG files the study capture tool and the fixture recorder write (8-bit grey, RGB or RGBA, not interlaced) so the evaluator can read
/// owner recordings on any platform. It uses only the framework's zlib stream: no package and no operating-system imaging API. It is a decoder for
/// the evaluator, which runs on the owner's own files; it is deliberately strict because a recording folder is still data from disk:
/// <list type="bullet">
/// <item>every chunk checksum is verified, the signature, IHDR first, contiguous IDAT chunks, IEND last and nothing after it are required;</item>
/// <item>size, pixel count, file size and the decompressed length are bounded, and the compressed stream must inflate to exactly the size the header
/// promises (a longer stream, a shorter one or a corrupt one is rejected, so a decompression bomb cannot grow memory);</item>
/// <item>palette, interlaced, 16-bit and unknown critical chunks are refused;</item>
/// <item>every failure is a <see cref="FormatException"/> with a short reason that contains no file content.</item>
/// </list>
/// The shipped app never uses this class on live game frames (it never saves frames). Its only use in the app is the owner-only Study page, which reads small copies
/// the owner's own study recordings made (<c>DbdPresence.Study</c>). The separate image helper process keeps handling untrusted images from the network (ADR-0013),
/// which is a different job with a different decoder.
/// </summary>
public sealed class PngDecoder : IFrameDecoder
{
    /// <summary>Largest accepted width or height.</summary>
    public const int MaxDimension = 8192;

    /// <summary>Largest accepted pixel count (a 5120 x 2880 frame is 14.7 million).</summary>
    public const long MaxPixels = 36L * 1024 * 1024;

    /// <summary>Largest accepted file in bytes.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    private const int SignatureLength = 8;
    private const int ChunkOverhead = 12;
    private const int IhdrLength = 13;

    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <inheritdoc />
    public ImageBuffer Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
        {
            throw new FormatException("The PNG file is larger than the accepted maximum.");
        }

        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Decodes an in-memory PNG.</summary>
    /// <exception cref="FormatException">The data is not a PNG this decoder accepts.</exception>
    public static ImageBuffer Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length > MaxFileBytes)
        {
            throw new FormatException("The PNG file is larger than the accepted maximum.");
        }

        if (png.Length < SignatureLength + (3 * ChunkOverhead) + IhdrLength || !png.StartsWith(s_signature))
        {
            throw new FormatException("Not a PNG file.");
        }

        Header? header = null;
        using var compressed = new MemoryStream();
        var offset = SignatureLength;
        var index = 0;
        var sawIdat = false;
        var idatEnded = false;
        var sawIend = false;
        while (offset < png.Length)
        {
            if (sawIend)
            {
                throw new FormatException("Data after IEND.");
            }

            if (png.Length - offset < ChunkOverhead)
            {
                throw new FormatException("Truncated chunk header.");
            }

            var declared = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (declared > png.Length - offset - ChunkOverhead)
            {
                throw new FormatException("A chunk is longer than the file.");
            }

            var length = (int)declared;
            var type = png.Slice(offset + 4, 4);
            var body = png.Slice(offset + 8, length);
            if (Crc(png.Slice(offset + 4, 4 + length)) != BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + length, 4)))
            {
                throw new FormatException("A chunk checksum is wrong.");
            }

            if (index == 0)
            {
                header = type.SequenceEqual("IHDR"u8) ? ReadHeader(body) : throw new FormatException("IHDR is not the first chunk.");
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                throw new FormatException("A second IHDR chunk.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (idatEnded)
                {
                    throw new FormatException("IDAT chunks are not contiguous.");
                }

                sawIdat = true;
                compressed.Write(body);
            }
            else
            {
                idatEnded = sawIdat;
                if (type.SequenceEqual("IEND"u8))
                {
                    sawIend = length == 0 ? true : throw new FormatException("IEND carries data.");
                }
                else if (IsCritical(type[0]) && !type.SequenceEqual("PLTE"u8))
                {
                    throw new FormatException("An unknown critical chunk.");
                }
            }

            offset += ChunkOverhead + length;
            index++;
        }

        if (!sawIend || !sawIdat || header is null)
        {
            throw new FormatException(sawIend ? "No image data." : "IEND is missing.");
        }

        var raw = Inflate(compressed, header.Value);
        return Unfilter(raw, header.Value);
    }

    private static Header ReadHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length != IhdrLength)
        {
            throw new FormatException("IHDR has the wrong length.");
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
        if (w is 0 or > MaxDimension || h is 0 or > MaxDimension || (long)w * h > MaxPixels)
        {
            throw new FormatException("The image size is out of range.");
        }

        var channels = body[9] switch
        {
            0 => 1,
            2 => 3,
            6 => 4,
            _ => throw new FormatException("Only grey, RGB and RGBA PNG files are supported."),
        };
        if (body[8] != 8 || body[10] != 0 || body[11] != 0)
        {
            throw new FormatException("Only 8-bit PNG files with the standard compression and filter methods are supported.");
        }

        return body[12] == 0 ? new Header((int)w, (int)h, channels) : throw new FormatException("Interlaced PNG files are not supported.");
    }

    private static byte[] Inflate(MemoryStream compressed, Header header)
    {
        var expected = checked(((header.Width * header.Channels) + 1) * (long)header.Height);
        if (expected > (compressed.Length * 1032L) + 1024)
        {
            // zlib cannot compress better than about 1032:1, so a stream this small cannot hold the promised image; refuse before allocating.
            throw new FormatException("The image data is too small for the size in the header.");
        }

        var raw = new byte[expected];
        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            var filled = 0;
            while (filled < raw.Length)
            {
                var read = zlib.Read(raw, filled, raw.Length - filled);
                if (read == 0)
                {
                    throw new FormatException("The image data is shorter than the header says.");
                }

                filled += read;
            }

            if (zlib.ReadByte() >= 0)
            {
                throw new FormatException("The image data is longer than the header says.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // The framework reports a corrupt zlib stream as InvalidDataException or, from the native layer, as ZLibException (an IOException).
            throw new FormatException("The image data is corrupt.");
        }

        return raw;
    }

    private static ImageBuffer Unfilter(byte[] raw, Header header)
    {
        var channels = header.Channels;
        var stride = header.Width * channels;
        var rows = new byte[stride * header.Height];
        for (var y = 0; y < header.Height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var source = raw.AsSpan((y * (stride + 1)) + 1, stride);
            var row = rows.AsSpan(y * stride, stride);
            var above = y == 0 ? default : rows.AsSpan((y - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                var left = x >= channels ? row[x - channels] : (byte)0;
                var up = y == 0 ? (byte)0 : above[x];
                var upLeft = y == 0 || x < channels ? (byte)0 : above[x - channels];
                row[x] = (byte)(source[x] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new FormatException("Unknown scanline filter."),
                });
            }
        }

        if (channels == 1)
        {
            return new ImageBuffer(header.Width, header.Height, PixelFormat.Gray8, rows);
        }

        var rgba = new byte[header.Width * header.Height * 4];
        for (var i = 0; i < header.Width * header.Height; i++)
        {
            rgba[i * 4] = rows[i * channels];
            rgba[(i * 4) + 1] = rows[(i * channels) + 1];
            rgba[(i * 4) + 2] = rows[(i * channels) + 2];
            rgba[(i * 4) + 3] = channels == 4 ? rows[(i * channels) + 3] : (byte)255;
        }

        return new ImageBuffer(header.Width, header.Height, PixelFormat.Rgba32, rgba);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static bool IsCritical(byte firstLetter) => (firstLetter & 0x20) == 0;

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private readonly record struct Header(int Width, int Height, int Channels);
}

/// <summary>
/// Picks the decoder by file extension: <c>.png</c> through <see cref="PngDecoder"/>, <c>.pgm</c> and <c>.ppm</c> through <see cref="PnmDecoder"/>.
/// JPEG (the fixture recorder's downscaled frames) has no platform-neutral decoder yet and is refused with an explicit message rather than
/// guessed (PENDING-ENV).
/// </summary>
public sealed class ExtensionFrameDecoder : IFrameDecoder
{
    private readonly PngDecoder _png = new();
    private readonly PnmDecoder _pnm = new();

    /// <inheritdoc />
    public ImageBuffer Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => _png.Decode(path),
            ".pgm" or ".ppm" => _pnm.Decode(path),
            ".jpg" or ".jpeg" => throw new NotSupportedException("JPEG frames cannot be evaluated yet: no platform-neutral JPEG decoder exists (PENDING-ENV). Use the study tool's PNG frames."),
            _ => throw new NotSupportedException("Unsupported frame file type; expected .png, .pgm or .ppm."),
        };
    }
}
