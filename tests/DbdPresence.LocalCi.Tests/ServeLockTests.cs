// Covers: red-team L3 (only one loop runs: an exclusive lock on a file in the closed data folder replaces a global named mutex that any program could create first; the lock is held for as long as
// the loop lives and lets go when it ends; asking whether a loop runs never keeps the lock; a missing data folder is not created). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ServeLockTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TheFirstLoopGetsTheLock_AnotherOneDoesNot_UntilTheFirstLetsGo()
    {
        var lockFile = _folder.Paths.LockFile;

        using (var first = ServeLock.TryAcquire(lockFile))
        {
            Assert.NotNull(first);
            Assert.Null(ServeLock.TryAcquire(lockFile));
        }

        using var again = ServeLock.TryAcquire(lockFile);
        Assert.NotNull(again);
    }

    [Fact]
    public void AskingWhetherALoopRuns_SeesTheHolder_AndNeverKeepsTheLock()
    {
        var lockFile = _folder.Paths.LockFile;
        Assert.False(ServeLock.IsHeld(lockFile));

        using (ServeLock.TryAcquire(lockFile))
        {
            Assert.True(ServeLock.IsHeld(lockFile));
            Assert.True(ServeLock.IsHeld(lockFile));
        }

        Assert.False(ServeLock.IsHeld(lockFile));
        using var taken = ServeLock.TryAcquire(lockFile);
        Assert.NotNull(taken);
    }

    [Fact]
    public void WithoutTheDataFolder_NothingIsCreated_AndNobodyIsHolding()
    {
        var lockFile = Path.Combine(_folder.Path, "nowhere", "serve.lock");

        Assert.False(ServeLock.IsHeld(lockFile));
        Assert.Throws<DirectoryNotFoundException>(() => ServeLock.TryAcquire(lockFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(lockFile)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]
    [InlineData(unchecked((int)0x80070021), true)]
    [InlineData(11, true)]
    [InlineData(unchecked((int)0x80070005), false)]
    [InlineData(unchecked((int)0x80070070), false)]
    [InlineData(0, false)]
    [InlineData(32, false)]
    public void OnlyAnOpenThatIsRefusedBecauseOfTheLock_IsTheAnswerNoOtherFailure(int hresult, bool isLock)
    {
        var exception = new IOException("x", hresult);

        Assert.Equal(isLock, ServeLock.IsSharingViolation(exception));
    }

    [Fact]
    public void AnArgumentThatIsEmpty_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ServeLock.TryAcquire(string.Empty));
        Assert.Throws<ArgumentException>(() => ServeLock.IsHeld(string.Empty));
    }
}
