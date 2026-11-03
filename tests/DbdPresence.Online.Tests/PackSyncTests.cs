// Covers: FR-ONL-07 (sync on start and every 6 hours, delta-only downloads, hash/size/format checks, atomic swap, rollback/freeze/channel rules,
// 429 backoff, last good cache kept on any failure), FR-PACK-04 (withdrawn licensed items disappear at the next sync), FR-PACK-10 (strict PNG,
// image helper for JPEG), NFR-SVC-04 (pack-index purpose, rollback), ADR-0013 (200 MB cap), ADR-0016 (CDN host and path guard).
using DbdPresence.Core.Images;
using DbdPresence.Online.Packs;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;
using DbdPresence.Packs.Sync;

namespace DbdPresence.Online.Tests;

public sealed class PackSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PublishedPack[] s_official = [new("official", "Official")];

    private static PublishedFile[] Two() =>
    [
        PublishedFile.Png("official-role-killer", "official", "role:killer", 1),
        PublishedFile.Png("official-role-survivor", "official", "role:survivor", 2, licensed: "Example Studio"),
    ];

    [Fact]
    public async Task HappyPath_FetchesVerifiesDownloadsAndCommitsAtomically()
    {
        using var w = new PackWorld();
        var files = Two();
        var document = w.Publish(10, s_official, files);

        var status = await w.Sync.SyncAsync(manual: false, Ct);

        Assert.Equal(PackSyncOutcome.Updated, status.LastOutcome);
        Assert.Equal(w.Time.GetUtcNow(), status.LastSuccessAt);
        Assert.False(status.CouldNotRefresh);
        Assert.Equal(2, w.FileRequests.Count());
        Assert.All(w.FileRequests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(10, w.Versions.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.Equal(document, System.Text.Encoding.UTF8.GetString(w.Files.Files[Path.Combine(PackWorld.Directory, "cache", "index.json")]));
        var synced = w.Sync.Synced!;
        Assert.Equal(2, synced.Index.Entries.Count);
        Assert.All(synced.Index.Entries, e => Assert.True(synced.Cache.Contains(e)));
        Assert.Equal($"https://{PackWorld.CdnHost}/packs/{files[0].FileName}", synced.UrlFor(synced.Index.Entries[0]));
        Assert.NotNull(synced.LocalPathFor!(synced.Index.Entries[0]));
    }

    [Fact]
    public async Task SecondSync_DownloadsOnlyTheHashesItDoesNotHave()
    {
        using var w = new PackWorld();
        var first = Two();
        w.Publish(1, s_official, first);
        await w.Sync.SyncAsync(false, Ct);
        w.Service.Requests.Clear();

        var added = PublishedFile.Png("official-character-the-trapper", "official", "character:the-trapper", 3);
        w.Publish(2, s_official, [.. first, added]);
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Updated, status.LastOutcome);
        Assert.Equal([$"/packs/{added.FileName}"], w.FileRequests.Select(r => r.Path));
        Assert.Equal(3, w.Sync.Synced!.Index.Entries.Count);
    }

    [Fact]
    public async Task SameIndexAgain_IsUpToDate_AndDownloadsNothing()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);
        w.Service.Requests.Clear();
        w.Time.Advance(TimeSpan.FromHours(6));

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.UpToDate, status.LastOutcome);
        Assert.Empty(w.FileRequests);
        Assert.Equal(w.Time.GetUtcNow(), status.LastSuccessAt); // "last updated" moves on every successful check
    }

    [Fact]
    public async Task TamperedFile_IsRefused_AndTheLastGoodCacheStays()
    {
        using var w = new PackWorld();
        var good = Two();
        w.Publish(1, s_official, good);
        await w.Sync.SyncAsync(false, Ct);
        var before = w.Sync.Synced!;

        var changed = PublishedFile.Png("official-role-killer", "official", "role:killer", 77);
        w.Publish(2, s_official, [changed, good[1]]);
        // Same size, same name, different bytes: what a hostile CDN would serve.
        var tampered = (byte[])changed.Bytes.Clone();
        tampered[^20] ^= 0xFF;
        w.Cdn[changed.FileName] = tampered;
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.FileRefused, status.LastOutcome);
        Assert.True(status.CouldNotRefresh);
        Assert.Same(before, w.Sync.Synced);
        Assert.Equal(1, w.Versions.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.False(w.Files.Files.ContainsKey(Path.Combine(PackWorld.Directory, "cache", "files", changed.FileName)));
    }

    [Fact]
    public async Task OversizeFile_IsRefusedWhileReading()
    {
        using var w = new PackWorld();
        var files = Two();
        w.Publish(1, s_official, files);
        w.Cdn[files[0].FileName] = [.. files[0].Bytes, .. new byte[4096]];

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.FileRefused, status.LastOutcome);
        Assert.Null(w.Sync.Synced);
        Assert.Null(w.Versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public async Task ShortFile_IsRefused()
    {
        using var w = new PackWorld();
        var files = Two();
        w.Publish(1, s_official, files);
        w.Cdn[files[0].FileName] = files[0].Bytes[..^8];

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.FileRefused, status.LastOutcome);
        Assert.Null(w.Sync.Synced);
    }

    [Fact]
    public async Task PngThatFailsTheStrictCheck_OrLiesAboutItsSize_IsRefused()
    {
        using var w = new PackWorld();
        var notPng = new PublishedFile("official-role-killer", "official", "role:killer", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], PackImageFormat.Png, 16, 16, null);
        w.Publish(1, s_official, [notPng]);

        Assert.Equal(PackSyncOutcome.FileRefused, (await w.Sync.SyncAsync(false, Ct)).LastOutcome);

        var wrongSize = PublishedFile.Png("official-role-survivor", "official", "role:survivor", 5, size: 16) with { Width = 32, Height = 32 };
        w.Publish(2, s_official, [wrongSize]);
        w.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(PackSyncOutcome.FileRefused, (await w.Sync.SyncAsync(false, Ct)).LastOutcome);
        Assert.Null(w.Sync.Synced);
    }

    [Fact]
    public async Task Jpeg_GoesThroughTheImageHelper_AndKeepsItsPngThumbnail()
    {
        using var w = new PackWorld();
        var jpeg = PublishedFile.Jpeg("official-map-hawkins", "official", "map:hawkins", 4);
        w.Publish(1, s_official, [jpeg]);

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Updated, status.LastOutcome);
        Assert.Single(w.Helper.Calls);
        var entry = w.Sync.Synced!.Index.Entries.Single();
        var thumbnail = w.Sync.Synced.LocalPathFor!(entry);
        Assert.NotNull(thumbnail);
        Assert.EndsWith(".png", thumbnail, StringComparison.Ordinal);
        Assert.True(StrictPng.TryValidate(w.Files.Files[thumbnail], out _, out _, out _));
    }

    [Fact]
    public async Task Jpeg_TheHelperRefuses_NothingIsStored()
    {
        using var w = new PackWorld();
        w.Helper.Succeeds = false;
        w.Publish(1, s_official, [PublishedFile.Jpeg("official-map-hawkins", "official", "map:hawkins", 4)]);

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.FileRefused, status.LastOutcome);
        Assert.Null(w.Sync.Synced);
    }

    [Fact]
    public async Task RolledBackIndex_IsRefused_AndTheNewerOneKeepsWorking()
    {
        using var w = new PackWorld();
        w.Publish(5, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);
        var current = w.Sync.Synced;

        w.Publish(4, s_official, [PublishedFile.Png("official-role-killer", "official", "role:killer", 9)]);
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.IndexRefused, status.LastOutcome);
        Assert.Contains("older", status.Note, StringComparison.Ordinal);
        Assert.Same(current, w.Sync.Synced);
        Assert.Equal(5, w.Versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public async Task RollbackStaysRefused_AfterARestartEvenWithoutTheVersionFile()
    {
        using var w = new PackWorld();
        w.Publish(5, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);

        w.Restart(freshVersions: true);
        w.Publish(4, s_official, Two());
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.IndexRefused, status.LastOutcome);
        Assert.Equal(5, w.Versions.GetHighest(SignedPurposes.PackIndex, "stable")); // re-seeded from the cached index
    }

    [Fact]
    public async Task WrongChannel_AndWrongPurpose_AreRefused()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two(), channel: "beta");
        Assert.Equal(PackSyncOutcome.IndexRefused, (await w.Sync.SyncAsync(false, Ct)).LastOutcome);

        var wrongPurpose = w.Publisher.Document(w.Time.GetUtcNow(), 2, s_official, Two(), purpose: SignedPurposes.Config);
        w.IndexBody = PackPublisher.Wrap(wrongPurpose);
        w.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PackSyncOutcome.IndexRefused, (await w.Sync.SyncAsync(false, Ct)).LastOutcome);

        Assert.Null(w.Sync.Synced);
        Assert.Empty(w.FileRequests);
    }

    [Fact]
    public async Task IndexSignedByAnUntrustedKey_OrUnparseable_IsRefused()
    {
        using var w = new PackWorld();
        using var stranger = new PackPublisher();
        w.IndexBody = PackPublisher.Wrap(stranger.Document(w.Time.GetUtcNow(), 1, s_official, Two()));
        Assert.Equal(PackSyncOutcome.IndexRefused, (await w.Sync.SyncAsync(false, Ct)).LastOutcome);

        // Validly signed, but not a pack index (an entry points at a pack that does not exist).
        w.IndexBody = PackPublisher.Wrap(w.Publisher.Document(w.Time.GetUtcNow(), 2, [], Two()));
        w.Time.Advance(TimeSpan.FromMinutes(2));
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.IndexRefused, status.LastOutcome);
        Assert.Null(w.Sync.Synced);
        Assert.Null(w.Versions.GetHighest(SignedPurposes.PackIndex, "stable")); // a refused document never moves the floor
    }

    [Fact]
    public async Task ExpiredIndex_KeepsShowingTheCachedPacks_WithACouldNotRefreshNote()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);
        Assert.False(w.Sync.Status.IndexExpired);

        w.Time.Advance(TimeSpan.FromDays(31));
        w.IndexStatus = 503;
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.True(status.IndexExpired);
        Assert.True(status.CouldNotRefresh);
        Assert.NotNull(w.Sync.Synced);
        Assert.All(w.Sync.Synced!.Index.Entries, e => Assert.True(w.Sync.Synced.Cache.Contains(e)));

        var restarted = w.Restart();
        Assert.NotNull(restarted.Synced); // an expired cached index still loads after a restart
        Assert.True(restarted.Status.IndexExpired);
        Assert.Contains("30 days", restarted.Status.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceOutage_KeepsTheLastGoodCache()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);
        var good = w.Sync.Synced;

        w.IndexStatus = 500;
        w.Time.Advance(TimeSpan.FromHours(6));
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Offline, status.LastOutcome);
        Assert.True(status.CouldNotRefresh);
        Assert.Same(good, w.Sync.Synced);
        Assert.NotNull(status.LastSuccessAt);
    }

    [Fact]
    public async Task FailedDownload_KeepsTheOldCacheAndIndex_AndTheNextSyncResumes()
    {
        using var w = new PackWorld();
        var first = Two();
        w.Publish(1, s_official, first);
        await w.Sync.SyncAsync(false, Ct);
        var before = w.Sync.Synced;

        var a = PublishedFile.Png("official-character-a", "official", "character:a", 11);
        var b = PublishedFile.Png("official-character-b", "official", "character:b", 12);
        w.Publish(2, s_official, [.. first, a, b]);
        w.CdnFailures.Add(b.FileName);
        var failed = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Offline, failed.LastOutcome);
        Assert.Same(before, w.Sync.Synced);
        Assert.Equal(1, w.Versions.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.Equal(2, w.Sync.Synced!.Index.Entries.Count);

        w.CdnFailures.Clear();
        w.Service.Requests.Clear();
        w.Time.Advance(TimeSpan.FromMinutes(5));
        var resumed = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Updated, resumed.LastOutcome);
        Assert.Equal([$"/packs/{b.FileName}"], w.FileRequests.Select(r => r.Path)); // a was kept from the failed attempt
        Assert.Equal(4, w.Sync.Synced!.Index.Entries.Count);
    }

    [Fact]
    public async Task WithdrawnLicensedItem_DisappearsAtTheNextSync_AndItsFileIsRemoved()
    {
        using var w = new PackWorld();
        var files = Two();
        w.Publish(1, s_official, files);
        await w.Sync.SyncAsync(false, Ct);
        var licensed = files[1];
        Assert.True(w.Files.Files.ContainsKey(Path.Combine(PackWorld.Directory, "cache", "files", licensed.FileName)));

        w.Publish(2, s_official, [files[0]]);
        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Updated, status.LastOutcome);
        Assert.Contains("1 licensed image was withdrawn", status.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(w.Sync.Synced!.Index.Entries, e => e.Id == licensed.Id);
        Assert.False(w.Files.Files.ContainsKey(Path.Combine(PackWorld.Directory, "cache", "files", licensed.FileName)));
    }

    [Fact]
    public async Task UnpublishedPack_DisappearsAtTheNextSync()
    {
        using var w = new PackWorld();
        w.Publish(1, [new("official", "Official"), new("extra", "Extra")], [.. Two(), PublishedFile.Png("extra-role-killer", "extra", "role:killer", 40)]);
        await w.Sync.SyncAsync(false, Ct);
        Assert.Equal(2, w.Sync.Synced!.Index.Packs.Count);

        w.Publish(2, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(["official"], w.Sync.Synced!.Index.Packs.Select(p => p.Id));
    }

    [Fact]
    public async Task IndexNeedingMoreThanTheCacheCap_IsRefusedBeforeAnyDownload()
    {
        using var w = new PackWorld(maxCacheBytes: 300);
        w.Publish(1, s_official, Two());

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.CacheFull, status.LastOutcome);
        Assert.Empty(w.FileRequests);
        Assert.Null(w.Sync.Synced);
        Assert.Null(w.Versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public async Task RateLimitedIndex_BacksOffByRetryAfter_ThenTriesAgain()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        w.IndexStatus = 429;
        w.IndexRetryAfter = TimeSpan.FromMinutes(2);

        var limited = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.RateLimited, limited.LastOutcome);
        Assert.Equal(w.Time.GetUtcNow() + TimeSpan.FromMinutes(2), limited.RetryAt);
        Assert.Contains("2 minutes", limited.Note, StringComparison.Ordinal);
        Assert.Single(w.IndexRequests);

        w.IndexStatus = 200;
        w.Time.Advance(TimeSpan.FromSeconds(60));
        await w.Sync.SyncAsync(false, Ct);
        Assert.Single(w.IndexRequests); // still inside the wait: the service is not contacted again

        w.Time.Advance(TimeSpan.FromSeconds(61));
        var ok = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.Updated, ok.LastOutcome);
        Assert.Equal(2, w.IndexRequests.Count());
    }

    [Fact]
    public async Task RateLimitedFile_StopsTheSync_AndKeepsTheOldCache()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);
        var before = w.Sync.Synced;
        var more = PublishedFile.Png("official-character-a", "official", "character:a", 21);
        w.Publish(2, s_official, [.. Two(), more]);
        w.Service.Handler = r => r.Host == PackWorld.CdnHost ? FakeService.Json(429, "{}", TimeSpan.FromSeconds(90)) : w.Serve(r);

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.RateLimited, status.LastOutcome);
        Assert.Same(before, w.Sync.Synced);
        Assert.Contains("90 seconds", status.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualChecks_AreThrottledLocally_ButAutomaticOnesAreNot()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());

        var first = await w.Sync.SyncAsync(manual: true, Ct);
        var second = await w.Sync.SyncAsync(manual: true, Ct);

        Assert.Equal(PackSyncOutcome.RateLimited, second.LastOutcome);
        Assert.Contains("seconds", second.Note, StringComparison.Ordinal);
        Assert.Equal(PackSyncOutcome.Updated, first.LastOutcome);
        Assert.Single(w.IndexRequests);
        Assert.NotNull(second.RetryAt);

        var automatic = await w.Sync.SyncAsync(manual: false, Ct);
        Assert.Equal(PackSyncOutcome.UpToDate, automatic.LastOutcome);

        w.Time.Advance(PackSyncService.ManualCooldown + TimeSpan.FromSeconds(1));
        Assert.Equal(PackSyncOutcome.UpToDate, (await w.Sync.SyncAsync(manual: true, Ct)).LastOutcome);
    }

    [Fact]
    public async Task ClosedGate_ContactsNothing()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        w.Gate.IsOpen = false;

        var status = await w.Sync.SyncAsync(false, Ct);

        Assert.Equal(PackSyncOutcome.NotAllowed, status.LastOutcome);
        Assert.False(status.CanSync);
        Assert.False(status.CouldNotRefresh);
        Assert.Empty(w.Service.Requests);
    }

    [Fact]
    public async Task EmergencySwitch_ClosesTheAccountGate_SoSyncContactsNothing()
    {
        using var h = new AccountHarness();
        h.Service.Handler = r => r.Path == "/v1/config"
            ? FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{System.Text.Json.JsonSerializer.Serialize(h.Issuer.Config(h.Time.GetUtcNow(), emergency: true))}}}""")
            : h.DefaultHandler(r);
        await h.Account.SignInAsync(Ct);
        using var gate = new AccountPackSyncGate(h.Account);

        Assert.False(gate.IsOpen);
        Assert.True(h.Account.Current.PresenceAllowed); // D8: presence is untouched

        using var normal = new AccountHarness();
        await normal.Account.SignInAsync(Ct);
        using var openGate = new AccountPackSyncGate(normal.Account);
        Assert.True(openGate.IsOpen);
    }

    [Fact]
    public async Task Restart_ShowsTheCachedPacksWithoutAnyRequest()
    {
        using var w = new PackWorld();
        w.Publish(3, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);
        w.Service.Requests.Clear();

        var restarted = w.Restart();

        Assert.Equal(2, restarted.Synced!.Index.Entries.Count);
        Assert.Equal(w.Time.GetUtcNow(), restarted.Status.LastSuccessAt);
        Assert.Empty(w.Service.Requests);
    }

    [Fact]
    public async Task OnlyTheCdnHostAndPackPathsAreUsed_ForFiles()
    {
        using var w = new PackWorld();
        w.Publish(1, s_official, Two());
        await w.Sync.SyncAsync(false, Ct);

        Assert.All(w.Service.Requests, r => Assert.Contains(r.Host, new[] { PackWorld.ApiHost, PackWorld.CdnHost }));
        Assert.All(w.FileRequests, r => Assert.Matches("^/packs/[0-9a-f]{64}\\.(png|jpg)$", r.Path));
        Assert.All(w.IndexRequests, r => Assert.Equal(PackWorld.ApiHost, r.Host));
    }
}
