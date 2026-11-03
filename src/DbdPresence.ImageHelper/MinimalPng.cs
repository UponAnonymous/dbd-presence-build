using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace DbdPresence.ImageHelper;

/// <summary>
/// Writes an 8-bit RGBA PNG holding only IHDR, one IDAT and IEND, so the output always passes the app's strict PNG
/// walker. WPF's own encoder adds ancillary chunks (colour and physical-size information) that the walker rejects.
/// </summary>
internal static class MinimalPng
{
    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <summary>Encodes top-down BGRA32 pixels (<paramref name="width"/> × 4 bytes per row) as a PNG.</summary>
    public static byte[] Encode(int width, int height, byte[] bgra)
    {
        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1); // first byte of each row: filter type 0 (none)
            for (var x = 0; x < width; x++)
            {
                var source = (y * stride) + (x * 4);
                var target = row + 1 + (x * 4);
                raw[target] = bgra[source + 2];
                raw[target + 1] = bgra[source + 1];
                raw[target + 2] = bgra[source];
                raw[target + 3] = bgra[source + 3];
            }
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = buffer.ToArray();
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: RGBA; compression, filter and interlace stay 0

        using var output = new MemoryStream();
        output.Write(s_signature);
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>
    /// Encodes top-down BGRA32 pixels as the smallest PNG this encoder can make: RGB when every pixel is opaque, a per-row adaptive filter
    /// (the one with the smallest sum of absolute values) and the strongest deflate level. The chunks are the same three as <see cref="Encode"/>.
    /// </summary>
    public static byte[] EncodeSmallest(int width, int height, byte[] bgra)
    {
        var opaque = true;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 255)
            {
                opaque = false;
                break;
            }
        }

        var bpp = opaque ? 3 : 4;
        var stride = width * bpp;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = ((y * width) + x) * 4;
                var target = (y * stride) + (x * bpp);
                pixels[target] = bgra[source + 2];
                pixels[target + 1] = bgra[source + 1];
                pixels[target + 2] = bgra[source];
                if (!opaque)
                {
                    pixels[target + 3] = bgra[source + 3];
                }
            }
        }

        var raw = new byte[(stride + 1) * height];
        var candidate = new byte[stride];
        var best = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var bestType = 0;
            var bestScore = long.MaxValue;
            for (var type = 0; type <= 4; type++)
            {
                FilterRow(type, pixels, y, stride, bpp, candidate);
                long score = 0;
                foreach (var b in candidate)
                {
                    score += b < 128 ? b : 256 - b;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestType = type;
                    (best, candidate) = (candidate, best);
                }
            }

            raw[y * (stride + 1)] = (byte)bestType;
            best.CopyTo(raw, (y * (stride + 1)) + 1);
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = buffer.ToArray();
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = opaque ? (byte)2 : (byte)6; // colour type: RGB or RGBA; compression, filter and interlace stay 0

        using var output = new MemoryStream();
        output.Write(s_signature);
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    // PNG filters: 0 none, 1 sub, 2 up, 3 average, 4 Paeth (per byte, using the byte one pixel to the left and the row above).
    private static void FilterRow(int type, byte[] pixels, int y, int stride, int bpp, byte[] target)
    {
        var row = y * stride;
        for (var i = 0; i < stride; i++)
        {
            int a = i >= bpp ? pixels[row + i - bpp] : 0;
            int b = y > 0 ? pixels[row - stride + i] : 0;
            int c = y > 0 && i >= bpp ? pixels[row - stride + i - bpp] : 0;
            int predictor = type switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            target[i] = (byte)(pixels[row + i] - predictor);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(MemoryStream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
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
