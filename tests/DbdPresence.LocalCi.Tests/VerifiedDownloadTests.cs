// Covers: FR-CI-04 (every download is checked against a checksum from the publisher before it is used: the Linux runner program against the SHA-256 in its release notes and, when GitHub
// gives one, the release file's own digest, from an address under the official release of that version, never an older version than the one cached; the Ubuntu image against the SHA-256
// pinned in the program, with the weekly rebuild starting from that same image), FR-CI-09 (the checksums are recorded in the data folder, the files are hashed again before every use,
// planted or linked entries are ignored, downloads have size limits and need free disk room), NFR-SEC-01. docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi.Tests;

public sealed class VerifiedDownloadTests
{
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Notes(string linuxSha) =>
        "## What's Changed\n* stuff\n\n## SHA-256 Checksums\n\n"
        + $"- actions-runner-linux-x64-2.338.0.tar.gz <!-- BEGIN SHA linux-x64 -->{linuxSha}<!-- END SHA linux-x64 -->\n";

    [Fact]
    public void ReleaseNotes_GiveTheShaBetweenTheMarkers()
    {
        var linux = new string('A', 64);
        var notes = Notes(linux) + $"- actions-runner-win-x64-2.338.0.zip <!-- BEGIN SHA win-x64 -->{new string('b', 64)}<!-- END SHA win-x64 -->\n";

        Assert.Equal(new string('a', 64), RunnerReleaseNotes.ShaFor(notes, RunnerReleaseNotes.LinuxPlatform));
        Assert.Equal(new string('b', 64), RunnerReleaseNotes.ShaFor(notes, "win-x64"));
    }

