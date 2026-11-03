// Covers: FR-CI-09 (folders of the program are deleted without ever following a link out of them; the data folder is never created as a side effect of writing a file; log text is one
// clean, bounded line), security review 2026-10-30 H1 M1 and the low findings. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class FoldersTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // ---- SafeTree

    private static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void ATreeWithReadOnlyFiles_IsDeletedCompletely()
    {
        var root = Path.Combine(_folder.Path, "tree");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        var file = Path.Combine(root, "a", "b", "locked.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(root, "top.txt"), "y");

        SafeTree.Delete(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ALinkInsideATree_IsRemovedAndItsTargetIsLeftAlone()
    {
        var outside = Path.Combine(_folder.Path, "precious");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var root = Path.Combine(_folder.Path, "job");
        Directory.CreateDirectory(root);
        if (!TryLink(Path.Combine(root, "to-outside"), outside, directory: true))
        {
            Assert.Skip("PENDING-ENV: this system does not let the test make a symbolic link.");
        }

        File.WriteAllText(Path.Combine(root, "own.txt"), "own");

        SafeTree.Delete(root);

        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void AFileLink_IsRemovedAndItsTargetIsLeftAlone()
    {
        var target = Path.Combine(_folder.Path, "target.txt");
        File.WriteAllText(target, "keep");
        var root = Path.Combine(_folder.Path, "job");
        Directory.CreateDirectory(root);
        if (!TryLink(Path.Combine(root, "link.txt"), target, directory: false))
        {
            Assert.Skip("PENDING-ENV: this system does not let the test make a symbolic link.");
        }

        SafeTree.Delete(root);

        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void AFolderThatIsItselfALink_IsOnlyUnlinked()
    {
        var outside = Path.Combine(_folder.Path, "precious");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var link = Path.Combine(_folder.Path, "linked");
        if (!TryLink(link, outside, directory: true))
        {
            Assert.Skip("PENDING-ENV: this system does not let the test make a symbolic link.");
        }

        Assert.True(SafeTree.IsLink(link));
        Assert.False(SafeTree.IsLink(outside));

        SafeTree.Delete(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void DeletingSomethingThatIsNotThere_IsNotAnError()
    {
        SafeTree.Delete(Path.Combine(_folder.Path, "nothing"));
        SafeTree.Delete(Path.Combine(_folder.Path, "nothing", "deeper"));
        Assert.False(SafeTree.IsLink(Path.Combine(_folder.Path, "nothing")));
    }

    // ---- the data folder is never created by writing

    [Fact]
    public void TheStateIntegrityAndLockFiles_NeverCreateTheDataFolder()
    {
        var absent = new LocalCiPaths(Path.Combine(_folder.Path, "absent"));
        var state = new StateStore(absent.StateFile);
        var integrity = new IntegrityStore(absent.IntegrityFile, absent.Machine);

        Assert.Throws<DirectoryNotFoundException>(() => state.Update(s => s.LastTokenCheck = DateTimeOffset.UnixEpoch));
        Assert.Throws<DirectoryNotFoundException>(() => integrity.Record(Path.Combine(absent.Machine, "x"), new string('a', 64)));

        Assert.False(Directory.Exists(absent.Machine));
        Assert.Empty(state.Load().Jobs);
        Assert.Null(integrity.Recorded(Path.Combine(absent.Machine, "x")));
    }

    [Fact]
    public void TheLog_NeverCreatesTheDataFolder_ButIsWrittenOnceItExists()
    {
        var absent = new LocalCiPaths(Path.Combine(_folder.Path, "absent"));
        var time = TestTime.New();
        var echo = new StringWriter();
        var log = new FileCiLog(absent.LogDirectory, time, new Redactor(), echo);

        log.Info("before setup");

        Assert.False(Directory.Exists(absent.Machine));
        Assert.Contains("before setup", echo.ToString(), StringComparison.Ordinal);

        Directory.CreateDirectory(absent.Machine);
        log.Info("after setup");

        Assert.Contains("after setup", File.ReadAllText(Path.Combine(absent.LogDirectory, FileCiLog.FileNameFor(time.GetUtcNow()))), StringComparison.Ordinal);
    }

    [Fact]
    public void ADownloadCannotCreateItsFolder()
    {
        Assert.Throws<DirectoryNotFoundException>(() => DataFolder.RequireExists(Path.Combine(_folder.Path, "no")));
        Assert.Throws<DirectoryNotFoundException>(() => DataFolder.CreateChild(Path.Combine(_folder.Path, "no", "child")));
        DataFolder.CreateChild(Path.Combine(_folder.Path, "child"));
        Assert.True(Directory.Exists(Path.Combine(_folder.Path, "child")));
    }

    // ---- log text

    [Fact]
    public void LogText_ReplacesControlCharacters_SoALineCannotForgeFurtherLines()
    {
        var cleaned = LogText.Clean("first\r\n2099-01-01 00:00:00Z INFO forged\tline \u001b[2J\u0007 \u202e done");

        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\u001b', cleaned);
        Assert.DoesNotContain('\u0007', cleaned);
        Assert.DoesNotContain('\u202e', cleaned);
        Assert.Contains("first", cleaned, StringComparison.Ordinal);
        Assert.Contains("forged line", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void LogText_CutsALongLine_AndSaysHowMuchWasLeftOut()
    {
        var cleaned = LogText.Clean(new string('x', 5000), 100);

        Assert.StartsWith(new string('x', 100), cleaned, StringComparison.Ordinal);
        Assert.Contains("4900 more characters not logged", cleaned, StringComparison.Ordinal);
        Assert.Equal(string.Empty, LogText.Clean(null));
        Assert.Equal("plain text", LogText.Clean("plain text"));
        Assert.True(LogText.Clean(new string('y', 100_000)).Length < LogText.MaximumLength + 60);
    }

    [Fact]
    public void ALineBudget_LetsTheFirstLinesThrough_ThenCountsWhatItDropped()
    {
        var budget = new LineBudget(3);

        Assert.True(budget.Allow());
        Assert.True(budget.Allow());
        Assert.True(budget.Allow());
        Assert.False(budget.Allow());
        Assert.False(budget.Allow());
        Assert.Equal(2, budget.Dropped);
    }

    [Fact]
    public void EveryMessageOfTheLogFile_IsOneCleanLine()
    {
        var time = TestTime.New();
        var log = new FileCiLog(_folder.Paths.LogDirectory, time, new Redactor(), null);

        log.Info("a\nb\u001b[31m c");

        var lines = File.ReadAllLines(Path.Combine(_folder.Paths.LogDirectory, FileCiLog.FileNameFor(time.GetUtcNow())));
        Assert.Single(lines);
        Assert.DoesNotContain('\u001b', lines[0]);
    }
}
