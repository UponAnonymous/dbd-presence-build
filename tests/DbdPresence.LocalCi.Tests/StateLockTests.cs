// Covers: local CI round 3, red-team L6 (the state file is changed under a lock file that no other program can hold at the same time, so the loop, the status, the probe and set-up, which run side by
// side, never overwrite each other's changes; a program that holds the lock is waited for, a lock nobody lets go stops the change with a message in words, and a reader never stops a writer). The
// waits are counted, not timed: no test sleeps. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class StateLockTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string LockFile => _folder.Paths.StateFile + ".lock";

    private FileStream HoldTheLock() => new(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);

    [Fact]
    public void AChange_IsMadeUnderTheLockFile_AndTheLockIsLetGoAfterwards()
    {
        var state = new StateStore(_folder.Paths.StateFile);

        state.Update(s => s.LastBootId = "a");

        Assert.True(File.Exists(LockFile));
        using (HoldTheLock())
        {
            // The lock file is free again: this open would have failed while the store still held it.
        }

        Assert.Equal("a", state.Load().LastBootId);
    }

    [Fact]
    public void AProgramThatHoldsTheLock_IsWaitedFor_AndTheChangeIsMadeWhenItLetsGo()
    {
        var holder = HoldTheLock();
        var waits = new List<TimeSpan>();
        var state = new StateStore(
            _folder.Paths.StateFile,
            wait: step =>
            {
                waits.Add(step);
                if (waits.Count == 3)
                {
                    holder.Dispose();
                }
            });

        state.Update(s => s.LastBootId = "after the wait");

        Assert.Equal(3, waits.Count);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromMilliseconds(25), w));
        Assert.Equal("after the wait", state.Load().LastBootId);
    }

    [Fact]
    public void ALockNobodyLetsGo_StopsTheChange_AfterThePatience_WithAMessageInWords_AndChangesNothing()
    {
        using var holder = HoldTheLock();
        var waits = 0;
        var state = new StateStore(_folder.Paths.StateFile, lockPatience: TimeSpan.FromMilliseconds(100), lockStep: TimeSpan.FromMilliseconds(25), wait: _ => waits++);
        File.WriteAllText(_folder.Paths.StateFile, "{ \"LastBootId\": \"kept\" }");

        var ex = Assert.Throws<IOException>(() => state.Update(s => s.LastBootId = "never written"));

        Assert.Equal(4, waits);
        Assert.Contains("is in use by another DbdLocalCi program that did not let go within 0 seconds", ex.Message, StringComparison.Ordinal);
        Assert.Contains("this change was not made", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Equal("kept", state.Load().LastBootId);
        Assert.False(File.Exists(_folder.Paths.StateFile + ".tmp"));
    }

    [Fact]
    public void TheDefaultPatienceIsFifteenSeconds_InStepsOf25Milliseconds()
    {
        using var holder = HoldTheLock();
        var total = TimeSpan.Zero;
        var steps = 0;
        var state = new StateStore(_folder.Paths.StateFile, wait: step =>
        {
            total += step;
            steps++;
        });

        Assert.Throws<IOException>(() => state.Update(s => s.LastBootId = "x"));

        Assert.Equal(TimeSpan.FromSeconds(15), total);
        Assert.Equal(600, steps);
    }

    [Fact]
    public void TwoStoresOnTheSameFile_NeverLoseEachOthersChanges()
    {
        var one = new StateStore(_folder.Paths.StateFile);
        var two = new StateStore(_folder.Paths.StateFile);

        for (var i = 0; i < 10; i++)
        {
            one.Update(s => s.ResidueItems.Add(Left.Words($"one {i}")));
            two.Update(s => s.ResidueItems.Add(Left.Words($"two {i}")));
        }

        var residue = one.Load().Residue;
        Assert.Contains("one 0", residue);
        Assert.Contains("two 9", residue);
        Assert.Equal(20, residue.Count);
    }

    [Fact]
    public async Task ChangesMadeAtTheSameTimeByTwoStores_AreAllKept()
    {
        // The wait is a yield and the patience counts the waits made, so a patience of 15 seconds would be used up in microseconds while the other store holds the lock for the length of one file write: the
        // test failed now and then under load (red-team round 4, F9). A patience that cannot be used up makes the test depend only on the other store letting go, which it always does; nothing is timed.
        var left = new StateStore(_folder.Paths.StateFile, lockPatience: TimeSpan.MaxValue, lockStep: TimeSpan.FromTicks(1), wait: _ => Thread.Yield());
        var right = new StateStore(_folder.Paths.StateFile, lockPatience: TimeSpan.MaxValue, lockStep: TimeSpan.FromTicks(1), wait: _ => Thread.Yield());
        left.Update(s => s.ResidueItems = []);

        var a = Task.Run(() => Enumerable.Range(0, 10).ToList().ForEach(i => left.Update(s => s.ResidueItems.Add(Left.Words($"left {i}")))), TestContext.Current.CancellationToken);
        var b = Task.Run(() => Enumerable.Range(0, 10).ToList().ForEach(i => right.Update(s => s.ResidueItems.Add(Left.Words($"right {i}")))), TestContext.Current.CancellationToken);
        await Task.WhenAll(a, b).OrFail();

        Assert.Equal(20, left.Load().Residue.Count);
    }

    [Fact]
    public async Task AReaderThatHasTheFileOpen_DoesNotStopTheWriterFromReplacingIt()
    {
        var state = new StateStore(_folder.Paths.StateFile);
        state.Update(s => s.LastBootId = "first");
        var reading = new FileStream(_folder.Paths.StateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // A real reader holds the file for a moment; Windows refuses the replace until it lets go, so the writer must wait for it rather than fail.
        var cancel = TestContext.Current.CancellationToken;
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancel).ConfigureAwait(false);
                await reading.DisposeAsync().ConfigureAwait(false);
            },
            cancel);
        state.Update(s => s.LastBootId = "second");
        await release;

        Assert.Equal("second", state.Load().LastBootId);
    }

    [Fact]
    public void AStateFileThatIsDamaged_OrRemovedBetweenTheCheckAndTheRead_ForgetsTheHistory_NotTheProgram()
    {
        var state = new StateStore(_folder.Paths.StateFile);
        File.WriteAllText(_folder.Paths.StateFile, "{ not json");

        Assert.Null(state.Load().LastBootId);
        state.Update(s => s.LastBootId = "fresh");
        Assert.Equal("fresh", state.Load().LastBootId);
    }

    [Fact]
    public void AChangeStartedInsideAnotherChange_IsRefused_SoItCannotBeLostQuietly_AndTheStoreWorksAfterwards()
    {
        var state = new StateStore(_folder.Paths.StateFile, wait: _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() => state.Update(s =>
        {
            s.LastBootId = "outer";
            state.Update(inner => inner.LastBootId = "inner");
        }));

        Assert.Contains("inside another change", ex.Message, StringComparison.Ordinal);
        Assert.Null(state.Load().LastBootId);
        state.Update(s => s.LastBootId = "after");
        Assert.Equal("after", state.Load().LastBootId);
    }

    [Fact]
    public void AChangeThatThrows_LetsGoOfTheLock_AndChangesNothing()
    {
        var state = new StateStore(_folder.Paths.StateFile, wait: _ => { });
        state.Update(s => s.LastBootId = "before");

        Assert.Throws<InvalidOperationException>(() => state.Update(s =>
        {
            s.LastBootId = "half";
            throw new InvalidOperationException("the change failed");
        }));

        Assert.Equal("before", state.Load().LastBootId);
        using (HoldTheLock())
        {
            // The lock was let go, so this open works.
        }

        state.Update(s => s.LastBootId = "next");
        Assert.Equal("next", state.Load().LastBootId);
    }

    [Fact]
    public void WithoutTheDataFolder_ThereIsNoLockAndNoChange()
    {
        var state = new StateStore(Path.Combine(_folder.Paths.Machine, "missing", "state.json"));

        Assert.ThrowsAny<IOException>(() => state.Update(s => s.LastBootId = "x"));
    }
}