    [Fact]
    public void ReleaseNotes_WithoutTheMarker_AreRefused()
    {
        var notes = "no checksums here <!-- BEGIN SHA win-x64 -->" + new string('a', 64) + "<!-- END SHA win-x64 -->";

        var ex = Assert.Throws<DownloadVerificationException>(() => RunnerReleaseNotes.ShaFor(notes, RunnerReleaseNotes.LinuxPlatform));

        Assert.Contains("linux-x64", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<LINUX_X64_SHA>")]
    [InlineData("abc123")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ReleaseNotes_WithAValueThatIsNotASha256_AreRefused(string value)
    {
        var notes = $"<!-- BEGIN SHA linux-x64 -->{value}<!-- END SHA linux-x64 -->";

        Assert.Throws<DownloadVerificationException>(() => RunnerReleaseNotes.ShaFor(notes, RunnerReleaseNotes.LinuxPlatform));
    }

    [Fact]
    public void ReleaseNotes_WithTwoDifferentValues_AreRefused()
    {
        var notes = $"<!-- BEGIN SHA linux-x64 -->{new string('a', 64)}<!-- END SHA linux-x64 --> <!-- BEGIN SHA linux-x64 -->{new string('b', 64)}<!-- END SHA linux-x64 -->";

        Assert.Throws<DownloadVerificationException>(() => RunnerReleaseNotes.ShaFor(notes, RunnerReleaseNotes.LinuxPlatform));
    }

    // ---- the address and the digest GitHub keeps

    [Theory]
    [InlineData("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz", true)]
    [InlineData("http://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz", false)]
    [InlineData("https://github.com/evil/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz", false)]
    [InlineData("https://github.com/actions/runner/releases/download/v2.337.0/actions-runner-linux-x64-2.338.0.tar.gz", false)]
    [InlineData("https://github.com/actions/runner/releases/download/v2.338.0/other.tar.gz", false)]
    [InlineData("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz?x=1", false)]
    [InlineData("https://github.com.evil.example/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz", false)]
    [InlineData("https://github.com/actions/runner/releases/download/v2.338.0/../../../../evil/x.tar.gz", false)]
    [InlineData("https://objects.githubusercontent.com/actions-runner-linux-x64-2.338.0.tar.gz", false)]
    public void ARunnerDownload_MustBeExactlyTheOfficialReleaseFile(string address, bool expected)
    {
        Assert.Equal(expected, RunnerAssetPolicy.IsExpectedAddress(new Uri(address), "2.338.0", RunnerAssetPolicy.LinuxFileName("2.338.0")));
    }

    [Theory]
    [InlineData("2.338.0", true)]
    [InlineData("10.0.1", true)]
    [InlineData("2.338", false)]
    [InlineData("2.338.0-rc1", false)]
    [InlineData("v2.338.0", false)]
    [InlineData("../2.338.0", false)]
    [InlineData("", false)]
    public void ARunnerVersion_IsThreeNumbers(string text, bool expected) => Assert.Equal(expected, RunnerAssetPolicy.IsVersion(text));

    [Fact]
    public void TheDigestOfAReleaseFile_IsReadOnlyInItsDocumentedForm()
    {
        var hex = new string('c', 64);

        Assert.Equal(hex, RunnerAssetPolicy.ParseDigest("sha256:" + hex));
        Assert.Equal(hex, RunnerAssetPolicy.ParseDigest("sha256:" + hex.ToUpperInvariant()));
        Assert.Null(RunnerAssetPolicy.ParseDigest(null));
        Assert.Null(RunnerAssetPolicy.ParseDigest(" "));
        Assert.Throws<DownloadVerificationException>(() => RunnerAssetPolicy.ParseDigest("sha1:" + new string('c', 40)));
        Assert.Throws<DownloadVerificationException>(() => RunnerAssetPolicy.ParseDigest("sha256:short"));
    }

    private static (RunnerCache Cache, FakeDownloads Downloads, FakeGitHubApi Api, LocalCiPaths Paths, FakeProtectedStorage Storage, FakeDisk Disk, IntegrityStore Integrity, MemoryCiLog Log) NewCache(
        TempFolder folder, byte[] linux, string notes, string? version = null, string? linuxDigest = null)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var paths = folder.Paths;
        var downloads = new FakeDownloads();
        var number = version ?? "2.338.0";
        var linuxUrl = new Uri($"https://github.com/actions/runner/releases/download/v{number}/actions-runner-linux-x64-{number}.tar.gz");
        downloads.Files[linuxUrl.ToString()] = linux;
        var api = new FakeGitHubApi { Release = new RunnerRelease(number, notes, linuxUrl, linuxDigest) };
        var retry = new Retry(TestTime.New(), redactor, log, attempts: 1);
        var storage = new FakeProtectedStorage();
        var disk = new FakeDisk();
        var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        return (new RunnerCache(api, downloads, paths, log, retry, integrity, storage, disk), downloads, api, paths, storage, disk, integrity, log);
    }

    [Fact]
    public async Task RunnerCache_KeepsAPackageOnlyAfterItsChecksumMatches_AndRecordsIt()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, _, paths, _, _, integrity, _) = NewCache(folder, linux, Notes(Sha(linux)));

        var version = await cache.RefreshAsync(CancellationToken.None);

        Assert.Equal("2.338.0", version);
        Assert.Equal(paths.LinuxPackage("2.338.0"), cache.LatestPackage());
        Assert.Empty(Directory.GetFiles(paths.RunnerVersionDirectory("2.338.0"), "*.part"));
        Assert.Equal(Sha(linux), integrity.Recorded(paths.LinuxPackage("2.338.0")));
        Assert.All(downloads.Limits, limit => Assert.Equal(LocalCiDefaults.MaximumRunnerPackageBytes, limit));
    }

