using System.IO;
using System.Security.Cryptography;
using System.Text;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Packs;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;
using DbdPresence.ReleaseSigner;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Updates.Tests;

/// <summary>The service as the update code sees it: one feed text, one key replacement text and package bytes by hash.</summary>
internal sealed class FakeReleases : IReleaseTransport
{
    public string? Feed { get; set; }

    public string? KeyReplacementDocument { get; set; }

    public Dictionary<string, byte[]> Packages { get; } = new(StringComparer.Ordinal);

    public int FeedRequests { get; private set; }

    public List<string> Downloads { get; } = [];

    /// <summary>When set, the feed request fails with it (an unexpected failure).</summary>
    public Exception? FeedFailure { get; set; }

    /// <summary>When true, the service cannot be reached (an outage: offline, timeout, server error).</summary>
    public bool Unreachable { get; set; }

    /// <summary>Awaited at the start of every feed request (a test holds a check open with it).</summary>
    public Func<Task>? BeforeFeed { get; set; }

    /// <summary>Awaited after a package was written, before the download result is returned (a test changes the world mid-download with it).</summary>
    public Func<Task>? AfterDownload { get; set; }

    public async Task<ApiResult<string>> GetFeedAsync(string channel, CancellationToken cancellationToken)
    {
        FeedRequests++;
        if (BeforeFeed is not null)
        {
            await BeforeFeed();
        }

        if (Unreachable)
        {
            return new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null);
        }

        if (FeedFailure is not null)
        {
            throw FeedFailure;
        }

        return Feed is null
            ? new ApiResult<string>(ApiOutcome.Rejected, null, "no_release", null, null)
            : new ApiResult<string>(ApiOutcome.Ok, Feed, null, null, null);
    }

    public Task<ApiResult<string>> GetKeyReplacementAsync(CancellationToken cancellationToken) =>
        Task.FromResult(KeyReplacementDocument is null
            ? new ApiResult<string>(ApiOutcome.Rejected, null, "no_key_replacement", null, null)
            : new ApiResult<string>(ApiOutcome.Ok, KeyReplacementDocument, null, null, null));

    public async Task<ApiResult<bool>> DownloadAsync(ReleasePackage package, Stream destination, Action<int>? progress, CancellationToken cancellationToken)
    {
        Downloads.Add(package.Sha256);
        if (!Packages.TryGetValue(package.Sha256, out var bytes))
        {
            return new ApiResult<bool>(ApiOutcome.Rejected, false, "not_found", null, null);
        }

        // The real client checks the bytes against the signed size and hash while it reads (ServiceClient.DownloadReleasePackageAsync).
        await destination.WriteAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, package.Size)), cancellationToken);
        progress?.Invoke(100);
        if (AfterDownload is not null)
        {
            await AfterDownload();
        }

        var ok = bytes.Length == package.Size && Convert.ToHexStringLower(SHA256.HashData(bytes)) == package.Sha256;
        return ok ? new ApiResult<bool>(ApiOutcome.Ok, true, null, null, null) : new ApiResult<bool>(ApiOutcome.Rejected, false, ServiceClient.PackageMismatchCode, null, null);
    }
}

internal sealed class Gate : IPackSyncGate
{
    public event EventHandler? Changed;

    public bool IsOpen { get; set; } = true;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

internal sealed class Log : IOnlineLog
{
    public List<string> Lines { get; } = [];

    public void Warning(string message) => Lines.Add(message);
}

internal sealed class MemoryFiles : IPackFileStore
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public bool Exists(string path) => Files.ContainsKey(path);

    public long Length(string path) => Files[path].Length;

    public byte[] ReadAllBytes(string path) => Files.TryGetValue(path, out var b) ? b : throw new FileNotFoundException(path);

    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes) => Files[path] = bytes.ToArray();

    public void Move(string source, string destination)
    {
        Files[destination] = Files[source];
        Files.Remove(source);
    }

    public void Delete(string path) => Files.Remove(path);

    public void CreateDirectory(string path)
    {
    }

    public IReadOnlyList<string> ListFiles(string directory) => [.. Files.Keys.Where(k => k.StartsWith(directory, StringComparison.Ordinal))];
}

