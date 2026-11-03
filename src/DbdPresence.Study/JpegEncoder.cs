using DbdPresence.Vision.Imaging;

namespace DbdPresence.Study;

/// <summary>
/// A small baseline JPEG writer (JFIF, 4:2:0, the standard Huffman tables of ITU T.81 Annex K, quality scaling as in the IJG library). The review file
/// needs smaller pictures than the lossless PNGs the study tool writes, and the platform-neutral code has no imaging library, so this is the one
/// encoder. It is deterministic: the same picture and quality always give the same bytes.
/// </summary>
internal static class JpegEncoder
{
    private static readonly int[] s_zigzag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly int[] s_baseLuma =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly int[] s_baseChroma =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly byte[] s_dcLumaBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] s_dcChromaBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] s_dcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] s_acLumaBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];

    private static readonly byte[] s_acLumaValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private static readonly byte[] s_acChromaBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] s_acChromaValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private static readonly double[] s_cosine = BuildCosine();

    private static readonly (int Code, int Length)[] s_dcLumaCodes = BuildCodes(s_dcLumaBits, s_dcValues);
    private static readonly (int Code, int Length)[] s_dcChromaCodes = BuildCodes(s_dcChromaBits, s_dcValues);
    private static readonly (int Code, int Length)[] s_acLumaCodes = BuildCodes(s_acLumaBits, s_acLumaValues);
    private static readonly (int Code, int Length)[] s_acChromaCodes = BuildCodes(s_acChromaBits, s_acChromaValues);

    /// <summary>Encodes a picture. Grey pictures are written as colour with no colour in them.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="quality">1 (smallest) to 100 (best). The review file uses about 80.</param>
    public static byte[] Encode(ImageBuffer image, int quality)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        var width = image.Width;
        var height = image.Height;
        var lumaTable = QuantisationTable(s_baseLuma, quality);
        var chromaTable = QuantisationTable(s_baseChroma, quality);

        var mcusX = (width + 15) / 16;
        var mcusY = (height + 15) / 16;
        var paddedWidth = mcusX * 16;
        var paddedHeight = mcusY * 16;
        var (y, cb, cr) = Planes(image, paddedWidth, paddedHeight);

        using var output = new MemoryStream();
        WriteHeaders(output, width, height, lumaTable, chromaTable);
        var bits = new BitWriter(output);
        int predictorY = 0, predictorCb = 0, predictorCr = 0;
        var block = new double[64];
        var quantised = new int[64];
        var chromaWidth = paddedWidth / 2;
        for (var my = 0; my < mcusY; my++)
        {
            for (var mx = 0; mx < mcusX; mx++)
            {
                for (var by = 0; by < 2; by++)
                {
                    for (var bx = 0; bx < 2; bx++)
                    {
                        Load(y, paddedWidth, (mx * 16) + (bx * 8), (my * 16) + (by * 8), block);
                        predictorY = EncodeBlock(bits, block, quantised, lumaTable, predictorY, s_dcLumaCodes, s_acLumaCodes);
                    }
                }

                Load(cb, chromaWidth, mx * 8, my * 8, block);
                predictorCb = EncodeBlock(bits, block, quantised, chromaTable, predictorCb, s_dcChromaCodes, s_acChromaCodes);
                Load(cr, chromaWidth, mx * 8, my * 8, block);
                predictorCr = EncodeBlock(bits, block, quantised, chromaTable, predictorCr, s_dcChromaCodes, s_acChromaCodes);
            }
        }

        bits.Flush();
        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    private static int[] QuantisationTable(int[] baseTable, int quality)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var table = new int[64];
        for (var i = 0; i < 64; i++)
        {
            table[i] = Math.Clamp(((baseTable[i] * scale) + 50) / 100, 1, 255);
        }

        return table;
    }

    /// <summary>Converts to level-shifted Y, Cb and Cr planes; the chroma planes are halved in both directions. Edges are repeated into the padding.</summary>
    private static (double[] Y, double[] Cb, double[] Cr) Planes(ImageBuffer image, int paddedWidth, int paddedHeight)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;
        var gray = image.Format == PixelFormat.Gray8;
        var yPlane = new double[paddedWidth * paddedHeight];
        var cbFull = new double[paddedWidth * paddedHeight];
        var crFull = new double[paddedWidth * paddedHeight];
        for (var py = 0; py < paddedHeight; py++)
        {
            var sy = Math.Min(py, height - 1);
            for (var px = 0; px < paddedWidth; px++)
            {
                var sx = Math.Min(px, width - 1);
                double r, g, b;
                if (gray)
                {
                    r = g = b = pixels[(sy * width) + sx];
                }
                else
                {
                    var i = ((sy * width) + sx) * 4;
                    r = pixels[i];
                    g = pixels[i + 1];
                    b = pixels[i + 2];
                }

                var o = (py * paddedWidth) + px;
                yPlane[o] = (0.299 * r) + (0.587 * g) + (0.114 * b) - 128;
                cbFull[o] = (-0.168736 * r) - (0.331264 * g) + (0.5 * b);
                crFull[o] = (0.5 * r) - (0.418688 * g) - (0.081312 * b);
            }
        }

        var cw = paddedWidth / 2;
        var ch = paddedHeight / 2;
        var cb = new double[cw * ch];
        var cr = new double[cw * ch];
        for (var cy = 0; cy < ch; cy++)
        {
            for (var cx = 0; cx < cw; cx++)
            {
                var o = (cy * 2 * paddedWidth) + (cx * 2);
                cb[(cy * cw) + cx] = (cbFull[o] + cbFull[o + 1] + cbFull[o + paddedWidth] + cbFull[o + paddedWidth + 1]) / 4;
                cr[(cy * cw) + cx] = (crFull[o] + crFull[o + 1] + crFull[o + paddedWidth] + crFull[o + paddedWidth + 1]) / 4;
            }
        }

        return (yPlane, cb, cr);
    }

    private static void Load(double[] plane, int stride, int x0, int y0, double[] block)
    {
        for (var row = 0; row < 8; row++)
        {
            Array.Copy(plane, ((y0 + row) * stride) + x0, block, row * 8, 8);
        }
    }

    private static int EncodeBlock(BitWriter bits, double[] block, int[] quantised, int[] table, int predictor, (int Code, int Length)[] dc, (int Code, int Length)[] ac)
    {
        Dct(block);
        for (var i = 0; i < 64; i++)
        {
            quantised[i] = (int)Math.Round(block[s_zigzag[i]] / table[s_zigzag[i]], MidpointRounding.AwayFromZero);
        }

        var difference = quantised[0] - predictor;
        var size = SizeOf(difference);
        bits.Write(dc[size].Code, dc[size].Length);
        if (size > 0)
        {
            bits.Write(difference < 0 ? difference - 1 : difference, size);
        }

        var run = 0;
        for (var i = 1; i < 64; i++)
        {
            var value = quantised[i];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                bits.Write(ac[0xF0].Code, ac[0xF0].Length);
                run -= 16;
            }

            var category = SizeOf(value);
            var symbol = (run << 4) | category;
            bits.Write(ac[symbol].Code, ac[symbol].Length);
            bits.Write(value < 0 ? value - 1 : value, category);
            run = 0;
        }

        if (run > 0)
        {
            bits.Write(ac[0].Code, ac[0].Length);
        }

        return quantised[0];
    }

    private static int SizeOf(int value)
    {
        var magnitude = Math.Abs(value);
        var size = 0;
        while (magnitude != 0)
        {
            size++;
            magnitude >>= 1;
        }

        return size;
    }

    /// <summary>Forward 8x8 DCT in place (two passes of the separable form).</summary>
    private static void Dct(double[] block)
    {
        Span<double> temp = stackalloc double[64];
        for (var row = 0; row < 8; row++)
        {
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var x = 0; x < 8; x++)
                {
                    sum += block[(row * 8) + x] * s_cosine[(u * 8) + x];
                }

                temp[(row * 8) + u] = sum;
            }
        }

        for (var col = 0; col < 8; col++)
        {
            for (var v = 0; v < 8; v++)
            {
                double sum = 0;
                for (var y = 0; y < 8; y++)
                {
                    sum += temp[(y * 8) + col] * s_cosine[(v * 8) + y];
                }

                block[(v * 8) + col] = sum;
            }
        }
    }

    private static double[] BuildCosine()
    {
        var table = new double[64];
        for (var u = 0; u < 8; u++)
        {
            var scale = u == 0 ? Math.Sqrt(1.0 / 8) : Math.Sqrt(2.0 / 8);
            for (var x = 0; x < 8; x++)
            {
                table[(u * 8) + x] = scale * Math.Cos(((2 * x) + 1) * u * Math.PI / 16);
            }
        }

        return table;
    }

    /// <summary>Builds the code of every symbol from the table's code-length counts (T.81 Annex C).</summary>
    private static (int Code, int Length)[] BuildCodes(byte[] counts, byte[] values)
    {
        var codes = new (int, int)[256];
        var code = 0;
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < counts[length - 1]; i++)
            {
                codes[values[k++]] = (code++, length);
            }

            code <<= 1;
        }

        return codes;
    }

    private static void WriteHeaders(MemoryStream output, int width, int height, int[] lumaTable, int[] chromaTable)
    {
        output.Write([0xFF, 0xD8]); // SOI
        output.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]); // JFIF 1.01, no density
        WriteQuantisation(output, 0, lumaTable);
        WriteQuantisation(output, 1, chromaTable);
        output.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        WriteHuffman(output, 0x00, s_dcLumaBits, s_dcValues);
        WriteHuffman(output, 0x10, s_acLumaBits, s_acLumaValues);
        WriteHuffman(output, 0x01, s_dcChromaBits, s_dcValues);
        WriteHuffman(output, 0x11, s_acChromaBits, s_acChromaValues);
        output.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
    }

    private static void WriteQuantisation(MemoryStream output, int id, int[] table)
    {
        output.Write([0xFF, 0xDB, 0x00, 0x43, (byte)id]);
        for (var i = 0; i < 64; i++)
        {
            output.WriteByte((byte)table[s_zigzag[i]]);
        }
    }

    private static void WriteHuffman(MemoryStream output, int classAndId, byte[] counts, byte[] values)
    {
        var length = 2 + 1 + 16 + values.Length;
        output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)classAndId]);
        output.Write(counts);
        output.Write(values);
    }

    private sealed class BitWriter(Stream output)
    {
        private int _buffer;
        private int _count;

        public void Write(int value, int length)
        {
            if (length == 0)
            {
                return;
            }

            _buffer = (_buffer << length) | (value & ((1 << length) - 1));
            _count += length;
            while (_count >= 8)
            {
                var b = (_buffer >> (_count - 8)) & 0xFF;
                output.WriteByte((byte)b);
                if (b == 0xFF)
                {
                    output.WriteByte(0x00); // byte stuffing
                }

                _count -= 8;
            }

            _buffer &= (1 << _count) - 1;
        }

        public void Flush()
        {
            if (_count > 0)
            {
                Write(0x7F, 8 - _count); // pad the last byte with ones
            }
        }
    }
}
