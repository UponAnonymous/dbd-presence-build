// Covers: V2-S6 gate V6.1 (tampered, downgraded and expired feeds are rejected), FR-ONL-10 (only the release key signs a feed; the channel is bound;
// a lower version is never offered; the package must match the signed size and SHA-256; the rollback floor rises only after a good download),
// D33 (updates are verified by our own signature, independent of Authenticode), ADR-0012 rules 2a, 3, 4 and 5.
using System.IO;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Releases;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Updates.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly ReleaseVersion s_current = ReleaseVersion.Parse("1.0.0");
    private readonly UpdateFixture _f = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task ValidFeed_OffersTheNewestHigherVersion_AndTheDownloadMatches()
    {
        var p11 = _f.Publish("1.1.0");
        var p12 = _f.Publish("1.2.0");
        _f.Transport.Feed = _f.Feed([p12, p11], version: 10);
        var service = _f.Service();

        var check = await service.CheckAsync(s_current, Ct);
        var path = Path.Combine(_f.Root, "download.nupkg");
        var outcome = await service.DownloadAsync(check.Candidate!, path, null, Ct);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, check.Outcome);
        Assert.Equal("1.2.0", check.Candidate!.Version.Text);
        Assert.Equal(UpdateDownloadOutcome.Downloaded, outcome);
        Assert.Equal(_f.Transport.Packages[p12.Sha256], File.ReadAllBytes(path));
        Assert.Equal(10, _f.Versions.GetHighest(SignedPurposes.Releases, "stable"));
    }

    [Fact]
    public async Task TamperedFeed_IsRefused_AndNothingIsDownloaded()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = UpdateFixture.Tamper(_f.Feed([p], version: 10));

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FeedRefused, check.Outcome);
        Assert.Equal(VerificationOutcome.BadSignature, check.Refusal);
        Assert.Null(check.Candidate);
        Assert.Empty(_f.Transport.Downloads);
        Assert.Null(_f.Versions.GetHighest(SignedPurposes.Releases, "stable"));
    }

    [Fact]
    public async Task DowngradedFeed_OlderThanOneAlreadySeen_IsRefused()
    {
        var p12 = _f.Publish("1.2.0");
        var service = _f.Service();
        _f.Transport.Feed = _f.Feed([p12], version: 20);
        var first = await service.CheckAsync(s_current, Ct);
        Assert.Equal(UpdateDownloadOutcome.Downloaded, await service.DownloadAsync(first.Candidate!, Path.Combine(_f.Root, "a.nupkg"), null, Ct));

        // An attacker (or a stale cache) serves an older, validly signed feed that still lists an older release.
        var p11 = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p11], version: 19);
        var second = await service.CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FeedRefused, second.Outcome);
        Assert.Equal(VerificationOutcome.RolledBack, second.Refusal);
    }

    [Fact]
    public async Task ExpiredFeed_IsRefused_SoAFrozenFeedCannotHoldBackUpdates()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        _f.Time.Advance(TimeSpan.FromDays(30));

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FeedRefused, check.Outcome);
        Assert.Equal(VerificationOutcome.Expired, check.Refusal);
    }

    [Fact]
    public async Task FeedSignedByTheContentKey_IsRefused_OnlyTheReleaseKeyMaySignCode()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10, key: _f.Content, kid: "content-test-1");

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(VerificationOutcome.KeyNotAllowedForPurpose, check.Refusal);
    }

    [Fact]
    public async Task FeedOfAnotherChannel_IsRefused()
    {
        var p = _f.Publish("1.1.0", channel: "beta");
        _f.Transport.Feed = _f.Feed([p], version: 10, channel: "beta");

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(VerificationOutcome.WrongChannel, check.Refusal);
    }

    [Fact]
    public async Task FeedWithOnlyLowerOrEqualVersions_OffersNothing_NeverADowngrade()
    {
        var p09 = _f.Publish("0.9.0");
        var p10 = _f.Publish("1.0.0");
        _f.Transport.Feed = _f.Feed([p10, p09], version: 10);

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.UpToDate, check.Outcome);
        Assert.Null(check.Candidate);
        Assert.Equal(10, _f.Versions.GetHighest(SignedPurposes.Releases, "stable"));
    }

    [Fact]
    public async Task PackageThatDoesNotMatchTheFeed_IsRefused_AndDeleted()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        var bytes = _f.Transport.Packages[p.Sha256];
        bytes[^1] ^= 0xFF;
        var service = _f.Service();
        var check = await service.CheckAsync(s_current, Ct);
        var path = Path.Combine(_f.Root, "bad.nupkg");

        var outcome = await service.DownloadAsync(check.Candidate!, path, null, Ct);

        Assert.Equal(UpdateDownloadOutcome.Refused, outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task VerifiedFeed_RaisesTheFloor_SoAnOlderOneIsRefused_EvenWithoutADownload()
    {
        var service = _f.Service();
        _f.Transport.Feed = _f.Feed([_f.Publish("1.2.0")], version: 20);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await service.CheckAsync(s_current, Ct)).Outcome);

        // No download happened. A validly signed, older feed is served next (a replayed or stale copy).
        _f.Transport.Feed = _f.Feed([_f.Publish("1.1.0")], version: 19);
        var second = await service.CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FeedRefused, second.Outcome);
        Assert.Equal(VerificationOutcome.RolledBack, second.Refusal);
        Assert.Empty(_f.Transport.Downloads);
    }

    [Fact]
    public async Task Floor_SurvivesARestart_EvenWhenTheGeneralVersionStoreIsLost()
    {
        _f.Transport.Feed = _f.Feed([_f.Publish("1.2.0")], version: 20);
        await _f.Service().CheckAsync(s_current, Ct);
        Assert.Contains("\"version\":20", System.Text.Encoding.UTF8.GetString(_f.Files.Files[UpdateFixture.FloorPath]), StringComparison.Ordinal);

        // A restart with an empty general version store: only the release floor file remains.
        var freshVerifier = new SignedDocumentVerifier(new TrustedKeySet(_f.Keys), new InMemoryVersionStore(), _f.Time);
        _f.Transport.Feed = _f.Feed([_f.Publish("1.1.0")], version: 19);
        var afterRestart = await _f.Service(verifier: freshVerifier).CheckAsync(s_current, Ct);

        Assert.Equal(VerificationOutcome.RolledBack, afterRestart.Refusal);
    }

    [Fact]
    public async Task DamagedFloorFile_FailsClosed_WithAPlainMessage()
    {
        _f.Files.Files[UpdateFixture.FloorPath] = UpdateFixture.Utf8("{ not json");
        _f.Transport.Feed = _f.Feed([_f.Publish("1.2.0")], version: 20);

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FloorUnavailable, check.Outcome);
        Assert.Null(check.Candidate);
        Assert.Equal(0, _f.Transport.FeedRequests);
        Assert.Contains(_f.Log.Lines, l => l.StartsWith("The update version floor file is damaged", StringComparison.Ordinal) && l.Contains("Updates stay off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FloorThatCannotBeSaved_FailsClosed()
    {
        var files = new FailingFiles();
        var service = new UpdateService(_f.Transport, _f.Verifier, _f.Gate, new FileReleaseFloor(files, "floor.json", _f.Log), UpdateService.StableChannel, _f.Time, _f.Log);
        _f.Transport.Feed = _f.Feed([_f.Publish("1.2.0")], version: 20);

        var check = await service.CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FloorUnavailable, check.Outcome);
        Assert.Null(check.Candidate);
        Assert.Contains(_f.Log.Lines, l => l.Contains("could not be saved", StringComparison.Ordinal));
    }

    private sealed class FailingFiles : DbdPresence.Packs.Cache.IPackFileStore
    {
        public bool Exists(string path) => false;

        public long Length(string path) => 0;

        public byte[] ReadAllBytes(string path) => throw new FileNotFoundException(path);

        public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes) => throw new IOException("disk full");

        public void Move(string source, string destination) => throw new IOException("disk full");

        public void Delete(string path)
        {
        }

        public void CreateDirectory(string path)
        {
        }

        public IReadOnlyList<string> ListFiles(string directory) => [];
    }

    private async Task<(UpdateService Service, ReleasePackage Package, string Path)> DownloadedAsync()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        var service = _f.Service();
        await service.CheckAsync(s_current, Ct);
        var path = Path.Combine(_f.Root, "judged.nupkg");
        Assert.Equal(UpdateDownloadOutcome.Downloaded, await service.DownloadAsync(p, path, null, Ct));
        return (service, p, path);
    }

    [Fact]
    public async Task Judge_IsValid_WhileTheOfferingFeedStands_AndAnOutageDoesNotChangeThat()
    {
        var (service, p, path) = await DownloadedAsync();
        Assert.Equal(PackageVerdict.Valid, service.Judge(p, path, out _));

        _f.Transport.Unreachable = true;
        var outage = await service.CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.Unreachable, outage.Outcome);
        Assert.Equal(PackageVerdict.Valid, service.Judge(p, path, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public async Task Judge_IsInvalid_WhenTheNewestFeedIsRefused_OffersNothingNewer_OrOffersAnotherPackage()
    {
        var (service, p, path) = await DownloadedAsync();
        _f.Transport.Feed = UpdateFixture.Tamper(_f.Feed([p], version: 11));
        await service.CheckAsync(s_current, Ct);
        Assert.Equal(PackageVerdict.Invalid, service.Judge(p, path, out _));

        _f.Transport.Feed = _f.Feed([], version: 12);
        await service.CheckAsync(s_current, Ct);
        Assert.Equal(PackageVerdict.Invalid, service.Judge(p, path, out _));

        var other = _f.Publish("1.3.0");
        _f.Transport.Feed = _f.Feed([other], version: 13);
        await service.CheckAsync(s_current, Ct);
        Assert.Equal(PackageVerdict.Invalid, service.Judge(p, path, out var reason));
        Assert.Equal("the newest verified feed offers another package", reason);
    }

    [Fact]
    public async Task Judge_IsUnverifiable_BeforeAnyFeedWasVerified_AndOnceTheOfferingFeedExpired()
    {
        var (service, p, path) = await DownloadedAsync();
        Assert.Equal(PackageVerdict.Unverifiable, _f.Service().Judge(p, path, out _));

        _f.Time.Advance(TimeSpan.FromDays(31));

        Assert.Equal(PackageVerdict.Unverifiable, service.Judge(p, path, out var reason));
        Assert.Equal("the feed that offered it has expired", reason);
    }

    [Fact]
    public async Task Judge_IsInvalid_WhenTheFileChanged()
    {
        var (service, p, path) = await DownloadedAsync();
        File.AppendAllText(path, "x");

        Assert.Equal(PackageVerdict.Invalid, service.Judge(p, path, out var reason));
        Assert.Equal("the file does not match the signed size and SHA-256", reason);
    }

    [Fact]
    public async Task OnlyTheOfferedPackage_CanBeDownloaded()
    {
        var p11 = _f.Publish("1.1.0");
        var other = _f.Publish("1.5.0");
        _f.Transport.Feed = _f.Feed([p11], version: 10);
        var service = _f.Service();
        await service.CheckAsync(s_current, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(other, Path.Combine(_f.Root, "x.nupkg"), null, Ct));
        Assert.Empty(_f.Transport.Downloads);
    }

    [Fact]
    public async Task WithOnlineFeaturesOff_NothingIsContacted()
    {
        _f.Gate.IsOpen = false;

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.OnlineFeaturesOff, check.Outcome);
        Assert.Equal(0, _f.Transport.FeedRequests);
    }

    [Fact]
    public async Task NothingPublished_IsReportedAsSuch()
    {
        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.NothingPublished, check.Outcome);
    }

    [Fact]
    public async Task FeedWithABrokenPayload_IsRefused()
    {
        var payload = """{"schemaVersion":1,"packageId":"SomethingElse","releases":[]}""";
        _f.Transport.Feed = ReleaseSigner.DocumentSigner.Sign(_f.Release, "release-test-1", SignedPurposes.Releases, "stable", 10, _f.Time.GetUtcNow(), _f.Time.GetUtcNow().AddDays(30), payload);

        var check = await _f.Service().CheckAsync(s_current, Ct);

        Assert.Equal(UpdateCheckOutcome.FeedRefused, check.Outcome);
        Assert.Equal("wrong_package_id", check.Detail);
    }
}
