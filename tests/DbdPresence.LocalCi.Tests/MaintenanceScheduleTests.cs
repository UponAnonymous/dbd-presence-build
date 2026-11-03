// Covers: FR-CI-04 and QA M1 (the schedule really runs: the token is checked every 6 hours, the runner program is refreshed daily, the base image is rebuilt weekly and only while no Linux machine is in
// use; each is due exactly at its interval and not a second before; heavy work waits while the game runs). docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class MaintenanceScheduleTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly Redactor _redactor = new();
    private readonly MemoryCiLog _log;
    private readonly FakeGitHubApi _api = new() { Release = new RunnerRelease("2.338.0", string.Empty, new Uri("https://github.com/a")) };
    private readonly FakeDownloads _downloads = new();
    private readonly FakeWsl _wsl = new();
    private readonly FakeProcesses _processes = new();
    private readonly IntegrityStore _integrity;
    private readonly StateStore _state;
    private readonly Maintenance _maintenance;

    public MaintenanceScheduleTests()
    {
        _log = new MemoryCiLog(_redactor);
        var paths = _folder.Paths;
        _state = new StateStore(paths.StateFile);
        _integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        var retry = new Retry(_time, _redactor, _log, attempts: 1);
        var disk = new FakeDisk();
        var sandbox = Sandboxes.New(_wsl, new FakeWslAccount(), new FakeWslSettingsFile(), _log, _redactor, _time);
        var image = new BaseImageBuilder(_downloads, _wsl, sandbox, paths, _log, retry, _integrity, disk, new ResidueRecorder(_state), _time);
        var runners = new RunnerCache(_api, _downloads, paths, _log, retry, _integrity, new FakeProtectedStorage(), disk);
        _maintenance = new Maintenance(runners, image, _api, _state, new JobGate(_processes), new FakeNotifier(), _time, _log, _redactor);
    }

    public void Dispose() => _folder.Dispose();

    private void HaveRunnerPackage()
    {
        var path = _folder.Paths.LinuxPackage("2.338.0");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "package");
        _integrity.Record(path, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("package"))));
    }

    private void HaveBaseImage()
    {
        Directory.CreateDirectory(_folder.Paths.BaseDirectory);
        File.WriteAllText(_folder.Paths.BaseArchive, "image");
        _integrity.Record(_folder.Paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("image"))));
    }

    [Theory]
    [InlineData(24 * 3600, true)]
    [InlineData((24 * 3600) - 1, false)]
    [InlineData((24 * 3600) + 1, true)]
    public async Task TheRunnerProgramIsRefreshedEveryDay_Exactly(int secondsAgo, bool due)
    {
        HaveRunnerPackage();
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now.AddSeconds(-secondsAgo);
            s.LastRunnerAttempt = now.AddDays(-2);
        });

        await _maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(due, _api.Calls.Contains("release"));
    }

    [Fact]
    public async Task AMissingRunnerProgram_IsFetchedAtOnce_NotOnlyOnceADay()
    {
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now;
            s.LastRunnerAttempt = now.AddHours(-1);
        });

        await _maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Contains("release", _api.Calls);
    }

    [Theory]
    [InlineData(7 * 24 * 3600, true)]
    [InlineData((7 * 24 * 3600) - 1, false)]
    [InlineData((7 * 24 * 3600) + 1, true)]
    public async Task TheBaseImageIsRebuiltEveryWeek_Exactly_OnlyWhileLinuxIsIdle(int secondsAgo, bool due)
    {
        HaveRunnerPackage();
        HaveBaseImage();
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now;
            s.LastBaseImageBuild = now.AddSeconds(-secondsAgo);
            s.LastBaseImageAttempt = now.AddDays(-2);
        });

        await _maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        var attemptedWhenIdle = _downloads.Requested.Count > 0;
        _downloads.Requested.Clear();
        _state.Update(s => s.LastBaseImageAttempt = now.AddDays(-2));
        await _maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        var attemptedWhenBusy = _downloads.Requested.Count > 0;

        Assert.Equal(due, attemptedWhenIdle);
        Assert.False(attemptedWhenBusy, "the base image was rebuilt while a Linux machine was in use");
    }

    [Fact]
    public async Task ABaseImageThatIsMissing_IsBuiltAtOnce_WhenLinuxIsIdle()
    {
        HaveRunnerPackage();
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now;
            s.LastBaseImageBuild = now;
        });

        await _maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.NotEmpty(_downloads.Requested);
    }

    [Fact]
    public async Task WhileTheGameRuns_NeitherTheDownloadNorTheImageBuildStarts_AndTheOwnerCanSeeWhyInTheLogOnce()
    {
        _processes.Running.Add("DeadByDaylight-Win64-Shipping");
        var now = _time.GetUtcNow();
        _state.Update(s => s.LastTokenCheck = now);

        await _maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        await _maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Empty(_downloads.Requested);
        Assert.DoesNotContain("release", _api.Calls);
        Assert.Single(_log.Lines, l => l.Contains("waiting with the downloads", StringComparison.Ordinal));

        _processes.Running.Clear();
        await _maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Contains("release", _api.Calls);
    }

    [Fact]
    public async Task AFailedRunnerRefresh_IsLogged_NotThrown_AndLeavesTheDailyCheckUndone()
    {
        HaveRunnerPackage();
        _api.Release = null;
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now.AddDays(-2);
            s.LastRunnerAttempt = now.AddDays(-2);
        });

        var ex = await Record.ExceptionAsync(() => _maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None));

        Assert.Null(ex);
        var state = _state.Load();
        Assert.Equal(now.AddDays(-2), state.LastRunnerCheck);
        Assert.Equal(now, state.LastRunnerAttempt);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("win32")]
    [InlineData("argument")]
    public async Task AnImageBuildThatFailsInAnyWay_IsLogged_NotThrown_SoARunnerStillStartsInTheSameRound(string kind)
    {
        HaveRunnerPackage();
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now;
        });
        _wsl.ShutdownFailure = kind switch
        {
            "timeout" => new TimeoutException("WSL did not finish shutting down within 3 minutes"),
            "win32" => new System.ComponentModel.Win32Exception(1058, "Secondary Logon is off"),
            _ => new ArgumentException("bad name"),
        };

        var ex = await Record.ExceptionAsync(() => _maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None));

        Assert.Null(ex);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("Could not build the Linux base image", StringComparison.Ordinal));
        Assert.NotNull(_state.Load().LastBaseImageAttempt);
    }

    [Fact]
    public async Task TheLoopsOwnStop_IsNotSwallowedByTheMaintenance()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _api.RepoFailure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _maintenance.RunDueAsync(linuxIdle: true, cts.Token));
    }

    [Fact]
    public async Task ATimeRecordedInTheFuture_CountsAsDue_SoASetBackClockCannotStallTheChecksForDays()
    {
        HaveRunnerPackage();
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastTokenCheck = now.AddDays(3);
            s.LastRunnerCheck = now.AddDays(3);
            s.LastRunnerAttempt = now.AddDays(3);
        });

        await _maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Contains("repo", _api.Calls);
        Assert.Contains("release", _api.Calls);
    }
}
