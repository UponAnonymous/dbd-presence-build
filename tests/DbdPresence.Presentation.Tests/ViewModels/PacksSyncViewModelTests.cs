// Covers: FR-ONL-07 (Packs page: synced official packs as cards with credit and licence note, "Last updated" / "Could not refresh" line,
// manual "Check for new packs" with real behaviour and rate-limit awareness), FR-PACK-03 (Behaviour's notice on cards with Behaviour art),
// FR-PACK-06 (selectable per category like imports; Discord gets the https URL), FR-PACK-04 (licensed items named), UI_SPEC "show nothing that cannot work".
using DbdPresence.Configuration;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Sync;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class PacksSyncViewModelTests : IDisposable
{
    private static readonly DateTimeOffset s_checked = new(2026, 10, 24, 14, 3, 0, TimeSpan.Zero);

    private readonly Harness _host = new();
    private readonly PackHarness _packs;
    private readonly FakeSync _sync = new();
    private readonly PacksViewModel _page;

    public PacksSyncViewModelTests()
    {
        _packs = new PackHarness(_host);
        _page = new PacksViewModel(_host.Settings, _host.Dispatcher, _packs.Library, _packs.Picker, _sync);
    }

    public void Dispose()
    {
        _page.Dispose();
        _host.Dispose();
    }

    private static PackEntry Entry(string id, string pack, string key, string marker, LicensedInfo? licensed = null)
    {
        var content = System.Text.Encoding.UTF8.GetBytes("image-" + marker);
        Assert.True(PackKey.TryParse(key, out var kind, out _));
        return new PackEntry(
            id, pack, kind, key, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), content.Length, 1024, 1024, PackImageFormat.Png, licensed);
    }

    private void Publish(PackInfo pack, params PackEntry[] entries)
    {
        _packs.Library.SetSynced(new SyncedPacks(
            new PackIndex([pack], entries), new AllCached(), e => $"https://cdn.example.org/packs/{e.CacheFileName}", null, e => $"/cache/{e.CacheFileName}"));
        _sync.RaiseChanged();
    }

    private static PackInfo Official(bool behaviour = false) =>
        new("official", "Official", 1, "DBD Presence", "Fan content shown as a courtesy.", "Art by the community", false, behaviour);

    [Fact]
    public void SyncedPack_AppearsAsAnOfficialCard_WithCreditAndLicenceNote_AndCannotBeRemoved()
    {
        var licensed = Entry("official-char-ghostface", "official", "character:ghostface", "g", new LicensedInfo("Example Studio"));
        Publish(Official(), Entry("official-role-killer", "official", "role:killer", "k"), licensed);

        var card = Assert.Single(_page.Cards);

        Assert.False(card.IsImported);
        Assert.Equal("Official pack", card.Kind);
        Assert.Equal("DBD Presence", card.Author);
        Assert.Equal("Art by the community", card.Credit);
        Assert.True(card.HasCredit);
        Assert.Equal("Fan content shown as a courtesy.", card.LicenceNote);
        Assert.Equal("Includes licensed images owned by others: Example Studio.", card.LicensedNote);
        Assert.False(card.RemoveCommand.CanExecute(null));
        Assert.False(_page.HasNoPacks);
        Assert.Contains("official pack", card.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void BehaviourNotice_ShowsOnlyOnCardsOfPacksFlaggedBehaviourArt()
    {
        Publish(Official(behaviour: true), Entry("official-role-killer", "official", "role:killer", "k"));
        var flagged = _page.Cards.Single();

        Assert.True(flagged.HasBehaviourNotice);
        Assert.Equal("© Behaviour Interactive. Unofficial, not affiliated with or endorsed by Behaviour Interactive.", flagged.BehaviourNotice);
        Assert.Equal(flagged.BehaviourNotice, _page.BehaviourNotice);

        Publish(Official(behaviour: false), Entry("official-role-killer", "official", "role:killer", "k"));
        var plain = _page.Cards.Single();

        Assert.False(plain.HasBehaviourNotice);
        Assert.Null(plain.BehaviourNotice);
        Assert.Null(_page.BehaviourNotice);
    }

    [Fact]
    public void ChoosingASyncedPack_WorksLikeAnImport_AndDiscordGetsItsHttpsUrl()
    {
        var killer = Entry("official-role-killer", "official", "role:killer", "k");
        Publish(Official(), killer);
        var card = _page.Cards.Single();
        var killerChoice = card.Categories.Single(c => c.Category == PackCategory.Killer);

        Assert.True(killerChoice.IsAvailable);
        Assert.False(card.Categories.Single(c => c.Category == PackCategory.Maps).IsAvailable);

        killerChoice.IsChosen = true;

        Assert.Equal("official", _host.Settings.Current.Packs.KillerPack);
        Assert.True(card.InUse);
        var row = _page.PreviewRows.Single(r => r.Label == "Killer icon");
        Assert.Equal("Image from the pack service", row.Discord);
        Assert.False(row.LocalOnly);
        Assert.Equal($"/cache/{killer.CacheFileName}", row.Preview!.Path);
        Assert.Null(_page.LocalImagesNote);

        killerChoice.IsChosen = false;

        Assert.Equal(PackSettings.NoPack, _host.Settings.Current.Packs.KillerPack);
    }

    [Fact]
    public void SyncSection_ShowsLastUpdated_AfterASuccessfulCheck()
    {
        _sync.Set(new PackSyncStatus(true, false, s_checked, PackSyncOutcome.Updated, false, null, null));

        Assert.True(_page.ShowSyncSection);
        Assert.StartsWith("Last updated ", _page.SyncStatusText, StringComparison.Ordinal);
        Assert.Contains("2026", _page.SyncStatusText, StringComparison.Ordinal);
        Assert.False(_page.CouldNotRefresh);
        Assert.False(_page.HasSyncNote);
        Assert.True(_page.CanCheckForPacks);
        Assert.False(_page.SyncUnavailable);
    }

    [Fact]
    public void SyncSection_SaysNothingWasDownloadedYet_BeforeTheFirstCheck()
    {
        _sync.Set(new PackSyncStatus(true, false, null, null, false, null, null));

        Assert.Equal("Official packs have not been downloaded yet.", _page.SyncStatusText);
        Assert.True(_page.CanCheckForPacks);
    }

    [Fact]
    public void AFailedCheck_ShowsCouldNotRefresh_WithTheNote_WhileTheCachedCardsStay()
    {
        Publish(Official(), Entry("official-role-killer", "official", "role:killer", "k"));

        _sync.Set(new PackSyncStatus(true, false, s_checked, PackSyncOutcome.Offline, false, null, "Could not reach the pack service. The packs already on this PC keep working."));

        Assert.True(_page.CouldNotRefresh);
        Assert.True(_page.HasSyncNote);
        Assert.Equal("Could not reach the pack service. The packs already on this PC keep working.", _page.SyncNote);
        Assert.StartsWith("Last updated ", _page.SyncStatusText, StringComparison.Ordinal);
        Assert.Single(_page.Cards);
    }

    [Fact]
    public void AnExpiredIndex_ShowsCouldNotRefresh_AndKeepsShowingThePacks()
    {
        Publish(Official(), Entry("official-role-killer", "official", "role:killer", "k"));

        _sync.Set(new PackSyncStatus(true, false, s_checked, PackSyncOutcome.UpToDate, true, null, "The pack list on this PC is older than 30 days and could not be refreshed. The packs you have keep working."));

        Assert.True(_page.CouldNotRefresh);
        Assert.Contains("30 days", _page.SyncNote, StringComparison.Ordinal);
        Assert.Single(_page.Cards);
    }

    [Fact]
    public void WithoutOnlineFeatures_NoCheckButtonIsOffered_AndTheReasonIsShown()
    {
        _sync.Set(new PackSyncStatus(false, false, null, null, false, null, null));

        Assert.False(_page.CanCheckForPacks);
        Assert.True(_page.SyncUnavailable);
        Assert.Contains("Account page", _page.SyncUnavailableText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _page.SyncStatusText);
    }

    [Fact]
    public async Task ManualCheck_AsksTheServiceByHand_AndReportsTheResult()
    {
        _sync.Set(new PackSyncStatus(true, false, null, null, false, null, null));
        _sync.Next = new PackSyncStatus(true, false, s_checked, PackSyncOutcome.Updated, false, null, null);

        await _page.CheckForPacksCommand.ExecuteAsync(null);

        Assert.Equal([true], _sync.Manual);
        Assert.Equal("Official packs were updated.", _page.StatusMessage);
        Assert.StartsWith("Last updated ", _page.SyncStatusText, StringComparison.Ordinal);
        Assert.Null(_page.ErrorMessage);
    }

    [Fact]
    public async Task ManualCheck_UpToDate_SaysSo()
    {
        _sync.Set(new PackSyncStatus(true, false, null, null, false, null, null));
        _sync.Next = new PackSyncStatus(true, false, s_checked, PackSyncOutcome.UpToDate, false, null, null);

        await _page.CheckForPacksCommand.ExecuteAsync(null);

        Assert.Equal("Official packs are already up to date.", _page.StatusMessage);
    }

    [Fact]
    public async Task ManualCheck_RateLimited_ShowsTheWaitFromTheService()
    {
        var retryAt = s_checked.AddMinutes(2);
        _sync.Set(new PackSyncStatus(true, false, s_checked, PackSyncOutcome.UpToDate, false, null, null));
        _sync.Next = new PackSyncStatus(true, false, s_checked, PackSyncOutcome.RateLimited, false, retryAt, "The pack service asked to wait. Try again in 2 minutes.");

        await _page.CheckForPacksCommand.ExecuteAsync(null);

        Assert.Equal("The pack service asked to wait. Try again in 2 minutes.", _page.StatusMessage);
        Assert.Equal("The pack service asked to wait. Try again in 2 minutes.", _page.SyncNote);
        Assert.True(_page.CouldNotRefresh);
    }

    [Fact]
    public async Task ManualCheck_DoesNothing_WhenOnlineFeaturesAreOff()
    {
        _sync.Set(new PackSyncStatus(false, false, null, null, false, null, null));

        await _page.CheckForPacksCommand.ExecuteAsync(null);

        Assert.Empty(_sync.Manual);
    }

    [Fact]
    public void WhileChecking_TheButtonIsDisabled_AndSaysSo()
    {
        _sync.Set(new PackSyncStatus(true, true, null, null, false, null, null));

        Assert.True(_page.IsChecking);
        Assert.False(_page.CanCheckForPacks);
        Assert.Equal("Checking…", _page.CheckForPacksText);

        _sync.Set(new PackSyncStatus(true, false, null, null, false, null, null));

        Assert.Equal("Check for new packs", _page.CheckForPacksText);
        Assert.True(_page.CanCheckForPacks);
    }

    [Fact]
    public void AWithdrawnPack_DisappearsFromThePage_AtTheNextSync()
    {
        Publish(Official(), Entry("official-role-killer", "official", "role:killer", "k"));
        Assert.Single(_page.Cards);

        _packs.Library.SetSynced(null);
        _sync.RaiseChanged();

        Assert.Empty(_page.Cards);
        Assert.True(_page.HasNoPacks);
    }

    [Fact]
    public void WithoutASyncService_TheSyncSectionDoesNotExist()
    {
        using var plain = _packs.Page();

        Assert.False(plain.ShowSyncSection);
        Assert.False(plain.SyncUnavailable);
        Assert.False(plain.CanCheckForPacks);
    }

    private sealed class AllCached : IPackContentCache
    {
        public bool Contains(PackEntry entry) => true;
    }

    private sealed class FakeSync : IPackSyncService
    {
        public event EventHandler? Changed;

        public PackSyncStatus Status { get; private set; } = PackSyncStatus.Unavailable;

        public SyncedPacks? Synced => null;

        public List<bool> Manual { get; } = [];

        public PackSyncStatus? Next { get; set; }

        public void Set(PackSyncStatus status)
        {
            Status = status;
            RaiseChanged();
        }

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<PackSyncStatus> SyncAsync(bool manual, CancellationToken cancellationToken)
        {
            Manual.Add(manual);
            Status = Next ?? Status;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(Status);
        }
    }
}
