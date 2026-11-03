// FR-PACK-15 and FR-ONL-15 (app side, S5b-6; PLAN_V2_S5B §2.6, D28): the saving-online queue (PictureUploader) behind the Characters page's
// IPictureUploadSink. Nothing goes online before the picture rules are accepted or while signed out (the queue checks again); pictures in use go
// first, then in the order added; a saved picture's hash maps to its unlisted address; 409 upload_limit stops the queue ("storage full") until
// space is freed; 507 upload_storage_full pauses it until GET /v1/me allows uploads; a daily 429 waits; outages are retried with backoff, at most
// three times a picture a day; the work survives a restart; removing a picture deletes its online copy (also later, after an outage).

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Core.Pictures;
using DbdPresence.Online.Uploads;

namespace DbdPresence.Online.Tests;

public sealed class PictureUploadQueueTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task NothingIsQueuedOrSent_BeforeTheRulesAreAccepted_OrWhileSignedOut()
    {
        using var q = await QueueHarness.CreateAsync(rules: PictureRules.Unset);
        var a = q.Picture(40);
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Empty(q.Queue.Waiting);
        q.Rules = PictureRules.Declined;
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Empty(q.Queue.Waiting);
        Assert.Empty(q.Service.Posts);

        using var signedOut = await QueueHarness.CreateAsync(signIn: false);
        signedOut.Queue.Enqueue(Req("the-trapper", signedOut.Picture(41)));
        await signedOut.SettleAsync();
        Assert.Empty(signedOut.Queue.Waiting);
        Assert.Empty(signedOut.H.Service.Requests);
    }

    [Fact]
    public async Task DecliningTheRulesLater_ForgetsTheWaitingPictures_AndSendsNothingMore()
    {
        using var q = await QueueHarness.CreateAsync();
        q.Service.Down = true;
        q.Queue.Enqueue(Req("the-trapper", q.Picture(42)));
        await q.SettleAsync();
        Assert.Single(q.Queue.Waiting);
        var posts = q.Service.Posts.Count;
        q.Rules = PictureRules.Declined;
        q.Service.Down = false;
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Empty(q.Queue.Waiting);
        Assert.Equal(posts, q.Service.Posts.Count);
    }

    [Fact]
    public async Task WhenOnlineFeaturesStop_WhileSignedIn_TheWaitingPicturesStayAndNothingIsSent()
    {
        // Round-2 QA review F4: the gate in the upload pass itself (an expired licence ends online features without signing out).
        using var q = await QueueHarness.CreateAsync();
        q.Service.Down = true;
        q.Queue.Enqueue(Req("the-trapper", q.Picture(46)));
        await q.SettleAsync();
        Assert.Single(q.Queue.Waiting);
        var posts = q.Service.Posts.Count;
        var requests = q.H.Service.Requests.Count;

        q.H.Time.Advance(TimeSpan.FromDays(8));
        q.H.Account.Reevaluate();
        Assert.False(q.H.Account.Current.OnlineFeaturesAllowed);
        Assert.True(q.H.Account.Current.IsSignedIn);
        q.Service.Down = false;
        q.Queue.CheckNow();
        await q.SettleAsync();

        Assert.Single(q.Queue.Waiting);
        Assert.Equal(posts, q.Service.Posts.Count);
        Assert.Equal(requests, q.H.Service.Requests.Count);
    }

    [Fact]
    public async Task SavesEachPicture_InUseFirst_ThenInTheOrderAdded_AndMapsItsHashToTheUnlistedAddress()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(43);
        var b = q.Picture(44);
        var c = q.Picture(45);
        q.Service.HoldRefresh();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Queue.Enqueue(Req("the-wraith", b));
        q.Queue.Enqueue(Req("the-nurse", c, inUse: true));
        q.Service.Release();
        await q.SettleAsync();

        Assert.Equal([c, a, b], q.Service.Posts);
        Assert.Empty(q.Queue.Waiting);
        var urls = q.Uploads.Current.UrlBySha256;
        foreach (var picture in new[] { a, b, c })
        {
            Assert.True(UploadPolicy.IsUploadUrl(urls[Sha(picture)], "cdn.arcavale.com"));
        }

        // The storage meter's figures follow each saved picture.
        Assert.Equal(new UploadUsage(a.Length + b.Length + c.Length, 3, QuotaService.DefaultLimitBytes, 300), q.Uploads.Current.Usage);
        Assert.Null(q.Uploads.Current.Saving);

        // A picture already online is not sent again.
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Equal(3, q.Service.Posts.Count);
    }

    [Fact]
    public async Task QuotaFull_StopsTheQueue_MarksTheRestStorageFull_AndRemovingAPictureResumesIt()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(46);
        var b = q.Picture(47);
        var c = q.Picture(48);
        q.Service.LimitBytes = Math.Max(a.Length, c.Length) + b.Length; // room for two
        q.Service.HoldRefresh();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Queue.Enqueue(Req("the-wraith", b));
        q.Queue.Enqueue(Req("the-nurse", c));
        q.Service.Release();
        await q.SettleAsync();

        // The third is refused on this PC from the reported quota, before anything is sent.
        Assert.Equal([a, b], q.Service.Posts);
        Assert.Equal([Sha(c)], q.Queue.Waiting);
        Assert.Equal(PictureSaveState.StorageFull, q.Uploads.Current.Saving![Sha(c)]);
        Assert.Contains("Your online storage is full", q.Uploads.Current.SavingMessage, StringComparison.Ordinal);

        // Nothing is replaced or deleted automatically; a later picture stays queued too.
        var d = q.Picture(49);
        q.Queue.Enqueue(Req("the-hillbilly", d));
        await q.SettleAsync();
        Assert.Equal(2, q.Service.Posts.Count);
        Assert.Equal(2, q.Service.Stored.Count);

        // Removing a picture deletes its online copy, frees its bytes and restarts the queue in the same order.
        q.Queue.Remove(Sha(a));
        await q.SettleAsync();
        Assert.Single(q.Service.Deletes);
        Assert.Equal([a, b, c], q.Service.Posts);
        Assert.Equal([Sha(d)], q.Queue.Waiting);
        Assert.Equal(PictureSaveState.StorageFull, q.Uploads.Current.Saving![Sha(d)]);
    }

    [Fact]
    public async Task TheServicesQuotaRefusal_NamesTheLimit_AndStopsTheQueueWithoutRetrying()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(50);
        var b = q.Picture(51);
        q.Service.RefuseFiles = true; // another PC took the last place: the service refuses although this PC thought it fits
        q.Service.HoldRefresh();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Queue.Enqueue(Req("the-wraith", b));
        q.Service.Release();
        await q.SettleAsync();
        Assert.Equal([a], q.Service.Posts);
        Assert.Equal(PictureSaveState.StorageFull, q.Uploads.Current.Saving![Sha(a)]);
        Assert.Equal(PictureSaveState.StorageFull, q.Uploads.Current.Saving![Sha(b)]);

        // Reading GET /v1/me again with no more room does not restart it (no loop against 409).
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);

        // More room reported by the service (the owner raised the limit, or a picture was deleted elsewhere) restarts it.
        q.Service.RefuseFiles = false;
        q.Service.LimitFiles = 400;
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Equal([a, a, b], q.Service.Posts);
        Assert.Empty(q.Queue.Waiting);
    }

    [Fact]
    public async Task AFullQueue_StaysStoppedAcrossARestart_UntilTheServiceReportsRoom()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(67);
        var b = q.Picture(68);
        q.Service.LimitBytes = a.Length;
        q.Service.HoldRefresh();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Queue.Enqueue(Req("the-wraith", b));
        q.Service.Release();
        await q.SettleAsync();
        Assert.Equal([a], q.Service.Posts);
        Assert.Equal(PictureSaveState.StorageFull, q.Uploads.Current.Saving![Sha(b)]);

        q.Restart();
        q.H.Time.Advance(PictureUploader.StartDelay);
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);
        Assert.Equal(PictureSaveState.StorageFull, q.Uploads.Current.Saving![Sha(b)]);

        q.Service.LimitBytes = a.Length + b.Length;
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Equal([a, b], q.Service.Posts);
    }

    [Fact]
    public async Task StoragePaused_PausesUntilGetMeAllowsUploads_AndResumesThen()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(52);
        q.Service.Paused = true;
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();

        // GET /v1/me already said paused: nothing is sent, and the page says why.
        Assert.Empty(q.Service.Posts);
        Assert.Equal(PictureSaveState.Paused, q.Uploads.Current.Saving![Sha(a)]);
        Assert.Equal(UploadMessages.SavingPaused, q.Uploads.Current.SavingMessage);
        Assert.Equal("upload_storage_full", q.Uploads.Current.BlockedReason);

        // Still paused at the next check six hours later; allowed at the one after.
        q.H.Time.Advance(PictureUploader.RefreshInterval);
        await q.SettleAsync();
        Assert.Empty(q.Service.Posts);
        q.Service.Paused = false;
        q.H.Time.Advance(PictureUploader.RefreshInterval);
        await q.SettleAsync();
        Assert.Equal([a], q.Service.Posts);
        Assert.Null(q.Uploads.Current.Saving);
    }

    [Fact]
    public async Task A507DuringAnUpload_PausesTheQueue_UntilTheNextCheck()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(53);
        var b = q.Picture(54);
        q.Service.PausedOnUpload = true; // GET /v1/me does not know yet; the upload answers 507
        q.Service.HoldRefresh();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Queue.Enqueue(Req("the-wraith", b));
        q.Service.Release();
        await q.SettleAsync();
        Assert.Equal([a], q.Service.Posts);
        Assert.Equal(PictureSaveState.Paused, q.Uploads.Current.Saving![Sha(b)]);

        q.Service.PausedOnUpload = false;
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Equal([a, a, b], q.Service.Posts);
    }

    [Fact]
    public async Task ABlockedAccount_KeepsPicturesPreviewOnly_WithThePlainReason()
    {
        using var q = await QueueHarness.CreateAsync();
        q.Service.Blocked = true;
        var a = q.Picture(55);
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Empty(q.Service.Posts);
        Assert.Equal(PictureSaveState.Blocked, q.Uploads.Current.Saving![Sha(a)]);
        Assert.Equal(UploadMessages.SavingBlocked, q.Uploads.Current.SavingMessage);
    }

    [Fact]
    public async Task ADailyLimit_WaitsForRetryAfter_AndSaysSavingResumesTomorrow()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(56);
        // The service's wait (the app never waits more than an hour before asking again, ServiceClient.MaxRetryAfter).
        q.Service.RateLimitedFor = TimeSpan.FromMinutes(30);
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);
        Assert.Equal(PictureSaveState.DailyLimit, q.Uploads.Current.Saving![Sha(a)]);
        Assert.Equal(UploadMessages.SavingTomorrow, q.Uploads.Current.SavingMessage);

        q.Service.RateLimitedFor = null;
        q.H.Time.Advance(TimeSpan.FromMinutes(29));
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);
        q.H.Time.Advance(TimeSpan.FromMinutes(1));
        await q.SettleAsync();
        Assert.Equal(2, q.Service.Posts.Count);
        Assert.Empty(q.Queue.Waiting);
    }

    [Fact]
    public async Task AnOutage_IsRetriedAfterOneAndFiveMinutes_AtMostThreeTimesADay_ThenTheNextDay()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(57);
        q.Service.Down = true;
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);
        Assert.Equal(PictureSaveState.Offline, q.Uploads.Current.Saving![Sha(a)]);
        Assert.Equal(UploadMessages.SavingOffline, q.Uploads.Current.SavingMessage);

        q.H.Time.Advance(TimeSpan.FromSeconds(59));
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);
        q.H.Time.Advance(TimeSpan.FromSeconds(1));
        await q.SettleAsync();
        Assert.Equal(2, q.Service.Posts.Count);
        q.H.Time.Advance(TimeSpan.FromMinutes(5));
        await q.SettleAsync();
        Assert.Equal(3, q.Service.Posts.Count);
        Assert.Equal(PictureSaveState.Failed, q.Uploads.Current.Saving![Sha(a)]);

        // No fourth attempt today, even when the page hands it over again.
        q.H.Time.Advance(TimeSpan.FromHours(1));
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Equal(3, q.Service.Posts.Count);

        // The next (UTC) day it is tried again.
        q.Service.Down = false;
        var midnight = new DateTimeOffset(q.H.Time.GetUtcNow().UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        q.H.Time.Advance(midnight - q.H.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        await q.SettleAsync();
        Assert.Equal(4, q.Service.Posts.Count);
        Assert.Empty(q.Queue.Waiting);
    }

    [Fact]
    public async Task AFileTheServiceRefuses_IsNotRetried_AndTheRestGoOn()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(58);
        var b = q.Picture(59);
        q.Service.Reject.Add(Sha(a));
        q.Service.HoldRefresh();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Queue.Enqueue(Req("the-wraith", b));
        q.Service.Release();
        await q.SettleAsync();
        Assert.Equal([a, b], q.Service.Posts);
        Assert.Equal(PictureSaveState.Failed, q.Uploads.Current.Saving![Sha(a)]);
        q.H.Time.Advance(TimeSpan.FromHours(2));
        await q.SettleAsync();
        Assert.Equal(2, q.Service.Posts.Count);
    }

    [Fact]
    public async Task AMissingOrChangedFile_IsDropped_AndNothingIsSent()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(60);
        q.Pictures.Remove(Sha(a));
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Empty(q.Service.Posts);
        Assert.Empty(q.Queue.Waiting);
    }

    [Fact]
    public async Task TheWork_SurvivesARestart()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(61);
        q.Service.Down = true;
        q.Queue.Enqueue(Req("the-trapper", a, inUse: true));
        await q.SettleAsync();
        Assert.Single(q.Queue.Waiting);

        q.Restart();
        Assert.Equal([Sha(a)], q.Queue.Waiting);
        q.Service.Down = false;
        q.H.Time.Advance(TimeSpan.FromMinutes(1)); // the start delay and the retry wait
        await q.SettleAsync();
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Equal([a, a], q.Service.Posts);
        Assert.Empty(q.Queue.Waiting);
    }

    [Theory]
    [InlineData("day")]
    [InlineData("key")]
    [InlineData("sha256")]
    [InlineData("bytes")]
    public async Task AQueueFileWithANullField_IsIgnoredWithoutCrashingStartup(string field)
    {
        // Round-2 security review L4: a null where text is expected used to throw ArgumentNullException out of the constructor.
        using var q = await QueueHarness.CreateAsync();
        var day = q.H.Time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var item = new Dictionary<string, object?>
        {
            ["key"] = "character:the-trapper",
            ["sha256"] = new string('a', 64),
            ["bytes"] = 10,
            ["inUse"] = false,
            ["order"] = 1,
            ["attempts"] = 0,
            ["day"] = day,
            ["notBefore"] = null,
            ["failed"] = false,
        };
        item[field] = null;
        q.H.Files.WriteAllBytesDurable(
            "online/picture-queue.json",
            JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, sequence = 1, halt = "None", haltUntil = (string?)null, items = new[] { item }, deletes = Array.Empty<object>() }));

        q.Restart();

        // Either the file is ignored as a whole, or the entry is left out as not valid: never a crash, never a queued picture.
        Assert.Empty(q.Queue.Waiting);
        Assert.Contains(q.H.Log.Lines, l => l.Contains("saving-online queue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADamagedOrHandEditedQueueFile_AddsNothingThePageWouldNot()
    {
        using var q = await QueueHarness.CreateAsync();
        var path = "online/picture-queue.json";
        q.H.Files.WriteAllBytesDurable(path, "{not json"u8);
        q.Restart();
        Assert.Empty(q.Queue.Waiting);
        Assert.Contains(q.H.Log.Lines, l => l.Contains("saving-online queue could not be read", StringComparison.Ordinal));

        var day = q.H.Time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        q.H.Files.WriteAllBytesDurable(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sequence = 2,
            halt = "None",
            haltUntil = (string?)null,
            items = new object[]
            {
                new { key = "role:killer", sha256 = new string('a', 64), bytes = 10, inUse = false, order = 1, attempts = 0, day, notBefore = (string?)null, failed = false },
                new { key = "character:the-trapper", sha256 = "../../x", bytes = 10, inUse = false, order = 2, attempts = 0, day, notBefore = (string?)null, failed = false },
                new { key = "character:the-trapper", sha256 = new string('b', 64), bytes = 3L * 1024 * 1024, inUse = false, order = 3, attempts = 0, day, notBefore = (string?)null, failed = false },
            },
            // Only the service's exact id form (a lower-case UUID) is kept: the same check as every service answer and every DELETE.
            deletes = new object[]
            {
                new { id = "../v1/me", sha256 = (string?)null },
                new { id = "------------------------------------", sha256 = (string?)null },
                new { id = "3F1C2B9A-7D4E-4B8A-9C2D-1E0F3A4B5C6D", sha256 = (string?)null },
                new { id = "{3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d}", sha256 = (string?)null },
                new { id = "3f1c2b9a7d4e4b8a9c2d1e0f3a4b5c6d", sha256 = (string?)null },
                new { id = "3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d", sha256 = (string?)null },
            },
        }));
        q.Restart();
        Assert.Empty(q.Queue.Waiting);
        Assert.Equal(1, q.Queue.PendingDeletes);
        Assert.Contains(q.H.Log.Lines, l => l.Contains("3 pictures and 5 deletes were left out", StringComparison.Ordinal));

        // The one valid delete is sent (the stand-in answers not_found, which counts as gone), and the queue keeps working.
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Equal(["3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d"], q.Service.Deletes);
        Assert.Equal(0, q.Queue.PendingDeletes);
        var a = q.Picture(69);
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Equal([a], q.Service.Posts);
    }

    [Fact]
    public async Task AnUnexpectedErrorInAPass_IsLogged_AndTheQueueStillRunsAfterwards()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(70);
        q.ReaderFails = true;
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Empty(q.Service.Posts);
        Assert.Contains(q.H.Log.Lines, l => l.Contains("unexpected error (InvalidOperationException)", StringComparison.Ordinal));
        Assert.Equal([Sha(a)], q.Queue.Waiting);

        // Not stuck as "running": the next trigger runs a new pass.
        q.ReaderFails = false;
        q.Queue.CheckNow();
        await q.SettleAsync();
        Assert.Equal([a], q.Service.Posts);
        Assert.Empty(q.Queue.Waiting);
    }

    [Fact]
    public async Task RemovingASavedPicture_DeletesItsOnlineCopy_AndAFailedDeleteIsSentLater()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(62);
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Single(q.Service.Stored);

        q.Service.Down = true;
        q.Queue.Remove(Sha(a));
        await q.SettleAsync();
        Assert.Equal(1, q.Queue.PendingDeletes);
        Assert.Single(q.Service.Stored);

        // It is kept across a restart and sent when the service is back.
        q.Restart();
        Assert.Equal(1, q.Queue.PendingDeletes);
        q.Service.Down = false;
        q.H.Time.Advance(PictureUploader.StartDelay);
        await q.SettleAsync();
        Assert.Empty(q.Service.Stored);
        Assert.Equal(0, q.Queue.PendingDeletes);
        Assert.False(q.Uploads.Current.UrlBySha256.ContainsKey(Sha(a)));
    }

    [Fact]
    public async Task RemovingAPictureWhileItIsBeingSaved_DeletesTheCopyAsSoonAsItIsOnline()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(63);
        q.Queue.CheckNow();
        await q.SettleAsync(); // the list and the quota are known
        q.Service.HoldUpload();
        q.Queue.Enqueue(Req("the-trapper", a));
        q.Service.WaitUntilHeld();
        q.Queue.Remove(Sha(a));
        q.Service.Release();
        await q.SettleAsync();
        Assert.Single(q.Service.Posts);
        Assert.Single(q.Service.Deletes);
        Assert.Empty(q.Service.Stored);
    }

    [Fact]
    public async Task SigningOut_ForgetsTheWaitingPictures_AndRevocationSaysWhy()
    {
        using var q = await QueueHarness.CreateAsync();
        q.Service.Down = true;
        q.Queue.Enqueue(Req("the-trapper", q.Picture(64)));
        await q.SettleAsync();
        Assert.Single(q.Queue.Waiting);
        await q.H.Account.SignOutAsync(Ct);
        await q.SettleAsync();
        Assert.Empty(q.Queue.Waiting);
        Assert.Null(q.Uploads.Current.Saving);
    }

    [Fact]
    public async Task SigningIn_ReadsTheListFirst_SoPicturesSavedFromAnotherPcAreNotSentAgain()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(65);
        q.Service.StoreDirectly(a); // saved from another PC
        q.Queue.Enqueue(Req("the-trapper", a));
        await q.SettleAsync();
        Assert.Empty(q.Service.Posts);
        Assert.True(q.Uploads.Current.UrlBySha256.ContainsKey(Sha(a)));
    }

    [Fact]
    public async Task ARefusedRequest_ForAGenericKeyOrABadHash_IsNotQueued()
    {
        using var q = await QueueHarness.CreateAsync();
        var a = q.Picture(66);
        q.Queue.Enqueue(new PictureUploadRequest("character:generic-killer", Sha(a), a.Length, false));
        q.Queue.Enqueue(new PictureUploadRequest("role:killer", Sha(a), a.Length, false));
        q.Queue.Enqueue(new PictureUploadRequest("character:the-trapper", "not-a-hash", a.Length, false));
        q.Queue.Enqueue(new PictureUploadRequest("character:the-trapper", Sha(a), PictureLimits.MaxFileBytes, false));
        await q.SettleAsync();
        Assert.Empty(q.Queue.Waiting);
        Assert.Empty(q.Service.Posts);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static PictureUploadRequest Req(string id, byte[] png, bool inUse = false) => new(PictureKeys.Of(id), Sha(png), png.Length, inUse);

    /// <summary>The queue over a real upload manager and account, with a stand-in service that keeps uploads and the quota.</summary>
    private sealed class QueueHarness : IDisposable
    {
        private QueueHarness(AccountHarness h, PictureRules rules)
        {
            H = h;
            Rules = rules;
            Service = new QuotaService(h);
            h.Service.Handler = Service.Handle;
            Uploads = new UploadManager(h.Account, h.Api, h.Files, "online/uploads.json", h.Time, h.Log);
            Queue = Build();
        }

        public AccountHarness H { get; }

        public QuotaService Service { get; }

        public UploadManager Uploads { get; private set; }

        public PictureUploader Queue { get; private set; }

        public Dictionary<string, byte[]> Pictures { get; } = new(StringComparer.Ordinal);

        public PictureRules Rules { get; set; }

        /// <summary>When true, reading a picture throws, standing in for any unexpected failure inside a pass.</summary>
        public bool ReaderFails { get; set; }

        public static async Task<QueueHarness> CreateAsync(PictureRules rules = PictureRules.Accepted, bool signIn = true)
        {
            var h = new AccountHarness();
            if (signIn)
            {
                Assert.True((await h.Account.SignInAsync(Ct)).Succeeded);
                Assert.True(h.Account.Current.OnlineFeaturesAllowed);
            }

            return new QueueHarness(h, rules);
        }

        /// <summary>A distinct PNG (its width, below 128, names it to the stand-in service), stored as an own picture.</summary>
        public byte[] Picture(int width)
        {
            var png = TestPngs.Rgb(width, 8, (byte)width);
            Pictures[Sha(png)] = png;
            Service.Known[width] = png;
            return png;
        }

        public async Task SettleAsync()
        {
            for (var i = 0; i < 5; i++)
            {
                await Queue.LastRun;
            }
        }

        /// <summary>The app closes and starts again over the same files.</summary>
        public void Restart()
        {
            Queue.Dispose();
            Uploads.Dispose();
            Uploads = new UploadManager(H.Account, H.Api, H.Files, "online/uploads.json", H.Time, H.Log);
            Queue = Build();
        }

        public void Dispose()
        {
            Service.Release();
            Queue.Dispose();
            Uploads.Dispose();
            H.Dispose();
            Service.Dispose();
        }

        private PictureUploader Build()
        {
            var queue = new PictureUploader(new PictureUploaderDependencies(
                Uploads, H.Account, sha => ReaderFails ? throw new InvalidOperationException("reader") : Pictures.TryGetValue(sha, out var png) ? png : null, () => Rules, H.Files, "online/picture-queue.json", H.Time, H.Log));
            queue.Start();
            return queue;
        }
    }
}

