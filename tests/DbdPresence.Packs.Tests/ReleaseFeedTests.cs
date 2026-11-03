// Covers: FR-ONL-10 (the signed release feed's payload rules: package id, at most 3 packages, file names, sizes, hashes; no lower version is ever
// preferred), PLAN_V2 §6.2 and NFR-SVC-05 (key replacement: the recovery key revokes and adds keys, never a recovery key), ADR-0012.
using System.Buffers.Text;
using System.Security.Cryptography;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Packs.Tests;

public sealed class ReleaseFeedTests
{
    private static readonly string s_sha = new('c', 64);

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.9", "1.10.0")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]
    [InlineData("0.9.0", "1.0.0-beta.1")]
    public void ReleaseVersion_OrdersBySemVerPrecedence(string lower, string higher)
    {
        var a = ReleaseVersion.Parse(lower);
        var b = ReleaseVersion.Parse(higher);

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.NotEqual(a, b);
        Assert.Equal(0, ReleaseVersion.Parse(lower).CompareTo(a));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-Beta")]
    [InlineData("1.0.0+build")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-01")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0 ")]
    [InlineData("9999999999.0.0")]
    public void ReleaseVersion_RefusesAnythingElse(string text) => Assert.Null(ReleaseVersion.TryParse(text));

    [Fact]
    public void Feed_ParsesAndSortsNewestFirst()
    {
        var payload = Payload(Entry("1.0.0"), Entry("1.2.0", sha: new string('d', 64), notes: "Fixes."), Entry("1.1.0", sha: new string('e', 64)));

        Assert.True(ReleaseFeed.TryParse(payload, "stable", out var feed, out var error), error);

        Assert.Equal(["1.2.0", "1.1.0", "1.0.0"], feed!.Releases.Select(r => r.Version.Text));
        Assert.Equal("DbdPresence-1.2.0-stable-full.nupkg", feed.Newest!.FileName);
        Assert.Equal("Fixes.", feed.Newest.Notes);
    }

    [Fact]
    public void Feed_RoundTripsThroughThePayloadWriter()
    {
        var payload = Payload(Entry("1.2.0", notes: "Quote \" and é"));
        Assert.True(ReleaseFeed.TryParse(payload, "stable", out var feed, out _));

        Assert.True(ReleaseFeed.TryParse(feed!.ToPayloadJson(), "stable", out var again, out var error), error);

        Assert.Equal(feed.Releases.Single(), again!.Releases.Single());
    }

    public static TheoryData<string, string> BadPayloads => new()
    {
        { """{"schemaVersion":2,"packageId":"DbdPresence","releases":[]}""", "unsupported_schema" },
        { """{"schemaVersion":1,"packageId":"Other","releases":[]}""", "wrong_package_id" },
        { """{"schemaVersion":1,"packageId":"DbdPresence","releases":[],"extra":1}""", "unexpected_members" },
        { """{"schemaVersion":1,"packageId":"DbdPresence","releases":{}}""", "bad_release_list" },
        { Payload(Entry("1.0.0"), Entry("1.1.0", sha: new string('d', 64)), Entry("1.2.0", sha: new string('e', 64)), Entry("1.3.0", sha: new string('f', 64))), "bad_release_list" },
        { Payload(Entry("1.0.0"), Entry("1.0.0", sha: new string('d', 64))), "duplicate_version" },
        { Payload(Entry("1.0.0"), Entry("1.1.0")), "duplicate_package" },
        { Payload(Entry("1.0.0", fileName: "DbdPresence-1.0.1-stable-full.nupkg")), "bad_file_name" },
        { Payload(Entry("1.0.0", fileName: "DbdPresence-1.0.0-beta-full.nupkg")), "bad_file_name" },
        { Payload(Entry("1.0.0", fileName: "../DbdPresence-1.0.0-stable-full.nupkg")), "bad_file_name" },
        { Payload(Entry("1.0.0", size: 0)), "bad_size" },
        { Payload(Entry("1.0.0", size: ReleaseFeed.MaxPackageBytes + 1)), "bad_size" },
        { Payload(Entry("1.0.0", sha: new string('C', 64))), "bad_sha256" },
        { Payload(Entry("1.0.0", sha: "abc")), "bad_sha256" },
        { Payload(Entry("1.0.0", notes: new string('n', ReleaseFeed.MaxNotesLength + 1))), "bad_notes" },
        { Payload(Entry("1.0.0-Beta")), "bad_version" },
        { """{"schemaVersion":1,"packageId":"DbdPresence","packageId":"DbdPresence","releases":[]}""", "malformed" },
        { "not json", "malformed" },
    };

