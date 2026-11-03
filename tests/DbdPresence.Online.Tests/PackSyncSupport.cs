using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Core.Images;
using DbdPresence.Online.Api;
using DbdPresence.Online.Http;
using DbdPresence.Online.Packs;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

/// <summary>Builds small PNG files that pass <see cref="StrictPng"/> (8-bit RGB), so the tests need no image library and no committed binaries.</summary>
internal static class TestPngs
{
    private static readonly uint[] s_crc = BuildCrcTable();

    public static byte[] Rgb(int width, int height, byte seed)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[(width * 3) + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 1; x < row.Length; x++)
                {
                    row[x] = (byte)(seed + x + y);
                }

                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>A file that starts like a JPEG; only the image helper (a fake in these tests) decides whether it is one.</summary>
    public static byte[] Jpeg(byte seed) => [0xFF, 0xD8, 0xFF, 0xE0, seed, 1, 2, 3, 4, 5, 6, 7, 0xFF, 0xD9];

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
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typeAndData));
        output.Write(crc);
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
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

/// <summary>One image the fake publisher serves: the index entry and the bytes the CDN answers with.</summary>
internal sealed record PublishedFile(string Id, string Pack, string Key, byte[] Bytes, PackImageFormat Format, int Width, int Height, string? LicensedHolder)
{
    public static PublishedFile Png(string id, string pack, string key, byte seed, string? licensed = null, int size = 16) =>
        new(id, pack, key, TestPngs.Rgb(size, size, seed), PackImageFormat.Png, size, size, licensed);

    public static PublishedFile Jpeg(string id, string pack, string key, byte seed) =>
        new(id, pack, key, TestPngs.Jpeg(seed), PackImageFormat.Jpeg, 64, 64, null);

    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Bytes));

    public string FileName => Sha256 + (Format == PackImageFormat.Png ? ".png" : ".jpg");
}

/// <summary>A pack in the fake index.</summary>
internal sealed record PublishedPack(string Id, string Name, bool Fallback = false, bool BehaviourArt = false);

