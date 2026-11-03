// Covers: FR-PACK-10 (untrusted images are decoded only in the helper process; documented exit codes;
// size, dimension and time caps; no metadata in the output), FR-ART-03 (corrupt, huge and wrong-type files are
// rejected with a clear result). PENDING-ENV on Linux: these run the real helper and need Windows (CI Windows job).
using System.Diagnostics;
using System.Text;
using DbdPresence.Core.Images;
using DbdPresence.ImageHelper;
using DbdPresence.Platform.Windows.Images;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class ImageHelperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdp-imagehelper-" + Guid.NewGuid().ToString("N"));

    public ImageHelperTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private (int ExitCode, string OutputPath) RunHelper(string input)
    {
        var output = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DbdPresence.ImageHelper.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(input);
        info.ArgumentList.Add(output);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "helper did not finish");
        return (process.ExitCode, output);
    }

    private (int ExitCode, string OutputPath) RunFit(string input, int edge)
    {
        var output = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DbdPresence.ImageHelper.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("fit");
        info.ArgumentList.Add(edge.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(input);
        info.ArgumentList.Add(output);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "helper did not finish");
        return (process.ExitCode, output);
    }

    private SafeImageLoader Loader(TimeSpan? timeout = null, string? helperDirectory = null) =>
        new(helperDirectory ?? AppContext.BaseDirectory, Path.Combine(_root, "work"), timeout ?? TimeSpan.FromSeconds(60));

    [Fact]
    public void ValidLargePng_IsScaledToFit1024KeepingAspect()
    {
        var (code, output) = RunHelper(Write("big.png", TestPng.Gray(3000, 2000)));

        Assert.Equal(0, code);
        Assert.True(PngHeader.TryReadSize(File.ReadAllBytes(output), out var w, out var h));
        Assert.Equal(1024, w);
        Assert.InRange(h, 682, 684);
    }

    [Fact]
    public void ValidSmallPng_KeepsItsSize()
    {
        var (code, output) = RunHelper(Write("small.png", TestPng.Gray(100, 60)));

        Assert.Equal(0, code);
        Assert.True(PngHeader.TryReadSize(File.ReadAllBytes(output), out var w, out var h));
        Assert.Equal((100, 60), (w, h));
    }

    [Fact]
    public void ValidJpeg_ProducesAPngWithin1024()
    {
        var jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", "checker-2000x1500.jpg"));

        var (code, output) = RunHelper(Write("photo.jpg", jpeg));

        Assert.Equal(0, code);
        var png = File.ReadAllBytes(output);
        Assert.True(PngHeader.TryReadSize(png, out var w, out var h));
        Assert.Equal(1024, w);
        Assert.InRange(h, 767, 769);
    }

    [Fact]
    public void OutputCarriesNoMetadataChunks()
    {
        var withMetadata = TestPng.Gray(64, 64, ("tEXt", TestPng.Text("Comment", "secret-location")), ("iTXt", TestPng.Text("XML:com.adobe.xmp", "xmp")));

        var (code, output) = RunHelper(Write("meta.png", withMetadata));

        Assert.Equal(0, code);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        foreach (var chunk in new[] { "tEXt", "iTXt", "zTXt", "iCCP", "eXIf", "secret-location" })
        {
            Assert.DoesNotContain(chunk, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TruncatedPng_ExitsWithDecodeFailed()
    {
        var png = TestPng.Gray(300, 300);

        var (code, _) = RunHelper(Write("cut.png", png[..(png.Length * 6 / 10)]));

        Assert.Equal(4, code);
    }

    [Fact]
    public void CorruptPng_ExitsWithDecodeFailed()
    {
        var data = new byte[200];
        new Random(5).NextBytes(data);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);

        var (code, _) = RunHelper(Write("corrupt.png", data));

        Assert.Equal(4, code);
    }

    [Fact]
    public void CorruptJpeg_ExitsWithDecodeFailed()
    {
        var data = new byte[300];
        new Random(6).NextBytes(data);
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(data, 0);

        var (code, _) = RunHelper(Write("corrupt.jpg", data));

        Assert.Equal(4, code);
    }

    [Fact]
    public void Header5000By5000_ExitsWithTooLargeBeforeDecoding()
    {
        var (code, output) = RunHelper(Write("huge.png", TestPng.Gray(5000, 5000)));

        Assert.Equal(3, code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void FileOver8Mb_ExitsWithTooLarge()
    {
        var data = new byte[(8 * 1024 * 1024) + 1];
        TestPng.Gray(10, 10).CopyTo(data, 0);

        var (code, _) = RunHelper(Write("fat.png", data));

        Assert.Equal(3, code);
    }

    [Fact]
    public void NonImage_ExitsWithBadInput()
    {
        var (code, output) = RunHelper(Write("notes.png", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")));

        Assert.Equal(2, code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void MissingInput_ExitsWithBadInput()
    {
        var (code, _) = RunHelper(Path.Combine(_root, "does-not-exist.png"));

        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Loader_ReturnsTheCleanPngForAValidFile()
    {
        var result = await Loader().LoadFileAsync(Write("ok.png", TestPng.Gray(2048, 1024)), CancellationToken.None);

        Assert.Equal(SafeImageStatus.Ok, result.Status);
        Assert.Equal((1024, 512), (result.Width, result.Height));
        Assert.True(PngHeader.TryReadSize(result.PngBytes!, out var w, out _));
        Assert.Equal(1024, w);
    }

    [Fact]
    public async Task Loader_AcceptsBytesFromAnImportArchive()
    {
        var result = await Loader().LoadBytesAsync(TestPng.Gray(40, 30), CancellationToken.None);

        Assert.Equal(SafeImageStatus.Ok, result.Status);
        Assert.Equal((40, 30), (result.Width, result.Height));
    }

    [Fact]
    public async Task Loader_MapsExitCodesToTypedResults()
    {
        var loader = Loader();

        var tooLarge = await loader.LoadFileAsync(Write("huge2.png", TestPng.Gray(5000, 5000)), CancellationToken.None);
        var bad = await loader.LoadFileAsync(Write("bad.png", [1, 2, 3, 4]), CancellationToken.None);
        var whole = TestPng.Gray(300, 300);
        var broken = await loader.LoadFileAsync(Write("cut2.png", whole[..(whole.Length * 6 / 10)]), CancellationToken.None);

        Assert.Equal(SafeImageStatus.TooLarge, tooLarge.Status);
        Assert.Equal(SafeImageStatus.BadInput, bad.Status);
        Assert.Equal(SafeImageStatus.DecodeFailed, broken.Status);
        Assert.All(new[] { tooLarge, bad, broken }, r => Assert.False(string.IsNullOrWhiteSpace(r.UserMessage)));
    }

    [Fact]
    public async Task Loader_KillsTheHelperWhenTheTimeLimitIsExceeded()
    {
        // One millisecond is shorter than any process start, so the helper cannot finish: the timeout path runs.
        var result = await Loader(TimeSpan.FromMilliseconds(1)).LoadFileAsync(Write("slow.png", TestPng.Gray(64, 64)), CancellationToken.None);

        Assert.Equal(SafeImageStatus.TimedOut, result.Status);
        Assert.Contains("too long", result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loader_ReportsAMissingHelper()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var result = await Loader(helperDirectory: empty).LoadFileAsync(Write("x.png", TestPng.Gray(8, 8)), CancellationToken.None);

        Assert.Equal(SafeImageStatus.HelperUnavailable, result.Status);
    }

    [Fact]
    public async Task Loader_NeverLoadsRemoteLocations()
    {
        var result = await Loader().LoadFileAsync("https://example.invalid/a.png", CancellationToken.None);

        Assert.Equal(SafeImageStatus.BadInput, result.Status);
    }

    [Fact]
    public async Task Loader_RemovesItsWorkingFiles()
    {
        await Loader().LoadBytesAsync(TestPng.Gray(8, 8), CancellationToken.None);

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_root, "work")));
    }

    [Theory]
    [InlineData(@"\\localhost\c$\Windows\win.ini")]
    [InlineData(@"\\?\UNC\localhost\share\a.png")]
    [InlineData(@"\\.\pipe\dbdp-test")]
    public async Task Loader_RejectsNetworkAndDevicePaths_BeforeTheHelperStarts(string path)
    {
        // The helper directory does not exist: had the loader tried to start the helper, the status would be HelperUnavailable.
        var loader = Loader(helperDirectory: Path.Combine(_root, "no-helper-here"));

        var result = await loader.LoadFileAsync(path, CancellationToken.None);

        Assert.Equal(SafeImageStatus.BadInput, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.UserMessage));
        Assert.DoesNotContain("localhost", result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Loader_AcceptsALocalPathOnAFixedDrive()
    {
        var result = await Loader().LoadFileAsync(Write("local.png", TestPng.Gray(16, 16)), CancellationToken.None);

        Assert.Equal(SafeImageStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Loader_DetailsNeverContainPathsOrExceptionMessages()
    {
        var missingHelper = await Loader(helperDirectory: Path.Combine(_root, "no-helper-here")).LoadFileAsync(Write("d1.png", TestPng.Gray(8, 8)), CancellationToken.None);
        var blockedWork = Path.Combine(_root, "blocked");
        File.WriteAllText(blockedWork, "a file where a folder is needed");
        var workFailed = await new SafeImageLoader(AppContext.BaseDirectory, Path.Combine(blockedWork, "sub"), TimeSpan.FromSeconds(60))
            .LoadBytesAsync(TestPng.Gray(8, 8), CancellationToken.None);
        var badPath = await Loader().LoadFileAsync("bad\0name.png", CancellationToken.None);

        foreach (var result in new[] { missingHelper, workFailed, badPath })
        {
            Assert.NotEqual(SafeImageStatus.Ok, result.Status);
            Assert.DoesNotContain(_root, result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AppContext.BaseDirectory, result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(SafeImageStatus.BadInput, workFailed.Status);
    }

    [Fact]
    public void HelperOutput_PassesTheStrictPngWalker()
    {
        var (code, output) = RunHelper(Write("strict.png", TestPng.Gray(300, 200)));

        Assert.Equal(0, code);
        Assert.True(StrictPng.TryValidate(File.ReadAllBytes(output), out var w, out var h, out var failure), failure);
        Assert.Equal((300, 200), (w, h));
    }

    [Fact]
    public void AnimatedGif_IsRejected()
    {
        // GIF is not an accepted type, so it stops at the magic-byte check (exit 2). This does not exercise the single-frame check,
        // which needs a multi-frame PNG, JPEG or WebP fixture (PENDING-ENV: none is committed).
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x3B];

        var (code, output) = RunHelper(Write("anim.gif", gif));

        Assert.Equal(2, code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Fit_ShrinksALargeImageToTheEdge_KeepingAspect()
    {
        var (code, output) = RunFit(Write("fit-big.png", TestPng.Gray(3000, 2000)), 512);

        Assert.Equal(0, code);
        Assert.True(PngHeader.TryReadSize(File.ReadAllBytes(output), out var w, out var h));
        Assert.Equal(512, w);
        Assert.InRange(h, 340, 342);
    }

    [Fact]
    public void Fit_NeverUpscales()
    {
        var (code, output) = RunFit(Write("fit-small.png", TestPng.Gray(100, 60)), 1024);

        Assert.Equal(0, code);
        Assert.True(PngHeader.TryReadSize(File.ReadAllBytes(output), out var w, out var h));
        Assert.Equal((100, 60), (w, h));
    }

    [Fact]
    public void Fit_OutputPassesTheStrictWalker_AndCarriesNoMetadata()
    {
        var withMetadata = TestPng.Gray(900, 700, ("tEXt", TestPng.Text("Comment", "secret-location")), ("iTXt", TestPng.Text("XML:com.adobe.xmp", "xmp")));

        var (code, output) = RunFit(Write("fit-meta.png", withMetadata), 512);

        Assert.Equal(0, code);
        var png = File.ReadAllBytes(output);
        Assert.True(StrictPng.TryValidate(png, out _, out _, out var failure), failure);
        var text = Encoding.Latin1.GetString(png);
        foreach (var chunk in new[] { "tEXt", "iTXt", "zTXt", "iCCP", "eXIf", "secret-location" })
        {
            Assert.DoesNotContain(chunk, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fit_LargeNoisyImageAt512_IsUnder2Mb()
    {
        var (code, output) = RunFit(Write("fit-noise.png", TestPng.Noise(2000, 2000, 11)), 512);

        Assert.Equal(0, code);
        Assert.True(new FileInfo(output).Length < 2 * 1024 * 1024);
    }

    [Fact]
    public void Fit_LargeNoisyImageAt1024_IsUnder2MbOrExitsWithOutputTooLargeAndNoFile()
    {
        var (code, output) = RunFit(Write("fit-noise2.png", TestPng.Noise(2000, 2000, 12)), 1024);

        if (code == 0)
        {
            Assert.True(new FileInfo(output).Length < 2 * 1024 * 1024);
        }
        else
        {
            Assert.Equal(6, code);
            Assert.False(File.Exists(output));
        }
    }

    [Fact]
    public void Fit_RefusesOversizeAndInvalidInputWithTheDefinedCodes()
    {
        Assert.Equal(3, RunFit(Write("fit-huge.png", TestPng.Gray(5000, 5000)), 512).ExitCode);
        Assert.Equal(2, RunFit(Write("fit-bad.png", [1, 2, 3, 4]), 512).ExitCode);
        var whole = TestPng.Gray(300, 300);
        Assert.Equal(4, RunFit(Write("fit-cut.png", whole[..(whole.Length * 6 / 10)]), 512).ExitCode);
    }

    [Fact]
    public void Fit_RejectsAnEdgeThatIsNotOffered()
    {
        var (code, output) = RunFit(Write("fit-edge.png", TestPng.Gray(64, 64)), 300);

        Assert.Equal(2, code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Fitter_ReturnsAFittedPng_AndMapsTheOversizeCode()
    {
        var fitter = Loader();

        var ok = await fitter.FitBytesAsync(TestPng.Gray(2048, 1024), 512, CancellationToken.None);
        var noisy = await fitter.FitFileAsync(Write("fit-noise3.png", TestPng.Noise(2000, 2000, 13)), 1024, CancellationToken.None);
        var wrongEdge = await fitter.FitBytesAsync(TestPng.Gray(8, 8), 77, CancellationToken.None);

        Assert.Equal(SafeImageStatus.Ok, ok.Status);
        Assert.Equal((512, 256), (ok.Width, ok.Height));
        Assert.True(noisy.Status is SafeImageStatus.Ok or SafeImageStatus.OutputTooLarge);
        if (noisy.Status == SafeImageStatus.Ok)
        {
            Assert.True(noisy.PngBytes!.Length < 2 * 1024 * 1024);
        }

        Assert.Equal(SafeImageStatus.BadInput, wrongEdge.Status);
    }

    [Fact]
    public async Task Fitter_StillAppliesTheTimeLimitAndRefusesRemoteLocations()
    {
        var fitter = Loader(TimeSpan.FromMilliseconds(1));

        var slow = await fitter.FitFileAsync(Write("fit-slow.png", TestPng.Gray(64, 64)), 512, CancellationToken.None);
        var remote = await fitter.FitFileAsync("https://example.invalid/a.png", 512, CancellationToken.None);

        Assert.Equal(SafeImageStatus.TimedOut, slow.Status);
        Assert.Equal(SafeImageStatus.BadInput, remote.Status);
    }

    [Fact]
    public void Watchdog_LimitsStillApply()
    {
        Assert.True(ResourceWatchdog.IsOverLimit(ResourceWatchdog.MaxWorkingSetBytes + 1, TimeSpan.Zero));
        Assert.True(ResourceWatchdog.IsOverLimit(0, ResourceWatchdog.MaxRunTime + TimeSpan.FromSeconds(1)));
        Assert.False(ResourceWatchdog.IsOverLimit(1024, TimeSpan.FromSeconds(1)));
    }
}
