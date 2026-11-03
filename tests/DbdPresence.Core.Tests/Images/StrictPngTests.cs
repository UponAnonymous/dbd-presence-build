// Covers: FR-PACK-10 (the helper's output is checked before the app decodes it), ADR-0013 (strict PNG chunk walker).
using System.Buffers.Binary;
using System.Text;
using DbdPresence.Core.Images;

namespace DbdPresence.Core.Tests.Images;

public sealed class StrictPngTests
{
    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void ValidPng_IsAcceptedWithItsSize()
    {
        Assert.True(StrictPng.TryValidate(Build(), out var w, out var h, out var failure), failure);
        Assert.Equal((640, 480), (w, h));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(2)]
    public void RgbAndRgba_AreAccepted(byte colourType)
    {
        Assert.True(StrictPng.TryValidate(Build(colourType: colourType), out _, out _, out _));
    }

    [Fact]
    public void SeveralIdatChunks_AreAccepted()
    {
        Assert.True(StrictPng.TryValidate(Build(idatChunks: 3), out _, out _, out _));
    }

    [Fact]
    public void MaximumDimensions_AreAccepted_OneMoreIsRejected()
    {
        Assert.True(StrictPng.TryValidate(Build(width: 1024, height: 1024), out _, out _, out _));
        Assert.False(StrictPng.TryValidate(Build(width: 1025, height: 10), out _, out _, out var failure));
        Assert.Equal("dimensions out of range", failure);
        Assert.False(StrictPng.TryValidate(Build(width: 10, height: 1025), out _, out _, out _));
    }

    [Theory]
    [InlineData(0u, 10u)]
    [InlineData(10u, 0u)]
    [InlineData(uint.MaxValue, 10u)]
    public void ZeroAndHugeDimensions_AreRejected(uint width, uint height)
    {
        Assert.False(StrictPng.TryValidate(Build(width: width, height: height), out _, out _, out _));
    }

    [Fact]
    public void BadCrc_IsRejected_InAnyChunk()
    {
        var png = Build();
        foreach (var chunkStart in ChunkStarts(png))
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(chunkStart, 4));
            var copy = png.ToArray();
            copy[chunkStart + 8 + length + 3] ^= 0x01;

            Assert.False(StrictPng.TryValidate(copy, out _, out _, out var failure));
            Assert.Equal("bad chunk checksum", failure);
        }
    }

    [Fact]
    public void FlippedDataByte_IsRejectedByTheCrc()
    {
        var png = Build();
        png[8 + 8 + 13 + 4 + 8 + 2] ^= 0x10; // inside the first IDAT body

        Assert.False(StrictPng.TryValidate(png, out _, out _, out _));
    }

    [Theory]
    [InlineData("tEXt")]
    [InlineData("iCCP")]
    [InlineData("pHYs")]
    [InlineData("gAMA")]
    [InlineData("eXIf")]
    [InlineData("zzzz")]
    public void AnyOtherChunk_IsRejected_EvenWithAValidCrc(string type)
    {
        var png = Build(extra: (type, [1, 2, 3]));

        Assert.False(StrictPng.TryValidate(png, out _, out _, out var failure));
        Assert.Equal("unexpected chunk", failure);
    }

    [Fact]
    public void TrailingBytes_AfterIend_AreRejected()
    {
        Assert.False(StrictPng.TryValidate([.. Build(), 0], out _, out _, out var failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void AValidChunkAfterIend_IsRejected()
    {
        var withSecondIend = Build(trailingIend: true);

        Assert.False(StrictPng.TryValidate(withSecondIend, out _, out _, out var failure));
        Assert.Equal("data after IEND", failure);
    }

    [Fact]
    public void MissingIend_Truncation_AndBadSignature_AreRejected()
    {
        var png = Build();

        Assert.False(StrictPng.TryValidate(png.AsSpan(0, png.Length - 12), out _, out _, out _));
        Assert.False(StrictPng.TryValidate(png.AsSpan(0, png.Length - 5), out _, out _, out _));
        Assert.False(StrictPng.TryValidate(png.AsSpan(0, 20), out _, out _, out _));
        var wrongSignature = png.ToArray();
        wrongSignature[1] = 0x51;
        Assert.False(StrictPng.TryValidate(wrongSignature, out _, out _, out _));
        Assert.False(StrictPng.TryValidate([], out _, out _, out _));
    }

    [Fact]
    public void NoImageData_IsRejected()
    {
        Assert.False(StrictPng.TryValidate(Build(idatChunks: 0), out _, out _, out var failure));
        Assert.Equal("no image data", failure);
    }

    [Fact]
    public void ChunkLengthBeyondTheFile_IsRejectedWithoutOverreading()
    {
        var png = Build();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8 + 8 + 13 + 4, 4), uint.MaxValue);

        Assert.False(StrictPng.TryValidate(png, out _, out _, out var failure));
        Assert.Equal("chunk longer than the file", failure);
    }

    [Theory]
    [InlineData(1, 6)] // bit depth 1
    [InlineData(16, 6)]
    [InlineData(8, 0)] // greyscale
    [InlineData(8, 3)] // palette
    [InlineData(8, 4)]
    public void OtherDepthsAndColourTypes_AreRejected(byte depth, byte colourType)
    {
        Assert.False(StrictPng.TryValidate(Build(depth: depth, colourType: colourType), out _, out _, out var failure));
        Assert.Equal("unsupported PNG format", failure);
    }

    [Fact]
    public void IhdrMustBeFirst()
    {
        var png = Build(swapFirstTwo: true);

        Assert.False(StrictPng.TryValidate(png, out _, out _, out _));
    }

    [Fact]
    public void IendWithData_IsRejected()
    {
        Assert.False(StrictPng.TryValidate(Build(iendData: [1]), out _, out _, out var failure));
        Assert.Equal("IEND has data", failure);
    }

    private static List<int> ChunkStarts(byte[] png)
    {
        var starts = new List<int>();
        var offset = 8;
        while (offset < png.Length)
        {
            starts.Add(offset);
            offset += 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
        }

        return starts;
    }

    private static byte[] Build(
        uint width = 640,
        uint height = 480,
        byte depth = 8,
        byte colourType = 6,
        int idatChunks = 1,
        (string Type, byte[] Data)? extra = null,
        bool trailingIend = false,
        bool swapFirstTwo = false,
        byte[]? iendData = null)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = depth;
        header[9] = colourType;

        var chunks = new List<byte[]> { Chunk("IHDR", header) };
        if (extra is { } e)
        {
            chunks.Add(Chunk(e.Type, e.Data));
        }

        for (var i = 0; i < idatChunks; i++)
        {
            chunks.Add(Chunk("IDAT", [0x78, 0x01, (byte)i, 4, 5, 6]));
        }

        chunks.Add(Chunk("IEND", iendData ?? []));
        if (trailingIend)
        {
            chunks.Add(Chunk("IEND", []));
        }

        if (swapFirstTwo)
        {
            (chunks[0], chunks[1]) = (chunks[1], chunks[0]);
        }

        return [.. s_signature, .. chunks.SelectMany(c => c)];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        typeAndData.CopyTo(chunk, 4);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), Crc32(typeAndData));
        return chunk;
    }

    // Bitwise reference implementation, independent of the product's table-driven one.
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
