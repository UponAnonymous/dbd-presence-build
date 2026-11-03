using System.IO.Compression;
using DbdPresence.Core.Images;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Roster;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>A stand-in for the image helper: bytes containing "BAD" fail to decode; anything else "normalises" with a marker byte.</summary>
internal sealed class FakeImageLoader(InMemoryLocalStorage files) : ISafeImageLoader
{
    public Task<SafeImageResult> LoadFileAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(files.Files.TryGetValue(path, out var bytes) ? Convert(bytes) : SafeImageResult.Failure(SafeImageStatus.BadInput));

    public Task<SafeImageResult> LoadBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Task.FromResult(Convert(data.ToArray()));

    private static SafeImageResult Convert(byte[] data) =>
        data.AsSpan().IndexOf("BAD"u8) >= 0
            ? SafeImageResult.Failure(SafeImageStatus.DecodeFailed)
            : SafeImageResult.Success(FakePng.Make(64, 64, 200 + (data.Length % 50), 7, System.Security.Cryptography.SHA256.HashData(data)), 64, 64);
}

/// <summary>A file picker that returns what the test queued.</summary>
internal sealed class FakeFilePicker : IFilePicker
{
    public string? Archive { get; set; }

    public string? Image { get; set; }

    public string? PickPackArchive() => Archive;

    public string? PickImage() => Image;
}

/// <summary>Builds small archives of fake images.</summary>
internal static class PackFixtures
{
    public const string Root = "/data/packs";

    /// <summary>The shipped roster, built from the embedded vocabulary exactly as the app does.</summary>
    public static CharacterRoster Roster { get; } = VocabularyRoster.From(Vocabulary.LoadEmbedded());

    public static byte[] Png(string marker) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. System.Text.Encoding.ASCII.GetBytes(marker + "-data")];

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
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

/// <summary>The Packs page wired to in-memory storage.</summary>
internal sealed class PackHarness
{
    public PackHarness(Harness host)
    {
        Host = host;
        Storage = new InMemoryLocalStorage();
        Library = new PackLibrary(Storage, PackFixtures.Root, new FakeImageLoader(Storage), PackFixtures.Roster);
        Picker = new FakeFilePicker();
        Provider = new Presentation.Presence.PackArtworkProvider(Library);
    }

    public Harness Host { get; }

    public InMemoryLocalStorage Storage { get; }

    public PackLibrary Library { get; }

    public FakeFilePicker Picker { get; }

    public Presentation.Presence.PackArtworkProvider Provider { get; }

    public PacksViewModel Page() => new(Host.Settings, Host.Dispatcher, Library, Picker);
}
