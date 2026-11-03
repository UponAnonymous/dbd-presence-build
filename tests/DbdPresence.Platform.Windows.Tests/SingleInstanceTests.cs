// Covers: FR-LIFE-03 (single instance; a second launch activates the first).
using DbdPresence.Platform.Windows.Startup;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task SecondAcquire_IsRefused_AndActivatesTheFirst()
    {
        var appId = $"DBDPresenceTest-{Guid.NewGuid():N}";
        using var first = SingleInstance.TryAcquire(appId);
        Assert.NotNull(first);
        var activated = new TaskCompletionSource();
        first.ActivationRequested += (_, _) => activated.TrySetResult();

        var second = await Task.Run(() => SingleInstance.TryAcquire(appId), TestContext.Current.CancellationToken);

        Assert.Null(second);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AfterDispose_AnotherInstanceCanAcquire()
    {
        var appId = $"DBDPresenceTest-{Guid.NewGuid():N}";
        SingleInstance.TryAcquire(appId)!.Dispose();

        using var again = SingleInstance.TryAcquire(appId);

        Assert.NotNull(again);
    }

    [Fact]
    public async Task Dispose_OnAnotherThread_ReleasesTheInstance()
    {
        // Regression: the mutex used to be owned, and ReleaseMutex from a different thread threw.
        var appId = $"DBDPresenceTest-{Guid.NewGuid():N}";
        var first = SingleInstance.TryAcquire(appId);
        Assert.NotNull(first);

        await Task.Run(first.Dispose, TestContext.Current.CancellationToken);

        using var again = SingleInstance.TryAcquire(appId);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ActivationBeforeAnyoneSubscribes_IsDeliveredOnSubscribe()
    {
        var appId = $"DBDPresenceTest-{Guid.NewGuid():N}";
        using var first = SingleInstance.TryAcquire(appId);
        Assert.NotNull(first);

        // A second launch during startup, before the app subscribes.
        Assert.Null(await Task.Run(() => SingleInstance.TryAcquire(appId), TestContext.Current.CancellationToken));
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        var delivered = new TaskCompletionSource();
        first.ActivationRequested += (_, _) => delivered.TrySetResult();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