/// <summary>Signs pack indexes like the service does, with a throw-away <c>pack-index</c> key.</summary>
internal sealed class PackPublisher : IDisposable
{
    public const string Kid = "pack-index-test";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TrustedKeySet Trusted => new(new Dictionary<string, TrustedKey>
    {
        [Kid] = new TrustedKey(_key.ExportSubjectPublicKeyInfo(), [SignedPurposes.PackIndex]),
    });

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public string Document(
        DateTimeOffset now, long version, IReadOnlyList<PublishedPack> packs, IReadOnlyList<PublishedFile> files, string channel = "stable", TimeSpan? lifetime = null, string purpose = SignedPurposes.PackIndex)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["packs"] = packs.Select(p => new Dictionary<string, object>
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["version"] = 1,
                ["author"] = "DBD Presence",
                ["licenceNote"] = "Fan content shown as a courtesy.",
                ["fallback"] = p.Fallback,
                ["behaviourArt"] = p.BehaviourArt,
            }).ToArray(),
            ["entries"] = files.Select(f => new Dictionary<string, object?>
            {
                ["id"] = f.Id,
                ["pack"] = f.Pack,
                ["kind"] = f.Key.Split(':')[0],
                ["key"] = f.Key,
                ["sha256"] = f.Sha256,
                ["size"] = f.Bytes.Length,
                ["width"] = f.Width,
                ["height"] = f.Height,
                ["format"] = f.Format == PackImageFormat.Png ? "png" : "jpeg",
                ["licensed"] = f.LicensedHolder is null ? null : new Dictionary<string, string> { ["holder"] = f.LicensedHolder },
            }).ToArray(),
        });
        var expires = now + (lifetime ?? TimeSpan.FromDays(30));
        var envelope = $$"""{"kid":"{{Kid}}","purpose":"{{purpose}}","channel":"{{channel}}","version":{{version}},"issuedAt":"{{TestIssuer.Iso(now)}}","expiresAt":"{{TestIssuer.Iso(expires)}}","payload":{{payload}}}""";
        var bytes = Encoding.UTF8.GetBytes(envelope);
        var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $$"""{"kid":"{{Kid}}","signed":"{{Base64Url.EncodeToString(bytes)}}","sig":"{{Base64Url.EncodeToString(signature)}}"}""";
    }

    public static string Wrap(string document) => $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(document)}}}""";

    public void Dispose() => _key.Dispose();
}

/// <summary>A stand-in for the image helper.</summary>
internal sealed class FakeImageHelper : ISafeImageLoader
{
    public bool Succeeds { get; set; } = true;

    public List<int> Calls { get; } = [];

    public Task<SafeImageResult> LoadFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult(SafeImageResult.Failure(SafeImageStatus.BadInput));

    public Task<SafeImageResult> LoadBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Calls.Add(data.Length);
        return Task.FromResult(Succeeds ? SafeImageResult.Success(TestPngs.Rgb(16, 16, 9), 16, 16) : SafeImageResult.Failure(SafeImageStatus.DecodeFailed));
    }
}

internal sealed class SwitchGate : IPackSyncGate
{
    private bool _open = true;

    public event EventHandler? Changed;

    public bool IsOpen
    {
        get => _open;
        set
        {
            _open = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>The pack sync wired to fakes: a signed-index publisher, a CDN, an in-memory disk and a fake clock.</summary>
internal sealed class PackWorld : IDisposable
{
    public const string ApiHost = "api-staging.arcavale.com";
    public const string CdnHost = "cdn-staging.arcavale.com";
    public const string Directory = "app/packs/synced";

    public PackWorld(long maxCacheBytes = PackCache.MaxCacheBytes)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        Versions = new InMemoryVersionStore();
        MaxCacheBytes = maxCacheBytes;
        var handler = new AllowListHandler(new HashSet<string>([ApiHost, CdnHost], StringComparer.OrdinalIgnoreCase), Service, CdnHost);
        Http = ServiceHttp.CreateClient(handler, new Uri($"https://{ApiHost}/"));
        Api = new ServiceClient(Http, Time);
        Service.Handler = Serve;
        Sync = Build();
    }

    public FakeTimeProvider Time { get; }

    public PackPublisher Publisher { get; } = new();

    public MemoryFiles Files { get; } = new();

    public InMemoryVersionStore Versions { get; private set; }

    public FakeService Service { get; } = new();

    public HttpClient Http { get; }

    public ServiceClient Api { get; }

    public FakeImageHelper Helper { get; } = new();

    public SwitchGate Gate { get; } = new();

    public TestLog Log { get; } = new();

    public long MaxCacheBytes { get; }

    public PackSyncService Sync { get; private set; }

    /// <summary>The published state: what the index endpoint answers with.</summary>
    public string? IndexBody { get; set; }

    /// <summary>The status the index endpoint answers with, when not 200.</summary>
    public int IndexStatus { get; set; } = 200;

    public TimeSpan? IndexRetryAfter { get; set; }

    /// <summary>Files the CDN serves, by file name; a missing name answers 404.</summary>
    public Dictionary<string, byte[]> Cdn { get; } = [];

    /// <summary>Files for which the CDN answers with a server error.</summary>
    public HashSet<string> CdnFailures { get; } = [];

    public IEnumerable<Recorded> IndexRequests => Service.Requests.Where(r => r.Path == "/v1/packs");

    public IEnumerable<Recorded> FileRequests => Service.Requests.Where(r => r.Host == CdnHost);

    public PackSyncService Build()
    {
        var cache = new PackCache(Files, Path.Combine(Directory, "cache"), MaxCacheBytes);
        var verifier = new SignedDocumentVerifier(Publisher.Trusted, Versions, Time);
        return new PackSyncService(new PackSyncDependencies(
            new HttpPackSyncTransport(Api, new Uri($"https://{CdnHost}/packs/")), verifier, cache, Files, Directory, Helper, Gate, Time, Log));
    }

    /// <summary>A new service over the same disk and version store (an app restart).</summary>
    public PackSyncService Restart(bool freshVersions = false)
    {
        Sync.Dispose();
        if (freshVersions)
        {
            Versions = new InMemoryVersionStore();
        }

        Sync = Build();
        return Sync;
    }

    /// <summary>Publishes a signed index and puts its files on the CDN.</summary>
    public string Publish(long version, IReadOnlyList<PublishedPack> packs, IReadOnlyList<PublishedFile> files, string channel = "stable", TimeSpan? lifetime = null)
    {
        var document = Publisher.Document(Time.GetUtcNow(), version, packs, files, channel, lifetime);
        IndexBody = PackPublisher.Wrap(document);
        foreach (var file in files)
        {
            Cdn[file.FileName] = file.Bytes;
        }

        return document;
    }

    public void Dispose()
    {
        Sync.Dispose();
        Publisher.Dispose();
        Http.Dispose();
    }

    public HttpResponseMessage Serve(Recorded request)
    {
        if (request.Host == ApiHost && request.Path == "/v1/packs")
        {
            return IndexStatus != 200 || IndexBody is null
                ? FakeService.Json(IndexStatus == 200 ? 404 : IndexStatus, """{"schemaVersion":1,"error":"unavailable"}""", IndexRetryAfter)
                : FakeService.Json(200, IndexBody);
        }

        if (request.Host == CdnHost && request.Path.StartsWith("/packs/", StringComparison.Ordinal))
        {
            var name = request.Path["/packs/".Length..];
            if (CdnFailures.Contains(name))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }

            return Cdn.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }
}
