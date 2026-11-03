// Covers: D32 / FR-ONL-13 (the app is locked until the device holds a verified licence: while locked the coordinator never calls the sink, so
// nothing reaches Discord; unlocking publishes), FR-ONL-05 / D8 (once unlocked, a service outage, a lapsed licence or a month offline never stops
// presence; only a received, signed revocation does, and the account service reports that as PresenceAllowed = false).
using DbdPresence.Configuration;
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class PresenceCoordinatorAccountTests
{
    private static (PresenceCoordinator Coordinator, FakeSink Sink, FakeLifecycleSource Game, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time) Create(FakeAccountService account)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero));
        var game = new FakeLifecycleSource();
        var sink = new FakeSink();
        var coordinator = new PresenceCoordinator(new FakeSettingsService(), new BundledApplicationCatalog(), game, sink, time, account: account);
        coordinator.Start();
        return (coordinator, sink, game, time);
    }

    [Theory]
    [InlineData(OnlineState.SignedOut)]
    [InlineData(OnlineState.NeedsInvite)]
    [InlineData(OnlineState.LicenceNeeded)]
    [InlineData(OnlineState.Revoked)]
    public void Locked_NeverCallsTheSink_EvenWhileTheGameRuns(OnlineState state)
    {
        var account = new FakeAccountService();
        account.Set(state);
        var (coordinator, sink, game, time) = Create(account);
        using var _ = coordinator;

        game.SetRunning(time.GetUtcNow());
        time.Advance(PresenceCoordinator.RefreshInterval * 3);
        coordinator.SetPaused(true);
        coordinator.SetPaused(false);
        var test = coordinator.StartConnectionTest();

        Assert.Equal(0, sink.PublishCalls);
        Assert.Empty(sink.ApplicationIds);
        Assert.Empty(sink.Published);
        Assert.Equal(ConnectionTestStart.Locked, test);
    }

    [Fact]
    public void Locked_ThenUnlocked_PublishesAndOpensTheApplication()
    {
        var account = new FakeAccountService();
        var (coordinator, sink, game, time) = Create(account);
        using var _ = coordinator;
        game.SetRunning(time.GetUtcNow());
        Assert.Equal(0, sink.PublishCalls);

        account.Set(OnlineState.Active);

        Assert.NotNull(sink.Last);
        Assert.Single(sink.ApplicationIds);
    }

    [Theory]
    [InlineData(OnlineState.Active)]
    [InlineData(OnlineState.OfflineWithGrace)]
    public void Unlocked_PublishesAtStart(OnlineState state)
    {
        var account = new FakeAccountService();
        account.Set(state, features: false);
        var (coordinator, sink, game, time) = Create(account);
        using var _ = coordinator;

        game.SetRunning(time.GetUtcNow());

        Assert.NotNull(sink.Last);
    }

    [Fact]
    public void ReceivedRevocation_StopsPresence_AndALaterActiveLicenceRestoresIt()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.Active);
        var (coordinator, sink, game, time) = Create(account);
        using var _ = coordinator;
        game.SetRunning(time.GetUtcNow());
        Assert.NotNull(sink.Last);

        account.Set(OnlineState.Revoked);
        Assert.Null(sink.Last);
        Assert.Contains("withdrawn", coordinator.Snapshot.Composition.Notes.Single(), StringComparison.Ordinal);

        account.Set(OnlineState.Active);
        Assert.NotNull(sink.Last);
    }

    [Fact]
    public void ServiceOutageForAMonth_AfterUnlock_DoesNotStopPresence()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.Active);
        var (coordinator, sink, game, time) = Create(account);
        using var _ = coordinator;
        game.SetRunning(time.GetUtcNow());
        account.Set(OnlineState.OfflineWithGrace, features: false);

        time.Advance(TimeSpan.FromDays(30));

        Assert.NotNull(sink.Last);
    }

    [Fact]
    public void ApplicationChange_WhileLocked_IsNotSentUntilUnlocked()
    {
        var account = new FakeAccountService();
        var settings = new FakeSettingsService();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 18, 0, 0, TimeSpan.Zero));
        var sink = new FakeSink();
        using var coordinator = new PresenceCoordinator(settings, new BundledApplicationCatalog(), new FakeLifecycleSource(), sink, time, account: account);
        coordinator.Start();

        settings.Update(s => s with { Discord = s.Discord with { UseOwnApplication = true, OwnApplicationId = "123456789012345678" } });

        Assert.Empty(sink.ApplicationIds);
        Assert.Equal(0, sink.PublishCalls);
    }
}
