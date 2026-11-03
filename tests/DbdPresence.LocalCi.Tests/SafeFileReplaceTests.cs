// Covers: FR-CI-09 (the elevated program writes into a folder the owner controls (the owner's .wslconfig) without being sent anywhere by a link the owner's own programs put there: a new file never gets a
// name that could have been guessed, a new content replaces a file by renaming a new file over its name, and a link or a hard link at that name is replaced, never written through), security review
// 2026-10-30 (own review, same confused-deputy class as the clean-up of a job's folder). docs/owner/free-ci.md.
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class SafeFileReplaceTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string PathOf(string name) => Path.Combine(_folder.Path, name);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Write_ReplacesTheContent_AndLeavesNoOtherFile()
    {
        var target = PathOf(".wslconfig");
        File.WriteAllText(target, "old", Encoding.UTF8);

        SafeFileReplace.Write(target, Bytes("[wsl2]\nguiApplications=false\n"));

        Assert.Equal("[wsl2]\nguiApplications=false\n", File.ReadAllText(target));
        Assert.Equal([target], Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public void Write_CreatesTheFile_WhenItIsNotThere()
    {
        var target = PathOf(".wslconfig");

        SafeFileReplace.Write(target, Bytes("new"));

        Assert.Equal("new", File.ReadAllText(target));
    }

    [Fact]
    public void AFileLinkAtTheTarget_IsReplacedByTheNewFile_AndWhatItLeadsToIsNeverWritten()
    {
        var secret = PathOf("secret.txt");
        File.WriteAllText(secret, "only administrators may read this");
        var target = PathOf(".wslconfig");
        MakeLink(target, secret);

        SafeFileReplace.Write(target, Bytes("[wsl2]\n"));

        Assert.Equal("only administrators may read this", File.ReadAllText(secret));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal("[wsl2]\n", File.ReadAllText(target));
    }

    [Fact]
    public void ADanglingLinkAtTheTarget_IsReplaced_AndNothingIsCreatedWhereItPointed()
    {
        var elsewhere = PathOf("not-there.dll");
        var target = PathOf(".wslconfig");
        MakeLink(target, elsewhere);

        SafeFileReplace.Write(target, Bytes("[wsl2]\n"));

        Assert.False(File.Exists(elsewhere));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal("[wsl2]\n", File.ReadAllText(target));
    }

    [Fact]
    public void TheNewFile_GetsAnUnguessableNameNextToTheTarget()
    {
        var first = SafeFileReplace.UnguessableSibling(PathOf(".wslconfig"), "new");
        var second = SafeFileReplace.UnguessableSibling(PathOf(".wslconfig"), "new");

        Assert.NotEqual(first, second);
        Assert.Matches(@"\.wslconfig\.new-[0-9a-f]{32}$", first);
        Assert.Equal(_folder.Path, Path.GetDirectoryName(first));
    }

    [Fact]
    public void WhenTheTargetIsAFolder_NothingIsReplaced_AndNoNewFileStaysBehind()
    {
        var target = PathOf(".wslconfig");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "kept");

        // Linux says IOException, Windows UnauthorizedAccessException; either way nothing is replaced.
        var error = Record.Exception(() => SafeFileReplace.Write(target, Bytes("new")));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error}");

        Assert.True(File.Exists(Path.Combine(target, "inside.txt")));
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.new-*"));
    }

    [Fact]
    public void CopyToNewFile_CopiesTheRestOfTheStream_AndNeverOverwrites()
    {
        var destination = PathOf("copy.bak");
        using var source = new MemoryStream(Bytes("original text"));

        SafeFileReplace.CopyToNewFile(source, destination);
        Assert.Equal("original text", File.ReadAllText(destination));

        using var again = new MemoryStream(Bytes("other"));
        Assert.Throws<IOException>(() => SafeFileReplace.CopyToNewFile(again, destination));
        Assert.Equal("original text", File.ReadAllText(destination));
    }

    [Fact]
    public void CopyToNewFile_RefusesAnExistingNameThatIsADanglingLink_AndCreatesNothingWhereItPointed()
    {
        // On Windows a create through a dangling link may follow it; that is why the elevated program only ever creates files at unguessable names (above).
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PENDING-ENV: on Windows a create through a dangling link may follow the link; the program avoids the question with unguessable names.");
        var elsewhere = PathOf("planted.dll");
        var destination = PathOf("copy.bak");
        MakeLink(destination, elsewhere);
        using var source = new MemoryStream(Bytes("content"));

        Assert.ThrowsAny<IOException>(() => SafeFileReplace.CopyToNewFile(source, destination));

        Assert.False(File.Exists(elsewhere));
    }

    private static void MakeLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("PENDING-ENV: this system does not let the test make a symbolic link (" + ex.Message + ").");
        }
    }
}
