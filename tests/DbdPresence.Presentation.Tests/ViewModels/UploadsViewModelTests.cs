// Covers FR-ONL-09 and FR-ONL-11 (V2-S5, app side): the upload policy before the first upload, uploads only with online features, an own
// image uploaded from the Packs page is then shown in Discord at its unlisted address, the Account page lists uploads with what they are used
// for and a local preview only, delete needs a confirmation step, and the copy says "unlisted" and that Discord may keep a cached copy.
using System.Security.Cryptography;
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

/// <summary>An <see cref="IUploadService"/> that stores uploads in memory.</summary>
internal sealed class FakeUploadService : IUploadService
{
    public event EventHandler? Changed;

    public UploadsSnapshot Current { get; private set; } = UploadsSnapshot.Empty with { Limit = 20, IsKnown = true };

    public List<byte[]> Uploaded { get; } = [];

    public List<string> Deleted { get; } = [];

    public UploadResult? NextFailure { get; set; }

    public void Set(UploadsSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AcceptPolicy() => Set(Current with { PolicyAccepted = true });

    public Task<UploadResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(new UploadResult(AccountFailure.None, "Uploads are up to date."));

    public Task<UploadResult> UploadAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken)
    {
        if (NextFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        var bytes = png.ToArray();
        Uploaded.Add(bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var upload = new UploadInfo($"00000000-0000-4000-8000-{Uploaded.Count:D12}", $"https://cdn.arcavale.com/u/{sha[..32]}.png", sha, "png", bytes.Length, 64, 64, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        Set(Current with { Items = [.. Current.Items, upload] });
        return Task.FromResult(new UploadResult(AccountFailure.None, "Image uploaded.", upload));
    }

    public Task<UploadResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        Deleted.Add(id);
        Set(Current with { Items = [.. Current.Items.Where(i => i.Id != id)] });
        return Task.FromResult(new UploadResult(AccountFailure.None, "Upload deleted. " + UploadPolicy.DiscordCache));
    }
}

public sealed class UploadsViewModelTests : IDisposable
{
    private readonly Harness _host = new();
    private readonly PackHarness _packs;
    private readonly FakeAccountService _account = new();
    private readonly FakeUploadService _service = new();
    private readonly UploadsViewModel _uploads;
    private readonly PacksViewModel _page;

    public UploadsViewModelTests()
    {
        _packs = new PackHarness(_host);
        // As the composition root does: the library follows the upload list, so Discord gets uploaded own images.
        _service.Changed += (_, _) => _packs.Library.SetUploads(_service.Current.UrlBySha256, "cdn.arcavale.com");
        _uploads = new UploadsViewModel(_service, _account, _host.Settings, _packs.Library, _host.Dispatcher);
        _page = new PacksViewModel(_host.Settings, _host.Dispatcher, _packs.Library, _packs.Picker, uploads: _uploads);
    }

    public void Dispose()
    {
        _page.Dispose();
        _uploads.Dispose();
        _host.Dispose();
    }

    private async Task<OverrideRowViewModel> OwnKillerImageAsync()
    {
        _packs.Storage.Files["/pics/mine.png"] = PackFixtures.Png("mine");
        _packs.Picker.Image = "/pics/mine.png";
        var row = _page.Overrides.Single(o => o.Key == "role:killer");
        await row.ChooseCommand.ExecuteAsync(null);
        return row;
    }

    [Fact]
    public void SignedOut_UploadsAreUnavailable_AndThePolicyIsNotOffered()
    {
        Assert.False(_uploads.IsAvailable);
        Assert.False(_uploads.ShowPolicy);
        Assert.False(_uploads.CanUpload);
        Assert.NotNull(_uploads.UnavailableText);
        Assert.Contains("unlisted, not private", _uploads.UnlistedText, StringComparison.Ordinal);
        Assert.Contains("Discord may keep showing", _uploads.DiscordCacheText, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicyShowsBeforeTheFirstUpload_AndAcceptingItEnablesUploads()
    {
        _account.Set(OnlineState.Active);
        Assert.True(_uploads.ShowPolicy);
        Assert.False(_uploads.CanUpload);
        Assert.Equal(3, _uploads.PolicyRules.Count);
        Assert.Contains(_uploads.PolicyRules, r => r.Contains("rights", StringComparison.Ordinal));
        Assert.Contains(_uploads.PolicyRules, r => r.Contains("offensive", StringComparison.Ordinal));
        Assert.Contains(_uploads.PolicyRules, r => r.Contains("may remove any upload", StringComparison.Ordinal));

        _uploads.AcceptPolicyCommand.Execute(null);

        Assert.True(_service.Current.PolicyAccepted);
        Assert.False(_uploads.ShowPolicy);
        Assert.True(_uploads.CanUpload);
    }

    [Fact]
    public async Task AnOwnImage_CanBeUploadedOnlyAfterThePolicy_AndThenDiscordShowsIt()
    {
        _account.Set(OnlineState.Active);
        var row = await OwnKillerImageAsync();
        Assert.True(row.ShowUpload);
        Assert.False(row.CanUpload);
        Assert.False(row.UploadCommand.CanExecute(null));

        _uploads.AcceptPolicyCommand.Execute(null);
        Assert.True(row.CanUpload);
        await row.UploadCommand.ExecuteAsync(null);

        var file = _host.Settings.Current.Packs.Overrides["role:killer"];
        Assert.Equal(_packs.Library.ReadOverrideFile(file), Assert.Single(_service.Uploaded));
        Assert.True(row.IsUploaded);
        Assert.False(row.ShowUpload);
        Assert.Contains("Discord shows it (unlisted)", row.CurrentText, StringComparison.Ordinal);
        Assert.Contains("unlisted address", _page.StatusMessage, StringComparison.Ordinal);
        var preview = _page.PreviewRows.Single(r => r.Label == "Killer icon");
        Assert.False(preview.LocalOnly);
        Assert.Equal("Your uploaded image (unlisted)", preview.Discord);
        Assert.Null(_page.LocalImagesNote);
    }

    [Fact]
    public async Task AFailedUpload_ShowsTheServicesReason_AndTheImageStaysPreviewOnly()
    {
        _account.Set(OnlineState.Active);
        _uploads.AcceptPolicyCommand.Execute(null);
        var row = await OwnKillerImageAsync();
        _service.NextFailure = new UploadResult(AccountFailure.Rejected, "The service has no room for new images right now. Try again later.");

        await row.UploadCommand.ExecuteAsync(null);

        Assert.Equal("The service has no room for new images right now. Try again later.", _page.ErrorMessage);
        Assert.False(row.IsUploaded);
        Assert.True(_page.PreviewRows.Single(r => r.Label == "Killer icon").LocalOnly);
    }

    [Fact]
    public async Task TheAccountList_SaysWhatEachUploadIsUsedFor_ShowsOnlyALocalPreview_AndDeletesAfterConfirmation()
    {
        _account.Set(OnlineState.Active);
        _uploads.AcceptPolicyCommand.Execute(null);
        var row = await OwnKillerImageAsync();
        await row.UploadCommand.ExecuteAsync(null);
        _service.Set(_service.Current with
        {
            Items = [.. _service.Current.Items, new UploadInfo("00000000-0000-4000-8000-0000000000ff", "https://cdn.arcavale.com/u/ffffffffffffffffffffffffffffffff.png", new string('f', 64), "png", 2048, 32, 32, DateTimeOffset.UnixEpoch)],
        });

        Assert.Equal(2, _uploads.Items.Count);
        var mine = _uploads.Items[0];
        Assert.Equal("Used for Killer icon", mine.UsedFor);
        Assert.NotNull(mine.Preview);
        Assert.StartsWith(PackFixtures.Root, mine.Preview!.Path, StringComparison.Ordinal);
        var elsewhere = _uploads.Items[1];
        Assert.Equal("Not used for any slot on this PC", elsewhere.UsedFor);
        Assert.True(elsewhere.HasNoPreview);
        Assert.Equal("2 of 20 images uploaded.", _uploads.QuotaText);

        elsewhere.DeleteCommand.Execute(null);
        Assert.True(elsewhere.IsConfirming);
        Assert.Empty(_service.Deleted);
        elsewhere.CancelDeleteCommand.Execute(null);
        Assert.False(elsewhere.IsConfirming);

        elsewhere.DeleteCommand.Execute(null);
        await elsewhere.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(["00000000-0000-4000-8000-0000000000ff"], _service.Deleted);
        Assert.Single(_uploads.Items);
        Assert.Contains("Discord may keep showing", _uploads.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtTheLimitOrWhenBlocked_NoUploadStarts()
    {
        _account.Set(OnlineState.Active);
        _uploads.AcceptPolicyCommand.Execute(null);
        var row = await OwnKillerImageAsync();
        _service.Set(_service.Current with { Limit = 0 });
        Assert.True(_uploads.IsAtLimit);
        Assert.False(row.CanUpload);

        _service.Set(_service.Current with { Limit = 20, BlockedReason = "uploads_blocked" });
        Assert.False(_uploads.CanUpload);
        Assert.Contains("turned off for your account", _uploads.BlockedText, StringComparison.Ordinal);
        Assert.False(row.CanUpload);
    }

    [Fact]
    public void WithoutOnlineFeatures_UploadsWait()
    {
        _account.Set(OnlineState.OfflineWithGrace, features: false);
        _service.AcceptPolicy();
        Assert.True(_uploads.IsSignedIn);
        Assert.False(_uploads.IsAvailable);
        Assert.False(_uploads.CanUpload);
    }

    [Fact]
    public void DeleteMyAccount_SaysUploadedImagesAreDeletedToo()
    {
        using var account = new AccountViewModel(_account, _host.Dispatcher, uploads: _uploads);
        Assert.True(account.ShowUploads);
        Assert.Contains("every image you uploaded", account.DeleteAccountText, StringComparison.Ordinal);
        using var without = new AccountViewModel(_account, _host.Dispatcher);
        Assert.DoesNotContain("uploaded", without.DeleteAccountText, StringComparison.Ordinal);
    }
}