    [Fact]
    public async Task RunnerCache_WithAChecksumMismatch_DeletesTheDownloadAndRefuses()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, _, _, paths, _, _, integrity, _) = NewCache(folder, linux, Notes(new string('0', 64)));

        await Assert.ThrowsAsync<DownloadVerificationException>(() => cache.RefreshAsync(CancellationToken.None));

        Assert.Null(cache.LatestPackage());
        Assert.Empty(Directory.GetFiles(paths.RunnerVersionDirectory("2.338.0")));
        Assert.Null(integrity.Recorded(paths.LinuxPackage("2.338.0")));
    }

    [Fact]
    public async Task RunnerCache_WithMissingMarker_DownloadsNothing()
    {
        using var folder = new TempFolder();
        var (cache, downloads, _, _, _, _, _, _) = NewCache(folder, [1], "release notes without checksums");

        await Assert.ThrowsAsync<DownloadVerificationException>(() => cache.RefreshAsync(CancellationToken.None));

        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task RunnerCache_WhenGitHubsOwnDigestAgreesWithTheNotes_TheDownloadIsUsed()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, _, _, _, _, _, _, _) = NewCache(folder, linux, Notes(Sha(linux)), linuxDigest: Sha(linux));

        await cache.RefreshAsync(CancellationToken.None);

        Assert.NotNull(cache.LatestPackage());
    }

    [Fact]
    public async Task RunnerCache_WhenGitHubsOwnDigestDiffersFromTheNotes_NothingIsDownloaded()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, _, _, _, _, _, _) = NewCache(folder, linux, Notes(Sha(linux)), linuxDigest: new string('d', 64));

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() => cache.RefreshAsync(CancellationToken.None));

        Assert.Contains("linux-x64", ex.Message, StringComparison.Ordinal);
        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task RunnerCache_WithADownloadAddressOutsideTheOfficialRelease_NothingIsDownloaded()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, api, _, _, _, _, _) = NewCache(folder, linux, Notes(Sha(linux)));
        api.Release = api.Release! with { LinuxPackage = new Uri("https://github.com/someone/else/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz") };

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() => cache.RefreshAsync(CancellationToken.None));

        Assert.Contains("download address", ex.Message, StringComparison.Ordinal);
        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task RunnerCache_NeverGoesBackToAnOlderRunner_ButAcceptsTheSameVersionAgain()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, _, paths, _, _, integrity, _) = NewCache(folder, linux, Notes(Sha(linux)));
        await cache.RefreshAsync(CancellationToken.None);
        var newer = "2.340.0";
        Directory.CreateDirectory(paths.RunnerVersionDirectory(newer));
        var newerPackage = paths.LinuxPackage(newer);
        await File.WriteAllTextAsync(newerPackage, "newer", TestContext.Current.CancellationToken);
        integrity.Record(newerPackage, Sha(Encoding.UTF8.GetBytes("newer")));
        downloads.Requested.Clear();

        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(() => cache.RefreshAsync(CancellationToken.None));

        Assert.Contains("older", ex.Message, StringComparison.Ordinal);
        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task RunnerCache_RunsASecondRefreshOfTheSameVersionWithoutDownloadingAgain()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, _, _, _, _, _, _) = NewCache(folder, linux, Notes(Sha(linux)));
        await cache.RefreshAsync(CancellationToken.None);
        downloads.Requested.Clear();

        await cache.RefreshAsync(CancellationToken.None);

        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task RunnerCache_ACachedFileThatChanged_IsDownloadedAgain()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, _, paths, _, _, _, _) = NewCache(folder, linux, Notes(Sha(linux)));
        await cache.RefreshAsync(CancellationToken.None);
        await File.WriteAllTextAsync(paths.LinuxPackage("2.338.0"), "tampered", TestContext.Current.CancellationToken);
        downloads.Requested.Clear();

        await cache.RefreshAsync(CancellationToken.None);

        Assert.Single(downloads.Requested);
        Assert.Equal(linux, await File.ReadAllBytesAsync(paths.LinuxPackage("2.338.0"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunnerCache_WithTooLittleFreeDisk_RefusesBeforeDownloading()
    {
        using var folder = new TempFolder();
        var linux = Encoding.UTF8.GetBytes("linux package");
        var (cache, downloads, _, _, _, disk, _, _) = NewCache(folder, linux, Notes(Sha(linux)));
        disk.Free = 100L * 1024 * 1024;

        var ex = await Assert.ThrowsAsync<IOException>(() => cache.RefreshAsync(CancellationToken.None));

        Assert.Contains("free disk space", ex.Message, StringComparison.Ordinal);
        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public void RunnerCache_KeepsOnlyTheNewestTwoVersions()
    {
        using var folder = new TempFolder();
        var (cache, _, _, paths, _, _, integrity, _) = NewCache(folder, [1], string.Empty);
        foreach (var version in new[] { "2.330.0", "2.331.0", "2.338.0", "2.9.0" })
        {
            Directory.CreateDirectory(paths.RunnerVersionDirectory(version));
            File.WriteAllText(paths.LinuxPackage(version), "x");
            integrity.Record(paths.LinuxPackage(version), Sha("x"u8.ToArray()));
        }

        cache.Prune();

        Assert.Equal(
            ["2.331.0", "2.338.0"],
            Directory.GetDirectories(paths.RunnerCache).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(paths.LinuxPackage("2.338.0"), cache.LatestPackage());
        Assert.Null(integrity.Recorded(paths.LinuxPackage("2.330.0")));
    }

    [Fact]
    public void RunnerCache_IgnoresAPlantedHigherVersion_AndNeverDeletesTheRealOnesBecauseOfIt()
    {
        using var folder = new TempFolder();
        var (cache, _, _, paths, storage, _, integrity, log) = NewCache(folder, [1], string.Empty);
        foreach (var version in new[] { "2.331.0", "2.338.0" })
        {
            Directory.CreateDirectory(paths.RunnerVersionDirectory(version));
            File.WriteAllText(paths.LinuxPackage(version), "real");
            integrity.Record(paths.LinuxPackage(version), Sha("real"u8.ToArray()));
        }

        Directory.CreateDirectory(paths.RunnerVersionDirectory("99.0.0"));
        File.WriteAllText(paths.LinuxPackage("99.0.0"), "planted");
        storage.PathProblems[paths.RunnerVersionDirectory("99.0.0")] = "it is owned by an ordinary user";

        cache.Prune();

        Assert.Equal(paths.LinuxPackage("2.338.0"), cache.LatestPackage());
        Assert.True(Directory.Exists(paths.RunnerVersionDirectory("2.331.0")));
        Assert.True(Directory.Exists(paths.RunnerVersionDirectory("2.338.0")));
        Assert.True(Directory.Exists(paths.RunnerVersionDirectory("99.0.0")));
        Assert.Contains(log.Lines, l => l.Contains("Ignoring", StringComparison.Ordinal) && l.Contains("99.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerCache_IgnoresAPackageWithNoRecordedChecksum_OrOneInAFolderThatCannotBeTrusted()
    {
        using var folder = new TempFolder();
        var (cache, _, _, paths, storage, _, integrity, _) = NewCache(folder, [1], string.Empty);
        Directory.CreateDirectory(paths.RunnerVersionDirectory("2.338.0"));
        File.WriteAllText(paths.LinuxPackage("2.338.0"), "no record");
        Assert.Null(cache.LatestPackage());

        integrity.Record(paths.LinuxPackage("2.338.0"), Sha("no record"u8.ToArray()));
        Assert.NotNull(cache.LatestPackage());

        storage.PathProblems[paths.RunnerCache] = "the runner folder is a link";
        Assert.Null(cache.LatestPackage());
    }

    // ---- the pinned Ubuntu image

    [Fact]
    public void ThePinnedUbuntuImage_IsOneExactFile_WithItsSha256AndAnAddressInCanonicalsFolder()
    {
        var image = UbuntuImage.Pinned;

        Assert.Matches(new Regex(@"^ubuntu-24\.04\.\d+-wsl-amd64\.wsl$", RegexOptions.CultureInvariant), image.FileName);
        Assert.Matches(new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant), image.Sha256);
        Assert.Equal("https://releases.ubuntu.com/noble/" + image.FileName, image.Address.ToString());
        Assert.True(image.SizeBytes is > 100_000_000 and < 1_000_000_000);
        Assert.True(AllowedHosts.IsAllowedDownloadStart(image.Address));
    }

    [Fact]
    public void ThePinnedUbuntuImage_IsTheOneCheckedOn2026_10_29()
    {
        // Changing these is a reviewed commit (security review 2026-10-30, M6); this test makes a silent change impossible.
        Assert.Equal("ubuntu-24.04.5-wsl-amd64.wsl", UbuntuImage.Pinned.FileName);
        Assert.Equal("bb415d824822c4b878125729af451a5d18fb13d1cf5cbed9a7393ad64ac6039e", UbuntuImage.Pinned.Sha256);
        Assert.Equal(388_975_696, UbuntuImage.Pinned.SizeBytes);
    }

    private sealed record BuilderRig(
        BaseImageBuilder Builder,
        FakeWsl Wsl,
        FakeDownloads Downloads,
        LocalCiPaths Paths,
        MemoryCiLog Log,
        IntegrityStore Integrity,
        FakeDisk Disk,
        FakeWslAccount Account,
        FakeWslSettingsFile Settings,
        StateStore State,
        UbuntuImage Pinned);

    private static BuilderRig NewRig(TempFolder folder, byte[] image, string? pinnedSha = null)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var time = TestTime.New();
        var pinned = new UbuntuImage("ubuntu-test.wsl", pinnedSha ?? Sha(image), new Uri("https://releases.ubuntu.com/noble/ubuntu-test.wsl"), image.Length);
        var downloads = new FakeDownloads();
        downloads.Files[pinned.Address.ToString()] = image;
        var wsl = new FakeWsl();
        var account = new FakeWslAccount();
        var settings = new FakeWslSettingsFile();
        var paths = folder.Paths;
        var retry = new Retry(time, redactor, log, attempts: 1);
        var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        var disk = new FakeDisk();
        var state = new StateStore(paths.StateFile);
        var sandbox = Sandboxes.New(wsl, account, settings, log, redactor, time);
        var builder = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, retry, integrity, disk, new ResidueRecorder(state), time, pinned);
        return new BuilderRig(builder, wsl, downloads, paths, log, integrity, disk, account, settings, state, pinned);
    }

    private static (BaseImageBuilder Builder, FakeWsl Wsl, FakeDownloads Downloads, LocalCiPaths Paths, MemoryCiLog Log, IntegrityStore Integrity, FakeDisk Disk) NewBuilder(TempFolder folder, byte[] image, string? pinnedSha = null)
    {
        var rig = NewRig(folder, image, pinnedSha);
        return (rig.Builder, rig.Wsl, rig.Downloads, rig.Paths, rig.Log, rig.Integrity, rig.Disk);
    }

    [Fact]
    public async Task BaseImage_IsBuiltFromThePinnedImage_InASessionOfTheAccount_ExportedThenTheBuildDistributionIsRemoved_AndNoChecksumListIsFetched()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var (builder, wsl, downloads, paths, _, integrity, _) = NewBuilder(folder, image);

        await builder.BuildAsync(CancellationToken.None);

        Assert.Equal(
            [
                "shutdown",
                "import dbd-ci-base",
                "exec dbd-ci-base as root",
                "terminate dbd-ci-base",
                "exec dbd-ci-base as root",
                "terminate dbd-ci-base",
                "export dbd-ci-base",
                "terminate dbd-ci-base",
                "unregister dbd-ci-base",
                "shutdown",
            ],
            wsl.Calls);
        Assert.True(File.Exists(paths.BaseArchive));
        Assert.Empty(wsl.Distributions);
        Assert.Equal(Encoding.UTF8.GetBytes("exported"), await File.ReadAllBytesAsync(paths.BaseArchive, TestContext.Current.CancellationToken));
        Assert.NotNull(integrity.Recorded(paths.BaseArchive));
        Assert.Equal(["https://releases.ubuntu.com/noble/ubuntu-test.wsl"], downloads.Requested);
        Assert.DoesNotContain(downloads.Requested, r => r.Contains("SHA256SUMS", StringComparison.Ordinal));
        Assert.True(builder.Exists);
    }

    /// <summary>Red-team F10: apt runs as root with the network on, so the Windows drives and Windows programs are switched off and the machine is stopped (so the settings apply) before anything is installed.</summary>
    [Fact]
    public async Task TheWindowsFreeSettingsAreWrittenAndTheMachineStopped_BeforeAptRuns()
    {
        using var folder = new TempFolder();
        var rig = NewRig(folder, Encoding.UTF8.GetBytes("ubuntu rootfs"));

        await rig.Builder.BuildAsync(CancellationToken.None);

        Assert.Equal([BaseImageBuilder.WslConfScript, BaseImageBuilder.SetupScript], rig.Wsl.ExecutedScripts);
        Assert.DoesNotContain("apt-get", BaseImageBuilder.WslConfScript, StringComparison.Ordinal);
        Assert.Contains("apt-get update", BaseImageBuilder.SetupScript, StringComparison.Ordinal);
        Assert.Contains("[interop]\\nenabled=false", BaseImageBuilder.WslConfScript, StringComparison.Ordinal);
        Assert.Contains("[automount]\\nenabled=false", BaseImageBuilder.WslConfScript, StringComparison.Ordinal);
        var calls = rig.Wsl.Calls.ToList();
        var firstExec = calls.IndexOf("exec dbd-ci-base as root");
        var secondExec = calls.LastIndexOf("exec dbd-ci-base as root");
        Assert.Contains("terminate dbd-ci-base", calls.Skip(firstExec + 1).Take(secondExec - firstExec - 1));
        Assert.Contains("WSLInterop", BaseImageBuilder.SetupScript, StringComparison.Ordinal);
        Assert.Contains("nothing is installed", BaseImageBuilder.SetupScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheImageIsImportedFromTheCheckedBytes_ThroughStandardInput_IntoAFolderOfItsOwnThatWasEmpty()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var rig = NewRig(folder, image);

        await rig.Builder.BuildAsync(CancellationToken.None);

        Assert.Equal(image, Assert.Single(rig.Wsl.Imported));
        var (directory, wasEmpty) = Assert.Single(rig.Wsl.ImportFolders);
        Assert.True(wasEmpty, "wsl --import was started in a folder that already held something");
        Assert.Equal(rig.Paths.WslDistrosFolder, Path.GetDirectoryName(directory));
        Assert.Matches(@"^dbd-ci-base-\d{14}$", Path.GetFileName(directory));
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>QA T12: a crash left the folder and the registration of the last build behind; the next build neither reuses the folder nor starts over it.</summary>
    [Fact]
    public async Task ABuildAfterACrash_RemovesTheEarlierBuildDistributionFirst_AndStartsInAnEmptyFolder()
    {
        using var folder = new TempFolder();
        var rig = NewRig(folder, Encoding.UTF8.GetBytes("ubuntu rootfs"));
        var crashed = rig.Paths.WslInstallDirectory("dbd-ci-base-20260101000000");
        Directory.CreateDirectory(crashed);
        await File.WriteAllTextAsync(Path.Combine(crashed, "ext4.vhdx"), "left behind by the crash", TestContext.Current.CancellationToken);
        rig.Wsl.Distributions.Add("dbd-ci-base");

        await rig.Builder.BuildAsync(CancellationToken.None);

        Assert.Contains("unregister dbd-ci-base", rig.Wsl.Calls.Take(5));
        Assert.True(Assert.Single(rig.Wsl.ImportFolders).WasEmpty);
        Assert.True(File.Exists(Path.Combine(crashed, "ext4.vhdx")), "an earlier build's folder is not the new build's to delete");
    }

    [Fact]
    public async Task WhenTheEarlierBuildDistributionCannotBeRemoved_NoNewBuildStarts()
    {
        using var folder = new TempFolder();
        var rig = NewRig(folder, Encoding.UTF8.GetBytes("ubuntu rootfs"));
        rig.Wsl.Distributions.Add("dbd-ci-base");
        rig.Wsl.UnregisterFailure = new InvalidOperationException("in use");

        await Assert.ThrowsAsync<IOException>(() => rig.Builder.BuildAsync(CancellationToken.None));

        Assert.DoesNotContain("import dbd-ci-base", rig.Wsl.Calls);
        Assert.Empty(rig.Wsl.Imported);
    }

    [Fact]
    public async Task AnEmptyExport_IsNeverKeptAsTheBaseImage()
    {
        using var folder = new TempFolder();
        var rig = NewRig(folder, Encoding.UTF8.GetBytes("ubuntu rootfs"));
        rig.Wsl.ExportBytes = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Builder.BuildAsync(CancellationToken.None));

        Assert.False(File.Exists(rig.Paths.BaseArchive));
        Assert.Empty(rig.Wsl.Distributions);
        Assert.Equal("shutdown", rig.Wsl.Calls[^1]);
    }

    [Fact]
    public async Task WhenTheExportFails_TheOldBaseImageStays_AndTheBuildMachineIsRemoved()
    {
        using var folder = new TempFolder();
        var rig = NewRig(folder, Encoding.UTF8.GetBytes("ubuntu rootfs"));
        Directory.CreateDirectory(rig.Paths.BaseDirectory);
        await File.WriteAllTextAsync(rig.Paths.BaseArchive, "the old image", TestContext.Current.CancellationToken);
        rig.Integrity.Record(rig.Paths.BaseArchive, Sha(Encoding.UTF8.GetBytes("the old image")));
        rig.Wsl.ExportFailure = new InvalidOperationException("disk full");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Builder.BuildAsync(CancellationToken.None));

        Assert.Equal("the old image", await File.ReadAllTextAsync(rig.Paths.BaseArchive, TestContext.Current.CancellationToken));
        Assert.Empty(rig.Wsl.Distributions);
        Assert.False(File.Exists(rig.Paths.BaseArchive + ".part"), "a half-written export was left in the data folder");
    }

    [Fact]
    public async Task ABuildDistributionThatCannotBeRemoved_IsRecordedAsALeftover_ForTheStatusAndTheDailyNotice()
    {
        using var folder = new TempFolder();
        var rig = NewRig(folder, Encoding.UTF8.GetBytes("ubuntu rootfs"));
        rig.Wsl.OnExecute = (_, _, _, _, _, _) => Task.FromResult(100);
        rig.Wsl.UnregisterFailure = new InvalidOperationException("in use");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Builder.BuildAsync(CancellationToken.None));

        Assert.Contains(rig.State.Load().Residue, r => r.Contains("dbd-ci-base", StringComparison.Ordinal) && r.Contains("could not be removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRecipeIdFollowsThePinnedImageAndBothScripts_AndAnImageIsOnlyCurrentForItsOwnRecipeWithinAWeek()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var rig = NewRig(folder, image);
        var other = NewRig(folder, Encoding.UTF8.GetBytes("another ubuntu"));
        await rig.Builder.BuildAsync(CancellationToken.None);
        var built = TestTime.New().GetUtcNow();
        var state = new CiState { BaseImageRecipe = rig.Builder.RecipeId, LastBaseImageBuild = built };

        Assert.Matches("^[0-9a-f]{16}$", rig.Builder.RecipeId);
        Assert.NotEqual(rig.Builder.RecipeId, other.Builder.RecipeId);
        Assert.True(rig.Builder.IsCurrent(state, built.AddDays(6)));
        Assert.False(rig.Builder.IsCurrent(state, built.AddDays(7)));
        Assert.False(rig.Builder.IsCurrent(new CiState { BaseImageRecipe = "older", LastBaseImageBuild = built }, built));
        Assert.False(rig.Builder.IsCurrent(new CiState { BaseImageRecipe = rig.Builder.RecipeId }, built));
        File.WriteAllText(rig.Paths.BaseArchive, "tampered");
        Assert.False(rig.Builder.IsCurrent(state, built));
    }

    [Fact]
    public async Task TheWeeklyRebuild_StartsFromTheSamePinnedImage_WithoutDownloadingItAgain()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var (builder, wsl, downloads, _, _, _, _) = NewBuilder(folder, image);
        await builder.BuildAsync(CancellationToken.None);
        downloads.Requested.Clear();
        wsl.Calls.Clear();

        await builder.BuildAsync(CancellationToken.None);

        Assert.Empty(downloads.Requested);
        Assert.Contains("import dbd-ci-base", wsl.Calls);
    }

    [Fact]
    public async Task ADownloadedImageThatChangedOnDisk_IsNotImported_ButDownloadedAgainAndChecked()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var (builder, _, downloads, paths, _, _, _) = NewBuilder(folder, image);
        await builder.BuildAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(paths.BaseDirectory, "ubuntu-test.wsl.tar.gz"), "tampered", TestContext.Current.CancellationToken);
        downloads.Requested.Clear();

        await builder.BuildAsync(CancellationToken.None);

        Assert.Single(downloads.Requested);
        Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(paths.BaseDirectory, "ubuntu-test.wsl.tar.gz"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheImageDownload_HasTheSizeOfThePinnedImageAsItsLimit_AndNeedsFreeDisk()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var (builder, _, downloads, _, _, _, disk) = NewBuilder(folder, image);
        disk.Free = 1024;

        await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(CancellationToken.None));
        Assert.Empty(downloads.Requested);

        disk.Free = 500L * 1024 * 1024 * 1024;
        await builder.BuildAsync(CancellationToken.None);

        Assert.Equal([image.Length], downloads.Limits);
    }

    [Fact]
    public void BaseImageScript_InstallsTheToolsAndCutsOffWindows()
    {
        var script = BaseImageBuilder.SetupScript;

        Assert.Contains("apt-get install -y python3 git curl unzip libicu74 ca-certificates", script, StringComparison.Ordinal);
        Assert.Contains("useradd", script, StringComparison.Ordinal);
        Assert.Contains("runner", script, StringComparison.Ordinal);
        Assert.Contains("[automount]\\nenabled=false", script, StringComparison.Ordinal);
        Assert.Contains("[interop]\\nenabled=false", script, StringComparison.Ordinal);
        Assert.Contains("[user]\\ndefault=runner", script, StringComparison.Ordinal);
        Assert.Contains("appendWindowsPath=false", script, StringComparison.Ordinal);
        Assert.Contains("[gpu]\\nenabled=false", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NOPASSWD", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseImageScript_TakesAwayEveryAdministratorRightOfTheRunner_AndChecksItBeforeFinishing()
    {
        var script = BaseImageBuilder.SetupScript;

        Assert.Contains("gpasswd -d runner", script, StringComparison.Ordinal);
        Assert.Contains("passwd -l root", script, StringComparison.Ordinal);
        Assert.Contains("passwd -l runner", script, StringComparison.Ordinal);
        Assert.Contains("sudo -n true", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("usermod -aG sudo", script, StringComparison.Ordinal);
        Assert.DoesNotContain("adduser runner sudo", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseImage_WithAChecksumMismatch_ImportsNothing()
    {
        using var folder = new TempFolder();
        var (builder, wsl, _, paths, _, integrity, _) = NewBuilder(folder, Encoding.UTF8.GetBytes("tampered"), new string('0', 64));

        await Assert.ThrowsAsync<DownloadVerificationException>(() => builder.BuildAsync(CancellationToken.None));

        Assert.Empty(wsl.Calls);
        Assert.False(File.Exists(paths.BaseArchive));
        Assert.Empty(Directory.GetFiles(paths.BaseDirectory, "*.tar.gz*"));
        Assert.Null(integrity.Recorded(paths.BaseArchive));
    }

    [Fact]
    public async Task BaseImage_WhenSetupInsideFails_StillRemovesTheBuildDistribution_AndTheSessionEnds()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var (builder, wsl, _, paths, _, _, _) = NewBuilder(folder, image);
        wsl.OnExecute = (_, _, _, _, _, _) => Task.FromResult(100);
        wsl.PreflightExit = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(CancellationToken.None));

        Assert.Contains("exit code 100", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["unregister dbd-ci-base", "shutdown"], wsl.Calls.Skip(wsl.Calls.Count - 2));
        Assert.False(File.Exists(paths.BaseArchive));
    }

    [Fact]
    public async Task TheBuild_NeverTouchesTheOwnersWsl_ItOnlyUsesTheAccountsFakeAndNamesOnlyItsOwnBuildMachine()
    {
        using var folder = new TempFolder();
        var image = Encoding.UTF8.GetBytes("ubuntu rootfs");
        var (builder, wsl, _, _, _, _, _) = NewBuilder(folder, image);
        wsl.Distributions.Add("Ubuntu");

        await builder.BuildAsync(CancellationToken.None);

        Assert.Contains("Ubuntu", wsl.Distributions);
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("Ubuntu", StringComparison.Ordinal));
    }
}
