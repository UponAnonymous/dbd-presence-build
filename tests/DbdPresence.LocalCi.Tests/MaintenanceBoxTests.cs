// Covers: FR-CI-04 and FR-CI-10, local CI round 3, QA T4, T5 and T5c (each piece of the maintenance is time-boxed: the token check 2 minutes, the runner download 30, the Linux image build 90; one wedged
// call is stopped exactly at its limit, logged, and tried again later; the loop's own stop passes through; nothing is stopped before its limit). The clock is the fake one: no waiting. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class MaintenanceBoxTests : IDisposable
{
    private MaintenanceRig? _rig;

    public void Dispose() => _rig?.Dispose();

    private MaintenanceRig Rig(bool image = false, TimeSpan? token = null, TimeSpan? download = null, TimeSpan? build = null) => _rig = new MaintenanceRig(token, download, build, withDownloadableImage: image);

    private static async Task AssertEndsExactlyAt(MaintenanceRig rig, Task running, TimeSpan limit)
    {
        rig.Time.Advance(limit - TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(running.IsCompleted, "stopped before its limit");

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await running.OrFail();
    }

    /// <summary>T4: the token check stops after 2 minutes.</summary>
    [Fact]
    public async Task AWedgedTokenCheck_IsStoppedAfterTwoMinutes_LoggedAndNotRepeatedForSixHours()
    {
        var rig = Rig();
        rig.EverythingJustDone();
        rig.State.Update(s => s.LastTokenCheck = null);
        rig.Api.RepoHang.On = true;

        var running = rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await rig.Api.RepoHang.StartedAsync();
        await AssertEndsExactlyAt(rig, running, TimeSpan.FromMinutes(2));

        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The token check took longer than 2 minutes and was stopped", StringComparison.Ordinal));
        Assert.NotNull(rig.State.Load().LastTokenCheck);
    }

    /// <summary>T5: the runner download stops after 30 minutes and is tried again after the 30-minute pause, not counted as done.</summary>
    [Fact]
    public async Task AWedgedRunnerDownload_IsStoppedAfterThirtyMinutes_AndLeavesTheDailyCheckUndone()
    {
        var rig = Rig();
        rig.EverythingJustDone();
        var longAgo = rig.Time.GetUtcNow().AddDays(-2);
        rig.State.Update(s =>
        {
            s.LastRunnerCheck = longAgo;
            s.LastRunnerAttempt = longAgo;
        });
        rig.Api.ReleaseHang.On = true;

        var running = rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await rig.Api.ReleaseHang.StartedAsync();
        await AssertEndsExactlyAt(rig, running, TimeSpan.FromMinutes(30));

        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The runner download took longer than 30 minutes and was stopped", StringComparison.Ordinal));
        var state = rig.State.Load();
        Assert.Equal(longAgo, state.LastRunnerCheck);
        Assert.Equal(longAgo.AddDays(2), state.LastRunnerAttempt);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("Runner packages are up to date", StringComparison.Ordinal));
    }

    /// <summary>T5c: the Linux image build stops after 90 minutes and is not counted as done.</summary>
    [Fact]
    public async Task AWedgedImageBuild_IsStoppedAfterNinetyMinutes_AndTheImageIsNotCountedAsBuilt()
    {
        var rig = Rig(image: true);
        rig.EverythingJustDone();
        var longAgo = rig.Time.GetUtcNow().AddDays(-30);
        rig.State.Update(s =>
        {
            s.LastBaseImageBuild = longAgo;
            s.LastBaseImageAttempt = longAgo;
            s.BaseImageRecipe = "an older recipe";
        });
        rig.Wsl.ShutdownHang.On = true;

        var running = rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        await rig.Wsl.ShutdownHang.StartedAsync();
        await AssertEndsExactlyAt(rig, running, TimeSpan.FromMinutes(90));

        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The Linux image build took longer than 90 minutes and was stopped", StringComparison.Ordinal));
        var state = rig.State.Load();
        Assert.Equal(longAgo, state.LastBaseImageBuild);
        Assert.Equal("an older recipe", state.BaseImageRecipe);
        Assert.Equal(longAgo.AddDays(30), state.LastBaseImageAttempt);
    }

    [Fact]
    public async Task EachLimit_CanBeChanged_AndTheOthersAreNotAffected()
    {
        var rig = Rig(token: TimeSpan.FromSeconds(10));
        rig.EverythingJustDone();
        rig.State.Update(s => s.LastTokenCheck = null);
        rig.Api.RepoHang.On = true;

        var running = rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await rig.Api.RepoHang.StartedAsync();
        await AssertEndsExactlyAt(rig, running, TimeSpan.FromSeconds(10));
    }

    /// <summary>The loop's own stop is not a time-out: it passes through every box.</summary>
    [Fact]
    public async Task TheLoopsOwnStop_PassesThroughTheRunnerDownload()
    {
        var rig = Rig();
        rig.EverythingJustDone();
        rig.State.Update(s =>
        {
            s.LastRunnerCheck = rig.Time.GetUtcNow().AddDays(-2);
            s.LastRunnerAttempt = rig.Time.GetUtcNow().AddDays(-2);
        });
        rig.Api.ReleaseHang.On = true;
        using var cts = new CancellationTokenSource();

        var running = rig.Maintenance.RunDueAsync(linuxIdle: false, cts.Token);
        await rig.Api.ReleaseHang.StartedAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("took longer than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLoopsOwnStop_PassesThroughTheImageBuild()
    {
        var rig = Rig(image: true);
        rig.EverythingJustDone();
        rig.State.Update(s =>
        {
            s.LastBaseImageBuild = rig.Time.GetUtcNow().AddDays(-30);
            s.LastBaseImageAttempt = rig.Time.GetUtcNow().AddDays(-30);
        });
        rig.Wsl.ShutdownHang.On = true;
        using var cts = new CancellationTokenSource();

        var running = rig.Maintenance.RunDueAsync(linuxIdle: true, cts.Token);
        await rig.Wsl.ShutdownHang.StartedAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("took longer than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkThatFinishesInsideItsLimit_IsNeverReportedAsTooSlow()
    {
        var rig = Rig();
        rig.EverythingJustDone();
        rig.State.Update(s => s.LastTokenCheck = null);

        await rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromHours(1));

        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("took longer than", StringComparison.Ordinal));
    }
}
