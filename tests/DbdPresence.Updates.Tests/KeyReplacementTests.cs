// Covers: V2-S6 gate V6.3 (the recovery-key flow is tested), PLAN_V2 §6.2 and NFR-SVC-05 (only the recovery key signs a key replacement; it revokes a
// compromised release key and trusts its successor; the change is kept across restarts and offline; an older replacement never undoes a newer one),
// ADR-0012 rule 2a.
using System.Security.Cryptography;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Updates.Tests;

public sealed class KeyReplacementTests : IDisposable
{
    private const string StorePath = "online/key-replacement.json";
    private static readonly ReleaseVersion s_current = ReleaseVersion.Parse("1.0.0");
    private readonly UpdateFixture _f = new();
    private readonly MemoryFiles _files = new();
    private readonly ECDsa _successor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _successor.Dispose();
        _f.Dispose();
    }

    [Fact]
    public async Task Replacement_RevokesTheCompromisedReleaseKey_AndTrustsItsSuccessor()
    {
        var p = _f.Publish("1.1.0");
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        var service = _f.Service(keys);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);

        // A feed signed with the stolen key is no longer accepted …
        _f.Transport.Feed = _f.Feed([p], version: 10);
        var withOldKey = await service.CheckAsync(s_current, Ct);

        // … and one signed with the successor is.
        _f.Transport.Feed = _f.Feed([p], version: 11, key: _successor, kid: "release-test-2");
        var withNewKey = await service.CheckAsync(s_current, Ct);

        Assert.Equal(VerificationOutcome.UnknownKey, withOldKey.Refusal);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, withNewKey.Outcome);
        Assert.Equal(1, keys.AppliedVersion);
        Assert.Equal(["release-test-1"], keys.Revoked);
        Assert.True(_files.Exists(StorePath));
    }

    [Fact]
    public async Task KeptReplacement_IsAppliedAgainAtTheNextStart_WithoutTheNetwork()
    {
        var first = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);
        Assert.True(await first.RefreshAsync(Ct));

        // A restart: a fresh verifier with only the compiled-in keys, and no service.
        var verifier = new SignedDocumentVerifier(new TrustedKeySet(_f.Keys), new InMemoryVersionStore(), _f.Time);
        var restarted = new KeyReplacementService(new FakeReleases(), verifier, _f.Keys, _files, StorePath, _f.Log);
        restarted.LoadStored();

        Assert.False(verifier.Keys.Contains("release-test-1"));
        Assert.True(verifier.Keys.IsAllowedFor("release-test-2", SignedPurposes.Releases));
        Assert.Equal(1, restarted.AppliedVersion);
    }

    [Fact]
    public async Task Replacement_SignedByAnyOtherKey_IsRefused()
    {
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1, key: _f.Release, kid: "release-test-1");

        Assert.False(await keys.RefreshAsync(Ct));

        Assert.True(_f.Verifier.Keys.Contains("release-test-1"));
        Assert.Contains(_f.Log.Lines, l => l.Contains("KeyNotAllowedForPurpose", StringComparison.Ordinal));
        Assert.False(_files.Exists(StorePath));
    }

    [Fact]
    public async Task OlderReplacement_NeverUndoesANewerOne()
    {
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 5);
        Assert.True(await keys.RefreshAsync(Ct));

        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(new KeyReplacement(["content-test-1"], []), version: 4);
        Assert.False(await keys.RefreshAsync(Ct));

        Assert.Equal(5, keys.AppliedVersion);
        Assert.True(_f.Verifier.Keys.Contains("content-test-1"));
    }

    [Fact]
    public async Task DamagedKeptCopy_KeepsUpdatesOff_UntilAFreshReplacementIsFetched()
    {
        // Security review 2026-10-25, L2: before, a damaged kept copy fell back to the compiled-in keys, which trusts the revoked key again.
        var p = _f.Publish("1.1.0");
        var first = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);
        Assert.True(await first.RefreshAsync(Ct));
        _files.Files[StorePath] = UpdateFixture.Utf8(UpdateFixture.Tamper(System.Text.Encoding.UTF8.GetString(_files.Files[StorePath])));

        var verifier = new SignedDocumentVerifier(new TrustedKeySet(_f.Keys), new InMemoryVersionStore(), _f.Time);
        var offline = new FakeReleases { Feed = _f.Feed([p], version: 10) };
        var restarted = new KeyReplacementService(offline, verifier, _f.Keys, _files, StorePath, _f.Log);
        restarted.LoadStored();
        var service = new UpdateService(offline, verifier, _f.Gate, new FileReleaseFloor(_f.Files, UpdateFixture.FloorPath, _f.Log), UpdateService.StableChannel, _f.Time, _f.Log, restarted);

        var blocked = await service.CheckAsync(s_current, Ct);

        Assert.True(restarted.IsDamaged);
        Assert.Equal((UpdateCheckOutcome.FloorUnavailable, "key_replacement_damaged"), (blocked.Outcome, blocked.Detail));
        Assert.Equal(0, offline.FeedRequests);
        Assert.Contains(_f.Log.Lines, l => l.StartsWith("The kept key replacement is damaged", StringComparison.Ordinal));

        // A fresh, valid replacement from the service ends it.
        offline.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);
        offline.Feed = _f.Feed([p], version: 11, key: _successor, kid: "release-test-2");
        var resumed = await service.CheckAsync(s_current, Ct);

        Assert.False(restarted.IsDamaged);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, resumed.Outcome);
        Assert.False(verifier.Keys.Contains("release-test-1"));
    }

    [Fact]
    public async Task Replacement_ThatLeavesOutAnEarlierRevocation_IsRefused()
    {
        // Security review 2026-10-25, M1: replacements are cumulative, or a later one would trust a revoked key again.
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);
        Assert.True(await keys.RefreshAsync(Ct));

        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(new KeyReplacement(["content-test-1"], []), version: 2);
        Assert.False(await keys.RefreshAsync(Ct));

        Assert.Equal(1, keys.AppliedVersion);
        Assert.False(_f.Verifier.Keys.Contains("release-test-1"));
        Assert.Contains(_f.Log.Lines, l => l.Contains("leaves out a key that an earlier replacement revoked", StringComparison.Ordinal));

        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(new KeyReplacement(["release-test-1", "content-test-1"], []), version: 2);
        Assert.True(await keys.RefreshAsync(Ct));
        Assert.False(_f.Verifier.Keys.Contains("content-test-1"));
    }

    [Fact]
    public async Task Replacement_ThatReAddsACompiledInKey_StillRevokes()
    {
        // Security review 2026-10-25, M2: an identical re-add is a no-op; before, it refused the whole document and the revocation was lost.
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        var content = _f.Keys["content-test-1"];
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(
            new KeyReplacement(
                ["release-test-1"],
                [new AddedKey("content-test-1", content.Spki, [.. content.Purposes]), new AddedKey("release-test-2", _successor.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases])]),
            version: 1);

        Assert.True(await keys.RefreshAsync(Ct));

        Assert.False(_f.Verifier.Keys.Contains("release-test-1"));
        Assert.True(_f.Verifier.Keys.IsAllowedFor("content-test-1", SignedPurposes.PackIndex));
        Assert.True(_f.Verifier.Keys.IsAllowedFor("release-test-2", SignedPurposes.Releases));
    }

    [Fact]
    public async Task FloorSetByARevokedReleaseKey_DoesNotBlockTheSuccessorsFeed()
    {
        // Threat-model review 2026-10-25, TM-01: the stolen release key parks the floor as high as the bound allows (here 1,000,000 above the
        // signing time in seconds); revoking the key must drop that floor in both the general store and the release floor file.
        var p = _f.Publish("1.1.0");
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        var service = _f.Service(keys);
        var parked = UpdateFixture.Start.ToUnixTimeSeconds() + SignedDocumentVerifier.MaxVersionAheadOfIssue;
        _f.Transport.Feed = _f.Feed([p], version: parked);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await service.CheckAsync(s_current, Ct)).Outcome);

        _f.Transport.Feed = _f.Feed([p], version: 11, key: _successor, kid: "release-test-2");
        Assert.Equal(VerificationOutcome.UnknownKey, (await service.CheckAsync(s_current, Ct)).Refusal);

        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);
        var afterReplacement = await service.CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, afterReplacement.Outcome);
        Assert.Equal(11, afterReplacement.FeedVersion);
        Assert.Equal(11, _f.Versions.GetHighest(SignedPurposes.Releases, "stable"));
    }

    [Fact]
    public async Task ReleaseFloorOfARevokedKey_IsDroppedFromTheFileToo_AndSurvivesARestart()
    {
        var p = _f.Publish("1.1.0");
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, _files, StorePath, _f.Log);
        var service = _f.Service(keys);
        _f.Transport.Feed = _f.Feed([p], version: UpdateFixture.Start.ToUnixTimeSeconds() + SignedDocumentVerifier.MaxVersionAheadOfIssue);
        await service.CheckAsync(s_current, Ct);
        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(Replace(), version: 1);
        _f.Transport.Feed = _f.Feed([p], version: 11, key: _successor, kid: "release-test-2");
        await service.CheckAsync(s_current, Ct);

        // A restart: the floor file now holds the successor's floor only.
        var floor = new FileReleaseFloor(_f.Files, UpdateFixture.FloorPath, _f.Log);

        Assert.False(floor.IsDamaged);
        Assert.Equal(11, floor.Highest("stable"));
    }

    [Fact]
    public void OfflineSigner_RefusesAFeedVersionOverTheBound_BeforeAnythingIsWritten()
    {
        // The signer verifies what it signed with the app's own verifier, so a mistyped or hostile --version never reaches the feed.
        var p = _f.Publish("1.1.0");

        var failure = Assert.Throws<InvalidOperationException>(() => _f.Feed([p], version: SignedDocumentVerifier.MaxVersion));

        Assert.Contains(nameof(VerificationOutcome.VersionTooFarAhead), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseFloorFileWrittenBeforeTheFix_StillLoads_AndIsNeverDropped()
    {
        _f.Files.Files[UpdateFixture.FloorPath] = UpdateFixture.Utf8("""{"schemaVersion":1,"channels":{"stable":20}}""");

        var floor = new FileReleaseFloor(_f.Files, UpdateFixture.FloorPath, _f.Log);
        floor.Forget(["release-test-1"]);

        Assert.False(floor.IsDamaged);
        Assert.Equal(20, floor.Highest("stable"));
        Assert.True(floor.Raise("stable", 30, "release-test-1"));
        floor.Forget(["release-test-1"]);
        Assert.Equal(20, floor.Highest("stable"));
        Assert.Equal(20, new FileReleaseFloor(_f.Files, UpdateFixture.FloorPath, _f.Log).Highest("stable"));
    }

    private KeyReplacement Replace() =>
        new(["release-test-1"], [new AddedKey("release-test-2", _successor.ExportSubjectPublicKeyInfo(), [SignedPurposes.Releases])]);
}
