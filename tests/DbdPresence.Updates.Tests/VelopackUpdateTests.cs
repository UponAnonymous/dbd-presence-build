// Covers: V2-S6 gate V6.2 (the update test runs through the custom IUpdateSource; on Windows CI it also runs against the real package the installer
// step packed, DBDP_UPDATE_TEST_PACKAGE) and V6.1 (tampered, downgraded and expired feeds are rejected end to end), FR-ONL-10 (Velopack's UpdateManager
// downloads only the verified package and refuses a lower version; nothing reaches the packages folder unless it matches the signed feed), ADR-0006
// (downgrades stay off: proven here), D33 (independent of Authenticode), the ADR-0006 open finding (autostart points at the stable launcher).
using System.IO;
using DbdPresence.App.Updates;
using DbdPresence.Core.Online;
using DbdPresence.Online.Updates;
using DbdPresence.Packs.Releases;
using DbdPresence.ReleaseSigner;
using Velopack;
using Velopack.Locators;

namespace DbdPresence.Updates.Tests;

public sealed class VelopackUpdateTests : IDisposable
{
    /// <summary>Set by the Windows CI step after the installer was packed: the real full package of this build.</summary>
    public const string RealPackageVariable = "DBDP_UPDATE_TEST_PACKAGE";

    private readonly UpdateFixture _f = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task NewerSignedRelease_IsDownloadedIntoThePackagesFolder_AndWaitsForARestart()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        var manager = Manager("1.0.0");

        var info = await manager.CheckForUpdatesAsync();
        await manager.DownloadUpdatesAsync(info!, cancelToken: Ct);