    [Theory]
    [MemberData(nameof(BadPayloads))]
    public void Feed_RefusesEveryBrokenRule(string payload, string reason)
    {
        Assert.False(ReleaseFeed.TryParse(payload, "stable", out var feed, out var error));

        Assert.Null(feed);
        Assert.Equal(reason, error);
    }

    [Fact]
    public void KeyReplacement_RevokesAndAddsKeys()
    {
        using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recovery = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var successor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var builtIn = new Dictionary<string, TrustedKey>
        {
            ["release-prod-1"] = new(release.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases]),
            ["recovery-prod-1"] = new(recovery.ExportSubjectPublicKeyInfo(), [SignedPurposes.KeyReplacement]),
        };
        var payload = Replacement(["release-prod-1"], [("release-prod-2", successor, [SignedPurposes.Releases])]);

        Assert.True(KeyReplacement.TryParse(payload, out var replacement, out var error), error);
        var applied = replacement!.Apply(builtIn);

        Assert.Equal(["recovery-prod-1", "release-prod-2"], applied.Effective.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(applied.Skipped);
        Assert.True(new TrustedKeySet(applied.Effective).IsAllowedFor("release-prod-2", SignedPurposes.Releases));
        Assert.Equal(payload, replacement.ToPayloadJson());
    }

    [Fact]
    public void KeyReplacement_NeverRevokesARecoveryKey_AndAppliesEveryOtherEntryOnItsOwn()
    {
        // Security review 2026-10-25, M2: one entry that cannot apply must not drop the others, above all not the revocations.
        using var recovery = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var successor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var builtIn = new Dictionary<string, TrustedKey>
        {
            ["recovery-prod-1"] = new(recovery.ExportSubjectPublicKeyInfo(), [SignedPurposes.KeyReplacement]),
            ["release-prod-1"] = new(release.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases]),
            ["config-prod-1"] = new(other.ExportSubjectPublicKeyInfo(), [SignedPurposes.Config]),
        };
        Assert.True(KeyReplacement.TryParse(
            Replacement(["recovery-prod-1", "release-prod-1"], [("config-prod-1", successor, [SignedPurposes.Config]), ("release-prod-2", successor, [SignedPurposes.Releases])]),
            out var replacement,
            out _));

        var applied = replacement!.Apply(builtIn);

