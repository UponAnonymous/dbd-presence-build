// Covers: FR-CI-09 (the checksum of every cached package and of the Linux base image is kept in a file inside the closed data folder, and the file is hashed again immediately before
// every use from the same open file; a changed, unrecorded or foreign file is refused), security review 2026-10-30 H1. docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class IntegrityStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly IntegrityStore _store;

    public IntegrityStoreTests()
    {
        _store = new IntegrityStore(_folder.Paths.IntegrityFile, _folder.Paths.Machine);
    }

    public void Dispose() => _folder.Dispose();

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_folder.Paths.Machine, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ARecordedChecksum_IsReadBack_AndSurvivesANewStoreOnTheSameFile()
    {
        var path = Write("runner/a.zip", "a");

        _store.Record(path, Sha("a").ToUpperInvariant());

        Assert.Equal(Sha("a"), _store.Recorded(path));
        Assert.Equal(Sha("a"), new IntegrityStore(_folder.Paths.IntegrityFile, _folder.Paths.Machine).Recorded(path));
        Assert.Null(_store.Recorded(Path.Combine(_folder.Paths.Machine, "runner", "b.zip")));
    }

    [Fact]
    public async Task AFileThatStillMatches_IsOpenedRewound_AndIsTheVerySameOpenFile()
    {
        var path = Write("base/image.tar", "the image");
        _store.Record(path, Sha("the image"));

        await using var verified = await _store.OpenVerifiedAsync(path, CancellationToken.None);

        Assert.Equal(0, verified.Position);
        using var reader = new StreamReader(verified, Encoding.UTF8);
        Assert.Equal("the image", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheVerifiedStream_CanOnlyBeRead()
    {
        // Windows keeps other writers out while the file is open (it is opened with FileShare.Read); the share mode itself cannot be seen from outside and Linux only advises, so this checks the access.
        var path = Write("base/image.tar", "the image");
        _store.Record(path, Sha("the image"));

        await using var verified = await _store.OpenVerifiedAsync(path, CancellationToken.None);

        Assert.True(verified.CanRead);
        Assert.True(verified.CanSeek);
        Assert.False(verified.CanWrite);
    }

    [Fact]
    public async Task AFileThatChangedAfterItWasRecorded_IsRefused_AndLeftClosed()
    {
        var path = Write("runner/a.zip", "good");
        _store.Record(path, Sha("good"));
        File.WriteAllText(path, "swapped");

        var ex = await Assert.ThrowsAsync<IntegrityException>(() => _store.OpenVerifiedAsync(path, CancellationToken.None));

        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
        File.Delete(path);
    }

    [Fact]
    public async Task AFileThatWasNeverRecorded_IsRefused()
    {
        var path = Write("runner/planted.zip", "planted");

        var ex = await Assert.ThrowsAsync<IntegrityException>(() => _store.OpenVerifiedAsync(path, CancellationToken.None));

        Assert.Contains("no recorded checksum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APathOutsideTheDataFolder_CannotBeRecorded()
    {
        var outside = Path.Combine(_folder.Path, "elsewhere.zip");

        Assert.Throws<IntegrityException>(() => _store.Record(outside, Sha("x")));
        Assert.Throws<IntegrityException>(() => _store.Recorded(Path.Combine(_folder.Paths.Machine, "..", "elsewhere.zip")));
    }

    [Fact]
    public void ADamagedRecordFile_MeansNothingIsVerified_NotThatAnythingIsTrusted()
    {
        var path = Write("runner/a.zip", "a");
        _store.Record(path, Sha("a"));
        File.WriteAllText(_folder.Paths.IntegrityFile, "{ not json");

        Assert.Null(_store.Recorded(path));
    }

    [Fact]
    public void ForgettingAFolder_DropsTheRecordsOfEverythingInIt_AndNothingElse()
    {
        var one = Write("runner/2.330.0/a.zip", "a");
        var two = Write("runner/2.330.0/b.tar.gz", "b");
        var keep = Write("runner/2.331.0/a.zip", "c");
        _store.Record(one, Sha("a"));
        _store.Record(two, Sha("b"));
        _store.Record(keep, Sha("c"));

        _store.ForgetUnder(Path.Combine(_folder.Paths.Machine, "runner", "2.330.0"));

        Assert.Null(_store.Recorded(one));
        Assert.Null(_store.Recorded(two));
        Assert.Equal(Sha("c"), _store.Recorded(keep));
    }

    [Fact]
    public async Task IsIntact_SaysWhetherTheFileStillMatches()
    {
        var path = Write("runner/a.zip", "a");
        Assert.False(await _store.IsIntactAsync(path, CancellationToken.None));

        _store.Record(path, Sha("a"));
        Assert.True(await _store.IsIntactAsync(path, CancellationToken.None));

        File.WriteAllText(path, "b");
        Assert.False(await _store.IsIntactAsync(path, CancellationToken.None));
        Assert.False(await _store.IsIntactAsync(Path.Combine(_folder.Paths.Machine, "missing"), CancellationToken.None));
    }

    [Fact]
    public void Paths_AreComparedWithoutRegardToCase_LikeWindows()
    {
        var path = Write("Runner/A.zip", "a");

        _store.Record(path, Sha("a"));

        Assert.Equal(Sha("a"), _store.Recorded(path.ToUpperInvariant().Replace(_folder.Paths.Machine.ToUpperInvariant(), _folder.Paths.Machine, StringComparison.Ordinal)));
    }

    /// <summary>
    /// QA M1b: what the loop checked is what WSL reads. The verified file is held open for reading only, so nothing can write to it, rename it or delete it while it is held. Windows enforces sharing
    /// modes (Linux only advises), so this runs in the Windows CI; on Linux it says why it is skipped.
    /// </summary>
    [Fact]
    public async Task AVerifiedFileIsHeldForReadingOnly_NobodyCanWriteRenameOrDeleteItWhileItIsHeld()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PENDING-ENV: Windows enforces sharing modes and Linux only advises; this runs in the Windows CI.");
        var path = Write("base/held.tar", "held");
        _store.Record(path, Sha("held"));

        await using (var held = await _store.OpenVerifiedAsync(path, TestContext.Current.CancellationToken))
        {
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
            Assert.True(Record.Exception(() => File.Delete(path)) is IOException or UnauthorizedAccessException, "a held file was deleted");
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Equal(4, reader.Length);
            Assert.Equal(0, held.Position);
        }

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