        Assert.False(info!.IsDowngrade);
        Assert.Equal("1.1.0", info.TargetFullRelease.Version.ToNormalizedString());
        Assert.Equal([p.FileName], _f.PackagesOnDisk());
        Assert.Equal(_f.Transport.Packages[p.Sha256], File.ReadAllBytes(Path.Combine(_f.PackagesDir, p.FileName)));
        Assert.Equal("1.1.0", manager.UpdatePendingRestart!.Version.ToNormalizedString());
    }

    [Fact]
    public async Task TamperedFeed_GivesVelopackNothing()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = UpdateFixture.Tamper(_f.Feed([p], version: 10));

        var info = await Manager("1.0.0").CheckForUpdatesAsync();

        Assert.Null(info);
        Assert.Empty(_f.Transport.Downloads);
        Assert.Empty(_f.PackagesOnDisk());
    }

    [Fact]
    public async Task ExpiredFeed_GivesVelopackNothing()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10, issuedAt: UpdateFixture.Start.AddDays(-31));

        Assert.Null(await Manager("1.0.0").CheckForUpdatesAsync());
    }

    [Fact]
    public async Task LowerVersion_IsNeverOffered_DowngradesStayOff()
    {
        var p = _f.Publish("0.9.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);

        Assert.Null(await Manager("1.0.0").CheckForUpdatesAsync());

        // Even an asset list that held a lower version would not be taken: Velopack's own rule with AllowVersionDowngrade false (ADR-0006).
        var direct = new UpdateManager(new FixedSource(SignedUpdateSource.ToAsset(p)), new UpdateOptions { AllowVersionDowngrade = false, ExplicitChannel = "stable" }, Locator("1.0.0"));
        Assert.Null(await direct.CheckForUpdatesAsync());
    }

    [Fact]
    public async Task PackageTamperedOnTheCdn_NeverReachesThePackagesFolder()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        _f.Transport.Packages[p.Sha256][100] ^= 0x01;
        var manager = Manager("1.0.0");
        var info = await manager.CheckForUpdatesAsync();

        await Assert.ThrowsAsync<UpdatePackageRefusedException>(() => manager.DownloadUpdatesAsync(info!, cancelToken: Ct));

        Assert.Empty(_f.PackagesOnDisk());
        Assert.Null(manager.UpdatePendingRestart);
    }

    [Fact]
    public async Task SourceRefusesAnAssetTheFeedDidNotOffer()
    {
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        var source = new SignedUpdateSource(_f.Service(), ReleaseVersion.Parse("1.0.0"));
        await source.GetReleaseFeed(Locator("1.0.0").Log, "DbdPresence", "stable");
        var forged = SignedUpdateSource.ToAsset(p) with { SHA256 = new string('0', 64) };

        await Assert.ThrowsAsync<UpdatePackageRefusedException>(() => source.DownloadReleaseEntry(Locator("1.0.0").Log, forged, Path.Combine(_f.Root, "x.partial"), _ => { }, Ct));
        Assert.Empty(_f.Transport.Downloads);

        var otherApp = await source.GetReleaseFeed(Locator("1.0.0").Log, "SomeOtherApp", "stable");
        Assert.Empty(otherApp.Assets);
    }

    [Fact]
    public async Task AppUpdater_ReportsEachOutcome_AndBecomesReadyToRestart()
    {
        using var updater = Updater("1.0.0", out var shutdowns);
        Assert.Equal(AppUpdateState.Idle, updater.Current.State);

        await updater.CheckNowAsync(Ct);
        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.NothingPublished), (updater.Current.State, updater.Current.Problem));

        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = UpdateFixture.Tamper(_f.Feed([p], version: 10));
        await updater.CheckNowAsync(Ct);
        Assert.Equal(AppUpdateProblem.FeedRefused, updater.Current.Problem);

        _f.Gate.IsOpen = false;
        await updater.CheckNowAsync(Ct);
        Assert.Equal(AppUpdateProblem.OnlineFeaturesOff, updater.Current.Problem);
        _f.Gate.IsOpen = true;

        _f.Transport.Feed = _f.Feed([p], version: 10);
        var changes = new List<AppUpdateState>();
        updater.Changed += (_, _) => changes.Add(updater.Current.State);
        await updater.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateState.ReadyToRestart, updater.Current.State);
        Assert.Equal("1.1.0", updater.Current.Version);
        Assert.Contains(AppUpdateState.Checking, changes);
        Assert.Contains(AppUpdateState.Downloading, changes);
        Assert.Empty(shutdowns);
    }

    [Fact]
    public async Task AppUpdater_ReportsARefusedPackage()
    {
        using var updater = Updater("1.0.0", out _);
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        _f.Transport.Packages[p.Sha256][0] ^= 0x01;

        await updater.CheckNowAsync(Ct);

        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.PackageRefused), (updater.Current.State, updater.Current.Problem));
        Assert.Empty(_f.PackagesOnDisk());
    }

    [Fact]
    public async Task AppUpdater_RestartToUpdate_HandsOverTheCheckedPackage()
    {
        var applied = new List<VelopackAsset>();
        using var updater = Updater("1.0.0", out var shutdowns, applied);
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        await updater.CheckNowAsync(Ct);

        updater.RestartToUpdate();

        Assert.Equal([p.FileName], applied.Select(a => a.FileName));
        Assert.Equal([1], shutdowns);
    }

    [Fact]
    public async Task AppUpdater_ChangedPackageOnDisk_IsDeleted_AndNeverInstalled()
    {
        // Security review 2026-10-25, M3: the package is checked against the signed size and SHA-256 again right before it is installed.
        var applied = new List<VelopackAsset>();
        using var updater = Updater("1.0.0", out var shutdowns, applied);
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        await updater.CheckNowAsync(Ct);
        Assert.Equal(AppUpdateState.ReadyToRestart, updater.Current.State);
        var onDisk = Path.Combine(_f.PackagesDir, p.FileName);
        var bytes = File.ReadAllBytes(onDisk);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(onDisk, bytes);

        updater.RestartToUpdate();

        Assert.Empty(applied);
        Assert.Empty(shutdowns);
        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.PackageRefused), (updater.Current.State, updater.Current.Problem));
        Assert.Empty(_f.PackagesOnDisk());
        Assert.Contains(_f.Log.Lines, l => l.Contains("does not match the signed size and SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_PendingPackage_IsDeleted_WhenItsFeedKeyIsRevoked()
    {
        // Security review 2026-10-25, M3: a package downloaded under a key that a key replacement revoked since is never installed.
        using var successor = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var files = new MemoryFiles();
        var keys = new KeyReplacementService(_f.Transport, _f.Verifier, _f.Keys, files, "online/key-replacement.json", _f.Log);
        var applied = new List<VelopackAsset>();
        using var updater = Updater("1.0.0", out _, applied, keys);
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        await updater.CheckNowAsync(Ct);
        Assert.Equal(AppUpdateState.ReadyToRestart, updater.Current.State);

        _f.Transport.KeyReplacementDocument = _f.KeyReplacementDocument(
            new KeyReplacement(["release-test-1"], [new AddedKey("release-test-2", successor.ExportSubjectPublicKeyInfo(), [Packs.Signing.SignedPurposes.Releases])]), version: 1);
        await updater.CheckNowAsync(Ct);
        updater.RestartToUpdate();

        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.FeedRefused), (updater.Current.State, updater.Current.Problem));
        Assert.Empty(_f.PackagesOnDisk());
        Assert.Empty(applied);
    }

    [Fact]
    public async Task AppUpdater_PendingPackageFromAnEarlierRun_IsCheckedAgain_AndReplacedWhenChanged()
    {
        // Security review 2026-10-25, M3: a package left from an earlier run is kept only while the newest verified feed vouches for it.
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        using (var first = Updater("1.0.0", out _))
        {
            await first.CheckNowAsync(Ct);
            Assert.Equal(AppUpdateState.ReadyToRestart, first.Current.State);
        }

        var onDisk = Path.Combine(_f.PackagesDir, p.FileName);
        File.WriteAllBytes(onDisk, [.. File.ReadAllBytes(onDisk).Take(100)]);
        using var restarted = Updater("1.0.0", out _);

        await restarted.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateState.ReadyToRestart, restarted.Current.State);
        Assert.Equal(_f.Transport.Packages[p.Sha256], File.ReadAllBytes(onDisk));
        Assert.Contains(_f.Log.Lines, l => l.Contains("was deleted because the newest verified feed no longer vouches for it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_UnexpectedFailure_IsReportedAsFailed_AndLoggedByTypeOnly()
    {
        // Security review 2026-10-25, L9: an unexpected exception must not stay at "Checking" or escape the background check.
        using var updater = Updater("1.0.0", out _);
        _f.Transport.FeedFailure = new FormatException("secret detail");

        await updater.CheckNowAsync(Ct);

        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.Failed), (updater.Current.State, updater.Current.Problem));
        Assert.Contains(_f.Log.Lines, l => l.StartsWith("The update check failed: ", StringComparison.Ordinal));
        Assert.DoesNotContain(_f.Log.Lines, l => l.Contains("secret detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_UpToDate_WhenTheFeedHasNothingNewer()
    {
        using var updater = Updater("1.1.0", out _);
        _f.Transport.Feed = _f.Feed([_f.Publish("1.1.0")], version: 10);

        await updater.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateState.UpToDate, updater.Current.State);
        Assert.Equal(UpdateFixture.Start, updater.Current.LastChecked);
    }

    [Fact]
    public void AppUpdater_WithoutAnInstall_NeverChecks()
    {
        using var updater = new AppUpdater(_f.Service(), null, Locator("1.0.0"), _f.Time, _f.Log, () => { });

        updater.Start();
        _f.Time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(AppUpdateState.NotInstalled, updater.Current.State);
        Assert.Equal(0, _f.Transport.FeedRequests);
    }

    [Fact]
    public async Task AppUpdater_ChecksOnItsSchedule()
    {
        using var updater = Updater("1.0.0", out _);
        updater.Start();

        _f.Time.Advance(AppUpdater.StartDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, _f.Transport.FeedRequests);
        _f.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => _f.Transport.FeedRequests == 1);
        _f.Time.Advance(AppUpdater.Interval);
        await WaitFor(() => _f.Transport.FeedRequests == 2);
    }

    [Fact]
    public async Task RealVelopackPackage_WhenCiProvidesOne_IsVerifiedAndKept()
    {
        // On Windows CI this is the full package that build/pack-installer.ps1 just made; elsewhere a stand-in package is used.
        var real = Environment.GetEnvironmentVariable(RealPackageVariable);
        string path;
        if (real is { Length: > 0 } && File.Exists(real))
        {
            path = Path.Combine(_f.Published, Path.GetFileName(real));
            File.Copy(real, path);
        }
        else
        {
            path = TestPackages.Write(_f.Published, "0.1.0");
        }

        var package = PackageReader.Read(path, "stable");
        _f.Transport.Packages[package.Sha256] = File.ReadAllBytes(path);
        _f.Transport.Feed = _f.Feed([package], version: 10);
        var manager = Manager("0.0.1");

        var info = await manager.CheckForUpdatesAsync();
        await manager.DownloadUpdatesAsync(info!, cancelToken: Ct);

        Assert.Equal([package.FileName], _f.PackagesOnDisk());
        Assert.Equal(package.Version.Text, manager.UpdatePendingRestart!.Version.ToNormalizedString());
        if (OperatingSystem.IsWindows() && real is { Length: > 0 })
        {
            // Velopack takes the new Update.exe out of the verified package (Windows only).
            Assert.True(File.Exists(Path.Combine(_f.Root, "Update.exe")), "Update.exe was not extracted from the verified package.");
        }
    }

    [Fact]
    public void StartupEntry_PointsAtTheStableLauncher_InAnInstall()
    {
        var root = Path.Combine(_f.Root, "install");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        var program = Path.Combine(current, "DBD-Presence.exe");
        File.WriteAllBytes(program, [0]);
        File.WriteAllBytes(Path.Combine(root, InstallLayout.LauncherName), [0]);
        var installed = new TestVelopackLocator("DbdPresence", "1.0.0", _f.PackagesDir, current, root, Path.Combine(root, "Update.exe"), "stable");

        Assert.Equal(Path.Combine(root, InstallLayout.LauncherName), InstallLayout.StartupExecutable(installed, program));
        Assert.Equal(program, InstallLayout.StartupExecutable(Locator("1.0.0"), program));
        Assert.Equal(program, InstallLayout.StartupExecutable(null, program));
        Assert.Equal("1.0.0", InstallLayout.CurrentVersion(installed, "0.0.1+abc")!.Text);
        Assert.Equal("0.2.0", InstallLayout.CurrentVersion(null, "0.2.0+abcdef0")!.Text);
        Assert.Null(InstallLayout.CurrentVersion(null, "unknown"));
    }

    // ---- Re-verification before apply (security review 2026-10-25 M3) and outage handling (final review LOW) ----

    private async Task<(AppUpdater Updater, ReleasePackage Package, List<VelopackAsset> Applied, List<int> Shutdowns)> ReadyAsync(KeyReplacementService? keys = null, IReleaseFloor? floor = null)
    {
        var applied = new List<VelopackAsset>();
        var updater = Updater("1.0.0", out var shutdowns, applied, keys, floor);
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        await updater.CheckNowAsync(Ct);
        Assert.Equal(AppUpdateState.ReadyToRestart, updater.Current.State);
        return (updater, p, applied, shutdowns);
    }

    [Fact]
    public async Task AppUpdater_Restart_AfterTheFeedExpired_InstallsNothing_AndKeepsTheFile()
    {
        var (updater, p, applied, shutdowns) = await ReadyAsync();
        using var _ = updater;
        _f.Time.Advance(TimeSpan.FromDays(31));

        updater.RestartToUpdate();

        Assert.Empty(applied);
        Assert.Empty(shutdowns);
        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.Failed), (updater.Current.State, updater.Current.Problem));
        Assert.Equal([p.FileName], _f.PackagesOnDisk());
        Assert.Contains(_f.Log.Lines, l => l.Contains("the feed that offered it has expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_Restart_AfterTheKeyWasRevoked_WithNoNewCheck_DeletesTheFile_AndInstallsNothing()
    {
        using var successor = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var keys = Keys();
        var (updater, _, applied, shutdowns) = await ReadyAsync(keys);
        using var _ = updater;
        _f.Transport.KeyReplacementDocument = RevokeReleaseKey(successor);
        Assert.True(await keys.RefreshAsync(Ct));

        updater.RestartToUpdate();

        Assert.Empty(applied);
        Assert.Empty(shutdowns);
        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.PackageRefused), (updater.Current.State, updater.Current.Problem));
        Assert.Empty(_f.PackagesOnDisk());
        Assert.Contains(_f.Log.Lines, l => l.Contains("the key that signed its feed is no longer trusted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_Restart_WithADamagedKeyReplacement_InstallsNothing_AndKeepsTheFile()
    {
        var files = new MemoryFiles();
        var keys = Keys(files);
        var (updater, p, applied, shutdowns) = await ReadyAsync(keys);
        using var _ = updater;
        files.Files["online/key-replacement.json"] = [1, 2, 3];
        keys.LoadStored();
        Assert.True(keys.IsDamaged);

        updater.RestartToUpdate();

        Assert.Empty(applied);
        Assert.Empty(shutdowns);
        Assert.Equal(AppUpdateProblem.Failed, updater.Current.Problem);
        Assert.Equal([p.FileName], _f.PackagesOnDisk());
        Assert.Contains(_f.Log.Lines, l => l.Contains("the kept key replacement or version floor is damaged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_Restart_WithADamagedFloor_InstallsNothing_AndKeepsTheFile()
    {
        var floor = new SwitchableFloor(new FileReleaseFloor(_f.Files, UpdateFixture.FloorPath, _f.Log));
        var (updater, p, applied, shutdowns) = await ReadyAsync(floor: floor);
        using var _ = updater;
        floor.Damaged = true;

        updater.RestartToUpdate();

        Assert.Empty(applied);
        Assert.Empty(shutdowns);
        Assert.Equal(AppUpdateProblem.Failed, updater.Current.Problem);
        Assert.Equal([p.FileName], _f.PackagesOnDisk());
    }

    [Fact]
    public async Task AppUpdater_Restart_AfterANewerFeedWasVerified_DeletesTheFile_AndInstallsNothing()
    {
        var floor = new FileReleaseFloor(_f.Files, UpdateFixture.FloorPath, _f.Log);
        var (updater, _, applied, shutdowns) = await ReadyAsync(floor: floor);
        using var _ = updater;
        Assert.True(floor.Raise("stable", 11, null));

        updater.RestartToUpdate();

        Assert.Empty(applied);
        Assert.Empty(shutdowns);
        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.PackageRefused), (updater.Current.State, updater.Current.Problem));
        Assert.Empty(_f.PackagesOnDisk());
        Assert.Contains(_f.Log.Lines, l => l.Contains("a newer feed was verified since", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_ChecksAgainAfterTheDownload_AndDeletesAPackageWhoseKeyWasRevokedMeanwhile()
    {
        using var successor = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var keys = Keys();
        var applied = new List<VelopackAsset>();
        using var updater = Updater("1.0.0", out _, applied, keys);
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        _f.Transport.AfterDownload = async () =>
        {
            _f.Transport.KeyReplacementDocument = RevokeReleaseKey(successor);
            Assert.True(await keys.RefreshAsync(Ct));
        };

        await updater.CheckNowAsync(Ct);

        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.PackageRefused), (updater.Current.State, updater.Current.Problem));
        Assert.Empty(_f.PackagesOnDisk());
        Assert.Empty(applied);
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("online-features-off")]
    [InlineData("nothing-published")]
    public async Task AppUpdater_AShortOutage_KeepsTheVerifiedPackage_AndRestartStillUsesIt(string outage)
    {
        var (updater, p, applied, shutdowns) = await ReadyAsync();
        using var _ = updater;
        switch (outage)
        {
            case "unreachable":
                _f.Transport.Unreachable = true;
                break;
            case "online-features-off":
                _f.Gate.IsOpen = false;
                break;
            default:
                _f.Transport.Feed = null;
                break;
        }

        await updater.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateState.ReadyToRestart, updater.Current.State);
        Assert.Equal([p.FileName], _f.PackagesOnDisk());
        Assert.Single(_f.Transport.Downloads);

        updater.RestartToUpdate();

        Assert.Equal([p.FileName], applied.Select(a => a.FileName));
        Assert.Equal([1], shutdowns);
    }

    [Fact]
    public async Task AppUpdater_AnOutageAtStart_KeepsAPackageFromAnEarlierRun_AndOffersItWithoutDownloadingAgain()
    {
        var (first, p, _, _) = await ReadyAsync();
        first.Dispose();
        _f.Transport.Unreachable = true;
        using var second = Updater("1.0.0", out _);

        await second.CheckNowAsync(Ct);

        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.Unreachable), (second.Current.State, second.Current.Problem));
        Assert.Equal([p.FileName], _f.PackagesOnDisk());

        _f.Transport.Unreachable = false;
        await second.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateState.ReadyToRestart, second.Current.State);
        Assert.Single(_f.Transport.Downloads);
    }

    [Fact]
    public async Task AppUpdater_ARefusedFeed_DeletesTheVerifiedPackage()
    {
        var (updater, p, _, _) = await ReadyAsync();
        using var _ = updater;
        _f.Transport.Feed = UpdateFixture.Tamper(_f.Feed([p], version: 11));

        await updater.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateProblem.FeedRefused, updater.Current.Problem);
        Assert.Empty(_f.PackagesOnDisk());
    }

    [Fact]
    public async Task AppUpdater_ADifferentOfferedPackage_ReplacesTheVerifiedOne()
    {
        var (updater, p11, _, _) = await ReadyAsync();
        using var _ = updater;
        var p12 = _f.Publish("1.2.0");
        _f.Transport.Feed = _f.Feed([p12, p11], version: 11);

        await updater.CheckNowAsync(Ct);

        Assert.Equal(AppUpdateState.ReadyToRestart, updater.Current.State);
        Assert.Equal("1.2.0", updater.Current.Version);
        Assert.Equal([p12.FileName], _f.PackagesOnDisk());
    }

    [Fact]
    public async Task AppUpdater_AFeedThatOffersNothingNewer_DeletesAPackageItNoLongerOffers()
    {
        var (updater, _, _, _) = await ReadyAsync();
        using var _ = updater;
        _f.Transport.Feed = _f.Feed([], version: 11);

        await updater.CheckNowAsync(Ct);

        Assert.Empty(_f.PackagesOnDisk());
        Assert.Equal(AppUpdateState.UpToDate, updater.Current.State);
    }

    [Fact]
    public async Task AppUpdater_RestartToUpdate_WhenTheInstallerHandOverFails_LogsTheTypeOnly_AndKeepsRunning()
    {
        using var updater = Updater("1.0.0", out var shutdowns, apply: _ => throw new InvalidOperationException("secret detail"));
        var p = _f.Publish("1.1.0");
        _f.Transport.Feed = _f.Feed([p], version: 10);
        await updater.CheckNowAsync(Ct);

        updater.RestartToUpdate();

        Assert.Empty(shutdowns);
        Assert.Equal((AppUpdateState.Problem, AppUpdateProblem.Failed), (updater.Current.State, updater.Current.Problem));
        Assert.Contains(_f.Log.Lines, l => l == "Handing the update to the installer failed: InvalidOperationException");
        Assert.DoesNotContain(_f.Log.Lines, l => l.Contains("secret detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppUpdater_DisposedWhileACheckIsRunning_DoesNotThrowWhenTheCheckEnds()
    {
        var updater = Updater("1.0.0", out _);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Transport.BeforeFeed = () =>
        {
            entered.SetResult();
            return release.Task;
        };
        var running = updater.CheckNowAsync(Ct);
        await entered.Task;

        updater.Dispose();
        release.SetResult();
        await running;

        Assert.Contains(_f.Log.Lines, l => l.Contains("finished after the updater was closed", StringComparison.Ordinal));
    }

    /// <summary>A floor that a test can declare damaged after the fact.</summary>
    private sealed class SwitchableFloor(IReleaseFloor inner) : IReleaseFloor
    {
        public bool Damaged { get; set; }

        public bool IsDamaged => Damaged || inner.IsDamaged;

        public long? Highest(string channel) => inner.Highest(channel);

        public bool Raise(string channel, long version, string? kid) => inner.Raise(channel, version, kid);

        public void Forget(IReadOnlyCollection<string> revokedKids) => inner.Forget(revokedKids);
    }

    private TestVelopackLocator Locator(string version) =>
        new("DbdPresence", version, _f.PackagesDir, null, null, Path.Combine(_f.Root, "Update.exe"), "stable");

    private UpdateManager Manager(string version) =>
        new(new SignedUpdateSource(_f.Service(), ReleaseVersion.Parse(version)), new UpdateOptions { AllowVersionDowngrade = false, ExplicitChannel = "stable" }, Locator(version));

    private AppUpdater Updater(string version, out List<int> shutdowns, List<VelopackAsset>? applied = null, KeyReplacementService? keys = null, IReleaseFloor? floor = null, Action<VelopackAsset>? apply = null)
    {
        var calls = new List<int>();
        shutdowns = calls;
        return new AppUpdater(_f.Service(keys, floor: floor), ReleaseVersion.Parse(version), Locator(version), _f.Time, _f.Log, () => calls.Add(1), apply ?? (a => (applied ?? []).Add(a)));
    }

    private KeyReplacementService Keys(MemoryFiles? files = null) =>
        new(_f.Transport, _f.Verifier, _f.Keys, files ?? new MemoryFiles(), "online/key-replacement.json", _f.Log);

    /// <summary>A key replacement that revokes the release key the test feeds are signed with.</summary>
    private string RevokeReleaseKey(System.Security.Cryptography.ECDsa successor) =>
        _f.KeyReplacementDocument(new KeyReplacement(["release-test-1"], [new AddedKey("release-test-2", successor.ExportSubjectPublicKeyInfo(), [Packs.Signing.SignedPurposes.Releases])]), version: 1);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }

    /// <summary>A source that offers a fixed asset list, to test Velopack's own downgrade rule.</summary>
    private sealed class FixedSource(params VelopackAsset[] assets) : Velopack.Sources.IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(Velopack.Logging.IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            Task.FromResult(new VelopackAssetFeed { Assets = assets });

        public Task DownloadReleaseEntry(Velopack.Logging.IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
            throw new InvalidOperationException("The downgrade test never downloads.");
    }
}