        Assert.Equal(["config-prod-1", "recovery-prod-1", "release-prod-2"], applied.Effective.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(other.ExportSubjectPublicKeyInfo(), applied.Effective["config-prod-1"].Spki);
        Assert.Contains(applied.Skipped, l => l.StartsWith("revoke recovery-prod-1", StringComparison.Ordinal));
        Assert.Contains(applied.Skipped, l => l.StartsWith("add config-prod-1", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyReplacement_ReAddingACompiledInKey_IsANoOp_AndTheRevocationsStillApply()
    {
        // Security review 2026-10-25, M2: before, an identical re-add refused the whole document (kid_in_use), so the revocation was lost.
        using var release = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var successor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var builtIn = new Dictionary<string, TrustedKey>
        {
            ["release-prod-1"] = new(release.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases]),
            ["release-prod-2"] = new(successor.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases]),
        };
        Assert.True(KeyReplacement.TryParse(Replacement(["release-prod-1"], [("release-prod-2", successor, [SignedPurposes.Releases])]), out var replacement, out _));

        var applied = replacement!.Apply(builtIn);

        Assert.Equal(["release-prod-2"], applied.Effective.Keys);
        Assert.Empty(applied.Skipped);
    }

    [Fact]
    public void KeyReplacement_KeepsRevocationsOf_TheEarlierOne()
    {
        Assert.True(KeyReplacement.TryParse("""{"schemaVersion":1,"revoke":["a-1","b-1"],"add":[]}""", out var both, out _));
        Assert.True(KeyReplacement.TryParse("""{"schemaVersion":1,"revoke":["b-1"],"add":[]}""", out var one, out _));

        Assert.True(both!.KeepsRevocationsOf(one!));
        Assert.False(one!.KeepsRevocationsOf(both));
    }

    [Fact]
    public void KeyReplacement_SkipsAKeyThatIsNotP256()
    {
        using var rsa = RSA.Create(2048);
        var payload = $$"""{"schemaVersion":1,"revoke":[],"add":[{"kid":"rsa-1","spki":"{{Base64Url.EncodeToString(rsa.ExportSubjectPublicKeyInfo())}}","purposes":["releases"]}]}""";

        Assert.True(KeyReplacement.TryParse(payload, out var replacement, out _));
        var applied = replacement!.Apply(new Dictionary<string, TrustedKey>());

        Assert.Empty(applied.Effective);
        Assert.Contains(applied.Skipped, l => l.Contains("not a P-256", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyReplacement_RefusesMalformedPayloads()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(KeyReplacement.TryParse(Replacement([], [("recovery-prod-2", other, [SignedPurposes.KeyReplacement])]), out _, out var error));
        Assert.Equal("bad_add", error);
        Assert.False(KeyReplacement.TryParse(Replacement([], [("mixed-1", other, [SignedPurposes.Releases, SignedPurposes.Config])]), out _, out error));
        Assert.Equal("bad_add", error);
        Assert.False(KeyReplacement.TryParse(Replacement(["same-1"], [("same-1", other, [SignedPurposes.Releases])]), out _, out error));
        Assert.Equal("bad_add", error);
        Assert.False(KeyReplacement.TryParse(Replacement([], []), out _, out error));
        Assert.Equal("empty", error);
    }

    [Fact]
    public void FeedAndKeyReplacementSignedByTheService_ParseWithTheSameRules()
    {
        // tests/fixtures/signing/ts-signed: produced by the TypeScript signer, whose payload rules live in service/src/releases/feed.ts.
        static string PayloadOf(string name)
        {
            using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signing", "ts-signed", name + ".json")));
            return fixture.RootElement.GetProperty("expectedPayload").GetRawText();
        }

        Assert.True(ReleaseFeed.TryParse(PayloadOf("ts-releases"), "beta", out var feed, out var error), error);
        Assert.Equal(["1.3.0-beta.2", "1.2.0"], feed!.Releases.Select(r => r.Version.Text));
        Assert.Equal("DbdPresence-1.3.0-beta.2-beta-full.nupkg", feed.Newest!.FileName);
        Assert.True(KeyReplacement.TryParse(PayloadOf("ts-key-replacement"), out var replacement, out error), error);
        Assert.Equal(["release-prod-1"], replacement!.Revoke);
        Assert.Equal("release-prod-2", replacement.Add.Single().Kid);
    }

    [Fact]
    public void KeyReplacementPurpose_HasItsLongLifetime_AndIsKnown()
    {
        Assert.Contains(SignedPurposes.KeyReplacement, SignedPurposes.All);
        Assert.Equal(TimeSpan.FromDays(3650), SignedPurposes.MaxLifetime(SignedPurposes.KeyReplacement));
    }

    private static string Entry(string version, string? sha = null, string? fileName = null, long size = 1234, string? notes = null)
    {
        var notesMember = notes is null ? string.Empty : $",\"notes\":{System.Text.Json.JsonSerializer.Serialize(notes)}";
        return $$"""{"version":"{{version}}","fileName":"{{fileName ?? $"DbdPresence-{version}-stable-full.nupkg"}}","size":{{size}},"sha256":"{{sha ?? s_sha}}"{{notesMember}}}""";
    }

    private static string Payload(params string[] entries) => $$"""{"schemaVersion":1,"packageId":"DbdPresence","releases":[{{string.Join(",", entries)}}]}""";

    private static string Replacement(string[] revoke, (string Kid, ECDsa Key, string[] Purposes)[] add) =>
        new KeyReplacementPayload(revoke, add).Json;

    private sealed record KeyReplacementPayload(string[] Revoke, (string Kid, ECDsa Key, string[] Purposes)[] Add)
    {
        public string Json =>
            "{\"schemaVersion\":1,\"revoke\":[" + string.Join(",", Revoke.Select(k => $"\"{k}\"")) + "],\"add\":[" +
            string.Join(",", Add.Select(a => $"{{\"kid\":\"{a.Kid}\",\"spki\":\"{Base64Url.EncodeToString(a.Key.ExportSubjectPublicKeyInfo())}\",\"purposes\":[{string.Join(",", a.Purposes.Select(p => $"\"{p}\""))}]}}")) +
            "]}";
    }
}
