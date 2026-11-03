// Covers: FR-DET-24 (the evaluator reads the PNG frames the study tool writes), DETECTION_VALIDATION §12.5. MECHANICS ONLY: the pixels are random
// bytes and invented shapes; the first group decodes the exact bytes the study tool's own writer produces.
using System.Buffers.Binary;
using System.IO.Compression;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class PngDecoderTests : IDisposable
{
    private static readonly int[] s_colourTypes = [0, 2, 6, 3, 4];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbd-presence-png-tests-" + Guid.NewGuid().ToString("N"));

    public PngDecoderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- The study tool's own writer ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(64, 48)]
    [InlineData(257, 31)]
    public void TheStudyToolsPng_DecodesToTheSamePixels(int width, int height)
    {
        var random = new Random(width * 1000 + height);
        var bgra = new byte[width * height * 4];
        random.NextBytes(bgra);
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255; // window captures are opaque
        }

        var image = PngDecoder.Decode(PngEncoder.Encode(width, height, bgra));

        Assert.Equal((width, height, PixelFormat.Rgba32), (image.Width, image.Height, image.Format));
        for (var i = 0; i < width * height; i++)
        {
            Assert.Equal(bgra[(i * 4) + 2], image.Pixels[i * 4]);
            Assert.Equal(bgra[(i * 4) + 1], image.Pixels[(i * 4) + 1]);
            Assert.Equal(bgra[i * 4], image.Pixels[(i * 4) + 2]);
            Assert.Equal(255, image.Pixels[(i * 4) + 3]);
        }
    }

    [Fact]
    public void AFull1080pStudyFrame_Decodes()
    {
        var bgra = new byte[1920 * 1080 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(i / 4 % 251);
            bgra[i + 3] = 255;
        }

        var image = PngDecoder.Decode(PngEncoder.Encode(1920, 1080, bgra));

        Assert.Equal((1920, 1080), (image.Width, image.Height));
        Assert.Equal(bgra[0], image.Pixels[2]);
    }

    [Fact]
    public void ExtensionDecoder_ReadsPngAndPnm_AndRefusesJpegAndUnknownTypes()
    {
        var bgra = new byte[4 * 4 * 4];
        Array.Fill(bgra, (byte)200);
        var png = Path.Combine(_root, "a.PNG");
        File.WriteAllBytes(png, PngEncoder.Encode(4, 4, bgra));
        var pgm = Path.Combine(_root, "a.pgm");
        File.WriteAllBytes(pgm, PnmDecoder.Encode(ImageBuffer.CreateGray(4, 4, 90)));
        var decoder = new ExtensionFrameDecoder();

        Assert.Equal(PixelFormat.Rgba32, decoder.Decode(png).Format);
        Assert.Equal(PixelFormat.Gray8, decoder.Decode(pgm).Format);
        Assert.Contains("JPEG", Assert.Throws<NotSupportedException>(() => decoder.Decode(Path.Combine(_root, "a.jpg"))).Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => decoder.Decode(Path.Combine(_root, "a.bmp")));
    }

    // ---- All five scanline filters and the three colour types -----------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(4, 4)]
    [InlineData(3, 1)]
    [InlineData(2, 4)]
    public void EveryFilterType_RoundTrips(int filter, int channels)
    {
        const int Width = 9;
        const int Height = 7;
        var random = new Random(filter * 10 + channels);
        var pixels = new byte[Width * Height * channels];
        random.NextBytes(pixels);

        var png = PngBuilder.Build(Width, Height, channels, pixels, filter);
        var image = PngDecoder.Decode(png);

        Assert.Equal(channels == 1 ? PixelFormat.Gray8 : PixelFormat.Rgba32, image.Format);
        for (var i = 0; i < Width * Height; i++)
        {
            if (channels == 1)
            {
                Assert.Equal(pixels[i], image.Pixels[i]);
            }
            else
            {
                Assert.Equal(pixels[i * channels], image.Pixels[i * 4]);
                Assert.Equal(pixels[(i * channels) + 1], image.Pixels[(i * 4) + 1]);
                Assert.Equal(pixels[(i * channels) + 2], image.Pixels[(i * 4) + 2]);
                Assert.Equal(channels == 4 ? pixels[(i * channels) + 3] : 255, image.Pixels[(i * 4) + 3]);
            }
        }
    }

    [Fact]
    public void AncillaryChunks_AndAPaletteSuggestion_AreSkipped_AndIdatMayBeSplit()
    {
        var pixels = new byte[4 * 4 * 3];
        new Random(5).NextBytes(pixels);
        var png = PngBuilder.Build(4, 4, 3, pixels, 0, extraBeforeIdat: [PngBuilder.Chunk("tEXt", "Comment\0hello"u8), PngBuilder.Chunk("PLTE", new byte[3])], splitIdat: true);

        var image = PngDecoder.Decode(png);

        Assert.Equal(pixels[0], image.Pixels[0]);
    }

    // ---- Rejections ----------------------------------------------------------------------------------------------

    [Fact]
    public void Rejects_ABadSignature_AnEmptyFile_AndATinyFile()
    {
        var good = PngBuilder.Build(2, 2, 3, new byte[12], 0);
        var bad = (byte[])good.Clone();
        bad[1] = (byte)'X';

        Assert.Throws<FormatException>(() => PngDecoder.Decode(bad));
        Assert.Throws<FormatException>(() => PngDecoder.Decode([]));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(good.AsSpan(0, 20)));
    }

    [Fact]
    public void Rejects_AWrongChecksum_ChunkStructureErrors_AndDataAfterIend()
    {
        var pixels = new byte[2 * 2 * 3];
        var good = PngBuilder.Build(2, 2, 3, pixels, 0);

        var badCrc = (byte[])good.Clone();
        badCrc[30] ^= 0xFF; // inside IHDR's checksum
        Assert.Contains("checksum", Assert.Throws<FormatException>(() => PngDecoder.Decode(badCrc)).Message, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => PngDecoder.Decode([.. good, 0]));
        Assert.Throws<FormatException>(() => PngDecoder.Decode([.. good, .. PngBuilder.Chunk("tEXt", "x"u8)]));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(PngBuilder.Build(2, 2, 3, pixels, 0, extraBeforeIdat: [PngBuilder.Header(2, 2, 3)])));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(PngBuilder.Build(2, 2, 3, pixels, 0, omitIend: true)));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(PngBuilder.Build(2, 2, 3, pixels, 0, omitIdat: true)));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(PngBuilder.Build(2, 2, 3, pixels, 0, extraBeforeIdat: [PngBuilder.Chunk("ZZZZ", [])])));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(PngBuilder.Build(2, 2, 3, pixels, 0, interleaveIdat: true)));
    }

    [Theory]
    [InlineData(8, 3, 0, 0, 0)] // palette
    [InlineData(16, 2, 0, 0, 0)] // 16-bit
    [InlineData(8, 4, 0, 0, 0)] // grey + alpha
    [InlineData(8, 2, 1, 0, 0)] // unknown compression
    [InlineData(8, 2, 0, 1, 0)] // unknown filter method
    [InlineData(8, 2, 0, 0, 1)] // Adam7 interlace
    [InlineData(1, 0, 0, 0, 0)] // 1-bit
    public void Rejects_FormatsItDoesNotSupport(int depth, int colourType, int compression, int filterMethod, int interlace)
    {
        var png = PngBuilder.BuildRaw(PngBuilder.Header(2, 2, colourType, depth, compression, filterMethod, interlace), PngBuilder.Zlib(new byte[(2 * 3) + 2]));

        Assert.Throws<FormatException>(() => PngDecoder.Decode(png));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(PngDecoder.MaxDimension + 1, 1)]
    [InlineData(1, PngDecoder.MaxDimension + 1)]
    [InlineData(PngDecoder.MaxDimension, PngDecoder.MaxDimension)] // 67 Mpixel: over the pixel budget
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Rejects_SizesOutsideTheLimits_BeforeAllocatingAnything(int width, int height)
    {
        var png = PngBuilder.BuildRaw(PngBuilder.Header(width, height, 2), PngBuilder.Zlib(new byte[16]));

        var error = Assert.Throws<FormatException>(() => PngDecoder.Decode(png));
        Assert.Contains("size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_TheLargestAllowedFrameSize_ButRefusesIt_WhenTheDataCannotHoldIt()
    {
        // 8192 x 4096 = 33.5 Mpixel is inside the budget, but a 16-byte stream cannot inflate to 134 MB (zlib's best ratio is about 1032:1).
        var png = PngBuilder.BuildRaw(PngBuilder.Header(8192, 4096, 6), PngBuilder.Zlib(new byte[16]));

        Assert.Contains("too small", Assert.Throws<FormatException>(() => PngDecoder.Decode(png)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADecompressionBomb_IsStoppedAtTheSizeTheHeaderPromises()
    {
        // The header promises 8 x 8 RGB (8 * (24 + 1) = 200 bytes); the stream inflates to 4 MB of zeros.
        var bomb = PngBuilder.BuildRaw(PngBuilder.Header(8, 8, 2), PngBuilder.Zlib(new byte[4 * 1024 * 1024]));

        Assert.Contains("longer", Assert.Throws<FormatException>(() => PngDecoder.Decode(bomb)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamThatEndsEarly_OrIsNotZlib_IsRejected()
    {
        var shortStream = PngBuilder.BuildRaw(PngBuilder.Header(8, 8, 2), PngBuilder.Zlib(new byte[100]));
        var notZlib = PngBuilder.BuildRaw(PngBuilder.Header(8, 8, 2), new byte[300]);

        Assert.Contains("shorter", Assert.Throws<FormatException>(() => PngDecoder.Decode(shortStream)).Message, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => PngDecoder.Decode(notZlib));
    }

    [Fact]
    public void AnUnknownScanlineFilter_IsRejected()
    {
        var raw = new byte[(2 * 3 + 1) * 2];
        raw[0] = 5;

        Assert.Throws<FormatException>(() => PngDecoder.Decode(PngBuilder.BuildRaw(PngBuilder.Header(2, 2, 2), PngBuilder.Zlib(raw))));
    }

    [Fact]
    public void ATooLargeFileOnDisk_IsRefusedWithoutReadingIt()
    {
        var path = Path.Combine(_root, "huge.png");
        using (var stream = File.Create(path))
        {
            stream.SetLength(PngDecoder.MaxFileBytes + 1);
        }

        Assert.Throws<FormatException>(() => new PngDecoder().Decode(path));
    }

    // ---- Fuzz ----------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryTruncation_OfAValidFile_FailsWithFormatException()
    {
        var png = PngBuilder.Build(8, 6, 3, new byte[8 * 6 * 3], 1);

        for (var length = 0; length < png.Length; length++)
        {
            Assert.Throws<FormatException>(() => PngDecoder.Decode(png.AsSpan(0, length)));
        }
    }

    [Fact]
    public void EverySingleBitFlip_EndsInAFormatException_OrAnImage_NeverAnythingElse()
    {
        var pixels = new byte[8 * 6 * 3];
        new Random(11).NextBytes(pixels);
        var png = PngBuilder.Build(8, 6, 3, pixels, 4);

        var rejected = 0;
        for (var bit = 0; bit < png.Length * 8; bit++)
        {
            var mutated = (byte[])png.Clone();
            mutated[bit / 8] ^= (byte)(1 << (bit % 8));
            try
            {
                _ = PngDecoder.Decode(mutated);
            }
            catch (FormatException)
            {
                rejected++;
            }
        }

        // Every chunk is protected by a checksum, so a flipped bit is caught unless it hits a checksum-neutral place (it never does for CRC-32).
        Assert.Equal(png.Length * 8, rejected);
    }

    [Fact]
    public void RandomMultiByteDamage_AndRandomBytes_NeverEscapeAsAnotherExceptionOrHang()
    {
        var random = new Random(2026);
        var pixels = new byte[16 * 16 * 4];
        random.NextBytes(pixels);
        var png = PngBuilder.Build(16, 16, 4, pixels, 4);

        for (var round = 0; round < 3000; round++)
        {
            var mutated = (byte[])png.Clone();
            for (var n = random.Next(1, 8); n > 0; n--)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            AssertOnlyFormatErrors(mutated);
        }

        for (var round = 0; round < 500; round++)
        {
            var junk = new byte[random.Next(0, 400)];
            random.NextBytes(junk);
            if (junk.Length > 8 && round % 2 == 0)
            {
                png.AsSpan(0, 8).CopyTo(junk); // valid signature, random body
            }

            AssertOnlyFormatErrors(junk);
        }
    }

    [Fact]
    public void RandomDamageThatKeepsEveryChecksumValid_IsStillBounded()
    {
        // The attacker recomputes the checksums: damage the compressed bytes and the header values, then rebuild the chunks.
        var random = new Random(77);
        for (var round = 0; round < 400; round++)
        {
            var compressed = PngBuilder.Zlib(new byte[(10 * 3 + 1) * 10]);
            for (var n = random.Next(0, 4); n > 0; n--)
            {
                compressed[random.Next(compressed.Length)] = (byte)random.Next(256);
            }

            var header = PngBuilder.Header(random.Next(0, 40), random.Next(0, 40), s_colourTypes[random.Next(s_colourTypes.Length)]);
            AssertOnlyFormatErrors(PngBuilder.BuildRaw(header, compressed));
        }
    }

    private static void AssertOnlyFormatErrors(byte[] data)
    {
        try
        {
            _ = PngDecoder.Decode(data);
        }
        catch (FormatException)
        {
            // expected for damaged input
        }
    }

    /// <summary>Builds PNG files for the tests, including deliberately broken ones.</summary>
    private static class PngBuilder
    {
        private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        public static byte[] Chunk(string type, ReadOnlySpan<byte> data)
        {
            var bytes = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length);
            System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
            data.CopyTo(bytes.AsSpan(8));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + data.Length), Crc(bytes.AsSpan(4, 4 + data.Length)));
            return bytes;
        }

        public static byte[] Header(int width, int height, int colourType, int depth = 8, int compression = 0, int filterMethod = 0, int interlace = 0)
        {
            var body = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(body, (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)height);
            body[8] = (byte)depth;
            body[9] = (byte)colourType;
            body[10] = (byte)compression;
            body[11] = (byte)filterMethod;
            body[12] = (byte)interlace;
            return Chunk("IHDR", body);
        }

        public static byte[] Zlib(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            return output.ToArray();
        }

        public static byte[] BuildRaw(byte[] header, byte[] compressed) => [.. s_signature, .. header, .. Chunk("IDAT", compressed), .. Chunk("IEND", [])];

        public static byte[] Build(
            int width,
            int height,
            int channels,
            byte[] pixels,
            int filter,
            byte[][]? extraBeforeIdat = null,
            bool splitIdat = false,
            bool omitIend = false,
            bool omitIdat = false,
            bool interleaveIdat = false)
        {
            var stride = width * channels;
            var raw = new byte[(stride + 1) * height];
            for (var y = 0; y < height; y++)
            {
                raw[y * (stride + 1)] = (byte)filter;
                for (var x = 0; x < stride; x++)
                {
                    var current = pixels[(y * stride) + x];
                    var left = x >= channels ? pixels[(y * stride) + x - channels] : 0;
                    var up = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
                    var upLeft = y > 0 && x >= channels ? pixels[((y - 1) * stride) + x - channels] : 0;
                    var predictor = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        _ => Paeth(left, up, upLeft),
                    };
                    raw[(y * (stride + 1)) + 1 + x] = (byte)(current - predictor);
                }
            }

            var colourType = channels switch { 1 => 0, 3 => 2, _ => 6 };
            var compressed = Zlib(raw);
            var file = new List<byte>(s_signature);
            file.AddRange(Header(width, height, colourType));
            foreach (var extra in extraBeforeIdat ?? [])
            {
                file.AddRange(extra);
            }

            if (!omitIdat)
            {
                if (splitIdat || interleaveIdat)
                {
                    var half = compressed.Length / 2;
                    file.AddRange(Chunk("IDAT", compressed.AsSpan(0, half)));
                    if (interleaveIdat)
                    {
                        file.AddRange(Chunk("tEXt", "a"u8));
                    }

                    file.AddRange(Chunk("IDAT", compressed.AsSpan(half)));
                }
                else
                {
                    file.AddRange(Chunk("IDAT", compressed));
                }
            }

            if (!omitIend)
            {
                file.AddRange(Chunk("IEND", []));
            }

            return [.. file];
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static uint Crc(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
