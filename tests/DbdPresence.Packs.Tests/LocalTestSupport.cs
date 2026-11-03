using System.IO.Compression;
using DbdPresence.Core.Images;

namespace DbdPresence.Packs.Tests;

/// <summary>
/// A stand-in for the image helper: bytes starting with "BAD" fail to decode, anything else "normalises" to a 64 by 64 PNG
/// (here: the input with a marker byte appended, so tests can tell it went through the loader).
/// </summary>
internal sealed class FakeImageLoader(InMemoryLocalStorage? files = null) : ISafeImageLoader
{
    public List<string> Loaded { get; } = [];

    public Task<SafeImageResult> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        Loaded.Add(path);
        return Task.FromResult(files is not null && files.Files.TryGetValue(path, out var bytes)
            ? Convert(bytes)
            : SafeImageResult.Failure(SafeImageStatus.BadInput));
    }

    public Task<SafeImageResult> LoadBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Loaded.Add("bytes");
        return Task.FromResult(Convert(data.ToArray()));
    }

    private static SafeImageResult Convert(byte[] data) =>
        data.AsSpan().StartsWith("BAD"u8) || (data.Length > 8 && data.AsSpan(8).StartsWith("BAD"u8))
            ? SafeImageResult.Failure(SafeImageStatus.DecodeFailed)
            : SafeImageResult.Success(FakePng.Make(64, 64, 200 + (data.Length % 50), 7, System.Security.Cryptography.SHA256.HashData(data)), 64, 64);
}

/// <summary>Builds small zip archives of fake images.</summary>
internal static class ZipBuilder
{
    public static byte[] Png(string marker = "") => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. System.Text.Encoding.ASCII.GetBytes(marker + "-data")];

    public static byte[] Build(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }

        return stream.ToArray();
    }
}
