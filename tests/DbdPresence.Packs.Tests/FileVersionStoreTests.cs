// Covers: NFR-SVC-04 (rollback protection survives a restart), ADR-0012 (persistent version store).
using System.Text;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Packs.Tests;

public sealed class FileVersionStoreTests
{
    private const string Path1 = "/cache/versions.json";

    private readonly InMemoryPackFileStore _files = new();

    [Fact]
    public void Versions_SurviveAReopen()
    {
        var first = new FileVersionStore(_files, Path1);
        first.RecordHighest(SignedPurposes.PackIndex, "stable", 7, null);
        first.RecordHighest(SignedPurposes.Config, "beta", 2, null);

        var second = new FileVersionStore(_files, Path1);

        Assert.Null(second.LoadFailure);
        Assert.Equal(7, second.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.Equal(2, second.GetHighest(SignedPurposes.Config, "beta"));
        Assert.Null(second.GetHighest(SignedPurposes.Config, "stable"));
    }

    [Fact]
    public void RecordHighest_NeverLowersAndDoesNotRewriteForALowerValue()
    {
        var store = new FileVersionStore(_files, Path1);
        store.RecordHighest(SignedPurposes.PackIndex, "stable", 7, null);
        _files.Operations.Clear();

        store.RecordHighest(SignedPurposes.PackIndex, "stable", 3, null);
        store.RecordHighest(SignedPurposes.PackIndex, "stable", 7, null);

        Assert.Equal(7, store.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.Empty(_files.Operations);
    }

    [Fact]
    public void Persisting_WritesATemporaryFileThenMoves()
    {
        var store = new FileVersionStore(_files, Path1);

        store.RecordHighest(SignedPurposes.PackIndex, "stable", 1, null);

        Assert.Equal([$"write {Path1}.tmp", $"move {Path1}.tmp -> {Path1}"], _files.Operations);
        Assert.False(_files.Exists(Path1 + ".tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"versions":[{"purpose":"pack-index","channel":"stable","version":-1}]}""")]
    [InlineData("""{"versions":[{"purpose":"pack-index","channel":"stable"}]}""")]
    [InlineData("""{"versions":[{"purpose":"pack-index","channel":"stable","version":1}""")]
    public void DamagedFile_StartsEmpty_AndSaysWhy(string text)
    {
        _files.Files[Path1] = Encoding.UTF8.GetBytes(text);

        var store = new FileVersionStore(_files, Path1);

        Assert.NotNull(store.LoadFailure);
        Assert.Null(store.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void FailedWrite_DoesNotAdvanceTheInMemoryFloor()
    {
        var store = new FileVersionStore(new FailingMoveStore(), Path1);

        Assert.Throws<IOException>(() => store.RecordHighest(SignedPurposes.PackIndex, "stable", 5, null));

        Assert.Null(store.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void Verifier_WithAPersistentStore_RejectsRollbackAfterRestart()
    {
        using var signer = new TestSigner();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var before = new SignedDocumentVerifier(signer.Trusted, new FileVersionStore(_files, Path1), time);
        var v5 = signer.Sign(TestSigner.Envelope("{}", version: 5));
        Assert.True(before.Verify(v5, SignedPurposes.PackIndex, "stable").IsValid);
        before.CommitVersion(SignedPurposes.PackIndex, "stable", 5, null);

        var after = new SignedDocumentVerifier(signer.Trusted, new FileVersionStore(_files, Path1), time);

        Assert.Equal(VerificationOutcome.RolledBack, after.Verify(signer.Sign(TestSigner.Envelope("{}", version: 4)), SignedPurposes.PackIndex, "stable").Outcome);
    }

    private sealed class FailingMoveStore : DbdPresence.Packs.Cache.IPackFileStore
    {
        public bool Exists(string path) => false;

        public long Length(string path) => 0;

        public byte[] ReadAllBytes(string path) => throw new FileNotFoundException(path);

        public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes)
        {
        }

        public void Move(string source, string destination) => throw new IOException("disk full");

        public void Delete(string path)
        {
        }

        public void CreateDirectory(string path)
        {
        }

        public IReadOnlyList<string> ListFiles(string directory) => [];
    }
}
