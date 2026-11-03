using System.Buffers.Binary;

namespace DbdPresence.Core.Images;

/// <summary>
/// Strict chunk-level check of the PNG the image helper returns (ADR-0013). The helper's output is expected to be plain:
/// signature, IHDR (8-bit, colour type 6 or 2, size at most <see cref="MaxDimension"/>), IDAT chunks, one IEND, nothing after it,
/// and every chunk CRC valid. Anything else is rejected before the app decodes the file in-process. This narrows what the in-process
/// decoder ever sees; it does not prove the compressed pixel data decodes.
/// </summary>
public static class StrictPng
{
    /// <summary>Largest accepted width or height.</summary>
    public const int MaxDimension = 1024;

    private const int SignatureLength = 8;
    private const int ChunkOverhead = 12;
    private const int IhdrLength = 13;

    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <summary>Checks <paramref name="png"/>; on success returns the pixel size.</summary>
    /// <param name="png">The complete file.</param>
    /// <param name="width">Set on success.</param>
    /// <param name="height">Set on success.</param>
    /// <param name="failure">A short reason (no file content) when the result is false; otherwise null.</param>
    public static bool TryValidate(ReadOnlySpan<byte> png, out int width, out int height, out string? failure)
    {
        width = 0;
        height = 0;
        if (png.Length < SignatureLength + (2 * ChunkOverhead) + IhdrLength || !png.StartsWith(s_signature))
        {
            failure = "not a PNG";
            return false;
        }

        var offset = SignatureLength;
        var index = 0;
        var idatCount = 0;
        var sawIend = false;
        while (offset < png.Length)
        {
            if (sawIend)
            {
                failure = "data after IEND";
                return false;
            }

            if (png.Length - offset < ChunkOverhead)
            {
                failure = "truncated chunk header";
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (length > png.Length - offset - ChunkOverhead)
            {
                failure = "chunk longer than the file";
                return false;
            }

            var type = png.Slice(offset + 4, 4);
            var body = png.Slice(offset + 8, (int)length);
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + (int)length, 4));
            if (Crc(png.Slice(offset + 4, 4 + (int)length)) != stored)
            {
                failure = "bad chunk checksum";
                return false;
            }

            if (index == 0)
            {
                if (!type.SequenceEqual("IHDR"u8))
                {
                    failure = "IHDR is not first";
                    return false;
                }

                if (!TryReadHeader(body, out width, out height, out failure))
                {
                    return false;
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idatCount++;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0)
                {
                    failure = "IEND has data";
                    return false;
                }

                sawIend = true;
            }
            else
            {
                failure = "unexpected chunk";
                return false;
            }

            offset += ChunkOverhead + (int)length;
            index++;
        }

        if (!sawIend || idatCount == 0)
        {
            failure = sawIend ? "no image data" : "missing IEND";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> body, out int width, out int height, out string? failure)
    {
        width = 0;
        height = 0;
        if (body.Length != IhdrLength)
        {
            failure = "bad IHDR length";
            return false;
        }

        var w = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
        if (w is 0 or > MaxDimension || h is 0 or > MaxDimension)
        {
            failure = "dimensions out of range";
            return false;
        }

        // bit depth 8; colour type 6 (RGBA) or 2 (RGB); compression 0; filter 0; interlace 0 or 1.
        if (body[8] != 8 || body[9] is not (6 or 2) || body[10] != 0 || body[11] != 0 || body[12] > 1)
        {
            failure = "unsupported PNG format";
            return false;
        }

        width = (int)w;
        height = (int)h;
        failure = null;
        return true;
    }

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
}
