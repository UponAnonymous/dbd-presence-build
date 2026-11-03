using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DbdPresence.Platform.Windows.Tests;

/// <summary>Builds small PNG files by hand so the tests need no image library and no committed binaries.</summary>
internal static class TestPng
{
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <summary>A valid 8-bit grayscale PNG whose pixels are a horizontal gradient, with optional extra chunks before the data.</summary>
    public static byte[] Gray(int width, int height, params (string Type, byte[] Data)[] extraChunks)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 0; // grayscale
        WriteChunk(output, "IHDR", header);
        foreach (var (type, data) in extraChunks)
        {
            WriteChunk(output, type, data);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var x = 0; x < width; x++)
            {
                row[x + 1] = (byte)(x * 255 / Math.Max(1, width - 1));
            }

            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>A valid 8-bit grayscale PNG of seeded random noise (barely compressible), for size-limit tests.</summary>
    public static byte[] Noise(int width, int height, int seed)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var random = new Random(seed);
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++)
            {
                random.NextBytes(row.AsSpan(1));
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static byte[] Text(string keyword, string value) => Encoding.Latin1.GetBytes(keyword + "\0" + value);

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(typeAndData, 0);
        data.CopyTo(typeAndData, 4);
        output.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
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
}
