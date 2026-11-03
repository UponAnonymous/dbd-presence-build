// Covers: security review L1 (network paths are refused before any file is read), L4 and QA F12 (an import streams its results, keeps a bounded total,
// reports progress and can be cancelled), L5 (adopted files are strict PNGs, stored files are re-hashed, a failed move leaves no temp file), QA F7 (the
// source hash is read off the calling thread), FR-PACK-10, FR-PACK-12, ADR-0013, ADR-0016.
using DbdPresence.Core.Images;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

/// <summary>A storage that forwards to the in-memory one and lets a test watch reads or make a move fail.</summary>
internal sealed class HookedStorage(InMemoryLocalStorage inner) : ILocalPackStorage
{
    public List<string> Reads { get; } = [];

    public Action<string>? OnRead { get; set; }

    public bool FailMove { get; set; }

    public bool Exists(string path) => inner.Exists(path);

    public long Length(string path) => inner.Length(path);

    public byte[] ReadAllBytes(string path)
    {
        Reads.Add(path);
        OnRead?.Invoke(path);
        return inner.ReadAllBytes(path);
    }

    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes) => inner.WriteAllBytesDurable(path, bytes);

    public void Move(string source, string destination)
    {
        if (FailMove)
        {
            throw new IOException("disk full");
        }

        inner.Move(source, destination);
    }

    public void Delete(string path) => inner.Delete(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public IReadOnlyList<string> ListFiles(string directory) => inner.ListFiles(directory);

    public IReadOnlyList<string> ListDirectories(string directory) => inner.ListDirectories(directory);

    public void DeleteDirectory(string directory) => inner.DeleteDirectory(directory);
}

