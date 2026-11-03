// Covers: NFR-SVC-04 (verify before parse, kid/purpose checks, rollback, freeze, size and depth caps), ADR-0012.
using System.Text;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class SignedDocumentVerifierTests : IDisposable
{
    private const string EmptyPayload = "{}";

    private readonly TestSigner _signer = new();
    private readonly InMemoryVersionStore _versions = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => _signer.Dispose();

    private SignedDocumentVerifier NewVerifier() => new(_signer.Trusted, _versions, _time);

    private VerificationResult Verify(byte[] document, string purpose = SignedPurposes.PackIndex, string channel = "stable") => NewVerifier().Verify(document, purpose, channel);

    [Fact]
    public void Valid_ReturnsEnvelopeFieldsAndRawPayload()
    {
        var result = Verify(_signer.Sign(TestSigner.Envelope("""{"a":[1,2]}""", version: 7)));

        Assert.True(result.IsValid);
        Assert.Equal(7, result.Envelope!.Version);
        Assert.Equal("stable", result.Envelope.Channel);
        Assert.Equal(TestSigner.Kid, result.Envelope.Kid);
        Assert.Equal("""{"a":[1,2]}""", result.Envelope.PayloadJson);
    }

    [Fact]
    public void Expiry_UsesTheInjectedClockAtTheBoundary()
    {
        var document = _signer.Sign(TestSigner.Envelope(EmptyPayload, expiresAt: "2026-01-31T00:00:00.000Z"));

        _time.SetUtcNow(new DateTimeOffset(2026, 1, 30, 23, 59, 59, TimeSpan.Zero));
        Assert.Equal(VerificationOutcome.Valid, Verify(document).Outcome);

        _time.SetUtcNow(new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(VerificationOutcome.Expired, Verify(document).Outcome);
    }

    [Fact]
    public void VerifyStored_ReadsAnExpiredOrRolledBackCopy_ButStillChecksSignaturePurposeAndChannel()
    {
        var document = _signer.Sign(TestSigner.Envelope("""{"a":1}""", purpose: SignedPurposes.Config, version: 3, expiresAt: "2026-01-10T00:00:00.000Z"));
        NewVerifier().CommitVersion(SignedPurposes.Config, "stable", 9, null);

        var fresh = NewVerifier().Verify(document, SignedPurposes.Config, "stable");
        var stored = NewVerifier().VerifyStored(document, SignedPurposes.Config, "stable");

        Assert.Equal(VerificationOutcome.Expired, fresh.Outcome);
        Assert.True(stored.IsValid);
        Assert.Equal("""{"a":1}""", stored.Envelope!.PayloadJson);
        Assert.Equal(9, _versions.GetHighest(SignedPurposes.Config, "stable")); // nothing recorded
        Assert.Equal(VerificationOutcome.WrongPurpose, NewVerifier().VerifyStored(document, SignedPurposes.Licence, "stable").Outcome);
        Assert.Equal(VerificationOutcome.WrongChannel, NewVerifier().VerifyStored(document, SignedPurposes.Config, "beta").Outcome);
        var tampered = (byte[])document.Clone();
        tampered[^20] ^= 1;
        Assert.False(NewVerifier().VerifyStored(tampered, SignedPurposes.Config, "stable").IsValid);
    }

    [Fact]
    public void RollbackProtection_RejectsLowerAcceptsEqualAndHigher()
    {
        Assert.Equal(VerificationOutcome.Valid, Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 5))).Outcome);
        NewVerifier().CommitVersion(SignedPurposes.PackIndex, "stable", 5, null);

        Assert.Equal(VerificationOutcome.RolledBack, Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 4))).Outcome);
        Assert.Equal(VerificationOutcome.Valid, Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 5))).Outcome);
        Assert.Equal(VerificationOutcome.Valid, Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 6))).Outcome);
        NewVerifier().CommitVersion(SignedPurposes.PackIndex, "stable", 6, null);
        Assert.Equal(6, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void Verify_NeverRecordsTheVersion_OnlyCommitVersionDoes()
    {
        // ADR-0012: verify and commit are split; a valid document alone must not ratchet the floor.
        Assert.True(Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 9))).IsValid);
        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "stable"));

        NewVerifier().CommitVersion(SignedPurposes.PackIndex, "stable", 9, null);

        Assert.Equal(9, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void Rejected_Documents_DoNotAdvanceTheStoredVersion()
    {
        Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 9, expiresAt: "2026-01-02T00:00:00.000Z")));
        Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 9, purpose: "config", expiresAt: "2026-01-10T00:00:00.000Z")));

        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.Null(_versions.GetHighest("config", "stable"));
    }

    [Fact]
    public void Versions_AreTrackedPerPurposeAndChannel()
    {
        _versions.RecordHighest(SignedPurposes.Config, "stable", 50, null);

        Assert.Equal(VerificationOutcome.Valid, Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, version: 1))).Outcome);
    }

    [Fact]
    public void SignatureIsCheckedBeforeTheEnvelopeIsParsed()
    {
        // Garbage bytes with a wrong signature: BadSignature (never reached parsing), not Malformed.
        var document = TestSigner.Document(TestSigner.Kid, Encoding.UTF8.GetBytes("not json"), new byte[64]);

        Assert.Equal(VerificationOutcome.BadSignature, Verify(document).Outcome);
    }

    [Fact]
    public void ValidlySignedGarbage_IsMalformed()
    {
        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.Sign("not json")).Outcome);
        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.Sign("[1,2,3]")).Outcome);
    }

    [Theory]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"stable","version":1,"issuedAt":"2026-01-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z"}""")]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"stable","version":"1","issuedAt":"2026-01-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"stable","version":-1,"issuedAt":"2026-01-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"stable","version":1.5,"issuedAt":"2026-01-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"stable","version":1,"issuedAt":"yesterday","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"stable","version":1,"issuedAt":"2026-03-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    [InlineData("""{"kid":"unit-key","purpose":"pack-index","channel":"","version":1,"issuedAt":"2026-01-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    [InlineData("""{"kid":"unit-key","kid":"unit-key","purpose":"pack-index","channel":"stable","version":1,"issuedAt":"2026-01-01T00:00:00.000Z","expiresAt":"2026-01-31T00:00:00.000Z","payload":{}}""")]
    public void BadEnvelopeShapes_AreMalformed(string envelope)
    {
        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.Sign(envelope)).Outcome);
    }

    [Fact]
    public void EnvelopeKidMustMatchTheOuterKid()
    {
        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload, kid: "someone-else"))).Outcome);
    }

    [Fact]
    public void WrongPurpose_IsReported()
    {
        var document = _signer.Sign(TestSigner.Envelope(EmptyPayload, purpose: SignedPurposes.Config, issuedAt: "2026-01-10T00:00:00.000Z", expiresAt: "2026-01-20T00:00:00.000Z"));

        Assert.Equal(VerificationOutcome.WrongPurpose, Verify(document).Outcome);
        Assert.Equal(VerificationOutcome.Valid, Verify(document, SignedPurposes.Config).Outcome);
    }

    [Fact]
    public void DocumentOfAnotherChannel_IsRejected_AndLeavesThatChannelsVersionAlone()
    {
        // A validly signed stable document must not stand in for the beta channel (whose history may be lower).
        var document = _signer.Sign(TestSigner.Envelope(EmptyPayload, version: 9));

        Assert.Equal(VerificationOutcome.WrongChannel, Verify(document, channel: "beta").Outcome);
        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "beta"));
        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void UnknownKid_IsReportedBeforeAnySignatureWork()
    {
        var document = TestSigner.Document("not-trusted", Encoding.UTF8.GetBytes("x"), new byte[64]);

        Assert.Equal(VerificationOutcome.UnknownKey, Verify(document).Outcome);
    }

    [Fact]
    public void SignatureFromAnotherKey_IsBadSignature()
    {
        using var other = new TestSigner();

        Assert.Equal(VerificationOutcome.BadSignature, Verify(other.Sign(TestSigner.Envelope(EmptyPayload))).Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(65)]
    public void WrongLengthSignature_IsBadSignature(int length)
    {
        var document = TestSigner.Document(TestSigner.Kid, Encoding.UTF8.GetBytes(TestSigner.Envelope(EmptyPayload)), new byte[length]);

        Assert.Equal(VerificationOutcome.BadSignature, Verify(document).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"kid":"unit-key"}""")]
    [InlineData("""{"kid":"unit-key","signed":"AAAA","sig":"not base64url!"}""")]
    [InlineData("""{"kid":"unit-key","signed":"AAAA=","sig":"AAAA"}""")]
    [InlineData("""{"kid":1,"signed":"AAAA","sig":"AAAA"}""")]
    [InlineData("""{"kid":"unit-key","kid":"unit-key","signed":"AAAA","sig":"AAAA"}""")]
    public void MalformedOuterDocuments_NeverThrow(string text)
    {
        Assert.Equal(VerificationOutcome.Malformed, Verify(Encoding.UTF8.GetBytes(text)).Outcome);
    }

    [Fact]
    public void EmptyInput_IsMalformed()
    {
        Assert.Equal(VerificationOutcome.Malformed, Verify([]).Outcome);
    }

    [Fact]
    public void OversizedInput_IsMalformed()
    {
        var big = new byte[SignedDocumentVerifier.MaxDocumentBytes + 1];
        Array.Fill(big, (byte)' ');

        Assert.Equal(VerificationOutcome.Malformed, Verify(big).Outcome);
    }

    [Fact]
    public void InputAtTheSizeCap_IsStillParsed()
    {
        var payload = "\"" + new string('a', 700_000) + "\"";
        var document = _signer.Sign(TestSigner.Envelope(payload));

        Assert.True(document.Length < SignedDocumentVerifier.MaxDocumentBytes);
        Assert.True(Verify(document).IsValid);
    }

    [Fact]
    public void DeeplyNestedEnvelope_IsMalformedNotAStackOverflow()
    {
        var payload = new string('[', 200) + new string(']', 200);

        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.Sign(TestSigner.Envelope(payload))).Outcome);
    }

    [Fact]
    public void DeeplyNestedOuterDocument_IsMalformed()
    {
        var text = string.Concat(Enumerable.Repeat("{\"a\":", 50)) + "1" + new string('}', 50);

        Assert.Equal(VerificationOutcome.Malformed, Verify(Encoding.UTF8.GetBytes(text)).Outcome);
    }

    [Fact]
    public void TrustedKeySet_RejectsNonP256AndGarbageKeys()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() => new TrustedKeySet(new Dictionary<string, TrustedKey> { ["k"] = new(p384.ExportSubjectPublicKeyInfo(), ["config"]) }));
        Assert.Throws<ArgumentException>(() => new TrustedKeySet(new Dictionary<string, TrustedKey> { ["k"] = new([1, 2, 3], ["config"]) }));
        Assert.Throws<ArgumentException>(() => new TrustedKeySet(new Dictionary<string, TrustedKey> { [""] = new([1], ["config"]) }));
    }

    [Fact]
    public void EmptyTrustedSet_TrustsNothing()
    {
        var verifier = new SignedDocumentVerifier(new TrustedKeySet(new Dictionary<string, TrustedKey>()), _versions, _time);

        Assert.Equal(VerificationOutcome.UnknownKey, verifier.Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload)), SignedPurposes.PackIndex, "stable").Outcome);
    }

    [Fact]
    public void KeyTrustedForOtherPurposesOnly_IsRejectedBeforeAnySignatureWork()
    {
        // The signature below is garbage on purpose: the scope check comes first and does not need it.
        var verifier = new SignedDocumentVerifier(_signer.TrustedFor([SignedPurposes.Config]), _versions, _time);
        var document = TestSigner.Document(TestSigner.Kid, Encoding.UTF8.GetBytes("x"), new byte[64]);

        Assert.Equal(VerificationOutcome.KeyNotAllowedForPurpose, verifier.Verify(document, SignedPurposes.PackIndex, "stable").Outcome);
    }

    [Fact]
    public void KeyScope_AllowsItsOwnPurposeAndNoOther()
    {
        var verifier = new SignedDocumentVerifier(_signer.TrustedFor([SignedPurposes.Config]), _versions, _time);
        var config = _signer.Sign(TestSigner.Envelope(EmptyPayload, purpose: SignedPurposes.Config, issuedAt: "2026-01-10T00:00:00.000Z", expiresAt: "2026-01-20T00:00:00.000Z"));
        var packIndex = _signer.Sign(TestSigner.Envelope(EmptyPayload));

        Assert.True(verifier.Verify(config, SignedPurposes.Config, "stable").IsValid);
        Assert.Equal(VerificationOutcome.KeyNotAllowedForPurpose, verifier.Verify(packIndex, SignedPurposes.PackIndex, "stable").Outcome);
        Assert.Equal(VerificationOutcome.KeyNotAllowedForPurpose, verifier.Verify(packIndex, SignedPurposes.Releases, "stable").Outcome);
    }

    [Fact]
    public void TrustedKey_WithoutPurposes_IsAConfigurationError()
    {
        Assert.Throws<ArgumentException>(() => _signer.TrustedFor([]));
        Assert.Throws<ArgumentException>(() => _signer.TrustedFor([""]));
    }

    [Fact]
    public void UnknownExpectedPurpose_IsACallerError()
    {
        Assert.Throws<ArgumentException>(() => Verify(_signer.Sign(TestSigner.Envelope(EmptyPayload)), "bogus"));
    }

    [Fact]
    public void InvalidUtf8InsideTheOuterKid_IsMalformedNotAnException()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"kid":"unit-key","signed":"AAAA","sig":"AAAA"}""");
        var at = Array.IndexOf(bytes, (byte)'u');
        bytes[at] = 0xFF;

        Assert.Equal(VerificationOutcome.Malformed, Verify(bytes).Outcome);
    }

    [Fact]
    public void InvalidUtf8InsideTheOuterSignedMember_IsMalformedNotAnException()
    {
        var prefix = Encoding.UTF8.GetBytes("""{"kid":"unit-key","signed":"A""");
        var suffix = Encoding.UTF8.GetBytes("""AAA","sig":"AAAA"}""");
        byte[] bytes = [.. prefix, 0xC3, 0x28, .. suffix];

        Assert.Equal(VerificationOutcome.Malformed, Verify(bytes).Outcome);
    }

    [Fact]
    public void InvalidUtf8InsideASignedEnvelopeString_IsMalformedNotAnException()
    {
        var text = TestSigner.Envelope(EmptyPayload).Replace("\"stable\"", "\"stab#le\"", StringComparison.Ordinal);
        var envelope = Encoding.UTF8.GetBytes(text);
        envelope[Array.IndexOf(envelope, (byte)'#')] = 0xFF;

        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.SignBytes(envelope)).Outcome);
    }

    [Fact]
    public void InvalidUtf8InsideTheEnvelopeKid_IsMalformedNotAnException()
    {
        var envelope = Encoding.UTF8.GetBytes(TestSigner.Envelope(EmptyPayload, kid: "unit-key"));
        // Overwrite the kid value inside the signed envelope (the first "unit-key") with an invalid sequence.
        var at = Encoding.UTF8.GetString(envelope).IndexOf("unit-key", StringComparison.Ordinal);
        envelope[at] = 0xC0;
        envelope[at + 1] = 0xAF;

        Assert.Equal(VerificationOutcome.Malformed, Verify(_signer.SignBytes(envelope)).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(200)]
    public void TrustedKeySet_VerifySignature_ReturnsFalseForAnyJunkSignature(int length)
    {
        var keys = _signer.Trusted;
        var junk = new byte[length];
        Array.Fill(junk, (byte)0xFF);

        Assert.False(keys.VerifySignature(TestSigner.Kid, "data"u8, junk));
    }

    [Theory]
    [InlineData(SignedPurposes.Config, 14)]
    [InlineData(SignedPurposes.PackIndex, 30)]
    [InlineData(SignedPurposes.Releases, 30)]
    [InlineData(SignedPurposes.Licence, 7)]
    public void Lifetime_IsCappedPerPurpose_AtTheBoundary(string purpose, int days)
    {
        var issued = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        // A licence's version is its signing time in seconds (TM-01).
        var version = purpose == SignedPurposes.Licence ? issued.ToUnixTimeSeconds() : 1;
        var atCap = _signer.Sign(TestSigner.Envelope(EmptyPayload, purpose: purpose, version: version, issuedAt: Iso(issued), expiresAt: Iso(issued.AddDays(days))));
        var overCap = _signer.Sign(TestSigner.Envelope(EmptyPayload, purpose: purpose, version: version, issuedAt: Iso(issued), expiresAt: Iso(issued.AddDays(days).AddSeconds(1))));

        Assert.Equal(VerificationOutcome.Valid, Verify(atCap, purpose).Outcome);
        Assert.Equal(VerificationOutcome.LifetimeTooLong, Verify(overCap, purpose).Outcome);
    }

    [Fact]
    public void IssuedAt_MayBeUpToFiveMinutesAhead_NotMore()
    {
        var now = _time.GetUtcNow();
        var fine = _signer.Sign(TestSigner.Envelope(EmptyPayload, issuedAt: Iso(now.AddMinutes(5)), expiresAt: Iso(now.AddDays(10))));
        var future = _signer.Sign(TestSigner.Envelope(EmptyPayload, issuedAt: Iso(now.AddMinutes(5).AddSeconds(1)), expiresAt: Iso(now.AddDays(10))));

        Assert.Equal(VerificationOutcome.Valid, Verify(fine).Outcome);
        Assert.Equal(VerificationOutcome.Malformed, Verify(future).Outcome);
    }

    [Fact]
    public void SeedFromCachedDocument_RaisesTheFloor_EvenForAnExpiredDocument_ButNeverLowersIt()
    {
        var old = _signer.Sign(TestSigner.Envelope(EmptyPayload, version: 8, expiresAt: "2026-01-02T00:00:00.000Z"));
        var verifier = NewVerifier();

        Assert.Equal(VerificationOutcome.Valid, verifier.SeedFromCachedDocument(old, SignedPurposes.PackIndex, "stable"));
        Assert.Equal(8, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));

        var lower = _signer.Sign(TestSigner.Envelope(EmptyPayload, version: 3));
        verifier.SeedFromCachedDocument(lower, SignedPurposes.PackIndex, "stable");
        Assert.Equal(8, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void SeedFromCachedDocument_IgnoresDocumentsThatDoNotVerify()
    {
        using var other = new TestSigner();
        var forged = other.Sign(TestSigner.Envelope(EmptyPayload, version: 99));

        Assert.Equal(VerificationOutcome.BadSignature, NewVerifier().SeedFromCachedDocument(forged, SignedPurposes.PackIndex, "stable"));
        Assert.Equal(VerificationOutcome.Malformed, NewVerifier().SeedFromCachedDocument("junk"u8, SignedPurposes.PackIndex, "stable"));
        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(1_000_000L, VerificationOutcome.Valid)]
    [InlineData(9_007_199_254_740_991L, VerificationOutcome.VersionTooFarAhead)]
    [InlineData(9_007_199_254_740_992L, VerificationOutcome.Malformed)]
    [InlineData(long.MaxValue, VerificationOutcome.Malformed)]
    public void Version_IsCappedAtTwoToTheFiftyThirdMinusOne(long version, VerificationOutcome expected)
    {
        // Security review 2026-10-25, L1: a version above 2^53 - 1 would put the floor out of reach and loses precision in the service.
        using var signer = new TestSigner();
        var verifier = new SignedDocumentVerifier(signer.Trusted, new InMemoryVersionStore(), new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        var result = verifier.Verify(signer.Sign(TestSigner.Envelope("{}", version: version)), SignedPurposes.PackIndex, "stable");

        Assert.Equal(expected, result.Outcome);
    }
}
