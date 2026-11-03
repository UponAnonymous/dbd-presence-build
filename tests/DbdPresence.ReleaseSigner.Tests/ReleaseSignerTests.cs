// Covers: V2-S6 gate V6.3 (the offline release-signing tool and the recovery-key flow are tested), NFR-SVC-05 (keys made offline, private keys only
// encrypted, never inside a repository), FR-ONL-10 (a feed for the newest three packages, no lower version, re-signing), PLAN_V2 §6.2 (cumulative
// key replacements signed by the recovery key), ADR-0012 (every document verifies with the app's own verifier). Keys here are made per test run and
// thrown away; no real key exists in the repository.
using System.IO;
using System.Text;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.ReleaseSigner.Tests;

public sealed class ReleaseSignerTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private static readonly DateTimeOffset s_now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdp-signer-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(s_now);
    private readonly StringWriter _out = new();
    private readonly ScriptedPrompt _prompt = new();

    public ReleaseSignerTests() => Directory.CreateDirectory(_root);

    private string Keys => Path.Combine(_root, "keys");

    private string Packages => Path.Combine(_root, "vpk");

    public void Dispose()
    {
        _out.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void KeyGen_WritesAnEncryptedPrivateKey_AndThePublicEntry()
    {
        var entry = Make(OfflineKeyKind.Release, "release-prod-1");

        var pem = File.ReadAllText(Path.Combine(Keys, "release-prod-1.private.pem"));
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", pem, StringComparison.Ordinal);
        Assert.Equal([SignedPurposes.Releases], entry.Purposes);
        var stored = PublicKeyEntry.Parse(File.ReadAllText(Path.Combine(Keys, "release-prod-1.public.json")));
        Assert.Equal(entry.Spki, stored.Spki);
        Assert.Contains(entry.Fingerprint, _out.ToString(), StringComparison.Ordinal);
        Assert.Matches("^([0-9a-f]{4} ){15}[0-9a-f]{4}$", entry.Fingerprint);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => ReleaseKeys.ImportEncrypted(pem, "wrong passphrase!"));
        using var loaded = ReleaseKeys.ImportEncrypted(pem, Passphrase);
        Assert.Equal(entry.Spki, loaded.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void KeyGen_RefusesAShortPassphrase_AMismatch_AnExistingKey_AndARepositoryFolder()
    {
        _prompt.Answers.Enqueue("short");
        Assert.Throws<SignerException>(() => Commands().KeyGen(OfflineKeyKind.Release, "release-prod-1", Keys));

        _prompt.Answers.Enqueue(Passphrase);
        _prompt.Answers.Enqueue(Passphrase + "x");
        Assert.Throws<SignerException>(() => Commands().KeyGen(OfflineKeyKind.Release, "release-prod-1", Keys));
        Assert.False(File.Exists(Path.Combine(Keys, "release-prod-1.private.pem")));

        Make(OfflineKeyKind.Release, "release-prod-1");
        var again = Assert.Throws<SignerException>(() => Make(OfflineKeyKind.Release, "release-prod-1"));
        Assert.Contains("release-prod-2", again.Message, StringComparison.Ordinal);

        var repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Assert.Throws<InvalidOperationException>(() => Commands().KeyGen(OfflineKeyKind.Release, "release-prod-9", Path.Combine(repository, "keys")));
        Assert.False(Directory.Exists(Path.Combine(repository, "keys")));
    }

    [Fact]
    public void SignFeed_ListsTheNewestThreePackages_AndTheAppVerifiesIt()
    {
        var release = Make(OfflineKeyKind.Release, "release-prod-1");
        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0-beta.1", "0.9.0" })
        {
            WritePackage(Packages, version);
        }

        WritePackage(Packages, "1.4.0", channel: "beta");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Fixes the tray icon.\n");

        var document = SignFeed("releases-stable.json", notes: Path.Combine(_root, "notes.txt"));

        var verified = Verify(release, document, SignedPurposes.Releases, "stable");
        Assert.True(ReleaseFeed.TryParse(verified.PayloadJson, "stable", out var feed, out var error), error);
        Assert.Equal(["1.3.0-beta.1", "1.2.0", "1.1.0"], feed!.Releases.Select(r => r.Version.Text));
        Assert.Equal("Fixes the tray icon.", feed.Newest!.Notes);
        Assert.All(feed.Releases.Skip(1), r => Assert.Null(r.Notes));
        Assert.Equal(PackageReader.Read(Path.Combine(Packages, "DbdPresence-1.2.0-stable-full.nupkg"), "stable").Sha256, feed.Releases[1].Sha256);
        Assert.Equal(s_now.ToUnixTimeSeconds(), verified.Version);
        Assert.Equal(s_now.AddDays(30), verified.ExpiresAt);
    }

    [Fact]
    public void SignFeed_RefusesALowerVersionThanThePreviousFeed_AndNeedsAHigherFeedVersion()
    {
        Make(OfflineKeyKind.Release, "release-prod-1");
        WritePackage(Packages, "1.2.0");
        SignFeed("first.json");

        var older = Path.Combine(_root, "older");
        WritePackage(older, "1.1.0");
        var refused = Assert.Throws<SignerException>(() => SignFeed("second.json", packages: older, previous: Path.Combine(_root, "first.json")));
        Assert.Contains("roll back", refused.Message, StringComparison.Ordinal);

        Assert.Throws<SignerException>(() => SignFeed("third.json", previous: Path.Combine(_root, "first.json"), version: s_now.ToUnixTimeSeconds()));
        var next = SignFeed("fourth.json", previous: Path.Combine(_root, "first.json"));
        Assert.Equal(s_now.ToUnixTimeSeconds() + 1, DocumentSigner.ReadEnvelopeUnverified(next).Version);
    }

    [Fact]
    public void SignFeed_RefusesAPackageWhoseNameIdOrVersionDisagree()
    {
        Make(OfflineKeyKind.Release, "release-prod-1");
        WritePackage(Packages, "1.2.0", fileName: "DbdPresence-1.3.0-stable-full.nupkg");

        var refused = Assert.Throws<SignerException>(() => SignFeed("feed.json"));

        Assert.Contains("should be named DbdPresence-1.2.0-stable-full.nupkg", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "feed.json")));
    }

    [Fact]
    public void SignFeed_WithTheWrongPassphrase_SignsNothing()
    {
        Make(OfflineKeyKind.Release, "release-prod-1");
        WritePackage(Packages, "1.2.0");
        _prompt.Answers.Enqueue("not the passphrase");

        Assert.Throws<SignerException>(() => Commands().SignFeed(KeyPath("release-prod-1"), null, "stable", "production", Packages, Path.Combine(_root, "feed.json"), null, null, null, null));

        Assert.False(File.Exists(Path.Combine(_root, "feed.json")));
    }

    [Fact]
    public void Resign_KeepsThePayload_WithNewTimesAndAHigherVersion()
    {
        var release = Make(OfflineKeyKind.Release, "release-prod-1");
        WritePackage(Packages, "1.2.0");
        var first = DocumentSigner.ReadEnvelopeUnverified(SignFeed("feed.json"));

        _time.Advance(TimeSpan.FromDays(25));
        _prompt.Answers.Enqueue(Passphrase);
        var second = Commands().Resign(KeyPath("release-prod-1"), null, Path.Combine(_root, "feed.json"), Path.Combine(_root, "feed-2.json"), null);

        var verified = Verify(release, second, SignedPurposes.Releases, "stable");
        Assert.Equal(first.PayloadJson, verified.PayloadJson);
        Assert.True(verified.Version > first.Version);
        Assert.Equal(_time.GetUtcNow().AddDays(30), verified.ExpiresAt);
    }

    [Fact]
    public void KeyReplacement_IsSignedByTheRecoveryKey_AndIsCumulative()
    {
        var recovery = Make(OfflineKeyKind.Recovery, "recovery-prod-1");
        Make(OfflineKeyKind.Release, "release-prod-2");
        Make(OfflineKeyKind.Release, "release-prod-3");

        _prompt.Answers.Enqueue(Passphrase);
        Commands().SignKeyReplacement(KeyPath("recovery-prod-1"), null, ["release-prod-1"], [PublicPath("release-prod-2")], null, Path.Combine(_root, "kr-1.json"), first: true);
        _time.Advance(TimeSpan.FromDays(1));
        _prompt.Answers.Enqueue(Passphrase);
        var second = Commands().SignKeyReplacement(KeyPath("recovery-prod-1"), null, ["release-prod-2"], [PublicPath("release-prod-3")], Path.Combine(_root, "kr-1.json"), Path.Combine(_root, "kr-2.json"));

        var verified = Verify(recovery, second, SignedPurposes.KeyReplacement, KeyReplacement.Channel);
        Assert.True(KeyReplacement.TryParse(verified.PayloadJson, out var replacement, out var error), error);
        Assert.Equal(["release-prod-1", "release-prod-2"], replacement!.Revoke);
        Assert.Equal(["release-prod-3"], replacement.Add.Select(k => k.Kid));
        Assert.Equal(verified.IssuedAt.AddDays(3650), verified.ExpiresAt);
    }

    [Fact]
    public void KeyReplacement_CannotAddARecoveryKey()
    {
        Make(OfflineKeyKind.Recovery, "recovery-prod-1");
        Make(OfflineKeyKind.Recovery, "recovery-prod-2");

        Assert.Throws<SignerException>(() => Commands().SignKeyReplacement(KeyPath("recovery-prod-1"), null, [], [PublicPath("recovery-prod-2")], null, Path.Combine(_root, "kr.json"), first: true));
    }

    [Fact]
    public void KeyReplacement_NeedsThePreviousOne_UnlessItIsTheFirst()
    {
        // Security review 2026-10-25, M1: a replacement signed without the earlier one would trust its revoked keys again.
        Make(OfflineKeyKind.Recovery, "recovery-prod-1");
        Make(OfflineKeyKind.Release, "release-prod-2");

        var refused = Assert.Throws<SignerException>(() => Commands().SignKeyReplacement(KeyPath("recovery-prod-1"), null, ["release-prod-1"], [PublicPath("release-prod-2")], null, Path.Combine(_root, "kr.json")));
        Assert.Contains("--previous", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "kr.json")));

        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, Cli.Run(["sign-key-replacement", "--key", KeyPath("recovery-prod-1"), "--revoke", "release-prod-1", "--out", Path.Combine(_root, "kr.json")], output, error, _prompt, _time));
        _prompt.Answers.Enqueue(Passphrase);
        Assert.Equal(0, Cli.Run(["sign-key-replacement", "--key", KeyPath("recovery-prod-1"), "--revoke", "release-prod-1", "--first", "--out", Path.Combine(_root, "kr.json")], output, error, _prompt, _time));
    }

    [Theory]
    [InlineData("recovery-prod-1")]
    [InlineData("recovery-prod-2")]
    public void KeyReplacement_RefusesToRevokeARecoveryKey_BeforeAskingForAPassphrase_AndWritesNothing(string revoked)
    {
        Make(OfflineKeyKind.Recovery, "recovery-prod-1");
        var output = Path.Combine(_root, "kr.json");

        var ex = Assert.Throws<SignerException>(() =>
            Commands().SignKeyReplacement(KeyPath("recovery-prod-1"), null, ["release-prod-1", revoked], [], null, output, first: true));

        Assert.Contains($"{revoked} is a recovery key", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Empty(_prompt.Answers);
    }

    [Fact]
    public void KeyReplacement_RefusesToRevokeItsOwnSigningKey_WhateverItIsCalled()
    {
        Make(OfflineKeyKind.Recovery, "root-of-trust-1");

        var ex = Assert.Throws<SignerException>(() =>
            Commands().SignKeyReplacement(KeyPath("root-of-trust-1"), null, ["root-of-trust-1"], [], null, Path.Combine(_root, "kr.json"), first: true));

        Assert.Contains("root-of-trust-1 is a recovery key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyFiles_AreWrittenWithCreateNew_NeverOverAnExistingFile()
    {
        var path = Path.Combine(_root, "exists.private.pem");
        File.WriteAllText(path, "keep me");

        Assert.Throws<IOException>(() => DbdPresence.ReleaseSigner.Commands.WriteNew(path, "new", System.Text.Encoding.ASCII));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void Versions_AboveTwoToTheFiftyThirdMinusOne_AreRefused()
    {
        // Security review 2026-10-25, L1: the service holds versions as JavaScript numbers.
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var payload = new ReleaseFeed(ReleaseFeed.ExpectedPackageId, "stable", []).ToPayloadJson();

        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentSigner.Sign(key, "release-prod-1", SignedPurposes.Releases, "stable", SignedDocumentVerifier.MaxVersion + 1, s_now, s_now.AddDays(1), payload));

        // Threat-model review 2026-10-25, TM-01: 2^53 - 1 is also over the bound above the signing time, and the signer's own check refuses it.
        var tooFar = Assert.Throws<InvalidOperationException>(() => DocumentSigner.Sign(key, "release-prod-1", SignedPurposes.Releases, "stable", SignedDocumentVerifier.MaxVersion, s_now, s_now.AddDays(1), payload));
        Assert.Contains("VersionTooFarAhead", tooFar.Message, StringComparison.Ordinal);
        Assert.NotEmpty(DocumentSigner.Sign(key, "release-prod-1", SignedPurposes.Releases, "stable", s_now.ToUnixTimeSeconds() + SignedDocumentVerifier.MaxVersionAheadOfIssue, s_now, s_now.AddDays(1), payload));
    }

    [Fact]
    public void Verify_ChecksADocumentAgainstAKeyListAsTheAppDoes()
    {
        var release = Make(OfflineKeyKind.Release, "release-prod-1");
        WritePackage(Packages, "1.2.0");
        SignFeed("feed.json");
        var trust = Path.Combine(_root, "trust.json");
        File.WriteAllText(trust, $$"""{"staging":[],"production":[{"kid":"release-prod-1","purposes":["releases"],"spki":"{{release.SpkiText}}"}]}""");

        Assert.True(Commands().Verify(Path.Combine(_root, "feed.json"), trust, "production", Packages));
        Assert.False(Commands().Verify(Path.Combine(_root, "feed.json"), trust, "staging", null));
        Assert.Contains("UnknownKey", _out.ToString(), StringComparison.Ordinal);

        File.WriteAllBytes(Path.Combine(Packages, "DbdPresence-1.2.0-stable-full.nupkg"), [1, 2, 3]);
        Assert.False(Commands().Verify(Path.Combine(_root, "feed.json"), trust, "production", Packages));
    }

    [Fact]
    public void Cli_ReportsUsageAndRefusals_WithExitCodes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, Cli.Run([], output, error, _prompt, _time));
        Assert.Equal(2, Cli.Run(["keygen", "release"], output, error, _prompt, _time));
        Assert.Equal(2, Cli.Run(["unknown"], output, error, _prompt, _time));
        _prompt.Answers.Enqueue("short");
        Assert.Equal(1, Cli.Run(["keygen", "release", "--kid", "release-prod-1", "--out", Keys], output, error, _prompt, _time));
        Assert.Contains("Stopped:", error.ToString(), StringComparison.Ordinal);
        _prompt.Answers.Enqueue(Passphrase);
        _prompt.Answers.Enqueue(Passphrase);
        Assert.Equal(0, Cli.Run(["keygen", "recovery", "--kid", "recovery-prod-1", "--out", Keys], output, error, _prompt, _time));
    }

    [Fact]
    public void CompiledInKeyList_HasNoRealReleaseOrRecoveryKeyYet_OnlyReservedEntries()
    {
        // The owner makes the real keys offline (docs/owner/release-keys.md); until then the entries are reserved and trust nothing.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TrustedServiceKeys.json"));
        using var json = System.Text.Json.JsonDocument.Parse(text);
        foreach (var set in new[] { "staging", "production" })
        {
            foreach (var key in json.RootElement.GetProperty(set).EnumerateArray())
            {
                var purposes = key.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()).ToArray();
                if (purposes.Contains(SignedPurposes.Releases) || purposes.Contains(SignedPurposes.KeyReplacement))
                {
                    Assert.True(key.TryGetProperty("awaitingKey", out var awaiting) && awaiting.GetBoolean() || key.TryGetProperty("spki", out _), key.ToString());
                    Assert.Single(purposes);
                }
            }
        }
    }

    private Commands Commands() => new(_out, _prompt, _time);

    private PublicKeyEntry Make(OfflineKeyKind kind, string kid)
    {
        _prompt.Answers.Enqueue(Passphrase);
        _prompt.Answers.Enqueue(Passphrase);
        return Commands().KeyGen(kind, kid, Keys);
    }

    private string KeyPath(string kid) => Path.Combine(Keys, kid + ".private.pem");

    private string PublicPath(string kid) => Path.Combine(Keys, kid + ".public.json");

    private string SignFeed(string output, string? packages = null, string? notes = null, string? previous = null, long? version = null, string set = "production")
    {
        _prompt.Answers.Enqueue(Passphrase);
        return Commands().SignFeed(KeyPath("release-prod-1"), null, "stable", set, packages ?? Packages, Path.Combine(_root, output), notes, previous, version, null);
    }

    /// <summary>A test package; a production app build unless <paramref name="online"/> gives another DbdPresence.Online.dll.</summary>
    private static string WritePackage(string directory, string version, string channel = "stable", string? fileName = null, byte[]? online = null) =>
        TestPackages.Write(directory, version, channel, fileName: fileName, onlineAssembly: online ?? OnlineAssemblies.Production);

    private SignedEnvelope Verify(PublicKeyEntry key, string document, string purpose, string channel)
    {
        var verifier = new SignedDocumentVerifier(new TrustedKeySet(new Dictionary<string, TrustedKey> { [key.Kid] = new(key.Spki, key.Purposes) }), new InMemoryVersionStore(), _time);
        var result = verifier.Verify(Encoding.UTF8.GetBytes(document), purpose, channel);
        Assert.Equal(VerificationOutcome.Valid, result.Outcome);
        return result.Envelope!;
    }

    private sealed class ScriptedPrompt : IPassphrasePrompt
    {
        public Queue<string> Answers { get; } = new();

        public string Read(string prompt) => Answers.Count > 0 ? Answers.Dequeue() : throw new InvalidOperationException("No scripted answer for: " + prompt);
    }
}
