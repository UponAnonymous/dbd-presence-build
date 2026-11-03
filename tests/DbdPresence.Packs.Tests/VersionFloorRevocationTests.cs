// Covers: NFR-SVC-04, NFR-SVC-05, ADR-0012 and the threat-model review 2026-10-25 TM-01: a version floor set by a stolen key must not outlive the key,
// and a version cannot be parked out of reach.
using System.Text;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class VersionFloorRevocationTests : IDisposable
{
    private const string Purpose = SignedPurposes.PackIndex;
    private const string OldKid = "old-key";
    private const string NewKid = "new-key";
    private const string FilePath = "/cache/versions.json";

    private static readonly DateTimeOffset s_now = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] s_all = [.. SignedPurposes.All];

    private readonly TestSigner _old = new();
    private readonly TestSigner _new = new();
    private readonly FakeTimeProvider _time = new(s_now);

    public void Dispose()
    {
        _old.Dispose();
        _new.Dispose();
    }

    private TrustedKeySet Both() => new(new Dictionary<string, TrustedKey> { [OldKid] = _old.AsTrustedKey(s_all), [NewKid] = _new.AsTrustedKey(s_all) });

    private TrustedKeySet OnlyNew() => new(new Dictionary<string, TrustedKey> { [NewKid] = _new.AsTrustedKey(s_all) });

    private static string Envelope(string kid, long version, string purpose = Purpose) =>
        TestSigner.Envelope("{}", purpose: purpose, version: version, kid: kid, issuedAt: "2026-01-14T00:00:00.000Z", expiresAt: "2026-01-20T00:00:00.000Z");

    private static long IssuedSeconds => new DateTimeOffset(2026, 1, 14, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void FloorAtTwoToTheFiftyThird_SetBeforeTheFix_IsDroppedWhenItsKeyIsReplaced()
    {
        // A file written by an earlier build: the floor is at 2^53 - 1 under the old key and blocks every genuine document.
        var verifier = new SignedDocumentVerifier(Both(), new InMemoryVersionStore(), _time);
        verifier.CommitVersion(Purpose, "stable", SignedDocumentVerifier.MaxVersion, OldKid);
        var genuine = _new.Sign(Envelope(NewKid, 1_000), NewKid);
        Assert.Equal(VerificationOutcome.RolledBack, verifier.Verify(genuine, Purpose, "stable").Outcome);

        verifier.UseKeys(OnlyNew());

        Assert.False(verifier.Keys.Contains(OldKid));
        Assert.Equal(VerificationOutcome.Valid, verifier.Verify(genuine, Purpose, "stable").Outcome);
        Assert.Equal([OldKid], verifier.RevokedKeyIds);
    }

    [Fact]
    public void AFloorWithinTheBound_SetByAStolenKey_IsDroppedWhenItsKeyIsReplaced()
    {
        var verifier = new SignedDocumentVerifier(Both(), new InMemoryVersionStore(), _time);
        var poisoned = _old.Sign(Envelope(OldKid, IssuedSeconds + SignedDocumentVerifier.MaxVersionAheadOfIssue), OldKid);
        var result = verifier.Verify(poisoned, Purpose, "stable");
        Assert.True(result.IsValid);
        verifier.CommitVersion(result.Envelope!);
        var genuine = _new.Sign(Envelope(NewKid, IssuedSeconds + 5), NewKid);
        Assert.Equal(VerificationOutcome.RolledBack, verifier.Verify(genuine, Purpose, "stable").Outcome);

        verifier.UseKeys(OnlyNew());

        Assert.Equal(VerificationOutcome.Valid, verifier.Verify(genuine, Purpose, "stable").Outcome);
    }

    [Fact]
    public void ReplacementOnAPersistentStore_DropsTheFloorOnDisk_AndSurvivesARestart()
    {
        var files = new InMemoryPackFileStore();
        var verifier = new SignedDocumentVerifier(Both(), new FileVersionStore(files, FilePath), _time);
        verifier.CommitVersion(Purpose, "stable", SignedDocumentVerifier.MaxVersion, OldKid);
        verifier.UseKeys(OnlyNew());

        var restarted = new FileVersionStore(files, FilePath);

        Assert.Null(restarted.LoadFailure);
        Assert.Null(restarted.GetHighest(Purpose, "stable"));
    }

    [Fact]
    public void ReplacementOnRestart_DropsAFloorThatTheFileStillHolds()
    {
        // The file still lists the revoked floor (the earlier write failed): applying the kept replacement at start drops it again.
        var files = new InMemoryPackFileStore();
        new FileVersionStore(files, FilePath).RecordHighest(Purpose, "stable", 9_000, OldKid);
        var verifier = new SignedDocumentVerifier(Both(), new FileVersionStore(files, FilePath), _time);
        Assert.Equal(VerificationOutcome.RolledBack, verifier.Verify(_new.Sign(Envelope(NewKid, 100), NewKid), Purpose, "stable").Outcome);

        verifier.UseKeys(OnlyNew());

        Assert.Equal(VerificationOutcome.Valid, verifier.Verify(_new.Sign(Envelope(NewKid, 100), NewKid), Purpose, "stable").Outcome);
    }

    [Fact]
    public void ReplacementThatKeepsAKey_KeepsThatKeysFloor()
    {
        var store = new InMemoryVersionStore();
        var verifier = new SignedDocumentVerifier(Both(), store, _time);
        verifier.CommitVersion(Purpose, "stable", 900, OldKid);
        verifier.CommitVersion(Purpose, "stable", 50, NewKid);
        Assert.Equal(900, store.GetHighest(Purpose, "stable"));

        verifier.UseKeys(OnlyNew());

        Assert.Equal(50, store.GetHighest(Purpose, "stable"));
        Assert.Equal(VerificationOutcome.RolledBack, verifier.Verify(_new.Sign(Envelope(NewKid, 49), NewKid), Purpose, "stable").Outcome);
    }

    [Fact]
    public void AFloorOfUnknownOrigin_IsNeverDropped()
    {
        var store = new InMemoryVersionStore();
        var verifier = new SignedDocumentVerifier(Both(), store, _time);
        verifier.CommitVersion(Purpose, "stable", 70, null);
        verifier.CommitVersion(Purpose, "stable", 900, OldKid);

        verifier.UseKeys(OnlyNew());

        Assert.Equal(70, store.GetHighest(Purpose, "stable"));
    }

    [Fact]
    public void SeedFromCachedDocument_RecordsTheSigningKey_SoARevokedKeysCachedIndexLeavesNoFloor()
    {
        var store = new InMemoryVersionStore();
        var verifier = new SignedDocumentVerifier(Both(), store, _time);
        Assert.Equal(VerificationOutcome.Valid, verifier.SeedFromCachedDocument(_old.Sign(Envelope(OldKid, 800), OldKid), Purpose, "stable"));
        Assert.Equal(800, store.GetHighest(Purpose, "stable"));

        verifier.UseKeys(OnlyNew());

        Assert.Null(store.GetHighest(Purpose, "stable"));

        // After the replacement the cached copy of the revoked key no longer verifies, so it cannot seed a floor again.
        Assert.Equal(VerificationOutcome.UnknownKey, verifier.SeedFromCachedDocument(_old.Sign(Envelope(OldKid, 800), OldKid), Purpose, "stable"));
        Assert.Null(store.GetHighest(Purpose, "stable"));
    }

    [Theory]
    [InlineData(SignedPurposes.Config)]
    [InlineData(SignedPurposes.PackIndex)]
    [InlineData(SignedPurposes.Roster)]
    [InlineData(SignedPurposes.Releases)]
    public void Version_MoreThanTheBoundAboveTheSigningTime_IsRejectedWithItsOwnReason(string purpose)
    {
        var verifier = new SignedDocumentVerifier(Both(), new InMemoryVersionStore(), _time);
        var atBound = _old.Sign(Envelope(OldKid, IssuedSeconds + SignedDocumentVerifier.MaxVersionAheadOfIssue, purpose), OldKid);
        var overBound = _old.Sign(Envelope(OldKid, IssuedSeconds + SignedDocumentVerifier.MaxVersionAheadOfIssue + 1, purpose), OldKid);

        Assert.Equal(VerificationOutcome.Valid, verifier.Verify(atBound, purpose, "stable").Outcome);
        Assert.Equal(VerificationOutcome.VersionTooFarAhead, verifier.Verify(overBound, purpose, "stable").Outcome);
        Assert.Equal(VerificationOutcome.VersionTooFarAhead, verifier.SeedFromCachedDocument(overBound, purpose, "stable"));
    }

    [Fact]
    public void Version_ALongGapAfterTheStoredFloor_IsStillAccepted()
    {
        // The service makes a version max(previous + 1, seconds now): after a year without a change the next version is ~31 million above the floor.
        var verifier = new SignedDocumentVerifier(Both(), new InMemoryVersionStore(), _time);
        verifier.CommitVersion(Purpose, "stable", IssuedSeconds - 31_536_000, OldKid);

        Assert.Equal(VerificationOutcome.Valid, verifier.Verify(_old.Sign(Envelope(OldKid, IssuedSeconds), OldKid), Purpose, "stable").Outcome);
    }

    [Fact]
    public void Version_OverTheBound_IsNotRecordedBecauseNothingValidReachesCommit()
    {
        var store = new InMemoryVersionStore();
        var verifier = new SignedDocumentVerifier(Both(), store, _time);

        var result = verifier.Verify(_old.Sign(Envelope(OldKid, SignedDocumentVerifier.MaxVersion), OldKid), Purpose, "stable");

        Assert.Null(result.Envelope);
        Assert.Null(store.GetHighest(Purpose, "stable"));
    }

    [Fact]
    public void Licence_VersionMustBeTheSigningTimeInWholeSeconds()
    {
        var verifier = new SignedDocumentVerifier(Both(), new InMemoryVersionStore(), _time);
        string Licence(long version, string issuedAt = "2026-01-14T00:00:00.500Z") =>
            TestSigner.Envelope("{}", purpose: SignedPurposes.Licence, version: version, kid: OldKid, issuedAt: issuedAt, expiresAt: "2026-01-20T00:00:00.000Z");

        Assert.Equal(VerificationOutcome.Valid, verifier.Verify(_old.Sign(Licence(IssuedSeconds), OldKid), SignedPurposes.Licence, "stable").Outcome);
        Assert.Equal(VerificationOutcome.VersionNotDerivedFromIssueTime, verifier.Verify(_old.Sign(Licence(IssuedSeconds + 1), OldKid), SignedPurposes.Licence, "stable").Outcome);
        Assert.Equal(VerificationOutcome.VersionNotDerivedFromIssueTime, verifier.Verify(_old.Sign(Licence(IssuedSeconds - 1), OldKid), SignedPurposes.Licence, "stable").Outcome);
        Assert.Equal(VerificationOutcome.VersionNotDerivedFromIssueTime, verifier.Verify(_old.Sign(Licence(SignedDocumentVerifier.MaxVersion), OldKid), SignedPurposes.Licence, "stable").Outcome);
    }

    [Fact]
    public void FileStore_KeepsTheKid_AndAnOldFileWithoutKidsStillLoads()
    {
        var files = new InMemoryPackFileStore();
        files.Files[FilePath] = Encoding.UTF8.GetBytes("""{"versions":[{"purpose":"pack-index","channel":"stable","version":12},{"purpose":"config","channel":"stable","version":3}]}""");

        var store = new FileVersionStore(files, FilePath);

        Assert.Null(store.LoadFailure);
        Assert.Equal(12, store.GetHighest(Purpose, "stable"));
        Assert.Equal(3, store.GetHighest(SignedPurposes.Config, "stable"));

        store.RecordHighest(Purpose, "stable", 40, OldKid);
        store.RecordHighest(Purpose, "stable", 30, NewKid);
        var reopened = new FileVersionStore(files, FilePath);

        Assert.Null(reopened.LoadFailure);
        Assert.Equal(40, reopened.GetHighest(Purpose, "stable"));
        reopened.Forget([OldKid]);
        Assert.Equal(30, reopened.GetHighest(Purpose, "stable"));
        reopened.Forget([NewKid]);
        Assert.Equal(12, reopened.GetHighest(Purpose, "stable"));
        Assert.Equal(12, new FileVersionStore(files, FilePath).GetHighest(Purpose, "stable"));
    }

    [Fact]
    public void FileStore_Forget_WithNothingToDrop_DoesNotRewriteTheFile()
    {
        var files = new InMemoryPackFileStore();
        var store = new FileVersionStore(files, FilePath);
        store.RecordHighest(Purpose, "stable", 5, NewKid);
        files.Operations.Clear();

        store.Forget([OldKid]);

        Assert.Empty(files.Operations);
    }

    [Theory]
    [InlineData("""{"versions":[{"purpose":"pack-index","channel":"stable","version":1,"kid":5}]}""")]
    [InlineData("""{"versions":[{"purpose":"pack-index","channel":"stable","version":1,"kid":null}]}""")]
    public void FileStore_AKidThatIsNotAString_IsADamagedFile(string text)
    {
        var files = new InMemoryPackFileStore();
        files.Files[FilePath] = Encoding.UTF8.GetBytes(text);

        var store = new FileVersionStore(files, FilePath);

        Assert.NotNull(store.LoadFailure);
        Assert.Null(store.GetHighest(Purpose, "stable"));
    }
}
