// Covers: FR-ONL-07 (sync on start and every 6 hours, and again when online features become available), D8 (a failed run never reaches presence).
using DbdPresence.Online.Packs;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Sync;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Online.Tests;

public sealed class PackSyncSchedulerTests
{
    [Fact]
    public async Task RunsAfterTheStartDelay_ThenEverySixHours()
    {
        var time = new FakeTimeProvider();
        var sync = new CountingSync();
        using var scheduler = new PackSyncScheduler(sync, new SwitchGate(), time, new TestLog());
        scheduler.Start();

        time.Advance(PackSyncScheduler.StartDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, sync.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        await scheduler.LastRun;
        Assert.Equal(1, sync.Calls);

        time.Advance(TimeSpan.FromHours(6));
        await scheduler.LastRun;
        time.Advance(TimeSpan.FromHours(6));
        await scheduler.LastRun;

        Assert.Equal(3, sync.Calls);
        Assert.Equal(TimeSpan.FromHours(6), PackSyncScheduler.Interval);
        Assert.All(sync.Manual, manual => Assert.False(manual));
    }

    [Fact]
    public async Task RunsAgainWhenOnlineFeaturesBecomeAvailable_ButNotOnOtherChanges()
    {
        var time = new FakeTimeProvider();
        var sync = new CountingSync();
        var gate = new SwitchGate { IsOpen = false };
        using var scheduler = new PackSyncScheduler(sync, gate, time, new TestLog());
        scheduler.Start();

        gate.IsOpen = false;
        Assert.Equal(0, sync.Calls);

        gate.IsOpen = true; // signed in with a current licence
        await scheduler.LastRun;
        Assert.Equal(1, sync.Calls);

        gate.IsOpen = true;
        gate.IsOpen = false; // emergency switch or sign-out: nothing to do
        Assert.Equal(1, sync.Calls);

        gate.IsOpen = true;
        await scheduler.LastRun;
        Assert.Equal(2, sync.Calls);
    }

    [Fact]
    public async Task Dispose_StopsTheSchedule_AndALateGateChangeDoesNothing()
    {
        var time = new FakeTimeProvider();
        var sync = new CountingSync();
        var gate = new SwitchGate { IsOpen = false };
        var scheduler = new PackSyncScheduler(sync, gate, time, new TestLog());
        scheduler.Start();
        scheduler.Dispose();

        time.Advance(TimeSpan.FromHours(12));
        gate.IsOpen = true;
        await scheduler.LastRun;

        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task ARunStoppedByShutdown_IsLogged_NotThrown()
    {
        var time = new FakeTimeProvider();
        var log = new TestLog();
        var sync = new CountingSync { Throw = new OperationCanceledException() };
        using var scheduler = new PackSyncScheduler(sync, new SwitchGate(), time, log);
        scheduler.Start();

        time.Advance(PackSyncScheduler.StartDelay);
        await scheduler.LastRun;

        Assert.Single(log.Lines);
        Assert.Contains("closing", log.Lines[0], StringComparison.Ordinal);
    }

    private sealed class CountingSync : IPackSyncService
    {
        public int Calls { get; private set; }

        public List<bool> Manual { get; } = [];

        public Exception? Throw { get; set; }

        public PackSyncStatus Status => PackSyncStatus.Unavailable;

        public SyncedPacks? Synced => null;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<PackSyncStatus> SyncAsync(bool manual, CancellationToken cancellationToken)
        {
            Calls++;
            Manual.Add(manual);
            return Throw is { } failure ? Task.FromException<PackSyncStatus>(failure) : Task.FromResult(Status);
        }
    }
}
