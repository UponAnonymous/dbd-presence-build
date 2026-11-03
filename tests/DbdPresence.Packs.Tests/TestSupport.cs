using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Packs.Tests;

/// <summary>Creates signed documents with a throw-away key, for cases the committed vectors do not cover.</summary>
internal sealed class TestSigner : IDisposable
{
    public const string Kid = "unit-key";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TrustedKeySet Trusted => TrustedFor(SignedPurposes.All);

    /// <summary>This signer's public key as a trusted key, for building a set that holds several signers under their own ids.</summary>
    public TrustedKey AsTrustedKey(IReadOnlyCollection<string> purposes) => new(_key.ExportSubjectPublicKeyInfo(), purposes);

    public TrustedKeySet TrustedFor(IReadOnlyCollection<string> purposes) =>
        new(new Dictionary<string, TrustedKey> { [Kid] = new TrustedKey(_key.ExportSubjectPublicKeyInfo(), purposes) });

    public static string Envelope(string payloadJson, string purpose = SignedPurposes.PackIndex, long version = 1, string kid = Kid, string expiresAt = "2026-01-31T00:00:00.000Z", string issuedAt = "2026-01-01T00:00:00.000Z") =>
        $$"""{"kid":"{{kid}}","purpose":"{{purpose}}","channel":"stable","version":{{version}},"issuedAt":"{{issuedAt}}","expiresAt":"{{expiresAt}}","payload":{{payloadJson}}}""";

    public byte[] Sign(string envelopeJson, string kid = Kid)
    {
        var bytes = Encoding.UTF8.GetBytes(envelopeJson);
        return Document(kid, bytes, _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public byte[] SignBytes(byte[] envelope, string kid = Kid) =>
        Document(kid, envelope, _key.SignData(envelope, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static byte[] Document(string kid, byte[] envelope, byte[] signature) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kid"] = kid,
            ["signed"] = Base64Url.EncodeToString(envelope),
            ["sig"] = Base64Url.EncodeToString(signature),
        }));

    public void Dispose() => _key.Dispose();
}

/// <summary>In-memory <see cref="IPackFileStore"/> with a write log.</summary>
internal sealed class InMemoryPackFileStore : IPackFileStore
{
    public Dictionary<string, byte[]> Files { get; } = VirtualPaths.NewFiles<byte[]>();

    public List<string> Operations { get; } = [];

    public bool Exists(string path) => Files.ContainsKey(path);

    public long Length(string path) => Files[path].Length;

    public byte[] ReadAllBytes(string path) => Files.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path);

    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes)
    {
        Operations.Add("write " + path);
        Files[path] = bytes.ToArray();
    }

    public void Move(string source, string destination)
    {
        Operations.Add($"move {source} -> {destination}");
        Files[destination] = Files[source];
        Files.Remove(source);
    }

    public void Delete(string path)
    {
        Operations.Add("delete " + path);
        Files.Remove(path);
    }

    public void CreateDirectory(string path)
    {
    }

    public IReadOnlyList<string> ListFiles(string directory) =>
        [.. Files.Keys.Where(k => VirtualPaths.IsUnder(k, directory, out _))];
}

/// <summary>Builders for pack data.</summary>
internal static class PackData
{
    public static byte[] Content(string name) => Encoding.UTF8.GetBytes("image-bytes-" + name);

    public static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static PackInfo Pack(string id, int version = 1) => new(id, id, version, "author", "note", null);

    public static PackEntry Entry(string id, string pack, string key, string contentName, LicensedInfo? licensed = null, PackImageFormat format = PackImageFormat.Png)
    {
        var content = Content(contentName);
        Assert.True(PackKey.TryParse(key, out var kind, out _));
        return new PackEntry(id, pack, kind, key, Sha(content), content.Length, 1024, 1024, format, licensed);
    }

    public static PackIndex Index(PackInfo[] packs, params PackEntry[] entries) => new(packs, entries);
}
