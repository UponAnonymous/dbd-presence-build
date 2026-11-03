using DbdPresence.Core.Images;
using DbdPresence.Core.Pictures;

namespace DbdPresence.Packs.Tests;

/// <summary>Builds fake PNG bytes that pass <c>StrictPng</c>: signature, IHDR with the given size, one IDAT of filler up to the length (at least 58 bytes), IEND.</summary>
internal static class FakePng
{
    public static byte[] Make(int width, int height, int length, byte filler = 7, ReadOnlySpan<byte> salt = default)
    {
        // A PNG that StrictPng accepts: signature, IHDR (8 bit, RGBA), one IDAT of filler bytes (not real image data), IEND, every CRC valid.
        var idat = new byte[Math.Max(length - 57, 1)];
        Array.Fill(idat, filler);
        salt[..Math.Min(salt.Length, idat.Length)].CopyTo(idat);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(stream, "IHDR"u8, ihdr);
        Chunk(stream, "IDAT"u8, idat);
        Chunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static void Chunk(MemoryStream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        stream.Write(number);
        var body = new byte[type.Length + data.Length];
        type.CopyTo(body);
        data.CopyTo(body.AsSpan(type.Length));
        stream.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
        stream.Write(number);
    }
}

/// <summary>A stand-in for the image helper's fit mode. The result for each edge comes from <paramref name="bytesAtEdge"/>; null fails the load.</summary>
internal sealed class FakeFitter(Func<int, byte[]?> bytesAtEdge, Func<int, bool>? tooLargeAtEdge = null) : IPictureFitter
{
    public List<int> Edges { get; } = [];

    public Task<SafeImageResult> FitBytesAsync(ReadOnlyMemory<byte> data, int maxEdge, CancellationToken cancellationToken) => Fit(maxEdge);

    public Task<SafeImageResult> FitFileAsync(string path, int maxEdge, CancellationToken cancellationToken) => Fit(maxEdge);

    private Task<SafeImageResult> Fit(int maxEdge)
    {
        Edges.Add(maxEdge);
        if (tooLargeAtEdge?.Invoke(maxEdge) == true)
        {
            return Task.FromResult(SafeImageResult.Failure(SafeImageStatus.OutputTooLarge));
        }

        var png = bytesAtEdge(maxEdge);
        return Task.FromResult(png is null ? SafeImageResult.Failure(SafeImageStatus.DecodeFailed) : SafeImageResult.Success(png, maxEdge, maxEdge));
    }
}
