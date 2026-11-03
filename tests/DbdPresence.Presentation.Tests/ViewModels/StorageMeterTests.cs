// Covers FR-PACK-17 (the storage meter "x of 60 MB used" from GET /v1/me) and FR-PACK-15 (S5b-6): the running app's picture environment reads the
// per-person quota the service reports (used bytes and the limit, D28) and the saving-online queue's reason for each waiting picture, so the
// Characters page names "storage full", "saving is paused" or "saving resumes tomorrow" instead of "saving", and its status strip says the
// storage is full. The Account page's upload figures use the same quota.
using DbdPresence.Core.Online;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Pictures;
using DbdPresence.Presentation.Characters;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class StorageMeterTests
{
    private const long Mb = 1024 * 1024;

    private static UploadInfo Upload(int n, long size) =>
        new($"00000000-0000-4000-8000-{n:D12}", $"https://cdn.arcavale.com/u/{n:x32}.png", new string((char)('a' + n), 64), "png", size, 64, 64, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheEnvironment_ReportsTheServicesUsageAndLimit_AndBeforeItReportsTheSumOfTheListAndTheDefault60Mb()
    {
        var account = new FakeAccountService();
        var uploads = new FakeUploadService();
        using var host = new Harness();
        var packs = new PackHarness(host);
        using var environment = new OnlinePictureEnvironment(account, uploads, packs.Library);

        uploads.Set(uploads.Current with { Items = [Upload(1, 300_000), Upload(2, 200_000)] });
        Assert.Equal(500_000, environment.UsedBytes);
        Assert.Equal(PictureLimits.OnlineQuotaBytes, environment.LimitBytes);

        // The owner lowered the limit to 40 MB; the service counts 13 MB (it also holds a copy from another PC).
        uploads.Set(uploads.Current with { Usage = new UploadUsage(13 * Mb, 3, 40 * Mb, 300) });
        Assert.Equal(13 * Mb, environment.UsedBytes);
        Assert.Equal(40 * Mb, environment.LimitBytes);
    }

    [Theory]
    [InlineData(PictureSaveState.Waiting, PictureOnlineStatus.Saving)]
    [InlineData(PictureSaveState.StorageFull, PictureOnlineStatus.StorageFull)]
    [InlineData(PictureSaveState.Paused, PictureOnlineStatus.Paused)]
    [InlineData(PictureSaveState.Blocked, PictureOnlineStatus.Blocked)]
    [InlineData(PictureSaveState.DailyLimit, PictureOnlineStatus.DailyLimit)]
    [InlineData(PictureSaveState.Failed, PictureOnlineStatus.Failed)]
    [InlineData(PictureSaveState.Offline, PictureOnlineStatus.ServiceUnreachable)]
    public void TheEnvironment_PassesOnTheQueuesReason(PictureSaveState state, PictureOnlineStatus expected)
    {
        var uploads = new FakeUploadService();
        using var host = new Harness();
        var packs = new PackHarness(host);
        using var environment = new OnlinePictureEnvironment(new FakeAccountService(), uploads, packs.Library);
        var sha = new string('c', 64);
        Assert.Null(environment.SavingStatusOf(sha));
        uploads.Set(uploads.Current with { Saving = new Dictionary<string, PictureSaveState> { [sha] = state } });
        Assert.Equal(expected, environment.SavingStatusOf(sha));
        Assert.Null(environment.SavingStatusOf(new string('d', 64)));
    }

    [Fact]
    public void TheMeter_FollowsTheReportedLimit()
    {
        using var h = new CharactersHarness();
        h.Environment.UsedBytes = 30 * Mb;
        h.Environment.LimitBytes = 40 * Mb;
        h.Environment.Raise();
        h.Page.ShowPacksCommand.Execute(null);
        Assert.Equal("30.0 of 40 MB used", h.Page.StorageText);
        Assert.Equal(0.75, h.Page.StorageFraction, 6);
    }

    [Theory]
    [InlineData(PictureOnlineStatus.StorageFull, "Preview only · storage full")]
    [InlineData(PictureOnlineStatus.Paused, "Preview only · saving is paused")]
    [InlineData(PictureOnlineStatus.DailyLimit, "Saving resumes tomorrow")]
    [InlineData(PictureOnlineStatus.Failed, "Couldn't save online")]
    [InlineData(PictureOnlineStatus.ServiceUnreachable, "Waiting for the service")]
    public async Task AWaitingPicture_ShowsTheQueuesReason_OnTheCharactersPage(PictureOnlineStatus status, string line)
    {
        using var h = new CharactersHarness();
        h.AllowSavingOnline();
        h.Select("K27");
        h.Storage.Files["/in/me.png"] = [1, 2, 3, 4];
        h.Picker.Image = "/in/me.png";
        await h.Page.AddFromPcCommand.ExecuteAsync(null);
        var item = h.Current.Pictures["character:the-onryo"].Items.Single();
        Assert.Equal(PictureTexts.DiscordLine(PictureOnlineStatus.Saving), h.Page.PictureOptions.Single(o => o.Item is not null).DiscordLine);

        h.Environment.Saving[item.Sha256] = status;
        h.Environment.Raise();

        Assert.Equal(line, h.Page.PictureOptions.Single(o => o.Item is not null).DiscordLine);
        if (status == PictureOnlineStatus.StorageFull)
        {
            Assert.Equal(CharactersStatusKind.StorageFull, h.Page.StatusKind);
            Assert.Equal(1, h.Page.StorageFullCount);
            Assert.Contains("Your online storage is full", h.Page.StatusStripText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheAccountPage_ShowsTheQuotaInMegabytes_AndTheQueuesSentence()
    {
        using var host = new Harness();
        var packs = new PackHarness(host);
        var account = new FakeAccountService();
        account.Set(OnlineState.Active);
        var service = new FakeUploadService();
        using var page = new UploadsViewModel(service, account, host.Settings, packs.Library, host.Dispatcher);

        service.Set(service.Current with { Usage = new UploadUsage(13_000_000, 2, 60 * Mb, 300) });
        Assert.Equal("12.4 of 60 MB used · 2 pictures.", page.QuotaText);
        Assert.False(page.IsAtLimit);

        service.Set(service.Current with { Usage = new UploadUsage(60 * Mb, 40, 60 * Mb, 300), SavingMessage = "Your online storage is full." });
        Assert.True(page.IsAtLimit);
        Assert.Equal("60.0 of 60 MB used · 40 pictures. Your online storage is full.", page.QuotaText);

        service.Set(service.Current with { BlockedReason = "upload_storage_full" });
        Assert.Contains("paused for everyone", page.BlockedText, StringComparison.Ordinal);
    }
}
