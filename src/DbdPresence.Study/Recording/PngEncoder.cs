using System.Buffers.Binary;
using System.IO.Compression;

namespace DbdPresence.StudyCapture;

/// <summary>
/// Minimal lossless PNG writer for B8G8R8A8 pixels (8-bit RGB, alpha dropped: window captures are opaque). No dependencies and
/// no platform calls, so it is unit tested on Linux through a linked source file in the Core test project.
/// </summary>
internal static class PngEncoder
{
    private static readonly uint[] s_crcTable = BuildCrcTable();
    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes the image and returns the PNG bytes.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (bgra.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The pixel data length must equal width * height * 4.", nameof(bgra));
        }

        using var raw = new MemoryStream(((width * 3) + 1) * height);
        var row = new byte[(width * 3) + 1];
        for (var y = 0; y < height; y++)
        {
            row[0] = 0; // filter type: none
            var source = bgra.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                row[1 + (x * 3)] = source[(x * 4) + 2];
                row[2 + (x * 3)] = source[(x * 4) + 1];
                row[3 + (x * 3)] = source[x * 4];
            }

            raw.Write(row);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }

        using var png = new MemoryStream();
        png.Write(s_signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    /// <summary>
    /// Encodes the image smaller than <see cref="Encode"/>: each row gets the scanline filter (none, sub, up, average or Paeth) with the smallest sum of
    /// absolute differences, and the deflate level is the best one unless the caller names another. Used for the cropped text regions of the review file, which are written once and
    /// sent over the internet; the study tool itself keeps the faster <see cref="Encode"/>. The decoded pixels are identical. The evaluator makes a
    /// recording's own pictures smaller after the fact (<c>FrameCompaction</c>) and names <see cref="CompressionLevel.Optimal"/> for that.
    /// </summary>
    public static byte[] EncodeSmall(int width, int height, ReadOnlySpan<byte> bgra, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (bgra.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The pixel data length must equal width * height * 4.", nameof(bgra));
        }

        var stride = width * 3;
        var rgb = new byte[stride * height];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3] = bgra[(i * 4) + 2];
            rgb[(i * 3) + 1] = bgra[(i * 4) + 1];
            rgb[(i * 3) + 2] = bgra[i * 4];
        }

        var raw = new byte[(stride + 1) * height];
        var candidate = new byte[stride];
        var best = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = rgb.AsSpan(y * stride, stride);
            var previous = y == 0 ? new byte[stride] : rgb.AsSpan((y - 1) * stride, stride).ToArray();
            var bestFilter = 0;
            var bestCost = long.MaxValue;
            for (var filter = 0; filter <= 4; filter++)
            {
                var cost = 0L;
                for (var x = 0; x < stride; x++)
                {
                    var left = x >= 3 ? row[x - 3] : 0;
                    var up = previous[x];
                    var upLeft = x >= 3 ? previous[x - 3] : 0;
                    var predicted = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        _ => Paeth(left, up, upLeft),
                    };
                    var value = (byte)(row[x] - predicted);
                    candidate[x] = value;
                    cost += value < 128 ? value : 256 - value;
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestFilter = filter;
                    (best, candidate) = (candidate, best);
                }
            }

            raw[y * (stride + 1)] = (byte)bestFilter;
            best.CopyTo(raw.AsSpan((y * (stride + 1)) + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write(s_signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        var crc = Crc32Update(0xFFFFFFFFu, type);
        crc = Crc32Update(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        stream.Write(number);
    }

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
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
}
