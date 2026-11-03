// Covers: NFR-SVC-04 (signing rules, shared vectors), FR-PACK-01 (verifier vectors), ADR-0012.
// The same files in tests/fixtures/signing run in the TypeScript suite (V2-S3/S4).
using System.Buffers.Text;
using System.Text.Json;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class SigningVectorTests
{
    private static readonly string s_root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "signing");

    public static TheoryData<string> VectorNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(s_root, "vectors"), "*.json").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            return data;
        }
    }

    [Fact]
    public void VectorSet_CoversEveryOutcome()
    {
        var expected = Directory.GetFiles(Path.Combine(s_root, "vectors"), "*.json")
            .Select(f => Load(Path.GetFileNameWithoutExtension(f)).Expected)
            .ToHashSet();

        foreach (var outcome in Enum.GetValues<VerificationOutcome>())
        {
            Assert.Contains(outcome, expected);
        }
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Vector_ProducesTheExpectedOutcome(string name)
    {
        var vector = Load(name);
        var versions = new InMemoryVersionStore();
        foreach (var stored in vector.Stored)
        {
            versions.RecordHighest(stored.Purpose, stored.Channel, stored.Version, null);
        }

        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), versions, new FakeTimeProvider(vector.Now));

        var result = verifier.Verify(System.Text.Encoding.UTF8.GetBytes(vector.DocumentText), vector.ExpectedPurpose, vector.ExpectedChannel);

        Assert.Equal(vector.Expected, result.Outcome);
        Assert.Equal(vector.Expected == VerificationOutcome.Valid, result.Envelope is not null);
    }

    [Fact]
    public void ValidVector_PayloadIsAValidPackIndex()
    {
        var vector = Load("valid");
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(vector.Now));

        var result = verifier.Verify(System.Text.Encoding.UTF8.GetBytes(vector.DocumentText), SignedPurposes.PackIndex, "stable");
        var parsed = PackIndexParser.Parse(result.Envelope!.PayloadJson);

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        Assert.Equal(3, parsed.Index!.Entries.Count);
        Assert.Equal("Test rights holder", parsed.Index.Entries.Single(e => e.Key == "character:ghostface").Licensed?.Holder);
    }

    [Fact]
    public void Vectors_NeverRecordAVersion_VerifyDoesNotCommit()
    {
        var vector = Load("valid");
        var versions = new InMemoryVersionStore();
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), versions, new FakeTimeProvider(vector.Now));

        Assert.True(verifier.Verify(System.Text.Encoding.UTF8.GetBytes(vector.DocumentText), SignedPurposes.PackIndex, "stable").IsValid);

        Assert.Null(versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void RolledBackVector_DoesNotLowerTheStoredVersion()
    {
        var vector = Load("rolled-back");
        var versions = new InMemoryVersionStore();
        versions.RecordHighest(SignedPurposes.PackIndex, "stable", 5, null);
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), versions, new FakeTimeProvider(vector.Now));

        verifier.Verify(System.Text.Encoding.UTF8.GetBytes(vector.DocumentText), SignedPurposes.PackIndex, "stable");

        Assert.Equal(5, versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    private static TrustedKeySet LoadTrustedKeys()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(s_root, "keys", "keys.json")));
        var keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in json.RootElement.GetProperty("keys").EnumerateArray())
        {
            var purposes = key.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!).ToArray();
            keys[key.GetProperty("kid").GetString()!] = new TrustedKey(Base64Url.DecodeFromChars(key.GetProperty("spki").GetString()!), purposes);
        }

        return new TrustedKeySet(keys);
    }

    private static Vector Load(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(s_root, "vectors", name + ".json")));
        var root = json.RootElement;
        var stored = root.GetProperty("storedVersions").EnumerateArray()
            .Select(e => new StoredVersion(e.GetProperty("purpose").GetString()!, e.GetProperty("channel").GetString()!, e.GetProperty("version").GetInt64()))
            .ToArray();
        return new Vector(
            Enum.Parse<VerificationOutcome>(root.GetProperty("expected").GetString()!),
            root.GetProperty("expectedPurpose").GetString()!,
            root.GetProperty("expectedChannel").GetString()!,
            DateTimeOffset.Parse(root.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            root.GetProperty("documentText").GetString()!,
            stored);
    }

    private sealed record StoredVersion(string Purpose, string Channel, long Version);

    private sealed record Vector(VerificationOutcome Expected, string ExpectedPurpose, string ExpectedChannel, DateTimeOffset Now, string DocumentText, StoredVersion[] Stored);
}
