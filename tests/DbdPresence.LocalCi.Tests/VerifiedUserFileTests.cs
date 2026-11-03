// Covers: red-team F2 and the automated reviews of 2026-10-30 (the files of other users that the elevated program reads, the account's .wslconfig and the program file set-up was started from,
// are opened without following a link, a link or a folder is refused, a missing file is null, and the open file is shared for reading only so nothing can write to it, rename it or delete it while it is
// held). Only Windows can say whether Windows does this, so these run in the Windows CI and say why they are skipped elsewhere (PENDING-ENV). docs/owner/free-ci.md.
using DbdPresence.LocalCi.Windows;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // Every test skips itself first where the Windows functions do not exist.
public sealed class VerifiedUserFileTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "PENDING-ENV: this opens files through the Windows API; it runs in the Windows CI.");

    private string NewFile(string name, string content)
    {
        var path = Path.Combine(_folder.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AnOrdinaryFile_IsOpenedForReading_AndHeldAgainstWritesRenamesAndDeletes()
    {
        RequireWindows();
        var path = NewFile("a.txt", "hello");

        using (var stream = VerifiedUserFile.OpenRead(path))
        {
            Assert.NotNull(stream);
            Assert.Equal("hello", new StreamReader(stream!).ReadToEnd());
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            Assert.True(Record.Exception(() => File.Delete(path)) is IOException or UnauthorizedAccessException);
        }

        File.Delete(path);
    }

    [Fact]
    public void AMissingFile_IsNull_NotAnError()
    {
        RequireWindows();

        Assert.Null(VerifiedUserFile.OpenRead(Path.Combine(_folder.Path, "missing.txt")));
        Assert.Null(VerifiedUserFile.OpenRead(Path.Combine(_folder.Path, "missing-folder", "x.txt")));
    }

    [Fact]
    public void ALink_IsRefused_NotFollowed()
    {
        RequireWindows();
        var target = NewFile("target.txt", "secret");
        var link = Path.Combine(_folder.Path, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception refused) when (refused is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Windows lets only an administrator (or a developer-mode account) make a symbolic link; where it is refused there is no link to test with (round 4, QA minor 11).
            Assert.Skip("PENDING-ENV: this system does not let the test make a symbolic link (" + refused.Message + ").");
        }

        var ex = Assert.Throws<IOException>(() => VerifiedUserFile.OpenRead(link));

        Assert.Contains("is a link", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolder_IsRefused()
    {
        RequireWindows();

        var ex = Assert.Throws<IOException>(() => VerifiedUserFile.OpenRead(_folder.Path));

        Assert.Contains("is a folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPath_IsRefused()
    {
        RequireWindows();

        Assert.Throws<ArgumentException>(() => VerifiedUserFile.OpenRead(string.Empty));
    }
}
