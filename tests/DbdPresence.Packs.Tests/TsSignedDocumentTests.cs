// Covers: NFR-SVC-04 (signing rules, cross-stack contract), FR-ONL-04 (licence documents), FR-ONL-08 (config documents), ADR-0012.
// The documents in tests/fixtures/signing/ts-signed were produced by the TypeScript service signer
// (service/src/signing/document.ts via service/scripts/make-ts-signed-fixtures.mjs); the C# verifier must accept them unchanged.
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class TsSignedDocumentTests
{
    private static readonly string s_root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "signing");

    public static TheoryData<string> DocumentNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(Path.Combine(s_root, "ts-signed"), "*.json").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            return data;
        }
    }

    [Fact]
    public void Fixtures_CoverEveryPurpose()
    {
        var purposes = Directory.GetFiles(Path.Combine(s_root, "ts-signed"), "*.json")
            .Select(f => Load(Path.GetFileNameWithoutExtension(f)).Purpose)
            .Order(StringComparer.Ordinal);

        Assert.Equal(SignedPurposes.All.Order(StringComparer.Ordinal), purposes);
    }

    [Theory]
    [MemberData(nameof(DocumentNames))]
    public void TsSignedDocument_VerifiesWithTheExpectedEnvelope(string name)
    {
        var fixture = Load(name);
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(fixture.Now));

        var result = verifier.Verify(System.Text.Encoding.UTF8.GetBytes(fixture.DocumentText), fixture.Purpose, fixture.Channel);

        Assert.Equal(VerificationOutcome.Valid, result.Outcome);
        var envelope = result.Envelope!;
        Assert.Equal("test-content-1", envelope.Kid);
        Assert.Equal(fixture.Version, envelope.Version);
        Assert.Equal(fixture.IssuedAt, envelope.IssuedAt);
        Assert.Equal(fixture.ExpiresAt, envelope.ExpiresAt);
        Assert.Equal(SignedPurposes.MaxLifetime(fixture.Purpose), envelope.ExpiresAt - envelope.IssuedAt);
        Assert.True(JsonNode.DeepEquals(fixture.Payload, JsonNode.Parse(envelope.PayloadJson)), $"payload differs: {envelope.PayloadJson}");
    }

    [Fact]
    public void TsSignedConfig_IsBoundToItsPurposeAndChannel()
    {
        var fixture = Load("ts-config");
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(fixture.Now));
        var bytes = System.Text.Encoding.UTF8.GetBytes(fixture.DocumentText);

        Assert.Equal(VerificationOutcome.WrongPurpose, verifier.Verify(bytes, SignedPurposes.Licence, "stable").Outcome);
        Assert.Equal(VerificationOutcome.WrongChannel, verifier.Verify(bytes, SignedPurposes.Config, "beta").Outcome);
    }

    [Fact]
    public void TsSignedLicence_ExpiresAfterSevenDays()
    {
        var fixture = Load("ts-licence");
        var bytes = System.Text.Encoding.UTF8.GetBytes(fixture.DocumentText);
        var justBefore = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(fixture.ExpiresAt.AddMilliseconds(-1)));
        var atExpiry = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(fixture.ExpiresAt));

        Assert.Equal(VerificationOutcome.Valid, justBefore.Verify(bytes, SignedPurposes.Licence, "stable").Outcome);
        Assert.Equal(VerificationOutcome.Expired, atExpiry.Verify(bytes, SignedPurposes.Licence, "stable").Outcome);
    }

    [Fact]
    public void TsSignedPackIndex_WithOneSignatureByteChanged_IsBadSignature()
    {
        var fixture = Load("ts-pack-index");
        using var outer = JsonDocument.Parse(fixture.DocumentText);
        var signature = Base64Url.DecodeFromChars(outer.RootElement.GetProperty("sig").GetString()!);
        signature[10] ^= 0x01;
        var tampered = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["kid"] = outer.RootElement.GetProperty("kid").GetString()!,
            ["signed"] = outer.RootElement.GetProperty("signed").GetString()!,
            ["sig"] = Base64Url.EncodeToString(signature),
        });
        var verifier = new SignedDocumentVerifier(LoadTrustedKeys(), new InMemoryVersionStore(), new FakeTimeProvider(fixture.Now));

        Assert.Equal(VerificationOutcome.BadSignature, verifier.Verify(System.Text.Encoding.UTF8.GetBytes(tampered), SignedPurposes.PackIndex, "stable").Outcome);
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

    private static Fixture Load(string name)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(s_root, "ts-signed", name + ".json")))!.AsObject();
        return new Fixture(
            root["expectedPurpose"]!.GetValue<string>(),
            root["expectedChannel"]!.GetValue<string>(),
            Time(root["now"]!.GetValue<string>()),
            root["expectedVersion"]!.GetValue<long>(),
            Time(root["expectedIssuedAt"]!.GetValue<string>()),
            Time(root["expectedExpiresAt"]!.GetValue<string>()),
            root["expectedPayload"]!.DeepClone(),
            root["documentText"]!.GetValue<string>());
    }

    private static DateTimeOffset Time(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private sealed record Fixture(string Purpose, string Channel, DateTimeOffset Now, long Version, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, JsonNode Payload, string DocumentText);
}