/// <summary>
/// A stand-in for the upload routes: keeps each account's uploads, enforces the quota like the Worker (409 upload_limit naming bytes or files),
/// and answers paused (507), blocked (403), rate-limited (429) or down (503) when told to.
/// </summary>
internal sealed class QuotaService(AccountHarness h) : IDisposable
{
    public const long DefaultLimitBytes = 60L * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly ManualResetEventSlim _released = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private bool _holdRefresh;
    private bool _holdUpload;
    private int _next;

    public Dictionary<int, byte[]> Known { get; } = [];

    public List<(string Id, string Sha256, long Size, string Url)> Stored { get; } = [];

    public List<byte[]> Posts { get; } = [];

    public List<string> Deletes { get; } = [];

    public HashSet<string> Reject { get; } = new(StringComparer.Ordinal);

    public long LimitBytes { get; set; } = DefaultLimitBytes;

    public int LimitFiles { get; set; } = 300;

    public bool Down { get; set; }

    public bool Paused { get; set; }

    public bool PausedOnUpload { get; set; }

    public bool Blocked { get; set; }

    public bool RefuseFiles { get; set; }

    public TimeSpan? RateLimitedFor { get; set; }

    public void HoldRefresh()
    {
        _released.Reset();
        _holdRefresh = true;
    }

