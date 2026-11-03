// Covers: FR-PACK-01 (content-addressed cache, hash and size verification, atomic index swap, garbage collection),
// FR-ONL-07 (download only new hashes), FR-PACK-04 (withdrawn licensed items), PLAN_V2 §6.3.
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class PackCacheTests
{
    private readonly InMemoryPackFileStore _store = new();

    private readonly InMemoryVersionStore _versions = new();

    private PackCache NewCache() => new(_store, "/cache");

    private SignedDocumentVerifier NewVerifier() =>
        new(new TrustedKeySet(new Dictionary<string, TrustedKey>()), _versions, new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)));

    private static SignedEnvelope EnvelopeOf(long version) =>
        new("k", SignedPurposes.PackIndex, "stable", version, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 30, 0, 0, 0, TimeSpan.Zero), "{}");

    [Fact]
    public void Add_StoresVerifiedFileUnderHashName()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");

        var result = NewCache().Add(entry, PackData.Content("k"));

        Assert.Equal(PackFileAddResult.Stored, result);
        Assert.Equal(Path.Combine("/cache", "files", $"{entry.Sha256}.png"), NewCache().PathOf(entry));
        Assert.Equal(PackData.Content("k"), _store.Files[NewCache().PathOf(entry)]);
        Assert.True(NewCache().Contains(entry));
        Assert.True(NewCache().IsIntact(entry));
    }

    [Fact]
    public void Add_UsesJpgExtensionForJpeg()
    {
        var entry = PackData.Entry("e1", "p", "map:hawkins", "m", format: PackImageFormat.Jpeg);

        NewCache().Add(entry, PackData.Content("m"));

        Assert.EndsWith(".jpg", NewCache().PathOf(entry), StringComparison.Ordinal);
        Assert.True(_store.Exists(NewCache().PathOf(entry)));
    }

    [Fact]
    public void Add_RejectsWrongHashWithoutStoringAnything()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");
        var tampered = PackData.Content("k");
        tampered[0] ^= 0xFF;

        var result = NewCache().Add(entry, tampered);

        Assert.Equal(PackFileAddResult.HashMismatch, result);
        Assert.Empty(_store.Files);
        Assert.Empty(_store.Operations);
    }

    [Fact]
    public void Add_RejectsWrongSizeWithoutStoringAnything()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");

        Assert.Equal(PackFileAddResult.SizeMismatch, NewCache().Add(entry, [.. PackData.Content("k"), 0]));
        Assert.Equal(PackFileAddResult.SizeMismatch, NewCache().Add(entry, []));
        Assert.Empty(_store.Files);
    }

    [Fact]
    public void Add_IsIdempotentAndWritesNothingTheSecondTime()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");
        NewCache().Add(entry, PackData.Content("k"));
        var operations = _store.Operations.Count;

        var again = NewCache().Add(entry, PackData.Content("k"));

        Assert.Equal(PackFileAddResult.AlreadyPresent, again);
        Assert.Equal(operations, _store.Operations.Count);
    }

    [Fact]
    public void Add_WritesToATemporaryFileThenMoves()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");

        NewCache().Add(entry, PackData.Content("k"));

        Assert.Equal(2, _store.Operations.Count);
        Assert.StartsWith("write ", _store.Operations[0], StringComparison.Ordinal);
        Assert.EndsWith(".tmp", _store.Operations[0], StringComparison.Ordinal);
        Assert.StartsWith("move ", _store.Operations[1], StringComparison.Ordinal);
        Assert.DoesNotContain(_store.Files.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Contains_IsFalseForATruncatedFile_AndIsIntactDetectsCorruptionOfSameSize()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");
        var cache = NewCache();
        cache.Add(entry, PackData.Content("k"));

        var corrupted = PackData.Content("k");
        corrupted[0] ^= 0x01;
        _store.Files[cache.PathOf(entry)] = corrupted;
        Assert.True(cache.Contains(entry));
        Assert.False(cache.IsIntact(entry));

        _store.Files[cache.PathOf(entry)] = corrupted[..3];
        Assert.False(cache.Contains(entry));
    }

    [Fact]
    public void Delta_ReturnsOnlyHashesNotInThePreviousIndex()
    {
        var pack = new[] { PackData.Pack("p") };
        var old = PackData.Index(pack, PackData.Entry("a", "p", "role:killer", "k"), PackData.Entry("b", "p", "role:survivor", "s"));
        var next = PackData.Index(
            pack,
            PackData.Entry("a", "p", "role:killer", "k"),
            PackData.Entry("b", "p", "role:survivor", "s2"),
            PackData.Entry("c", "p", "character:x", "x"),
            PackData.Entry("d", "p", "character:y", "x"));

        var delta = PackCache.Delta(old, next);

        Assert.Equal(["b", "c"], delta.Select(e => e.Id));
    }

    [Fact]
    public void Delta_WithoutAPreviousIndexIsEverythingOncePerHash()
    {
        var pack = new[] { PackData.Pack("p") };
        var next = PackData.Index(pack, PackData.Entry("a", "p", "role:killer", "same"), PackData.Entry("b", "p", "role:survivor", "same"));

        Assert.Single(PackCache.Delta(null, next));
        Assert.Empty(PackCache.Delta(next, next));
    }

    [Fact]
    public void Missing_ChecksTheDiskNotThePreviousIndex()
    {
        var pack = new[] { PackData.Pack("p") };
        var a = PackData.Entry("a", "p", "role:killer", "k");
        var b = PackData.Entry("b", "p", "role:survivor", "s");
        var index = PackData.Index(pack, a, b);
        var cache = NewCache();
        cache.Add(a, PackData.Content("k"));

        Assert.Equal([b], cache.Missing(index));
    }

    [Fact]
    public void CommitIndex_ReplacesAtomicallyThenCollectsUnreferencedFiles()
    {
        var pack = new[] { PackData.Pack("p") };
        var keep = PackData.Entry("keep", "p", "role:killer", "keep");
        var drop = PackData.Entry("drop", "p", "role:survivor", "drop");
        var cache = NewCache();
        cache.Add(keep, PackData.Content("keep"));
        cache.Add(drop, PackData.Content("drop"));
        cache.CommitIndex("old"u8, PackData.Index(pack, keep, drop), NewVerifier(), EnvelopeOf(1));
        _store.Operations.Clear();
        _store.Files[Path.Combine("/cache", "files", "notes.txt")] = [1];
        var leftoverTemp = Path.Combine("/cache", "files", $"{PackData.Sha(PackData.Content("zz"))}.png.{new string('a', 32)}.tmp");
        _store.Files[leftoverTemp] = [2];

        var removed = cache.CommitIndex("new"u8, PackData.Index(pack, keep), NewVerifier(), EnvelopeOf(2));

        Assert.Equal(1, removed);
        Assert.True(_store.Exists(leftoverTemp), "garbage collection never touches .tmp files: one may be a write in progress");
        Assert.Equal(2, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));
        Assert.Equal("new"u8.ToArray(), cache.ReadIndexDocument());
        Assert.True(cache.Contains(keep));
        Assert.False(_store.Exists(cache.PathOf(drop)));
        Assert.True(_store.Exists(Path.Combine("/cache", "files", "notes.txt")), "files that are not cache names are never touched");
        Assert.StartsWith($"write {Path.Combine("/cache", "index.json.")}", _store.Operations[0], StringComparison.Ordinal);
        Assert.EndsWith(".tmp", _store.Operations[0], StringComparison.Ordinal);
        Assert.StartsWith("move " + Path.Combine("/cache", "index.json."), _store.Operations[1], StringComparison.Ordinal);
        Assert.EndsWith($" -> {Path.Combine("/cache", "index.json")}", _store.Operations[1], StringComparison.Ordinal);
        Assert.All(_store.Operations.Skip(2), op => Assert.StartsWith("delete ", op, StringComparison.Ordinal));
    }

    [Fact]
    public void ReadIndexDocument_IsNullBeforeTheFirstCommit()
    {
        Assert.Null(NewCache().ReadIndexDocument());
    }

    [Fact]
    public void FilesSharedByTwoEntries_SurviveWhileEitherIsReferenced()
    {
        var pack = new[] { PackData.Pack("p") };
        var a = PackData.Entry("a", "p", "role:killer", "same");
        var b = PackData.Entry("b", "p", "role:survivor", "same");
        var cache = NewCache();
        cache.Add(a, PackData.Content("same"));

        cache.CollectGarbage(PackData.Index(pack, b));

        Assert.True(cache.Contains(b));
    }

    [Fact]
    public void WithdrawnLicensed_ListsOnlyLicensedEntriesAbsentFromTheNewIndex()
    {
        var pack = new[] { PackData.Pack("p") };
        var holder = new LicensedInfo("Holder");
        var gone = PackData.Entry("ghostface", "p", "character:ghostface", "g", holder);
        var kept = PackData.Entry("leon", "p", "character:leon", "l", holder);
        var plainGone = PackData.Entry("trapper", "p", "character:trapper", "t");
        var old = PackData.Index(pack, gone, kept, plainGone);
        var next = PackData.Index(pack, kept);

        Assert.Equal([gone], PackCache.WithdrawnLicensed(old, next));
    }

    [Fact]
    public void CommitIndex_RecordsTheVersionOnlyAfterTheFilesAreCommitted()
    {
        var cache = NewCache();
        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "stable"));

        cache.CommitIndex("doc"u8, PackIndex.Empty, NewVerifier(), EnvelopeOf(4));

        Assert.Equal(4, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void CommitIndex_WhenTheWriteFails_LeavesTheVersionFloorAlone()
    {
        var failing = new ThrowingOnMoveStore();
        var cache = new PackCache(failing, "/cache");

        Assert.Throws<IOException>(() => cache.CommitIndex("doc"u8, PackIndex.Empty, NewVerifier(), EnvelopeOf(4)));

        Assert.Null(_versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void SeedVersionFloor_RaisesTheFloorFromTheCachedIndexDocument()
    {
        using var signer = new TestSigner();
        var verifier = new SignedDocumentVerifier(signer.Trusted, _versions, new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var cache = NewCache();
        Assert.Null(cache.SeedVersionFloor(verifier, "stable"));

        _store.Files[Path.Combine("/cache", "index.json")] = signer.Sign(TestSigner.Envelope("{}", version: 12));

        Assert.Equal(VerificationOutcome.Valid, cache.SeedVersionFloor(verifier, "stable"));
        Assert.Equal(12, _versions.GetHighest(SignedPurposes.PackIndex, "stable"));
    }

    [Fact]
    public void PathOf_RejectsAnEntryWhoseNameIsNotContentAddressed()
    {
        var good = PackData.Entry("e1", "p", "role:killer", "k");
        var bad = good with { Sha256 = "../../evil" };

        Assert.Throws<ArgumentException>(() => NewCache().PathOf(bad));
        Assert.Throws<ArgumentException>(() => NewCache().Add(bad, PackData.Content("k")));
        Assert.Throws<ArgumentException>(() => NewCache().Contains(good with { Sha256 = new string('A', 64) }));
    }

    [Fact]
    public void Add_UsesAUniqueTemporaryNameForEveryWrite()
    {
        var a = PackData.Entry("a", "p", "role:killer", "a");
        var b = PackData.Entry("b", "p", "role:survivor", "b");
        var cache = NewCache();

        cache.Add(a, PackData.Content("a"));
        cache.Add(b, PackData.Content("b"));
        _store.Files.Remove(cache.PathOf(a));
        cache.Add(a, PackData.Content("a"));

        var temps = _store.Operations.Where(o => o.StartsWith("write ", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, temps.Count);
        Assert.Equal(3, temps.Distinct().Count());
    }

    [Fact]
    public void RemoveStaleTempFiles_DeletesOnlyTemporaryWriteLeftovers()
    {
        var entry = PackData.Entry("e1", "p", "role:killer", "k");
        var cache = NewCache();
        cache.Add(entry, PackData.Content("k"));
        var temp = Path.Combine("/cache", "files", $"{entry.Sha256}.png.{new string('b', 32)}.tmp");
        var other = Path.Combine("/cache", "files", "notes.tmp");
        _store.Files[temp] = [1];
        _store.Files[other] = [1];

        Assert.Equal(1, cache.RemoveStaleTempFiles());

        Assert.False(_store.Exists(temp));
        Assert.True(_store.Exists(other));
        Assert.True(cache.Contains(entry));
    }

    [Fact]
    public void Add_RefusesAFileThatWouldExceedTheTotalCap()
    {
        var a = PackData.Entry("a", "p", "role:killer", "a");
        var b = PackData.Entry("b", "p", "role:survivor", "b");
        var cache = new PackCache(_store, "/cache", a.Size + b.Size - 1);

        Assert.Equal(PackFileAddResult.Stored, cache.Add(a, PackData.Content("a")));
        Assert.Equal(PackFileAddResult.CacheFull, cache.Add(b, PackData.Content("b")));
        Assert.False(cache.Contains(b));
    }

    [Fact]
    public void Missing_ThrowsWhenTheWholeIndexCannotFit()
    {
        var a = PackData.Entry("a", "p", "role:killer", "a");
        var b = PackData.Entry("b", "p", "role:survivor", "b");
        var cache = new PackCache(_store, "/cache", a.Size + b.Size - 1);
        var index = PackData.Index([PackData.Pack("p")], a, b);

        Assert.False(cache.Fits(index));
        Assert.Throws<PackCacheLimitException>(() => cache.Missing(index));
        Assert.Equal([a], new PackCache(_store, "/cache", a.Size + b.Size).Missing(PackData.Index([PackData.Pack("p")], a)));
    }

    private sealed class ThrowingOnMoveStore : IPackFileStore
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

    [Fact]
    public void PhysicalStore_RoundTripsOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbdp-packs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PackCache(new PhysicalPackFileStore(), root);
            var entry = PackData.Entry("e1", "p", "role:killer", "k");

            Assert.Equal(PackFileAddResult.Stored, cache.Add(entry, PackData.Content("k")));
            cache.CommitIndex("doc"u8, PackData.Index([PackData.Pack("p")], entry), NewVerifier(), EnvelopeOf(1));
            cache.CommitIndex("doc2"u8, PackIndex.Empty, NewVerifier(), EnvelopeOf(2));

            Assert.False(cache.Contains(entry));
            Assert.Equal("doc2"u8.ToArray(), cache.ReadIndexDocument());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "files")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
