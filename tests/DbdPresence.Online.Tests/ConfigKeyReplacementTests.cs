// Covers: threat-model review 2026-10-25 TM-04 (item 3: a key replacement is fetched on the configuration refresh, not only in update checks)
// and TM-01 (the version floor of a revoked key is dropped), PLAN_V2 §6.2, NFR-SVC-05, ADR-0012.
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Online.Config;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Tests;

public sealed class ConfigKeyReplacementTests : IDisposable
{
    private const string RecoveryKid = "recovery-test-1";
    private const string SuccessorKid = "config-test-2";
    private const string ConfigPayload = """{"schemaVersion":1,"applications":[],"disable":{"capture":false,"detection":false},"emergencyStop":false}""";

    private readonly AccountHarness _h = new();
    private readonly ECDsa _recovery = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _successor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly InMemoryVersionStore _versions = new();
    private readonly MemoryFiles _files = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _recovery.Dispose();
        _successor.Dispose();
        _h.Dispose();
    }

    private Dictionary<string, TrustedKey> BuiltIn() => new(StringComparer.Ordinal)
    {
        [TestIssuer.Kid] = _h.Issuer.AsTrustedKey(),
        [RecoveryKid] = new TrustedKey(_recovery.ExportSubjectPublicKeyInfo(), [SignedPurposes.KeyReplacement]),
    };

    private string Signed(ECDsa key, string kid, string purpose, string channel, long version, string payload)
    {
        var now = _h.Time.GetUtcNow();
        var lifetime = purpose == SignedPurposes.KeyReplacement ? TimeSpan.FromDays(365) : TimeSpan.FromDays(7);
        var envelope = $$"""{"kid":"{{kid}}","purpose":"{{purpose}}","channel":"{{channel}}","version":{{version}},"issuedAt":"{{TestIssuer.Iso(now)}}","expiresAt":"{{TestIssuer.Iso(now + lifetime)}}","payload":{{payload}}}""";
        var bytes = Encoding.UTF8.GetBytes(envelope);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $$"""{"kid":"{{kid}}","signed":"{{Base64Url.EncodeToString(bytes)}}","sig":"{{Base64Url.EncodeToString(signature)}}"}""";
    }

    private string Replacement(ECDsa? signer = null)
    {
        var replacement = new KeyReplacement([TestIssuer.Kid], [new AddedKey(SuccessorKid, _successor.ExportSubjectPublicKeyInfo(), [SignedPurposes.Config])]);
        return Signed(signer ?? _recovery, RecoveryKid, SignedPurposes.KeyReplacement, KeyReplacement.Channel, 1, replacement.ToPayloadJson());
    }

    private (RemoteConfigService Config, KeyReplacementService Keys, SignedDocumentVerifier Verifier) Build(string configDocument, string? keysDocument, bool withKeys = true)
    {
        var builtIn = BuiltIn();
        var verifier = new SignedDocumentVerifier(new TrustedKeySet(builtIn), _versions, _h.Time);
        var keys = new KeyReplacementService(new HttpReleaseTransport(_h.Api, new Uri("https://cdn.arcavale.com/releases/")), verifier, builtIn, _files, "online/key-replacement.json", _h.Log);
        _h.Service.Handler = r => r.Path switch
        {
            "/v1/config" => FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(configDocument)}}}"""),
            "/v1/keys" when keysDocument is not null => FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(keysDocument)}}}"""),
            "/v1/keys" => FakeService.Json(404, """{"schemaVersion":1,"error":"no_key_replacement"}"""),
            _ => _h.DefaultHandler(r),
        };
        var config = new RemoteConfigService(_h.Api, verifier, new MemoryFiles(), "online/config.json", _h.Time, _h.Log, withKeys ? keys : null);
        return (config, keys, verifier);
    }

    private string SuccessorConfig() => Signed(_successor, SuccessorKid, SignedPurposes.Config, "stable", _h.Time.GetUtcNow().ToUnixTimeSeconds(), ConfigPayload);

    [Fact]
    public async Task ConfigRefresh_FetchesAndAppliesTheReplacement_ThenAcceptsTheSuccessorsConfig()
    {
        var (config, keys, verifier) = Build(SuccessorConfig(), Replacement());

        // Before the refresh the successor is not trusted: this is the update-check-free path (TM-04).
        Assert.False(verifier.Keys.Contains(SuccessorKid));
        Assert.True(await config.RefreshAsync(Ct));

        Assert.Equal(1, keys.AppliedVersion);
        Assert.False(verifier.Keys.Contains(TestIssuer.Kid));
        Assert.True(verifier.Keys.IsAllowedFor(SuccessorKid, SignedPurposes.Config));
        Assert.Single(_h.Service.To("/v1/keys"));
    }

    [Fact]
    public async Task ConfigRefresh_DropsTheFloorThatTheRevokedKeySet_SoAGenuineConfigIsAccepted()
    {
        _versions.RecordHighest(SignedPurposes.Config, "stable", SignedDocumentVerifier.MaxVersion, TestIssuer.Kid);
        var (config, _, _) = Build(SuccessorConfig(), Replacement());

        Assert.True(await config.RefreshAsync(Ct));
        Assert.True(config.Current.IsFresh);
    }

    [Fact]
    public async Task ConfigRefresh_WithoutAKeyReplacementService_DoesNotAskForKeys()
    {
        var (config, _, _) = Build(_h.Issuer.Config(_h.Time.GetUtcNow()), Replacement(), withKeys: false);

        Assert.True(await config.RefreshAsync(Ct));

        Assert.Empty(_h.Service.To("/v1/keys"));
    }

    [Fact]
    public async Task ConfigRefresh_RefusesAReplacementThatTheRecoveryKeyDidNotSign_AndKeepsTheTrustedKeys()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (config, keys, verifier) = Build(_h.Issuer.Config(_h.Time.GetUtcNow()), Replacement(attacker));

        Assert.True(await config.RefreshAsync(Ct));

        Assert.Null(keys.AppliedVersion);
        Assert.True(verifier.Keys.Contains(TestIssuer.Kid));
        Assert.False(verifier.Keys.Contains(SuccessorKid));
        Assert.Contains(_h.Log.Lines, l => l.Contains("Key replacement refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigRefresh_WithNoReplacementPublished_StillRefreshesTheConfig()
    {
        var (config, keys, _) = Build(_h.Issuer.Config(_h.Time.GetUtcNow()), null);

        Assert.True(await config.RefreshAsync(Ct));

        Assert.Null(keys.AppliedVersion);
        Assert.Single(_h.Service.To("/v1/keys"));
    }
}