    public void HoldUpload()
    {
        _released.Reset();
        _held.Reset();
        _holdUpload = true;
    }

    public void WaitUntilHeld() => Assert.True(_held.Wait(TimeSpan.FromSeconds(10)));

    public void Release()
    {
        _holdRefresh = false;
        _holdUpload = false;
        _released.Set();
    }

    public void Dispose()
    {
        _released.Dispose();
        _held.Dispose();
    }

    public void StoreDirectly(byte[] png)
    {
        lock (_lock)
        {
            Store(png);
        }
    }

    public HttpResponseMessage Handle(Recorded request)
    {
        if (request.Path == "/v1/me" && request.Method == HttpMethod.Get)
        {
            if (_holdRefresh)
            {
                Assert.True(_released.Wait(TimeSpan.FromSeconds(10)));
            }

            return Down ? Unavailable() : FakeService.Json(200, MeJson());
        }

        if (request.Path == "/v1/uploads" && request.Method == HttpMethod.Post)
        {
            if (_holdUpload)
            {
                _held.Set();
                Assert.True(_released.Wait(TimeSpan.FromSeconds(10)));
            }

            return Upload(request);
        }

        if (request.Path.StartsWith("/v1/uploads/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
        {
            if (Down)
            {
                return Unavailable();
            }

            lock (_lock)
            {
                var id = request.Path["/v1/uploads/".Length..];
                Deletes.Add(id);
                return Stored.RemoveAll(s => s.Id == id) > 0
                    ? FakeService.Json(200, """{"schemaVersion":1,"deleted":true,"fileRemoved":true}""")
                    : FakeService.Json(404, """{"schemaVersion":1,"error":"not_found"}""");
            }
        }

        return h.DefaultHandler(request);
    }

    private static HttpResponseMessage Unavailable() => FakeService.Json(503, """{"schemaVersion":1,"error":"internal"}""");

    private HttpResponseMessage Upload(Recorded request)
    {
        // The body is the multipart form; the PNG's width (below 128, so it survives the text decoding) names the picture.
        var at = request.Body.IndexOf("IHDR", StringComparison.Ordinal);
        var width = (request.Body[at + 6] << 8) | request.Body[at + 7];
        var png = Known[width];
        lock (_lock)
        {
            Posts.Add(png);
            if (Down)
            {
                return Unavailable();
            }

            if (RateLimitedFor is { } wait)
            {
                return FakeService.Json(429, """{"schemaVersion":1,"error":"rate_limited"}""", wait);
            }

            if (Blocked)
            {
                return FakeService.Json(403, """{"schemaVersion":1,"error":"uploads_blocked"}""");
            }

            if (Paused || PausedOnUpload)
            {
                return FakeService.Json(507, """{"schemaVersion":1,"error":"upload_storage_full"}""");
            }

            if (Reject.Contains(Sha(png)))
            {
                return FakeService.Json(422, """{"schemaVersion":1,"error":"image_rejected","reason":"chunk_not_allowed"}""");
            }

            var existing = Stored.FirstOrDefault(s => s.Sha256 == Sha(png));
            if (existing.Id is not null)
            {
                return FakeService.Json(200, CreatedJson(existing, true));
            }

            if (RefuseFiles || Stored.Count >= LimitFiles)
            {
                return FakeService.Json(409, """{"schemaVersion":1,"error":"upload_limit","limit":"files"}""");
            }

            if (Stored.Sum(s => s.Size) + png.Length > LimitBytes)
            {
                return FakeService.Json(409, """{"schemaVersion":1,"error":"upload_limit","limit":"bytes"}""");
            }

            return FakeService.Json(201, CreatedJson(Store(png), false));
        }
    }

    private (string Id, string Sha256, long Size, string Url) Store(byte[] png)
    {
        _next++;
        var entry = ($"00000000-0000-4000-8000-{_next:x12}", Sha(png), (long)png.Length, $"https://cdn.arcavale.com/u/{_next:x32}.png");
        Stored.Add(entry);
        return entry;
    }

    private static string UploadJson((string Id, string Sha256, long Size, string Url) s) =>
        JsonSerializer.Serialize(new { id = s.Id, url = s.Url, sha256 = s.Sha256, format = "png", size = s.Size, width = 8, height = 8, createdAt = "2026-10-23T18:00:00.000Z" });

    private static string CreatedJson((string Id, string Sha256, long Size, string Url) s, bool existing) =>
        $$"""{"schemaVersion":1,"upload":{{UploadJson(s)}},"existing":{{(existing ? "true" : "false")}}}""";

    private string MeJson()
    {
        lock (_lock)
        {
            var items = string.Join(",", Stored.Select(UploadJson));
            var blocked = Blocked ? "\"uploads_blocked\"" : Paused ? "\"upload_storage_full\"" : "null";
            var uploads = $$"""
                {"items":[{{items}}],"limit":{{LimitFiles}},"limitBytes":{{LimitBytes}},"limitFiles":{{LimitFiles}},"usedBytes":{{Stored.Sum(s => s.Size)}},"usedFiles":{{Stored.Count}},"blocked":{{blocked}}
                """;
            return """{"schemaVersion":1,"user":{"id":"user-1","displayName":"Test Player","status":"active","access":"granted"},"devices":[],"deviceLimit":3,"uploads":"""
                + uploads + "}}";
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
