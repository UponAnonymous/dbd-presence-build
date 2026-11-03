using DbdPresence.Configuration;
using DbdPresence.Core.Images;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using DbdPresence.Presentation.Characters;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>Fake PNG bytes that pass <c>StrictPng</c>: signature, IHDR with the given size, one IDAT of filler up to the length (at least 58 bytes), IEND.</summary>
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

/// <summary>The image helper's fit mode. The output depends on the input's first byte so different inputs give different pictures; the edges asked for are recorded.</summary>
internal sealed class FakeFitter : IPictureFitter
{
    public List<int> Edges { get; } = [];

    public Func<int, byte, byte[]?>? Override { get; set; }

    public Task<SafeImageResult> FitBytesAsync(ReadOnlyMemory<byte> data, int maxEdge, CancellationToken cancellationToken) => Fit(maxEdge, data.Span);

    public Task<SafeImageResult> FitFileAsync(string path, int maxEdge, CancellationToken cancellationToken) => Fit(maxEdge, System.Text.Encoding.UTF8.GetBytes(path));

    // The output depends on the input (its hash is written into the first bytes of the image data), so different inputs give different pictures.
    private Task<SafeImageResult> Fit(int maxEdge, ReadOnlySpan<byte> input)
    {
        Edges.Add(maxEdge);
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        var png = Override is { } make ? make(maxEdge, hash[0]) : FakePng.Make(maxEdge, maxEdge, 3000, hash[0], hash.AsSpan(0, 8));

        return Task.FromResult(png is null ? SafeImageResult.Failure(SafeImageStatus.DecodeFailed) : SafeImageResult.Success(png, maxEdge, maxEdge));
    }
}

internal sealed class RecordingSink : IPictureUploadSink
{
    public List<PictureUploadRequest> Saved { get; } = [];

    public List<string> Removed { get; } = [];

    public void Enqueue(PictureUploadRequest request) => Saved.Add(request);

    public void Remove(string sha256) => Removed.Add(sha256);
}

internal sealed class FakePictureEnvironment : IPictureEnvironment
{
    public event EventHandler? Changed;

    public bool SignedIn { get; set; }

    public bool UploadsBlocked { get; set; }

    public IReadOnlyDictionary<string, string> SavedUrlBySha256 { get; set; } = new Dictionary<string, string>();

    public long UsedBytes { get; set; }

    public long LimitBytes { get; set; } = PictureLimits.OnlineQuotaBytes;

    public string? DefaultPackId { get; set; }

    public IReadOnlyDictionary<string, string>? SourceShaByEntryId { get; set; }

    public Dictionary<string, PictureOnlineStatus> Saving { get; } = new(StringComparer.Ordinal);

    public PictureOnlineStatus? SavingStatusOf(string sha256) => Saving.TryGetValue(sha256, out var status) ? status : null;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class CachedAll : IPackContentCache
{
    public bool Contains(PackEntry entry) => true;
}

/// <summary>The Characters page wired to in-memory storage and fakes.</summary>
internal sealed class CharactersHarness : IDisposable
{
    public const string Root = "/data/packs";

    public CharactersHarness(AppSettings? settings = null)
    {
        Host = new Harness(settings);
        Storage = new InMemoryLocalStorage();
        Library = new PackLibrary(Storage, Root, new FakeImageLoader(Storage), PackFixtures.Roster);
        Fitter = new FakeFitter();
        Store = new PictureStore(Storage, Root, Fitter, Host.Time);
        Picker = new FakeFilePicker();
        Sink = new RecordingSink();
        Environment = new FakePictureEnvironment();
        Resolver = new CharacterPictureResolver(Library, Store, Environment, PackFixtures.Roster);
        Provider = new Presentation.Presence.PackArtworkProvider(Library, Resolver);
        Page = new CharactersViewModel(Host.Settings, Host.Dispatcher, PackFixtures.Roster, Library, Store, Picker, Environment, Sink, Resolver);
    }

    public Harness Host { get; }

    public InMemoryLocalStorage Storage { get; }

    public PackLibrary Library { get; }

    public FakeFitter Fitter { get; }

    public PictureStore Store { get; }

    public FakeFilePicker Picker { get; }

    public RecordingSink Sink { get; }

    public FakePictureEnvironment Environment { get; }

    public CharacterPictureResolver Resolver { get; }

    public Presentation.Presence.PackArtworkProvider Provider { get; }

    public CharactersViewModel Page { get; }

    public AppSettings Current => Host.Settings.Current;

    /// <summary>Signs in and accepts the picture rules: the two things that must hold before a picture is handed to the saving-online queue.</summary>
    public void AllowSavingOnline()
    {
        Environment.SignedIn = true;
        Page.AcceptRulesCommand.Execute(null);
    }

    public CharacterTileViewModel Tile(string code) => Page.Tiles.Single(t => t.Code == code);

    public void Select(string code) => Page.SelectedTile = Tile(code);

    public static PackEntry Entry(string pack, string key, string marker) =>
        new($"{pack}-{key.Replace(':', '-')}", pack, key.StartsWith("role", StringComparison.Ordinal) ? PackEntryKind.Role : PackEntryKind.Character, key,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(marker))), 1000, 512, 512, PackImageFormat.Png, null);

    public void SyncDefaultPack(bool behaviour = false, params string[] keys)
    {
        var packs = new[] { new PackInfo("def", "Default portraits", 1, "owner", "note", null, false, behaviour), new PackInfo("back", "Back", 1, "owner", "note", null, true) };
        var entries = keys.Select(k => Entry("def", k, "def" + k)).Append(Entry("back", "role:killer", "kk")).Append(Entry("back", "role:survivor", "ss")).ToArray();
        Library.SetSynced(new SyncedPacks(new PackIndex(packs, entries), new CachedAll(), e => $"https://cdn.example.org/{e.CacheFileName}", null, e => $"/cache/{e.CacheFileName}"));
        Environment.DefaultPackId = "def";
        Environment.Raise();
    }

    public void Dispose()
    {
        Page.Dispose();
        Host.Dispose();
    }
}
