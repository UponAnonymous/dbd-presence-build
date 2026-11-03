// Covers: FR-PACK-12 (every saved file under 2 MB: 1024 px redone at 512 with a notice, refused when still too big; a 512 px image of noise stays under
// 2 MB), FR-PACK-10 (opening and shrinking only through the image helper interface), FR-PACK-14 (links validated, nothing stored), D28 (owner rules).
using System.Security.Cryptography;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Pictures;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class PictureStoreTests
{
    private const string Root = "/data/packs";
    private readonly InMemoryLocalStorage _storage = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));

    private PictureStore Store(FakeFitter fitter) => new(_storage, Root, fitter, _time);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task AddFile_SavesTheHelpersOutputContentAddressed_WithSizeSourceHashAndTime()
    {
        _storage.Files["/in/me.png"] = [1, 2, 3];
        var png = FakePng.Make(512, 512, 300_000);
        var fitter = new FakeFitter(_ => png);

        var result = await Store(fitter).AddFileAsync("/in/me.png", 512, CancellationToken.None);

        Assert.Null(result.Error);
        var item = result.Item!;
        Assert.Equal(Sha(png), item.Sha256);
        Assert.Equal(Sha(png) + ".png", item.File);
        Assert.Equal(300_000, item.Bytes);
        Assert.Equal(Sha([1, 2, 3]), item.SourceSha256);
        Assert.Equal(PictureOrigin.Pc, item.Origin);
        Assert.Equal(_time.GetUtcNow(), item.AddedAt);
        Assert.Equal(png, _storage.Files[$"{Root}/pictures/{item.File}"]);
        Assert.Equal([512], fitter.Edges);
        Assert.Null(result.Notice);
        Assert.True(PictureSlots.IsValid(item));
    }

    [Fact]
    public async Task AddFile_AtLargeSize_RedoesAtStandardSizeWhen2MbOrMore_WithANotice()
    {
        var fitter = new FakeFitter(edge => edge == 1024 ? FakePng.Make(1024, 1024, (int)PictureLimits.MaxFileBytes) : FakePng.Make(512, 512, 400_000));

        var result = await Store(fitter).AddFileAsync("/in/big.png", 1024, CancellationToken.None);

        Assert.Equal([1024, 512], fitter.Edges);
        Assert.NotNull(result.Item);
        Assert.Equal(400_000, result.Item.Bytes);
        Assert.Contains("512", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFile_HelperRefusesTheLargeSizeAsTooBig_RedoesAtStandardSize_WithANotice()
    {
        var fitter = new FakeFitter(_ => FakePng.Make(512, 512, 400_000), edge => edge == 1024);

        var result = await Store(fitter).AddFileAsync("/in/big.png", 1024, CancellationToken.None);

        Assert.Equal([1024, 512], fitter.Edges);
        Assert.NotNull(result.Item);
        Assert.Contains("512", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFile_HelperRefusesBothSizesAsTooBig_ReportsTheTwoMegabyteMessage()
    {
        var fitter = new FakeFitter(_ => null, _ => true);

        var result = await Store(fitter).AddFileAsync("/in/noise.png", 1024, CancellationToken.None);

        Assert.Null(result.Item);
        Assert.Contains("2 MB", result.Error, StringComparison.Ordinal);
        Assert.Equal([1024, 512], fitter.Edges);
    }

    [Fact]
    public async Task AddFile_JustUnder2MbAtLargeSize_KeepsLargeSize()
    {
        var fitter = new FakeFitter(_ => FakePng.Make(1024, 1024, (int)PictureLimits.MaxFileBytes - 1));

        var result = await Store(fitter).AddFileAsync("/in/a.png", 1024, CancellationToken.None);

        Assert.Equal([1024], fitter.Edges);
        Assert.Equal(PictureLimits.MaxFileBytes - 1, result.Item!.Bytes);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task AddFile_StillTooBigAtStandardSize_IsRefused_AndNothingIsStored()
    {
        var fitter = new FakeFitter(edge => FakePng.Make(edge, edge, (int)PictureLimits.MaxFileBytes));

        var result = await Store(fitter).AddFileAsync("/in/noise.png", 1024, CancellationToken.None);

        Assert.Null(result.Item);
        Assert.Contains("2 MB", result.Error, StringComparison.Ordinal);
        Assert.Equal([1024, 512], fitter.Edges);
        Assert.DoesNotContain(_storage.Files.Keys, k => k.Contains("/pictures/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddFile_NoiseAt512_StaysUnder2Mb()
    {
        // 512 x 512 x 4 bytes is 1 MiB before compression, so even incompressible noise is under the limit (PLAN_V2_S5B section 2.6).
        var rawMax = 512 * 512 * 4 + 1024;
        var fitter = new FakeFitter(edge => FakePng.Make(edge, edge, rawMax));

        var result = await Store(fitter).AddFileAsync("/in/noise.png", 512, CancellationToken.None);

        Assert.True(rawMax < PictureLimits.MaxFileBytes);
        Assert.NotNull(result.Item);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(513)]
    public async Task AddFile_RefusesAnOutputLargerThanTheRequestedSize(int edge)
    {
        var fitter = new FakeFitter(_ => FakePng.Make(edge, 100, 5000));

        var result = await Store(fitter).AddFileAsync("/in/a.png", 512, CancellationToken.None);

        Assert.Null(result.Item);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AddFile_RefusesOutputThatIsNotAPng_AndHelperFailures()
    {
        var notPng = await Store(new FakeFitter(_ => new byte[5000])).AddFileAsync("/in/a.png", 512, CancellationToken.None);
        var failed = await Store(new FakeFitter(_ => null)).AddFileAsync("/in/a.png", 512, CancellationToken.None);

        Assert.Null(notPng.Item);
        Assert.Null(failed.Item);
        Assert.Contains("could not be opened", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFile_UnknownSize_FallsBackToStandard()
    {
        var fitter = new FakeFitter(edge => FakePng.Make(edge, edge, 5000));

        await Store(fitter).AddFileAsync("/in/a.png", 300, CancellationToken.None);

        Assert.Equal([512], fitter.Edges);
    }

    [Fact]
    public async Task AddBytes_IsAnImport_AndKeepsTheHashOfTheOriginalBytes()
    {
        byte[] original = [9, 9, 9, 9];
        var fitter = new FakeFitter(edge => FakePng.Make(edge, edge, 6000));

        var result = await Store(fitter).AddBytesAsync(original, 512, CancellationToken.None);

        Assert.Equal(PictureOrigin.Import, result.Item!.Origin);
        Assert.Equal(Sha(original), result.Item.SourceSha256);
    }

    [Fact]
    public void AddLink_AcceptsHttps_RefusesTheRest_AndStoresNothing()
    {
        var store = Store(new FakeFitter(_ => null));

        var ok = store.AddLink("https://example.com/a.png");
        var bad = store.AddLink("http://example.com/a.png");
        var ip = store.AddLink("https://10.0.0.1/a.png");

        Assert.Equal("https://example.com/a.png", ok.Item!.Link);
        Assert.Null(ok.Item.File);
        Assert.Null(bad.Item);
        Assert.Equal("Use a link that starts with https://.", bad.Error);
        Assert.Null(ip.Item);
        Assert.Empty(_storage.Files);
    }

    [Fact]
    public void PathOf_FindsFilesInPicturesAndInTheOlderOverridesFolder_AndNeverForALink()
    {
        var store = Store(new FakeFitter(_ => null));
        var sha = new string('c', 64);
        var legacy = new PictureItem(sha, null, 0, sha + ".png", null, PictureOrigin.Pc, _time.GetUtcNow());
        var link = store.AddLink("https://example.com/a.png").Item!;
        Assert.Null(store.PathOf(legacy));
        _storage.Files[$"{Root}/overrides/{sha}.png"] = FakePng.Make(10, 10, 700);

        Assert.Equal($"{Root}/overrides/{sha}.png", store.PathOf(legacy)?.Replace('\\', '/'));
        Assert.Equal(700, store.BytesOf(legacy));
        Assert.Null(store.PathOf(link));
        Assert.Equal(0, store.BytesOf(link));
    }

    [Fact]
    public async Task Delete_RemovesTheFileFromBothFolders_AndIgnoresLinksAndBadNames()
    {
        var fitter = new FakeFitter(edge => FakePng.Make(edge, edge, 6000));
        var store = Store(fitter);
        var item = (await store.AddFileAsync("/in/a.png", 512, CancellationToken.None)).Item!;
        _storage.Files[$"{Root}/overrides/{item.File}"] = [1];

        store.Delete(item);
        store.Delete(store.AddLink("https://example.com/a.png").Item!);
        store.Delete(item with { File = "../../etc/passwd" });

        Assert.Null(store.PathOf(item));
        Assert.DoesNotContain(_storage.Files.Keys, k => k.Contains(item.Sha256, StringComparison.Ordinal));
    }

    [Fact]
    public void Adopt_CopiesACleanPng_AndRefusesEmptyAndOversizedFiles()
    {
        var store = Store(new FakeFitter(_ => null));
        var png = FakePng.Make(64, 64, 900);

        var item = store.Adopt(png);

        Assert.Equal(Sha(png), item!.Sha256);
        Assert.Equal(PictureOrigin.Import, item.Origin);
        Assert.NotNull(store.PathOf(item));
        Assert.Null(store.Adopt([]));
        Assert.Null(store.Adopt(new byte[PictureLimits.MaxFileBytes]));
        Assert.Null(store.AdoptFile("/missing.png"));
    }

    [Fact]
    public void Read_ReturnsStoredBytes_AndNullForMissing()
    {
        var store = Store(new FakeFitter(_ => null));
        var item = store.Adopt(FakePng.Make(64, 64, 900))!;

        Assert.Equal(900, store.Read(item)!.Length);
        Assert.Null(store.Read(item with { Sha256 = new string('e', 64), File = new string('e', 64) + ".png" }));
    }
}
