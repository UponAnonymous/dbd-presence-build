// Covers: FR-CI-04 and local CI round 4 QA M-4 (R8 and R9): the token is checked every 6 hours, exactly, and every check is written down (a good one, a refused one, a timed-out one) so the next round does
// not repeat it; the runner program is refreshed daily and a refresh is written down as done only when it worked, while an attempt of any kind is written down so a failure is retried after 30 minutes and
// not before; the status asks the data folder, the account and the leftovers only when the data folder exists. docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class MaintenanceCadenceTests : IDisposable
{
    private readonly MaintenanceRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private int Count(string call) => _rig.Api.Calls.Count(c => c == call);

    private void HaveRunnerPackage()
    {
        var path = _rig.Paths.LinuxPackage("2.338.0");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "package");
        _rig.Integrity.Record(path, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("package"))));
    }

    private void OnlyTheTokenIsDue(TimeSpan since)
    {
        HaveRunnerPackage();
        var now = _rig.Time.GetUtcNow();
        _rig.State.Update(s =>
        {
            s.LastTokenCheck = now - since;
            s.LastRunnerCheck = now;
            s.LastRunnerAttempt = now;
            s.LastBaseImageBuild = now;
            s.LastBaseImageAttempt = now;
        });
    }

    // ---- the token check

    [Theory]
    [InlineData(6 * 3600, true)]
    [InlineData((6 * 3600) - 1, false)]
    [InlineData((6 * 3600) + 1, true)]
    [InlineData(0, false)]
    public async Task TheTokenIsCheckedEverySixHours_Exactly(int secondsAgo, bool due)
    {
        OnlyTheTokenIsDue(TimeSpan.FromSeconds(secondsAgo));

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(due ? 1 : 0, Count("repo"));
    }

    [Fact]
    public async Task ATokenCheckThatWorked_IsWrittenDown_SoTheNextRoundDoesNotAskGitHubAgain()
    {
        OnlyTheTokenIsDue(TimeSpan.FromHours(7));

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        _rig.Time.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59));
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(1, Count("repo"));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), _rig.State.Load().LastTokenCheck);

        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Equal(2, Count("repo"));
    }

    [Fact]
    public async Task ATokenCheckThatGitHubRefuses_IsWrittenDownToo_SoItIsNotRepeatedEveryRound()
    {
        OnlyTheTokenIsDue(TimeSpan.FromHours(7));
        _rig.Api.RepoFailure = new GitHubApiException("Bad credentials", 401, isTransient: false);

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(1, Count("repo"));
        Assert.Equal(_rig.Time.GetUtcNow(), _rig.State.Load().LastTokenCheck);
    }

    [Fact]
    public async Task ATokenCheckThatTimesOut_IsWrittenDownToo()
    {
        OnlyTheTokenIsDue(TimeSpan.FromHours(7));
        _rig.Api.RepoHang.On = true;
        var startedAt = _rig.Time.GetUtcNow();
        var running = _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await _rig.Api.RepoHang.StartedAsync();

        _rig.Time.Advance(TimeSpan.FromMinutes(2));
        await running.OrFail();

        Assert.Equal(startedAt, _rig.State.Load().LastTokenCheck);
        // The call that hung never got an answer (the fake notes a call when it answers); the next round, two minutes later, does not ask again.
        _rig.Api.RepoHang.On = false;
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Equal(0, Count("repo"));
    }

    // ---- the runner program

    private void OnlyTheRunnerProgramIsDue(TimeSpan checkedAgo, TimeSpan attemptedAgo)
    {
        HaveRunnerPackage();
        var now = _rig.Time.GetUtcNow();
        _rig.State.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now - checkedAgo;
            s.LastRunnerAttempt = now - attemptedAgo;
            s.LastBaseImageBuild = now;
            s.LastBaseImageAttempt = now;
        });
    }

    [Fact]
    public async Task ARefreshThatWorked_IsWrittenDownAsDoneAndAsAnAttempt_SoItIsNotDoneAgainForADay()
    {
        OnlyTheRunnerProgramIsDue(TimeSpan.FromHours(25), TimeSpan.FromHours(25));
        var package = Encoding.UTF8.GetBytes("linux");
        var address = new Uri("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz");
        _rig.Downloads.Files[address.ToString()] = package;
        _rig.Api.Release = new RunnerRelease("2.338.0", $"<!-- BEGIN SHA linux-x64 -->{Convert.ToHexStringLower(SHA256.HashData(package))}<!-- END SHA linux-x64 -->", address);

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        var state = _rig.State.Load();
        Assert.Equal(1, Count("release"));
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastRunnerCheck);
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastRunnerAttempt);

        _rig.Time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Equal(1, Count("release"));

        _rig.Time.Advance(TimeSpan.FromMinutes(1));
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Equal(2, Count("release"));
    }

    [Theory]
    [InlineData((30 * 60) - 1, false)]
    [InlineData(30 * 60, true)]
    [InlineData((30 * 60) + 1, true)]
    public async Task AFailedRefresh_IsTriedAgainAfterThirtyMinutes_Exactly(int secondsSinceTheAttempt, bool due)
    {
        OnlyTheRunnerProgramIsDue(TimeSpan.FromDays(2), TimeSpan.FromSeconds(secondsSinceTheAttempt));

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(due ? 1 : 0, Count("release"));
    }

    [Fact]
    public async Task AFailedRefresh_WritesDownTheAttempt_ButNotTheDailyCheck()
    {
        OnlyTheRunnerProgramIsDue(TimeSpan.FromDays(2), TimeSpan.FromDays(2));
        _rig.Api.Release = null;
        var before = _rig.State.Load().LastRunnerCheck;

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        var state = _rig.State.Load();
        Assert.Equal(before, state.LastRunnerCheck);
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastRunnerAttempt);
        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Equal(1, Count("release"));
    }

    // ---- the status asks the folder only when it is there (R9)

    [Fact]
    public async Task TheStatus_AsksTheDataFolderTheAccountAndTheLeftovers_OnlyWhenTheDataFolderExists()
    {
        using var folder = new TempFolder();
        var storage = new FakeProtectedStorage();
        storage.DataFolderProblems.Add(@"C:\ProgramData\DbdLocalCi gives access to all users.");
        var account = new FakeWslAccount();
        account.Problems.Add("the account dbd-ci-wsl is switched off");
        var state = new StateStore(folder.Paths.StateFile);
        state.Update(s => s.ResidueItems = [Left.Words("a folder is left")]);
        var secrets = new FakeSecretStore();
        secrets.Save(new SecretText("x"));
        var time = TestTime.New();

        var there = await new StatusCollector(secrets, new FakeScheduler(), new FakeGitHubApi(), state, folder.Paths, time, new Redactor(), null, storage, account).CollectAsync(CancellationToken.None);

        Assert.Equal([@"C:\ProgramData\DbdLocalCi gives access to all users."], there.DataFolderProblems);
        Assert.Equal(["the account dbd-ci-wsl is switched off"], there.AccountProblems);
        Assert.Equal(["a folder is left"], there.Residue);

        var gone = new LocalCiPaths(Path.Combine(folder.Path, "nowhere"));
        var missing = await new StatusCollector(secrets, new FakeScheduler(), new FakeGitHubApi(), new StateStore(folder.Paths.StateFile), gone, time, new Redactor(), null, storage, account).CollectAsync(CancellationToken.None);

        Assert.Null(missing.DataFolderProblems);
        Assert.Null(missing.AccountProblems);
        Assert.Null(missing.Residue);
    }

    [Fact]
    public async Task TheStatus_WithoutAStorageOrAnAccount_AsksNeither()
    {
        using var folder = new TempFolder();
        var secrets = new FakeSecretStore();
        secrets.Save(new SecretText("x"));

        var facts = await new StatusCollector(secrets, new FakeScheduler(), new FakeGitHubApi(), new StateStore(folder.Paths.StateFile), folder.Paths, TestTime.New(), new Redactor()).CollectAsync(CancellationToken.None);

        Assert.Null(facts.DataFolderProblems);
        Assert.Null(facts.AccountProblems);
        Assert.Empty(facts.Residue!);
    }
}
