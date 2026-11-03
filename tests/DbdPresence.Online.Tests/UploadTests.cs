// FR-ONL-09 and FR-ONL-11 (app side, V2-S5): the shared contract fixture tests/fixtures/uploads/contract.json (also checked against the real
// Worker by service/test/contract-uploads.test.ts) parses into the app's types; uploads are checked on the PC before they leave it, need the
// policy and online features, go only to the API host in the one multipart shape the service reads, and the list survives a restart and is
// forgotten on sign-out.

using System.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Uploads;

namespace DbdPresence.Online.Tests;

public sealed class UploadTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly JsonElement s_contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "uploads", "contract.json"))).RootElement;

    private static string Body(string member) => s_contract.GetProperty(member).GetRawText();

    private static UploadManager NewManager(AccountHarness h) => new(h.Account, h.Api, h.Files, "online/uploads.json", h.Time, h.Log);

    private static async Task<AccountHarness> SignedInAsync()
    {
        var h = new AccountHarness();
        h.Service.Handler = Wrap(h, _ => null);
        Assert.True((await h.Account.SignInAsync(Ct)).Succeeded);
        Assert.True(h.Account.Current.OnlineFeaturesAllowed);
        return h;
    }

    [Fact]
    public void TheRosterReporterFlag_IsReadFromTheSameAnswer_AndIsNullWhenTheServiceDoesNotSayIt()
    {
        Assert.False(ServiceClient.ParseRosterReporter(s_contract.GetProperty("me")));
        using var yes = JsonDocument.Parse("""{"user":{"rosterReporter":true}}""");
        Assert.True(ServiceClient.ParseRosterReporter(yes.RootElement));
        foreach (var text in new[] { "{}", """{"user":{}}""", """{"user":{"rosterReporter":"yes"}}""", """{"user":5}""" })
        {
            using var doc = JsonDocument.Parse(text);
            Assert.Null(ServiceClient.ParseRosterReporter(doc.RootElement));
        }
    }

    [Fact]
    public void ContractFixture_ParsesIntoTheAppsTypes()
    {
        var list = ServiceClient.ParseUploadList(s_contract.GetProperty("me").GetProperty("uploads"), CdnHost);
        Assert.Equal(300, list.Limit);
        Assert.Null(list.BlockedReason);
        // The per-person quota (D28, FR-ONL-15).
        Assert.Equal(new UploadUsage(48213, 1, 62_914_560, 300), list.Usage);
        var item = Assert.Single(list.Items);
        Assert.Equal("3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d", item.Id);
        Assert.Equal("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png", item.Url);
        Assert.Equal("png", item.Format);
        Assert.Equal(48213, item.Size);
        Assert.Equal((512, 512), (item.Width, item.Height));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), item.CreatedAt);

        var created = ServiceClient.ParseUpload(s_contract.GetProperty("created").GetProperty("body").GetProperty("upload"), CdnHost);
        Assert.Equal(item, created);
        foreach (var error in s_contract.GetProperty("errors").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, error.GetProperty("body").GetProperty("error").ValueKind);
        }
    }

    private const string CdnHost = "cdn.arcavale.com";
    private const string GoodUrl = "https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png";

    [Theory]
    [InlineData("http://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://evil.example/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com.evil.example/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn-staging.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com:443/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://me:pw@cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png?track=1")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.png#x")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcde.png")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdeg.png")]
    [InlineData("https://cdn.arcavale.com/u/0123456789abcdef0123456789abcdef.svg")]
    [InlineData("https://cdn.arcavale.com/packs/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://cdn.arcavale.com/u/../packs/0123456789abcdef0123456789abcd.png")]
    public void AnUploadAddressOtherThanExactlyOnTheContentHost_IsRefused(string url)
    {
        var tampered = JsonDocument.Parse(Body("me").Replace(GoodUrl, url, StringComparison.Ordinal)).RootElement;
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(tampered.GetProperty("uploads"), CdnHost));
        Assert.False(UploadPolicy.IsUploadUrl(url, CdnHost));
    }

    [Theory]
    [InlineData("3F1C2B9A-7D4E-4B8A-9C2D-1E0F3A4B5C6D")]
    [InlineData("3f1c2b9a7d4e4b8a9c2d1e0f3a4b5c6d")]
    [InlineData("{3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d}")]
    [InlineData("------------------------------------")]
    [InlineData("../v1/me")]
    public void AnUploadIdOtherThanALowerCaseUuid_IsRefused_InAnswersAndBeforeADelete(string id)
    {
        var tampered = JsonDocument.Parse(Body("me").Replace("3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d", id, StringComparison.Ordinal)).RootElement;
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(tampered.GetProperty("uploads"), CdnHost));
        Assert.False(UploadPolicy.IsUploadId(id));
        Assert.True(UploadPolicy.IsUploadId("3f1c2b9a-7d4e-4b8a-9c2d-1e0f3a4b5c6d"));
    }

    [Fact]
    public async Task ADeleteByAnInvalidId_IsNeverSent()
    {
        using var h = await SignedInAsync();
        using var uploads = NewManager(h);
        var result = await uploads.DeleteByIdAsync("../v1/me", Ct);
        Assert.False(result.Succeeded);
        Assert.Equal(UploadManager.InvalidId, result.Code);
        Assert.DoesNotContain(h.Service.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public void AnUploadWithABadHashOrWithoutAKnownHost_IsRefused()
    {
        var badHash = JsonDocument.Parse(Body("me").Replace("5f2b1e0c", "5F2B1E0C", StringComparison.Ordinal)).RootElement;
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(badHash.GetProperty("uploads"), CdnHost));
        var good = JsonDocument.Parse(Body("me")).RootElement;
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(good.GetProperty("uploads"), null));
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(good.GetProperty("uploads"), "cdn-staging.arcavale.com"));
        Assert.True(UploadPolicy.IsUploadUrl(GoodUrl, CdnHost));
        Assert.True(UploadPolicy.IsUploadUrl(GoodUrl.Replace(".png", ".jpg", StringComparison.Ordinal), CdnHost));
    }

    [Fact]
    public async Task AStoredListWithAForeignAddress_IsIgnored()
    {
        using var h = new AccountHarness();
        h.Files.WriteAllBytesDurable(
            "online/uploads.json",
            Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"policyAcceptedAt":null,"known":true,"limit":20,"blocked":null,"items":[{"id":"x","url":"https://evil.example/u/0123456789abcdef0123456789abcdef.png","sha256":"{{new string('a', 64)}}","format":"png","size":1,"width":1,"height":1,"createdAt":"2026-11-02T10:00:00.000Z"}]}"""));
        using var uploads = NewManager(h);
        Assert.Equal(UploadsSnapshot.Empty, uploads.Current);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Upload_RefusesAnAnswerWhoseHashIsNotTheHashOfTheBytesSent()
    {
        // The fixture's answer names a hash that is not the hash of these bytes.
        using var h = await SignedInAsync();
        h.Service.Handler = Wrap(h, r => r.Method == HttpMethod.Post && r.Path == "/v1/uploads" ? FakeService.Json(201, s_contract.GetProperty("created").GetProperty("body").GetRawText()) : null);
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        var result = await uploads.UploadAsync(TestPngs.Rgb(64, 48, 4), Ct);
        Assert.False(result.Succeeded);
        Assert.Equal(UploadMessages.AnswerMismatch, result.Message);
        Assert.Empty(uploads.Current.Items);
    }

    [Fact]
    public void MultipartBody_IsTheOneShapeTheServiceReads()
    {
        var body = ServiceClient.MultipartBody("dbdp-abc", [1, 2, 3], "image/png");
        var text = Encoding.Latin1.GetString(body);
        Assert.Equal("--dbdp-abc\r\nContent-Disposition: form-data; name=\"file\"; filename=\"image\"\r\nContent-Type: image/png\r\n\r\n\u0001\u0002\u0003\r\n--dbdp-abc--\r\n", text);
    }

    [Fact]
    public async Task Upload_NeedsThePolicyFirst_AndSendsNothingBefore()
    {
        using var h = await SignedInAsync();
        using var uploads = NewManager(h);
        var result = await uploads.UploadAsync(TestPngs.Rgb(64, 64, 1), Ct);
        Assert.False(result.Succeeded);
        Assert.Equal(UploadMessages.PolicyRequired, result.Message);
        Assert.Empty(h.Service.To("/v1/uploads"));
    }

    [Fact]
    public async Task Upload_RefusesFilesThatAreNotAStrictPngWithinTheLimits_WithoutSendingThem()
    {
        using var h = await SignedInAsync();
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        Assert.Equal(UploadMessages.NotPng, (await uploads.UploadAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, Ct)).Message);
        var withTrailing = TestPngs.Rgb(32, 32, 2).Concat(new byte[] { 0x3C, 0x21 }).ToArray();
        Assert.Equal(UploadMessages.NotPng, (await uploads.UploadAsync(withTrailing, Ct)).Message);
        Assert.Equal(UploadMessages.TooLarge, (await uploads.UploadAsync(TestPngs.Rgb(1025, 4, 3), Ct)).Message);
        Assert.Empty(h.Service.To("/v1/uploads"));
    }

    [Fact]
    public async Task Upload_GoesToTheApiHostAsMultipart_AndTheListUpdatesAndSurvivesARestart()
    {
        using var h = await SignedInAsync();
        var png = TestPngs.Rgb(64, 48, 4);
        h.Service.Handler = Wrap(h, r => r.Method == HttpMethod.Post && r.Path == "/v1/uploads" ? FakeService.Json(201, CreatedFor(png)) : null);
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        var changed = 0;
        uploads.Changed += (_, _) => changed++;
        var result = await uploads.UploadAsync(png, Ct);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(UploadMessages.Uploaded, result.Message);
        var sent = Assert.Single(h.Service.To("/v1/uploads"));
        Assert.Equal(AccountHarness.Host, sent.Host);
        Assert.StartsWith("Bearer at_", sent.Authorization, StringComparison.Ordinal);
        Assert.StartsWith("--dbdp-", sent.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"file\"", sent.Body, StringComparison.Ordinal);
        Assert.Single(uploads.Current.Items);
        Assert.True(changed >= 1);
        Assert.Equal(GoodUrl, uploads.Current.UrlBySha256[ShaOf(png)]);

        using var again = NewManager(h);
        Assert.True(again.Current.PolicyAccepted);
        Assert.Equal(uploads.Current.Items, again.Current.Items);
    }

    [Fact]
    public async Task Refresh_ReadsTheListFromGetMe()
    {
        using var h = await SignedInAsync();
        using var uploads = NewManager(h);
        Assert.False(uploads.Current.IsKnown);
        var result = await uploads.RefreshAsync(Ct);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(uploads.Current.IsKnown);
        Assert.Equal(300, uploads.Current.Limit);
        Assert.Single(uploads.Current.Items);
        Assert.Equal(new UploadUsage(48213, 1, 62_914_560, 300), uploads.Current.Usage);
        Assert.Equal(48213, uploads.Current.UsedBytes);

        // The quota survives a restart with the list.
        using var again = NewManager(h);
        Assert.Equal(uploads.Current.Usage, again.Current.Usage);
    }

    [Fact]
    public void AnOlderServiceWithoutTheQuota_IsRead_AndAPartOrNegativeQuotaIsRefused()
    {
        var older = JsonDocument.Parse(Body("me")
            .Replace("\"limitBytes\": 62914560,", string.Empty, StringComparison.Ordinal)
            .Replace("\"limitFiles\": 300,", string.Empty, StringComparison.Ordinal)
            .Replace("\"usedBytes\": 48213,", string.Empty, StringComparison.Ordinal)
            .Replace("\"usedFiles\": 1,", string.Empty, StringComparison.Ordinal)).RootElement;
        var list = ServiceClient.ParseUploadList(older.GetProperty("uploads"), CdnHost);
        Assert.Null(list.Usage);
        Assert.Equal(48213, new UploadsSnapshot(list.Items, list.Limit, null, false, true).UsedBytes);

        var part = JsonDocument.Parse(Body("me").Replace("\"usedFiles\": 1,", string.Empty, StringComparison.Ordinal)).RootElement;
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(part.GetProperty("uploads"), CdnHost));
        var negative = JsonDocument.Parse(Body("me").Replace("\"usedBytes\": 48213", "\"usedBytes\": -1", StringComparison.Ordinal)).RootElement;
        Assert.Throws<JsonException>(() => ServiceClient.ParseUploadList(negative.GetProperty("uploads"), CdnHost));
    }

    [Theory]
    [InlineData("bytes", "Your online storage is full (0 of 60 MB used)")]
    [InlineData("files", "holds at most 300 pictures")]
    public async Task TheQuotaRefusal_SaysWhichLimitIsFull(string limit, string expected)
    {
        using var h = await SignedInAsync();
        h.Service.Handler = Wrap(h, r => r.Method == HttpMethod.Post && r.Path == "/v1/uploads"
            ? FakeService.Json(409, $$"""{"schemaVersion":1,"error":"upload_limit","limit":"{{limit}}"}""")
            : r.Method == HttpMethod.Get && r.Path == "/v1/me" ? FakeService.Json(200, Body("me").Replace("\"usedBytes\": 48213", "\"usedBytes\": 0", StringComparison.Ordinal)) : null);
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        await uploads.RefreshAsync(Ct);
        var result = await uploads.UploadAsync(TestPngs.Rgb(16, 16, 9), Ct);
        Assert.False(result.Succeeded);
        Assert.Equal("upload_limit", result.Code);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(409, """{"schemaVersion":1,"error":"upload_limit"}""", "limit of")]
    [InlineData(403, """{"schemaVersion":1,"error":"uploads_blocked"}""", UploadMessages.Blocked)]
    [InlineData(403, """{"schemaVersion":1,"error":"online_features_off"}""", UploadMessages.OnlineOff)]
    [InlineData(507, """{"schemaVersion":1,"error":"storage_full"}""", UploadMessages.StorageFull)]
    [InlineData(507, """{"schemaVersion":1,"error":"upload_storage_full"}""", UploadMessages.StorageFull)]
    [InlineData(422, """{"schemaVersion":1,"error":"image_rejected","reason":"metadata_not_allowed"}""", "extra data")]
    [InlineData(413, """{"schemaVersion":1,"error":"body_too_large"}""", UploadMessages.TooLarge)]
    [InlineData(403, """{"schemaVersion":1,"error":"invite_required"}""", UploadMessages.NeedsInvite)]
    public async Task Upload_ExplainsEachRefusal(int status, string body, string expected)
    {
        using var h = await SignedInAsync();
        h.Service.Handler = Wrap(h, r => r.Path == "/v1/uploads" ? FakeService.Json(status, body) : null);
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        var result = await uploads.UploadAsync(TestPngs.Rgb(16, 16, 5), Ct);
        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.Empty(uploads.Current.Items);
        if (body.Contains("uploads_blocked", StringComparison.Ordinal))
        {
            Assert.Equal("uploads_blocked", uploads.Current.BlockedReason);
        }
    }

    [Fact]
    public async Task Upload_WhenTheServiceIsDown_IsAnOutage_AndPresenceStaysOn()
    {
        using var h = await SignedInAsync();
        h.Service.Handler = Wrap(h, r => r.Path == "/v1/uploads" ? throw new HttpRequestException("down") : null);
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        var result = await uploads.UploadAsync(TestPngs.Rgb(16, 16, 6), Ct);
        Assert.Equal(AccountFailure.Offline, result.Failure);
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task Delete_RemovesTheUpload_AndANotFoundAnswerAlsoForgetsIt()
    {
        using var h = await SignedInAsync();
        var deletes = 0;
        h.Service.Handler = Wrap(h, r => r.Method == HttpMethod.Delete && r.Path.StartsWith("/v1/uploads/", StringComparison.Ordinal)
            ? (deletes++ == 0 ? FakeService.Json(200, s_contract.GetProperty("deleted").GetProperty("body").GetRawText()) : FakeService.Json(404, """{"schemaVersion":1,"error":"not_found"}"""))
            : null);
        using var uploads = NewManager(h);
        await uploads.RefreshAsync(Ct);
        var id = uploads.Current.Items[0].Id;
        var deleted = await uploads.DeleteAsync(id, Ct);
        Assert.True(deleted.Succeeded, deleted.Message);
        Assert.Contains("Discord may keep showing", deleted.Message, StringComparison.Ordinal);
        Assert.Empty(uploads.Current.Items);
        Assert.Equal($"/v1/uploads/{id}", Assert.Single(h.Service.Requests, r => r.Method == HttpMethod.Delete).Path);

        await uploads.RefreshAsync(Ct);
        var gone = await uploads.DeleteAsync(id, Ct);
        Assert.True(gone.Succeeded);
        Assert.Equal(UploadMessages.NotFound, gone.Message);
        Assert.Empty(uploads.Current.Items);
    }

    [Fact]
    public async Task Delete_SaysWhenTheStoredCopyIsDeletedLater()
    {
        using var h = await SignedInAsync();
        h.Service.Handler = Wrap(h, r => r.Method == HttpMethod.Delete && r.Path.StartsWith("/v1/uploads/", StringComparison.Ordinal)
            ? FakeService.Json(200, """{"schemaVersion":1,"deleted":true,"fileRemoved":false}""")
            : null);
        using var uploads = NewManager(h);
        await uploads.RefreshAsync(Ct);
        var result = await uploads.DeleteAsync(uploads.Current.Items[0].Id, Ct);
        Assert.True(result.Succeeded);
        Assert.Equal(UploadMessages.DeletedFilePending, result.Message);
        Assert.Empty(uploads.Current.Items);
    }

    [Fact]
    public async Task DeleteMyAccount_SaysWhenUploadedFilesAreDeletedLater()
    {
        using var h = await SignedInAsync();
        h.Service.Handler = Wrap(h, r => r.Method == HttpMethod.Delete && r.Path == "/v1/me" ? FakeService.Json(200, """{"schemaVersion":1,"deleted":true,"filesRemoved":false}""") : null);
        var result = await h.Account.DeleteAccountAsync(Ct);
        Assert.True(result.Succeeded);
        Assert.Equal(Account.AccountMessages.DeletedFilesPending, result.Message);
        Assert.False(h.Account.Current.IsSignedIn);
    }

    [Fact]
    public async Task SigningOut_ForgetsTheListAndThePolicy()
    {
        using var h = await SignedInAsync();
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        await uploads.RefreshAsync(Ct);
        Assert.NotEmpty(uploads.Current.Items);
        await h.Account.SignOutAsync(Ct);
        Assert.Equal(UploadsSnapshot.Empty, uploads.Current);
        Assert.False(h.Files.Exists("online/uploads.json"));
    }

    [Fact]
    public async Task WithoutOnlineFeatures_NothingIsSent()
    {
        using var h = new AccountHarness();
        using var uploads = NewManager(h);
        uploads.AcceptPolicy();
        var result = await uploads.UploadAsync(TestPngs.Rgb(16, 16, 7), Ct);
        Assert.Equal(AccountFailure.SignInRequired, result.Failure);
        Assert.Empty(h.Service.Requests);
    }

    [Fact]
    public async Task ADamagedStoredList_IsIgnored()
    {
        using var h = new AccountHarness();
        h.Files.WriteAllBytesDurable("online/uploads.json", "{not json"u8);
        using var uploads = NewManager(h);
        Assert.Equal(UploadsSnapshot.Empty, uploads.Current);
        Assert.Contains(h.Log.Lines, l => l.Contains("upload list could not be read", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    private static string ShaOf(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>The fixture's 201 answer, naming the hash of <paramref name="png"/> as the real service does.</summary>
    private static string CreatedFor(byte[] png) =>
        s_contract.GetProperty("created").GetProperty("body").GetRawText().Replace("5f2b1e0c9d8a7b6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a3b2c", ShaOf(png), StringComparison.Ordinal);

    private static Func<Recorded, HttpResponseMessage> Wrap(AccountHarness h, Func<Recorded, HttpResponseMessage?> uploads) =>
        request => uploads(request) ?? (request.Method == HttpMethod.Get && request.Path == "/v1/me" ? FakeService.Json(200, Body("me")) : h.DefaultHandler(request));
}