/// <summary>Throw-away keys, a clock, a temp folder and helpers to sign feeds the way the offline tool does.</summary>
internal sealed class UpdateFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    public UpdateFixture()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PackagesDir);
        Directory.CreateDirectory(Published);
        Keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal)
        {
            ["release-test-1"] = new(Release.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases]),
            ["recovery-test-1"] = new(Recovery.ExportSubjectPublicKeyInfo(), [SignedPurposes.KeyReplacement]),
            ["content-test-1"] = new(Content.ExportSubjectPublicKeyInfo(), [SignedPurposes.PackIndex, SignedPurposes.Roster]),
        };
        Verifier = new SignedDocumentVerifier(new TrustedKeySet(Keys), Versions, Time);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dbdp-updates-" + Guid.NewGuid().ToString("N"));

    /// <summary>Velopack's packages folder of the pretend install.</summary>
    public string PackagesDir => Path.Combine(Root, "packages");

    /// <summary>Where the owner's packed releases are.</summary>
    public string Published => Path.Combine(Root, "published");

    public FakeTimeProvider Time { get; } = new(Start);

    public ECDsa Release { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ECDsa Recovery { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ECDsa Content { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Dictionary<string, TrustedKey> Keys { get; }

    public InMemoryVersionStore Versions { get; } = new();

    public SignedDocumentVerifier Verifier { get; }

    public FakeReleases Transport { get; } = new();

    public Gate Gate { get; } = new();

    public Log Log { get; } = new();

    /// <summary>Where the release floor file is kept (survives a "restart": a new service over the same files).</summary>
    public MemoryFiles Files { get; } = new();

    public const string FloorPath = "online/release-floor.json";

    public UpdateService Service(KeyReplacementService? keys = null, SignedDocumentVerifier? verifier = null, IReleaseFloor? floor = null) =>
        new(Transport, verifier ?? Verifier, Gate, floor ?? new FileReleaseFloor(Files, FloorPath, Log), UpdateService.StableChannel, Time, Log, keys);

    /// <summary>Packs a release and puts its bytes on the fake CDN.</summary>
    public ReleasePackage Publish(string version, string channel = "stable")
    {
        var path = TestPackages.Write(Published, version, channel);
        var package = PackageReader.Read(path, channel);
        Transport.Packages[package.Sha256] = File.ReadAllBytes(path);
        return package;
    }

    /// <summary>Signs a feed like the tool does (the tool's own signer; the app verifies it).</summary>
    public string Feed(IReadOnlyList<ReleasePackage> packages, long version, string channel = "stable", ECDsa? key = null, string kid = "release-test-1", DateTimeOffset? issuedAt = null, TimeSpan? lifetime = null)
    {
        var issued = issuedAt ?? Time.GetUtcNow();
        var payload = new ReleaseFeed(ReleaseFeed.ExpectedPackageId, channel, packages).ToPayloadJson();
        return DocumentSigner.Sign(key ?? Release, kid, SignedPurposes.Releases, channel, version, issued, issued + (lifetime ?? TimeSpan.FromDays(30)), payload);
    }

    public string KeyReplacementDocument(KeyReplacement replacement, long version, ECDsa? key = null, string kid = "recovery-test-1") =>
        DocumentSigner.Sign(key ?? Recovery, kid, SignedPurposes.KeyReplacement, KeyReplacement.Channel, version, Time.GetUtcNow(), Time.GetUtcNow().AddDays(3650), replacement.ToPayloadJson());

    /// <summary>Changes one character inside the signed envelope, keeping the document well formed.</summary>
    public static string Tamper(string document)
    {
        var start = document.IndexOf("\"signed\":\"", StringComparison.Ordinal) + 20;
        var chars = document.ToCharArray();
        chars[start] = chars[start] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    public string[] PackagesOnDisk() => [.. Directory.GetFiles(PackagesDir, "*.nupkg").Select(Path.GetFileName)!];

    public void Dispose()
    {
        Release.Dispose();
        Recovery.Dispose();
        Content.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open on Windows (Velopack's lock file) only leaves the temporary folder behind.
            Directory.CreateDirectory(Path.Combine(Root, ".cleanup-failed"));
        }
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
