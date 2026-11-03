// Covers: QA M1 and the minor findings (the state file never holds a secret even when a caller forgot to clean a text; it keeps the last 10 jobs and at most 20 distinct leftovers; a damaged file only
// forgets the history), and the exact edges of every limit in the program (each "less than" and "at least" is tested on both sides of its number, so a one-character slip cannot go unseen):
// free disk, Windows build, the token warning, how soon a check is due, how old the base image may be. docs/owner/free-ci.md.
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class StateAndBoundaryTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();

    public void Dispose() => _folder.Dispose();

    // ---- the state file

    [Fact]
    public void EveryTextInTheStateFile_PassesTheRedactorFirst()
    {
        var redactor = new Redactor();
        redactor.Register("TOP-SECRET-VALUE-123");
        var state = new StateStore(_folder.Paths.StateFile, redactor);

        state.Update(s =>
        {
            s.Jobs.Add(new JobRecord { At = _time.GetUtcNow(), Os = "Linux TOP-SECRET-VALUE-123", Result = "failed: TOP-SECRET-VALUE-123" });
            s.ResidueItems = [Left.Words("left TOP-SECRET-VALUE-123")];
            s.SelfTest = new SelfTestRecord { At = _time.GetUtcNow(), Outcome = "Failed TOP-SECRET-VALUE-123" };
            s.BaseImageRecipe = "recipe TOP-SECRET-VALUE-123";
        });

        var written = File.ReadAllText(_folder.Paths.StateFile);
        Assert.DoesNotContain("TOP-SECRET-VALUE-123", written, StringComparison.Ordinal);
        Assert.Contains("[hidden]", written, StringComparison.Ordinal);
        var loaded = state.Load();
        Assert.Equal("Linux [hidden]", loaded.Jobs[0].Os);
        Assert.Equal("failed: [hidden]", loaded.Jobs[0].Result);
        Assert.Equal(["left [hidden]"], loaded.Residue);
        Assert.Equal("Failed [hidden]", loaded.SelfTest!.Outcome);
        Assert.Equal("recipe [hidden]", loaded.BaseImageRecipe);
    }

    [Fact]
    public void ASecretThatWasRegisteredAfterTheTextWasWritten_IsRemovedAtTheNextWrite()
    {
        var redactor = new Redactor();
        var state = new StateStore(_folder.Paths.StateFile, redactor);
        state.Update(s => s.ResidueItems = [Left.Words("a folder holding LATER-SECRET-0123456789")]);
        Assert.Contains("LATER-SECRET-0123456789", File.ReadAllText(_folder.Paths.StateFile), StringComparison.Ordinal);

        redactor.Register("LATER-SECRET-0123456789");
        state.Update(_ => { });

        Assert.DoesNotContain("LATER-SECRET-0123456789", File.ReadAllText(_folder.Paths.StateFile), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStateKeepsTheTenNewestJobs_AndAtMostTwentyDistinctLeftovers()
    {
        var state = new StateStore(_folder.Paths.StateFile);

        state.Update(s =>
        {
            s.Jobs = [.. Enumerable.Range(0, 15).Select(i => new JobRecord { At = _time.GetUtcNow().AddMinutes(i), Os = "Linux", Result = $"r{i}" })];
            s.ResidueItems = [.. Enumerable.Range(0, 30).Select(i => Left.Words($"leftover {i % 25}"))];
        });

        var loaded = state.Load();
        Assert.Equal(10, loaded.Jobs.Count);
        Assert.Equal("r14", loaded.Jobs[0].Result);
        Assert.Equal("r5", loaded.Jobs[^1].Result);
        Assert.Equal(20, loaded.Residue.Count);
        Assert.Equal(20, loaded.Residue.Distinct().Count());
        Assert.Equal("leftover 0", loaded.Residue[0]);
    }

    [Fact]
    public void ADamagedStateFile_ForgetsTheHistory_AndTheNextWriteReplacesIt()
    {
        File.WriteAllText(_folder.Paths.StateFile, "{ this is not json");
        var state = new StateStore(_folder.Paths.StateFile);

        Assert.Empty(state.Load().Jobs);
        state.Update(s => s.LastRunnerCheck = _time.GetUtcNow());

        Assert.NotNull(state.Load().LastRunnerCheck);
        _ = JsonDocument.Parse(File.ReadAllText(_folder.Paths.StateFile));
        Assert.False(File.Exists(_folder.Paths.StateFile + ".tmp"));
    }

    [Fact]
    public void AMissingStateFile_IsAnEmptyState_AndTheDataFolderIsNeverCreatedByAWrite()
    {
        var nowhere = Path.Combine(_folder.Path, "nowhere", "state.json");
        var state = new StateStore(nowhere);

        Assert.Empty(state.Load().Jobs);
        Assert.Throws<DirectoryNotFoundException>(() => state.Update(_ => { }));
        Assert.False(Directory.Exists(Path.GetDirectoryName(nowhere)));
    }

    // ---- the exact edges

    private static LinuxJobHost HostWithFreeSpace(TempFolder folder, long free)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var paths = folder.Paths;
        var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        Directory.CreateDirectory(paths.BaseDirectory);
        File.WriteAllText(paths.BaseArchive, "base");
        integrity.Record(paths.BaseArchive, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("base"u8.ToArray())));
        var sandbox = Sandboxes.New(new FakeWsl(), new FakeWslAccount(), new FakeWslSettingsFile(), log, redactor, TestTime.New());
        var state = AcceptedProbe.In(new StateStore(paths.StateFile));
        return new LinuxJobHost(new FakeWsl(), sandbox, paths, log, redactor, integrity, new FakeProtectedStorage(), new FakeDisk { Free = free }, new ResidueRecorder(state), TestTime.New(), AcceptedProbe.Gate(state));
    }

    [Theory]
    [InlineData(8 * Gigabyte, true)]
    [InlineData(8 * Gigabyte - 1, false)]
    [InlineData(8 * Gigabyte + 1, true)]
    [InlineData(5 * Gigabyte, false)]
    public void ALinuxJobNeedsEightGigabytesFree_Exactly(long free, bool ready)
    {
        Assert.Equal(ready, HostWithFreeSpace(_folder, free).NotReadyReason() is null);
    }

    [Theory]
    [InlineData(10 * Gigabyte, true)]
    [InlineData(10 * Gigabyte - 1, false)]
    public async Task TheBaseImageBuildNeedsTenGigabytesFree_Exactly(long free, bool builds)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var image = System.Text.Encoding.UTF8.GetBytes("ubuntu");
        var pinned = new UbuntuImage("ubuntu-test.wsl", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)), new Uri("https://releases.ubuntu.com/noble/ubuntu-test.wsl"), image.Length);
        var downloads = new FakeDownloads();
        downloads.Files[pinned.Address.ToString()] = image;
        var paths = _folder.Paths;
        var wsl = new FakeWsl();
        var sandbox = Sandboxes.New(wsl, new FakeWslAccount(), new FakeWslSettingsFile(), log, redactor, _time);
        var builder = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, new Retry(_time, redactor, log, attempts: 1), new IntegrityStore(paths.IntegrityFile, paths.Machine), new FakeDisk { Free = free }, new ResidueRecorder(new StateStore(paths.StateFile)), _time, pinned);

        var ex = await Record.ExceptionAsync(() => builder.BuildAsync(CancellationToken.None));

        Assert.Equal(builds, ex is null);
        if (!builds)
        {
            Assert.IsType<IOException>(ex);
            Assert.Empty(downloads.Requested);
        }
    }

    [Theory]
    [InlineData(25L, true)]
    [InlineData(24L, false)]
    public async Task AFirstSetupNeedsTwentyFiveGigabytesFree_Exactly(long gigabytes, bool goesOn)
    {
        var redactor = new Redactor();
        var console = new FakeConsole();
        var wizard = SetupTests.NewWizard(_folder, console, new FakeMachine { FreeBytes = gigabytes * Gigabyte }, new FakeWsl(), new FakeGitHubApi(), new FakeSecretStore(), new FakeScheduler(), redactor, new MemoryCiLog(redactor), out _);

        await wizard.RunAsync(CancellationToken.None);

        Assert.Equal(goesOn, !console.AllText.Contains("are needed for a first set-up", StringComparison.Ordinal));
        Assert.Equal(goesOn, console.AllText.Contains("Step 2 of", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(19041, true)]
    [InlineData(19040, false)]
    [InlineData(22631, true)]
    public async Task SetupNeedsWindowsBuild19041_Exactly(int build, bool goesOn)
    {
        var redactor = new Redactor();
        var console = new FakeConsole();
        var wizard = SetupTests.NewWizard(_folder, console, new FakeMachine { WindowsBuild = build }, new FakeWsl(), new FakeGitHubApi(), new FakeSecretStore(), new FakeScheduler(), redactor, new MemoryCiLog(redactor), out _);

        await wizard.RunAsync(CancellationToken.None);

        Assert.Equal(!goesOn, console.AllText.Contains("too old", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(7.0, "WARNING")]
    [InlineData(7.99, "WARNING")]
    [InlineData(8.0, "saved, expires on")]
    [InlineData(6.0, "WARNING")]
    [InlineData(0.0, "WARNING")]
    [InlineData(0.5, "WARNING")]
    [InlineData(-0.001, "EXPIRED")]
    [InlineData(-1.0, "EXPIRED")]
    public void TheTokenWarningStartsAtSevenWholeDays_AndExpiryAtTheMoment(double daysLeft, string expected)
    {
        var facts = new StatusFacts(_time.GetUtcNow(), true, true, true, [], null, [], _time.GetUtcNow().AddDays(daysLeft), 0, null, BaseImageBuilt: true);

        Assert.Contains(expected, StatusReport.Build(facts), StringComparison.Ordinal);
    }

    private (Maintenance Maintenance, FakeGitHubApi Api, StateStore State) NewMaintenance()
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var api = new FakeGitHubApi { Release = new RunnerRelease("2.338.0", string.Empty, new Uri("https://github.com/a")) };
        var downloads = new FakeDownloads();
        var wsl = new FakeWsl();
        var paths = _folder.Paths;
        var state = new StateStore(paths.StateFile);
        var retry = new Retry(_time, redactor, log, attempts: 1);
        var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        var disk = new FakeDisk();
        var sandbox = Sandboxes.New(wsl, new FakeWslAccount(), new FakeWslSettingsFile(), log, redactor, _time);
        var image = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, retry, integrity, disk, new ResidueRecorder(state), _time);
        var runners = new RunnerCache(api, downloads, paths, log, retry, integrity, new FakeProtectedStorage(), disk);
        return (new Maintenance(runners, image, api, state, new JobGate(new FakeProcesses()), new FakeNotifier(), _time, log, redactor), api, state);
    }

    [Theory]
    [InlineData(6 * 3600, true)]
    [InlineData((6 * 3600) - 1, false)]
    [InlineData((6 * 3600) + 1, true)]
    public async Task TheTokenIsCheckedEverySixHours_Exactly(int secondsAgo, bool due)
    {
        var (maintenance, api, state) = NewMaintenance();
        var now = _time.GetUtcNow();
        state.Update(s =>
        {
            s.LastTokenCheck = now.AddSeconds(-secondsAgo);
            s.LastRunnerCheck = now;
            s.LastBaseImageBuild = now;
        });

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(due, api.Calls.Contains("repo"));
    }

    [Theory]
    [InlineData(30 * 60, true)]
    [InlineData((30 * 60) - 1, false)]
    public async Task AFailedDownloadIsTriedAgainAfterThirtyMinutes_Exactly(int secondsAgo, bool due)
    {
        var (maintenance, api, state) = NewMaintenance();
        var now = _time.GetUtcNow();
        state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerAttempt = now.AddSeconds(-secondsAgo);
        });

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(due, api.Calls.Contains("release"));
    }

    [Fact]
    public async Task TheBaseImageIsCurrentForSevenDays_ToTheTick()
    {
        var image = System.Text.Encoding.UTF8.GetBytes("ubuntu");
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var pinned = new UbuntuImage("ubuntu-test.wsl", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image)), new Uri("https://releases.ubuntu.com/noble/ubuntu-test.wsl"), image.Length);
        var downloads = new FakeDownloads();
        downloads.Files[pinned.Address.ToString()] = image;
        var paths = _folder.Paths;
        var wsl = new FakeWsl();
        var sandbox = Sandboxes.New(wsl, new FakeWslAccount(), new FakeWslSettingsFile(), log, redactor, _time);
        var builder = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, new Retry(_time, redactor, log, attempts: 1), new IntegrityStore(paths.IntegrityFile, paths.Machine), new FakeDisk(), new ResidueRecorder(new StateStore(paths.StateFile)), _time, pinned);
        await builder.BuildAsync(CancellationToken.None);
        var built = _time.GetUtcNow();
        var state = new CiState { BaseImageRecipe = builder.RecipeId, LastBaseImageBuild = built };

        Assert.True(builder.IsCurrent(state, built.AddDays(7).AddTicks(-1)));
        Assert.False(builder.IsCurrent(state, built.AddDays(7)));
        Assert.True(builder.IsCurrent(state, built));
    }
}
