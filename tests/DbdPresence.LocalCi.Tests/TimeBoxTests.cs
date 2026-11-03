// Covers: FR-CI-07 and local CI round 3, red-team L2 (no step waits for ever: a piece of work with a time limit of its own ends when the limit runs out, with a plain message and as a timeout, never as a
// cancel; the caller's own cancel stays a cancel; the limit is measured on the injected clock; wsl --install and the whole base image build have limits of their own), L5 (a leftover that is gone is
// taken off the list), M3 (the machine record). docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class TimeBoxTests : IDisposable
{
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task WorkThatEndsInTime_GivesItsResult()
    {
        var result = await TimeBox.RunAsync(TimeSpan.FromMinutes(1), _time, "Counting", _ => Task.FromResult(42), CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task WorkThatRunsOutOfTime_IsEnded_AsATimeout_WithAPlainMessage_AtTheExactLimit()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = TimeBox.RunAsync(
            TimeSpan.FromMinutes(5),
            _time,
            "Counting",
            async ct =>
            {
                reached.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            },
            CancellationToken.None);
        await reached.Task.OrFail();

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.False(running.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Equal("Counting did not finish within 5 minutes, so it was ended.", ex.Message);
    }

    [Fact]
    public async Task ALimitUnderAMinute_IsSaidInSeconds()
    {
        var running = TimeBox.RunAsync(TimeSpan.FromSeconds(30), _time, "Waiting", ct => Task.Delay(Timeout.Infinite, ct), CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Contains("within 30 seconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersCancel_StaysACancel_EvenAfterTheLimit()
    {
        using var cts = new CancellationTokenSource();
        var running = TimeBox.RunAsync(TimeSpan.FromMinutes(5), _time, "Counting", ct => Task.Delay(Timeout.Infinite, ct), cts.Token);

        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => running.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task AnErrorOfTheWork_PassesThroughUnchanged()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TimeBox.RunAsync(TimeSpan.FromMinutes(1), _time, "Counting", _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal("boom", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ALimitOfZeroOrLess_IsRefused(int seconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TimeBox.RunAsync(TimeSpan.FromSeconds(seconds), _time, "Counting", _ => Task.CompletedTask, CancellationToken.None));
    }

    // ---- wsl --install

    [Fact]
    public async Task WslInstall_IsEndedAfterItsLimit_AndSaysWhatDidNotFinish()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new HangingInstaller(reached);
        var installer = new LimitedWslInstaller(inner, WslLimits.Install, _time);

        var running = installer.InstallWithoutDistributionAsync(CancellationToken.None);
        await reached.Task.OrFail();
        _time.Advance(WslLimits.Install);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Equal("Installing WSL did not finish within 30 minutes, so it was ended.", ex.Message);
        Assert.True(inner.Cancelled);
    }

    [Fact]
    public async Task WslInstall_GivesTheAnswerOfTheInstaller_WhenItEndsInTime()
    {
        var inner = new FakeWslInstaller { Restart = true };
        var installer = new LimitedWslInstaller(inner, WslLimits.Install, _time);

        Assert.True(await installer.InstallWithoutDistributionAsync(CancellationToken.None));
        Assert.Equal(["install"], inner.Calls.ToArray());
    }

    [Fact]
    public async Task WslInstall_PassesTheOwnersCancelOn_AsACancel()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = new LimitedWslInstaller(new HangingInstaller(reached), WslLimits.Install, _time);
        using var cts = new CancellationTokenSource();
        var running = installer.InstallWithoutDistributionAsync(cts.Token);
        await reached.Task.OrFail();

        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => running.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<TimeoutException>(ex);
    }

    // ---- the whole base image build

    [Fact]
    public async Task TheWholeBaseImageBuild_HasALimitOfItsOwn_AndItsMachineIsStillRemoved()
    {
        using var rig = new MaintenanceRig(withDownloadableImage: true);
        var wsl = rig.Wsl;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wsl.OnExecute = async (_, _, _, _, _, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        };
        var builder = Builder(rig, TimeSpan.FromMinutes(10));

        var running = builder.BuildAsync(CancellationToken.None);
        await reached.Task.OrFail();
        rig.Time.Advance(TimeSpan.FromMinutes(10));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Contains("Building the Linux base image did not finish within 10 minutes", ex.Message, StringComparison.Ordinal);
        Assert.Empty(wsl.Distributions);
    }

    [Fact]
    public void TheBuildAndInstallLimits_AreThoseOfTheLimitsTable()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), WslLimits.ImageBuild);
        Assert.Equal(TimeSpan.FromMinutes(30), WslLimits.Install);
    }

    private static BaseImageBuilder Builder(MaintenanceRig rig, TimeSpan limit)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("ubuntu");
        var pinned = new UbuntuImage("ubuntu-test.wsl", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), new Uri("https://releases.ubuntu.com/noble/ubuntu-test.wsl"), bytes.Length);
        rig.Downloads.Files[pinned.Address.ToString()] = bytes;
        var sandbox = Sandboxes.New(rig.Wsl, new FakeWslAccount(), new FakeWslSettingsFile(), rig.Log, rig.Redactor, rig.Time);
        return new BaseImageBuilder(rig.Downloads, rig.Wsl, sandbox, rig.Paths, rig.Log, new Retry(rig.Time, rig.Redactor, rig.Log, attempts: 1), rig.Integrity, new FakeDisk(), new ResidueRecorder(rig.State), rig.Time, pinned, limit);
    }

    private sealed class HangingInstaller : IWslInstaller
    {
        private readonly TaskCompletionSource _reached;

        public HangingInstaller(TaskCompletionSource reached)
        {
            _reached = reached;
        }

        public bool Cancelled { get; private set; }

        public async Task<bool> InstallWithoutDistributionAsync(CancellationToken cancellationToken)
        {
            _reached.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return false;
        }

        public Task<string> UpdateAsync(CancellationToken cancellationToken) => Task.FromResult("not used");
    }
}
