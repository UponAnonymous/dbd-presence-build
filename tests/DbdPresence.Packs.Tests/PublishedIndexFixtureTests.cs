// Covers: FR-PACK-01 (pack index format), FR-ONL-07 (signed index from the service), FR-PACK-04 (withdrawn licensed
// items absent), NFR-SVC-04 (cross-stack signing). tests/fixtures/packs/published-index.json is a pack index as the
// service publishes and signs it (service/scripts/make-published-index-fixture.mjs; service/test/packs.test.ts checks
// that publishing through the admin API produces this payload). The C# verifier and parser must accept it unchanged.
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class PublishedIndexFixtureTests
{
    private static readonly string s_fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void ServicePublishedIndex_VerifiesAndParses()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtures, "packs", "published-index.json")))!.AsObject();
        var now = DateTimeOffset.Parse(fixture["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(now));

        var result = verifier.Verify(System.Text.Encoding.UTF8.GetBytes(fixture["documentText"]!.GetValue<string>()), SignedPurposes.PackIndex, "stable");

        Assert.Equal(VerificationOutcome.Valid, result.Outcome);
        Assert.Equal(fixture["expectedVersion"]!.GetValue<long>(), result.Envelope!.Version);
        Assert.Equal(TimeSpan.FromDays(30), result.Envelope.ExpiresAt - result.Envelope.IssuedAt);

        var parsed = PackIndexParser.Parse(result.Envelope.PayloadJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        var index = parsed.Index!;
        Assert.Equal(["backup", "original"], index.Packs.Select(p => p.Id));
        Assert.True(index.Packs.Single(p => p.Id == "backup").Fallback);
        Assert.False(index.Packs.Single(p => p.Id == "original").Fallback);

        // S5b-3: the default pack flag and the upload's source hash travel in the signed index.
        Assert.Equal(["original"], index.Packs.Where(p => p.Default).Select(p => p.Id));
        var trapper = index.Entries.Single(e => e.Key == "character:the-trapper");
        Assert.Equal(trapper.Sha256, trapper.SourceSha256);
        Assert.All(index.Entries.Where(e => e.Key != "character:the-trapper"), e => Assert.Null(e.SourceSha256));
        Assert.Equal(6, index.Entries.Count);
        Assert.Contains(index.Entries, e => e.Format == PackImageFormat.Jpeg && e.CacheFileName.EndsWith(".jpg", StringComparison.Ordinal));
        Assert.Equal("Example Rights Holder", index.Entries.Single(e => e.Key == "character:the-shape").Licensed!.Holder);
        Assert.All(index.Entries.Where(e => e.Key != "character:the-shape"), e => Assert.Null(e.Licensed));

        // The withdrawn licensed entry is not in the index at all.
        var withdrawn = fixture["withdrawnEntryIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.NotEmpty(withdrawn);
        Assert.DoesNotContain(index.Entries, e => withdrawn.Contains(e.Id));
        Assert.DoesNotContain(index.Entries, e => e.Key == "character:ghostface");
    }

    [Fact]
    public void ServicePublishedIndex_CarriesTheBehaviourArtFlagPerPack()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(s_fixtures, "packs", "published-index.json")))!.AsObject();
        var packs = fixture["expectedPayload"]!["packs"]!.AsArray();

        Assert.True(packs.Single(p => p!["id"]!.GetValue<string>() == "original")!["behaviourArt"]!.GetValue<bool>());
        Assert.False(packs.Single(p => p!["id"]!.GetValue<string>() == "backup")!["behaviourArt"]!.GetValue<bool>());
    }

    private static TrustedKeySet LoadTrustedKeys()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(s_fixtures, "signing", "keys", "keys.json")));
        var keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in json.RootElement.GetProperty("keys").EnumerateArray())
        {
            var purposes = key.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!).ToArray();
            keys[key.GetProperty("kid").GetString()!] = new TrustedKey(Base64Url.DecodeFromChars(key.GetProperty("spki").GetString()!), purposes);
        }

        return new TrustedKeySet(keys);
    }
}