public sealed class PictureHardeningTests
{
    private const string Root = "/data/packs";
    private readonly InMemoryLocalStorage _files = new();
    private readonly HookedStorage _storage;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));

    public PictureHardeningTests() => _storage = new HookedStorage(_files);

    private PictureStore Store(FakeFitter fitter, Func<string, bool>? isLocal = null) => new(_storage, Root, fitter, _time, isLocal);

    private static FakeFitter Fitter() => new(edge => FakePng.Make(edge, edge, 3000));

    // ---- L1: network paths -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\server\share\a.png")]
    [InlineData("//server/share/a.png")]
    [InlineData("https://example.com/a.png")]
    public async Task AddFile_RefusesNetworkPaths_BeforeAnyReadOrHelperCall(string path)
    {
        var fitter = Fitter();

        var result = await Store(fitter).AddFileAsync(path, 512, CancellationToken.None);

        Assert.Null(result.Item);
        Assert.Equal(LocalPathGuard.RefusalMessage, result.Error);
        Assert.Empty(_storage.Reads);
        Assert.Empty(fitter.Edges);
    }

    [Fact]
    public async Task AddFile_AsksTheInjectedPolicy_ForADriveThatIsNotLocal()
    {
        _files.Files["/mapped/a.png"] = [1, 2, 3];
        var fitter = Fitter();

        var result = await Store(fitter, path => !path.StartsWith("/mapped/", StringComparison.Ordinal)).AddFileAsync("/mapped/a.png", 512, CancellationToken.None);

        Assert.Null(result.Item);
        Assert.Empty(_storage.Reads);
        Assert.Empty(fitter.Edges);
    }

    [Fact]
    public void AdoptFile_RefusesNetworkPaths_WithoutReading()
    {
        var item = Store(Fitter()).AdoptFile(@"\\server\share\a.png");

        Assert.Null(item);
        Assert.Empty(_storage.Reads);
    }

    [Fact]
    public async Task AnalyseImport_RefusesNetworkPaths_BeforeAnyRead()
    {
        var library = new PackLibrary(_storage, Root, new FakeImageLoader(_files), TestRoster.Embedded);

        var analysis = await library.AnalyseImportAsync(@"\\server\share\pack.zip", CancellationToken.None);

        Assert.True(analysis.IsRejected);
        Assert.Equal(LocalPathGuard.RefusalMessage, analysis.RejectedReason);
        Assert.Empty(_storage.Reads);
    }

    [Theory]
    [InlineData(@"\\?\UNC\server\share\a.png")]
    [InlineData(@"\\server\share")]
    [InlineData("//server/share")]
    [InlineData("http://x/a.png")]
    [InlineData("")]
    public void TheGuard_RefusesUncAndUrlShapes(string path)
    {
        Assert.False(LocalPathGuard.IsAcceptable(path));
    }

    // ---- F7: the hash is not read on the calling thread --------------------------------------------------------------

    [Fact]
    public async Task AddFile_ReadsTheSourceOffTheCallingThread()
    {
        _files.Files["/in/a.png"] = [1, 2, 3];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _storage.OnRead = _ =>
        {
            entered.Set();

            // A read on the caller's own thread could never be released (the caller is the one waiting): the wait would end by this limit and fail the test.
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "the read blocked the caller");
        };

        var pending = Store(Fitter()).AddFileAsync("/in/a.png", 512, CancellationToken.None);

        Assert.False(pending.IsCompleted);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        release.Set();
        var result = await pending;
        Assert.NotNull(result.Item);
    }

    // ---- L5: adopt, re-hash, temp files ------------------------------------------------------------------------------

    [Fact]
    public void Adopt_RefusesBytesThatAreNotAStrictPng()
    {
        var store = Store(Fitter());
        var good = FakePng.Make(64, 64, 900);
        var badCrc = (byte[])good.Clone();
        badCrc[^1] ^= 0xFF;
        var trailing = good.Concat(new byte[] { 1, 2, 3 }).ToArray();

        Assert.NotNull(store.Adopt(good));
        Assert.Null(store.Adopt(badCrc));
        Assert.Null(store.Adopt(trailing));
        Assert.Null(store.Adopt(new byte[900]));
        Assert.Null(store.Adopt(FakePng.Make(2000, 64, 900)));
    }

    [Fact]
    public void AdoptFile_RefusesAFileThatIsNotAStrictPng()
    {
        _files.Files["/in/x.png"] = new byte[900];

        Assert.Null(Store(Fitter()).AdoptFile("/in/x.png"));
    }

    [Fact]
    public void Adopt_WhenTheMoveFails_ReturnsNull_AndLeavesNoTempFile()
    {
        _storage.FailMove = true;

        var item = Store(Fitter()).Adopt(FakePng.Make(64, 64, 900));

        Assert.Null(item);
        Assert.DoesNotContain(_files.Files.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddFile_WhenTheMoveFails_LeavesNoTempFile()
    {
        _storage.FailMove = true;

        var result = await Store(Fitter()).AddFileAsync("/in/a.png", 512, CancellationToken.None);

        Assert.Null(result.Item);
        Assert.DoesNotContain(_files.Files.Keys, k => k.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Adopt_WritesAgainAFileWhoseContentIsNotWhatItsNameSays()
    {
        var store = Store(Fitter());
        var png = FakePng.Make(64, 64, 900);
        var item = store.Adopt(png)!;
        var stored = Path.Combine(Root, "pictures", item.File!);
        _files.Files[stored] = [9, 9, 9];

        var again = store.Adopt(png);

        Assert.Equal(item.Sha256, again!.Sha256);
        Assert.Equal(png, _files.Files[stored]);
    }

    [Fact]
    public void Read_RefusesAFileThatWasChangedOnDisk()
    {
        var store = Store(Fitter());
        var item = store.Adopt(FakePng.Make(64, 64, 900))!;
        Assert.NotNull(store.Read(item));
        _files.Files[Path.Combine(Root, "pictures", item.File!)] = FakePng.Make(64, 64, 901);

        Assert.Null(store.Read(item));
    }

    // ---- L4 / F12: the import streams, is bounded, reports progress and can be cancelled ----------------------------

    private sealed class Progress : IProgress<ImportProgress>
    {
        public List<ImportProgress> Seen { get; } = [];

        public void Report(ImportProgress value) => Seen.Add(value);
    }

    private static byte[] Archive(int count) =>
        ZipBuilder.Build([.. Enumerable.Range(0, count).Select(i => ($"image-{i}.png", ZipBuilder.Png("m" + i)))]);

    [Fact]
    public async Task AnalyseImport_KeepsOnlyAsMuchAsTheTotalCapAllows_AndSaysWhy()
    {
        _files.Files["/in/a.zip"] = Archive(6);
        var oneImage = FakePng.Make(64, 64, 200).Length;
        var library = new PackLibrary(_storage, Root, new FakeImageLoader(_files), TestRoster.Embedded, new ImportLimits { MaxResultBytes = (3 * oneImage) + 100 });

        var analysis = await library.AnalyseImportAsync("/in/a.zip", CancellationToken.None);

        Assert.Equal(3, analysis.Items.Count);
        Assert.Equal(3, analysis.Skipped.Count(s => s.Reason.Contains("too big to hold", StringComparison.Ordinal)));
        Assert.Equal(["image-0.png", "image-1.png", "image-2.png"], analysis.Items.Select(i => i.DisplayName));
    }

    [Fact]
    public async Task AnalyseImport_ReportsProgressAfterEachImage()
    {
        _files.Files["/in/a.zip"] = Archive(5);
        var library = new PackLibrary(_storage, Root, new FakeImageLoader(_files), TestRoster.Embedded);
        var progress = new Progress();

        await library.AnalyseImportAsync("/in/a.zip", CancellationToken.None, progress);

        Assert.Equal(Enumerable.Range(1, 5).Select(i => new ImportProgress(i, 5)), progress.Seen);
    }

    private sealed class CancellingLoader(CancellationTokenSource source, int cancelAfter) : ISafeImageLoader
    {
        public int Started { get; private set; }

        public Task<SafeImageResult> LoadFileAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SafeImageResult> LoadBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Started++;
            if (Started == cancelAfter)
            {
                source.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SafeImageResult.Success(FakePng.Make(64, 64, 200 + Started), 64, 64));
        }
    }

    [Fact]
    public async Task AnalyseImport_StopsWhenCancelled_AndStartsNothingMore()
    {
        _files.Files["/in/a.zip"] = Archive(30);
        using var source = new CancellationTokenSource();
        var loader = new CancellingLoader(source, cancelAfter: 2);
        var library = new PackLibrary(_storage, Root, loader, TestRoster.Embedded);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.AnalyseImportAsync("/in/a.zip", source.Token));

        Assert.True(loader.Started < 30);
    }
}
