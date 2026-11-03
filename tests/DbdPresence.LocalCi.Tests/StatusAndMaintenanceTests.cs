// Covers: FR-CI-07 (status in plain words: set up or not, task running, runners waiting or busy, last 10 jobs, token expiry with a warning 7 days
// before, disk use, whether GitHub currently refuses jobs - UNKNOWN until measured), FR-CI-04 (daily runner check, weekly base image; downloads and the build are gated like a job
// and time-boxed; the token notice), FR-CI-08 (the program's commands). docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class StatusAndMaintenanceTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();

    public void Dispose() => _folder.Dispose();

    private StatusFacts Facts(
        bool tokenSaved = true,
        bool taskExists = true,
        bool loop = true,
        IReadOnlyList<RunnerInfo>? runners = null,
        IReadOnlyList<JobRecord>? jobs = null,
        DateTimeOffset? expires = null,
        SelfTestRecord? selfTest = null) =>
        new(_time.GetUtcNow(), tokenSaved, taskExists, loop, runners ?? [], null, jobs ?? [], expires, 3L * 1024 * 1024 * 1024, selfTest, BaseImageBuilt: true);

    [Fact]
    public void BeforeAnySetup_TheStatusSaysNoAndPointsToSetUp()
    {
        var text = StatusReport.Build(Facts(tokenSaved: false, taskExists: false, loop: false));

        Assert.Contains("Set up: NO", text, StringComparison.Ordinal);
        Assert.Contains("not installed", text, StringComparison.Ordinal);
        Assert.Contains("choose 1", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunnersAreDescribedAsWaitingOrBusy()
    {
        var waiting = StatusReport.Build(Facts(runners: [new RunnerInfo(1, "dbd-linux-20261029120000", "online", false), new RunnerInfo(3, "other", "online", true)]));
        var busy = StatusReport.Build(Facts(runners: [new RunnerInfo(1, "dbd-linux-20261029120000", "online", true)]));
        var none = StatusReport.Build(Facts(runners: [new RunnerInfo(3, "dbd-presence-build-box", "online", true)]));

        Assert.Contains("Runners on GitHub: Linux waiting for a job.", waiting, StringComparison.Ordinal);
        Assert.Contains("Runners on GitHub: Linux busy with a job.", busy, StringComparison.Ordinal);
        Assert.Contains("Runners on GitHub: Linux none.", none, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheLastTenJobsAreShown_NewestFirst()
    {
        var jobs = Enumerable.Range(0, 14).Select(i => new JobRecord { At = _time.GetUtcNow().AddMinutes(i), Os = "Linux", Result = $"result-{i}" }).ToList();

        var text = StatusReport.Build(Facts(jobs: jobs));

        Assert.Contains("result-13", text, StringComparison.Ordinal);
        Assert.Contains("result-4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("result-3\n", text.Replace("\r", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(text.IndexOf("result-13", StringComparison.Ordinal) < text.IndexOf("result-12", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(30, "saved, expires on")]
    [InlineData(7, "WARNING")]
    [InlineData(3, "WARNING")]
    [InlineData(-1, "EXPIRED")]
    public void TheTokenExpiry_WarnsSevenDaysBefore(int days, string expected)
    {
        var text = StatusReport.Build(Facts(expires: _time.GetUtcNow().AddDays(days).AddHours(1)));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhetherGitHubRefusesJobs_IsUnknownUntilMeasured_ThenFollowsTheLastTestCheck()
    {
        string Refusal(string outcome) => StatusReport.Build(Facts(selfTest: new SelfTestRecord { At = _time.GetUtcNow(), Outcome = outcome }));

        Assert.Contains("UNKNOWN. Nobody has measured it yet", StatusReport.Build(Facts()), StringComparison.Ordinal);
        Assert.Contains("No. At ", Refusal(nameof(SelfTestOutcome.PassedOnThisPc)), StringComparison.Ordinal);
        Assert.Contains("No refusal seen", Refusal(nameof(SelfTestOutcome.Failed)), StringComparison.Ordinal);
        Assert.Contains("UNKNOWN, and possible", Refusal(nameof(SelfTestOutcome.FailedToStart)), StringComparison.Ordinal);
        Assert.Contains("UNKNOWN. At", Refusal(nameof(SelfTestOutcome.PassedOnGitHub)), StringComparison.Ordinal);
        Assert.Contains("UNKNOWN. The last test", Refusal(nameof(SelfTestOutcome.NotPickedUp)), StringComparison.Ordinal);
        Assert.Contains("was cancelled before it finished (a newer push cancels the older run of CI (Linux)), so nothing was measured. Choose 3 again.", Refusal(nameof(SelfTestOutcome.Cancelled)), StringComparison.Ordinal);
        Assert.DoesNotContain("did not finish", Refusal(nameof(SelfTestOutcome.Cancelled)), StringComparison.Ordinal);
    }

    [Fact]
    public void DiskUseIsShown()
    {
        var text = StatusReport.Build(Facts());

        Assert.Contains("3.0 GB", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCollector_AsksGitHub_AndReportsAProblemInWords()
    {
        var api = new FakeGitHubApi();
        var secrets = new FakeSecretStore();
        secrets.Save(new SecretText("x"));
        Directory.CreateDirectory(_folder.Paths.Machine);
        File.WriteAllBytes(Path.Combine(_folder.Paths.Machine, "big.bin"), new byte[2048]);
        var state = new StateStore(_folder.Paths.StateFile);
        var collector = new StatusCollector(secrets, new FakeScheduler { TaskExists = true, Running = true }, api, state, _folder.Paths, _time, new Redactor());

        var facts = await collector.CollectAsync(CancellationToken.None);

        Assert.True(facts.TokenSaved);
        Assert.True(facts.LoopRunning);
        Assert.Equal(2048, facts.DiskBytes);
        Assert.NotNull(facts.Runners);
    }

    private readonly FakeNotifier _notifier = new();

    private (Maintenance Maintenance, FakeGitHubApi Api, FakeDownloads Downloads, FakeWsl Wsl, StateStore State) NewMaintenance(FakeProcesses? processes = null)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var api = new FakeGitHubApi { Release = new RunnerRelease("2.338.0", string.Empty, new Uri("https://github.com/a")) };
        var downloads = new FakeDownloads();
        var wsl = new FakeWsl();
        var retry = new Retry(_time, redactor, log, attempts: 1);
        var paths = _folder.Paths;
        var state = new StateStore(paths.StateFile);
        var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        var storage = new FakeProtectedStorage();
        var disk = new FakeDisk();
        var sandbox = Sandboxes.New(wsl, new FakeWslAccount(), new FakeWslSettingsFile(), log, redactor, _time);
        var image = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, retry, integrity, disk, new ResidueRecorder(state), _time);
        var maintenance = new Maintenance(new RunnerCache(api, downloads, paths, log, retry, integrity, storage, disk), image, api, state, new JobGate(processes ?? new FakeProcesses()), _notifier, _time, log, redactor);
        return (maintenance, api, downloads, wsl, state);
    }

    [Fact]
    public async Task Maintenance_ChecksTheTokenAndStoresItsExpiry()
    {
        var (maintenance, api, _, _, state) = NewMaintenance();

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Equal(api.Repo.TokenExpiresAt, state.Load().TokenExpiresAt);
        Assert.Contains("repo", api.Calls);
    }

    [Fact]
    public async Task Maintenance_DoesNotRebuildTheImageWhileALinuxEnvironmentIsInUse()
    {
        var (maintenance, _, downloads, wsl, _) = NewMaintenance();

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Empty(wsl.Calls);
        Assert.DoesNotContain(downloads.Requested, r => r.Contains("ubuntu", StringComparison.Ordinal));
        Assert.Empty(downloads.Requested);
    }

    [Fact]
    public async Task Maintenance_AFailedDownloadIsLogged_NotThrown_AndNotRetriedForThirtyMinutes()
    {
        var (maintenance, api, downloads, _, state) = NewMaintenance();

        await maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        var firstRequests = downloads.Requested.Count;
        await maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Equal(firstRequests, downloads.Requested.Count);
        Assert.NotNull(state.Load().LastBaseImageAttempt);
        Assert.Contains("release", api.Calls);
        _time.Advance(TimeSpan.FromMinutes(31));
        await maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.True(downloads.Requested.Count > firstRequests);
    }

    [Theory]
    [InlineData(new string[0], LocalCiCommand.Menu, false)]
    [InlineData(new[] { "setup" }, LocalCiCommand.Setup, false)]
    [InlineData(new[] { "SETUP", "--elevated" }, LocalCiCommand.Setup, true)]
    [InlineData(new[] { "serve" }, LocalCiCommand.Serve, false)]
    [InlineData(new[] { "status" }, LocalCiCommand.Status, false)]
    [InlineData(new[] { "selftest" }, LocalCiCommand.SelfTest, false)]
    [InlineData(new[] { "uninstall", "--elevated" }, LocalCiCommand.Uninstall, true)]
    [InlineData(new[] { "--help" }, LocalCiCommand.Help, false)]
    [InlineData(new[] { "bogus" }, LocalCiCommand.Unknown, false)]
    public void TheCommandLine_IsUnderstood(string[] args, LocalCiCommand command, bool elevated)
    {
        var parsed = ParsedCommand.Parse(args);

        Assert.Equal(command, parsed.Command);
        Assert.Equal(elevated, parsed.Elevated);
    }

    [Fact]
    public void TheScheduledTask_RunsServeAtTheOwnersLogon_WithHighestPrivileges_Hidden_AndRestartsOnFailure()
    {
        var xml = TaskDefinition.Build(@"C:\Tools\DbdLocalCi.exe", @"PC\owner & co");

        Assert.Contains("<LogonTrigger>", xml, StringComparison.Ordinal);
        Assert.Contains("<UserId>PC\\owner &amp; co</UserId>", xml, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
        Assert.Contains("<Hidden>true</Hidden>", xml, StringComparison.Ordinal);
        Assert.Contains("<RestartOnFailure>", xml, StringComparison.Ordinal);
        Assert.Contains("<Command>C:\\Tools\\DbdLocalCi.exe</Command>", xml, StringComparison.Ordinal);
        Assert.Contains("<Arguments>serve</Arguments>", xml, StringComparison.Ordinal);
        System.Xml.Linq.XDocument.Parse(xml);
    }

    // ---- status lines (D41: one lane, the variables, the account, the leftovers)

    [Fact]
    public void AnOfflineRunner_IsNotReportedAsWaitingForAJob()
    {
        var text = StatusReport.Build(Facts(runners: [new RunnerInfo(1, "dbd-linux-20261029120000", "offline", false)]));

        Assert.DoesNotContain("Linux waiting for a job", text, StringComparison.Ordinal);
        Assert.Contains("Linux none (1 offline registration left over from a crash", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbd-linux", "Checks on this PC: ON.")]
    [InlineData(null, "Checks on this PC: OFF.")]
    [InlineData("somebody-elses", "Checks on this PC: PROBLEM. LINUX_RUNNER is set to 'somebody-elses'")]
    public void WhetherChecksGoToThisPc_FollowsTheVariable(string? value, string expected)
    {
        var text = StatusReport.Build(Facts() with { Variables = new Dictionary<string, string?> { ["LINUX_RUNNER"] = value } });

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WINDOWS_RUNNER")]
    [InlineData("SECRETS_LINUX_RUNNER")]
    [InlineData("SECRETS_WINDOWS_RUNNER")]
    public void AVariableThisProgramDoesNotServe_IsAPlainProblem_AndSaysWhatToDo(string name)
    {
        var text = StatusReport.Build(Facts() with { Variables = new Dictionary<string, string?> { ["LINUX_RUNNER"] = "dbd-linux", [name] = "dbd-windows" } });

        Assert.Contains($"PROBLEM: {name} is set to 'dbd-windows'", text, StringComparison.Ordinal);
        Assert.Contains("only Linux checks run on this PC", text, StringComparison.Ordinal);
        Assert.Contains("Delete the variable on GitHub", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenGitHubCouldNotBeAsked_TheStatusSaysItDoesNotKnowWhereChecksGo()
    {
        var text = StatusReport.Build(Facts(runners: null) with { Runners = null, RunnerProblem = "no answer", Variables = null });

        Assert.Contains("could not ask GitHub, so it is not known whether checks are sent to this PC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedToken_IsSaidPlainly_NotAsAnExpiryDate()
    {
        var text = StatusReport.Build(Facts(expires: _time.GetUtcNow().AddDays(20)) with { TokenRefused = true });

        Assert.Contains("GitHub REFUSES it", text, StringComparison.Ordinal);
        Assert.DoesNotContain("expires on", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAccountProblem_BlocksJobsInTheStatusText_AndAGoodAccountIsConfirmed()
    {
        var problem = StatusReport.Build(Facts() with { AccountProblems = ["the account dbd-ci-wsl is switched off"] });
        var good = StatusReport.Build(Facts() with { AccountProblems = [] });
        var unknown = StatusReport.Build(Facts());

        Assert.Contains("Windows account dbd-ci-wsl (owns the Linux machine): PROBLEM. No job starts until this is repaired", problem, StringComparison.Ordinal);
        Assert.Contains("the account dbd-ci-wsl is switched off", problem, StringComparison.Ordinal);
        Assert.Contains("as it must be", good, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows account", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void LeftoversAreShownAsAProblem_AndNoneAsNone()
    {
        var left = StatusReport.Build(Facts() with { Residue = ["the Linux machine dbd-ci-job-20261029120000 could not be removed"] });
        var none = StatusReport.Build(Facts() with { Residue = [] });
        var unknown = StatusReport.Build(Facts());

        Assert.Contains("Leftovers: PROBLEM. The last clean-up could not remove: the Linux machine dbd-ci-job-20261029120000 could not be removed", left, StringComparison.Ordinal);
        Assert.Contains("Leftovers: none.", none, StringComparison.Ordinal);
        Assert.DoesNotContain("Leftovers", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCollector_ReportsTheVariables_TheAccount_AndTheLeftovers()
    {
        var api = new FakeGitHubApi();
        api.Variables["LINUX_RUNNER"] = "dbd-linux";
        api.Variables["WINDOWS_RUNNER"] = "dbd-windows";
        var secrets = new FakeSecretStore();
        secrets.Save(new SecretText("x"));
        var state = new StateStore(_folder.Paths.StateFile);
        state.Update(s => s.ResidueItems = [Left.Words("a folder is left")]);
        var account = new FakeWslAccount();
        account.Problems.Add("the account dbd-ci-wsl is locked out");
        var collector = new StatusCollector(secrets, new FakeScheduler { TaskExists = true }, api, state, _folder.Paths, _time, new Redactor(), null, new FakeProtectedStorage(), account);

        var facts = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal("dbd-linux", facts.Variables!["LINUX_RUNNER"]);
        Assert.Equal("dbd-windows", facts.Variables["WINDOWS_RUNNER"]);
        Assert.Null(facts.Variables["SECRETS_LINUX_RUNNER"]);
        Assert.Equal(["the account dbd-ci-wsl is locked out"], facts.AccountProblems);
        Assert.Equal(["a folder is left"], facts.Residue);
    }

    [Fact]
    public async Task TheCollector_WhenGitHubRefusesTheToken_SaysSo_AndDoesNotLeakTheAnswer()
    {
        var api = new FakeGitHubApi { RunnersFailure = new GitHubApiException("Bad credentials github_pat_11ABCDEFGHIJKLMNOPQRSTUV", 401, isTransient: false) };
        var secrets = new FakeSecretStore();
        secrets.Save(new SecretText("x"));
        var collector = new StatusCollector(secrets, new FakeScheduler(), api, new StateStore(_folder.Paths.StateFile), _folder.Paths, _time, new Redactor());

        var facts = await collector.CollectAsync(CancellationToken.None);

        Assert.True(facts.TokenRefused);
        Assert.Null(facts.Runners);
        Assert.DoesNotContain("github_pat_", facts.RunnerProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProgramLine_SaysWhereTheInstalledCopyIs_OrThatItIsNotInstalled_OrNothingWhenNotAsked()
    {
        var installed = StatusReport.Build(Facts() with { ProgramPath = @"C:\Program Files\DbdLocalCi\DbdLocalCi.exe", ProgramInstalled = true });
        var missing = StatusReport.Build(Facts() with { ProgramPath = @"C:\Program Files\DbdLocalCi\DbdLocalCi.exe", ProgramInstalled = false });
        var notAsked = StatusReport.Build(Facts());

        Assert.Contains(@"Program: installed in C:\Program Files\DbdLocalCi\DbdLocalCi.exe (the background task runs that copy).", installed, StringComparison.Ordinal);
        Assert.Contains("Program: NOT installed in Program Files. Choose 1 (Set up).", missing, StringComparison.Ordinal);
        Assert.DoesNotContain("Program:", notAsked, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDataFolderLine_SaysWhatIsWrong_OrThatItIsClosed_OrNothingWhenNotAsked()
    {
        var wrong = StatusReport.Build(Facts() with { DataFolderProblems = [@"C:\ProgramData\DbdLocalCi gives access to all users."] });
        var closed = StatusReport.Build(Facts() with { DataFolderProblems = [] });
        var notAsked = StatusReport.Build(Facts());

        Assert.Contains(@"Data folder: PROBLEM. The background task will not run until this is repaired (choose 1, Set up): C:\ProgramData\DbdLocalCi gives access to all users.", wrong, StringComparison.Ordinal);
        Assert.Contains("Data folder: closed to everyone but Administrators and the system.", closed, StringComparison.Ordinal);
        Assert.DoesNotContain("Data folder:", notAsked, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBaseImageLine_SaysBuiltOrNotBuiltYet()
    {
        Assert.Contains("Linux base image: built.", StatusReport.Build(Facts()), StringComparison.Ordinal);
        Assert.Contains("Linux base image: not built yet.", StatusReport.Build(Facts() with { BaseImageBuilt = false }), StringComparison.Ordinal);
    }

    // ---- the daily notice about leftovers (L2)

    [Fact]
    public async Task Leftovers_GiveTheOwnerOneNoticeADay_AndTheLogWarnsOnce()
    {
        var (maintenance, _, _, _, state) = NewMaintenance();
        state.Update(s =>
        {
            s.ResidueItems = [Left.Machine("dbd-ci-job-20261029120000")];
            s.LastTokenCheck = _time.GetUtcNow();
            s.LastRunnerCheck = _time.GetUtcNow();
            s.LastBaseImageBuild = _time.GetUtcNow();
        });

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Single(_notifier.Messages);
        Assert.Contains("could not remove everything after a Linux job", _notifier.Messages[0], StringComparison.Ordinal);
        Assert.Contains("dbd-ci-job-20261029120000", _notifier.Messages[0], StringComparison.Ordinal);
        _time.Advance(TimeSpan.FromHours(25));
        state.Update(s => s.LastTokenCheck = _time.GetUtcNow());
        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Equal(2, _notifier.Messages.Count);
    }

    [Fact]
    public async Task NoLeftovers_NoNotice()
    {
        var (maintenance, _, _, _, _) = NewMaintenance();

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);

        Assert.Empty(_notifier.Messages);
    }

    [Fact]
    public async Task WhileTheGameRuns_TheLeftoverNoticeIsHeldBack_AndShownWhenItEnds()
    {
        var processes = new FakeProcesses();
        processes.Running.Add("DeadByDaylight-Win64-Shipping");
        var (maintenance, _, _, _, state) = NewMaintenance(processes);
        state.Update(s => s.ResidueItems = [Left.Words("a folder is left")]);

        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Empty(_notifier.Messages);

        processes.Running.Clear();
        await maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Single(_notifier.Messages);
    }
}
