// Covers: FR-CI-01 (setup: Windows build, free disk and virtualization checks; the program installs itself in its protected folder; the data folder is closed; the dedicated account and WSL for it;
// the account's settings; the pinned, verified Linux base image and the runner program; the token validated and saved before anything is changed on GitHub; the one repository variable, set last and
// taken away again unless the test run proves a job ran on this PC; variables this program never serves are offered for deletion; the sign-in task only for a protected program),
// FR-CI-08 (uninstall: asks first; the variable first so jobs go back to GitHub, then the task, the runners, the leftovers, every Linux machine of the account, the account itself, the caches,
// the token and the installed program; a variable that cannot be deleted keeps the token and the program and ends with NOT DONE and an error), FR-CI-11 (exact names), D41. docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi.Tests;

public sealed class SetupTests : IDisposable
{
    private const string GoodToken = "github_pat_11GOODGOODGOODGOODGOOD_abcdefghijklmnopqrstuvwxyz";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A wizard whose downloads, runner release and test run all work, so a test changes only what it is about.</summary>
    internal static SetupWizard NewWizard(
        TempFolder folder,
        FakeConsole console,
        FakeMachine machine,
        FakeWsl wsl,
        FakeGitHubApi api,
        FakeSecretStore store,
        FakeScheduler scheduler,
        Redactor redactor,
        MemoryCiLog log,
        out StateStore state,
        FakeInstaller? installer = null,
        FakeProtectedStorage? storage = null,
        FakeWslAccount? account = null,
        FakeWslSettingsFile? settings = null,
        FakeWslInstaller? wslInstaller = null)
    {
        var time = TestTime.New();
        var paths = folder.Paths;
        var downloads = new FakeDownloads();
        var image = Encoding.UTF8.GetBytes("ubuntu");
        var pinned = new UbuntuImage("ubuntu-test.wsl", Sha(image), new Uri("https://releases.ubuntu.com/noble/ubuntu-test.wsl"), image.Length);
        downloads.Files[pinned.Address.ToString()] = image;
        var linux = Encoding.UTF8.GetBytes("linux");
        var linuxUrl = new Uri("https://github.com/actions/runner/releases/download/v2.338.0/actions-runner-linux-x64-2.338.0.tar.gz");
        downloads.Files[linuxUrl.ToString()] = linux;
        api.Release ??= new RunnerRelease(
            "2.338.0",
            $"<!-- BEGIN SHA linux-x64 -->{Sha(linux)}<!-- END SHA linux-x64 -->",
            linuxUrl);
        if (api.RunStates.Count == 0)
        {
            api.RunStates.Add(new WorkflowRunInfo(77, "completed", "success", time.GetUtcNow()));
            api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "success", "dbd-linux-20261029120000", 9, ["self-hosted", "dbd-linux"]));
        }

        var retry = new Retry(time, redactor, log, attempts: 1);
        state = new StateStore(paths.StateFile, redactor);
        var tokenSource = new SecretSource(store, redactor);
        api.TokenInUse = () => tokenSource.Current()?.Reveal();
        var selfTest = new SelfTest(api, state, time, log, redactor, (_, _) => Task.CompletedTask);
        var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        var trust = storage ?? new FakeProtectedStorage();
        var disk = new FakeDisk();
        var theAccount = account ?? new FakeWslAccount();
        var sandbox = Sandboxes.New(wsl, theAccount, settings ?? new FakeWslSettingsFile(), log, redactor, time);
        var residue = new ResidueRecorder(state);
        var builder = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, retry, integrity, disk, residue, time, pinned);
        var isolation = new IsolationProbe(wsl, sandbox, paths, integrity, new FakeProbeMarker(), new FakeWritableLocations(), residue, log, redactor, time);
        var review = new ProbeReview(isolation, state, console, () => IsolationProbe.RecipeIdFor(sandbox.Facts, builder.RecipeId), time, log);
        return new SetupWizard(
            console,
            machine,
            wsl,
            wslInstaller ?? new FakeWslInstaller(),
            theAccount,
            sandbox,
            trust,
            installer ?? new FakeInstaller(),
            builder,
            new RunnerCache(api, downloads, paths, log, retry, integrity, trust, disk),
            store,
            tokenSource,
            api,
            scheduler,
            selfTest,
            review,
            state,
            paths,
            time,
            redactor,
            log,
            @"C:\Downloads\DbdLocalCi.exe");
    }

    private sealed record Parts(
        SetupWizard Wizard,
        FakeConsole Console,
        FakeMachine Machine,
        FakeWsl Wsl,
        FakeGitHubApi Api,
        FakeSecretStore Store,
        FakeScheduler Scheduler,
        StateStore State,
        FakeInstaller Installer,
        FakeProtectedStorage Storage,
        FakeWslAccount Account,
        FakeWslSettingsFile Settings,
        FakeWslInstaller WslInstaller);

    private Parts Build(IEnumerable<string>? secrets = null, IEnumerable<bool>? answers = null)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var console = new FakeConsole(secrets ?? [GoodToken], answers);
        var machine = new FakeMachine();
        var wsl = new FakeWsl();
        var api = new FakeGitHubApi();
        var store = new FakeSecretStore();
        var scheduler = new FakeScheduler();
        var installer = new FakeInstaller();
        var storage = new FakeProtectedStorage();
        var account = new FakeWslAccount();
        var settings = new FakeWslSettingsFile();
        var wslInstaller = new FakeWslInstaller();
        var wizard = NewWizard(_folder, console, machine, wsl, api, store, scheduler, redactor, log, out var state, installer, storage, account, settings, wslInstaller);
        return new Parts(wizard, console, machine, wsl, api, store, scheduler, state, installer, storage, account, settings, wslInstaller);
    }

    [Fact]
    public async Task WithoutAdministratorRights_NothingIsChanged()
    {
        var parts = Build();
        parts.Machine.Administrator = false;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(parts.Wizard.ProvenOnThisPc);
        Assert.Contains("administrator", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Wsl.Calls);
        Assert.Empty(parts.Api.Calls);
        Assert.Empty(parts.Scheduler.Calls);
        Assert.Empty(parts.Installer.Calls);
        Assert.Empty(parts.Storage.Calls);
        Assert.Empty(parts.Account.Calls);
        Assert.Empty(parts.WslInstaller.Calls);
        Assert.False(parts.Store.Exists());
    }

    [Theory]
    [InlineData(18362, 100, true)]
    [InlineData(22631, 10, true)]
    public async Task AnOldWindowsOrALowDisk_StopsSetupWithAPlainMessage(int build, long freeGigabytes, bool shouldStop)
    {
        var parts = Build();
        parts.Machine.WindowsBuild = build;
        parts.Machine.FreeBytes = freeGigabytes * 1024 * 1024 * 1024;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(shouldStop, outcome == SetupOutcome.Stopped);
        Assert.Empty(parts.Wsl.Calls);
        Assert.Empty(parts.Installer.Calls);
        Assert.Empty(parts.Account.Calls);
    }

    [Fact]
    public async Task VirtualizationSwitchedOff_AsksFirst_AndStopsWhenTheOwnerDeclines()
    {
        var parts = Build(answers: [false]);
        parts.Machine.Virtualization = false;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Single(parts.Console.Questions);
        Assert.Contains("BIOS", parts.Console.Questions[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProgramInstallsItself_BeforeAnythingElse_AndTheDataFolderIsClosedBeforeTheAccountAndAnyDownload()
    {
        var parts = Build();
        var journal = new List<string>();
        parts.Installer.Calls.Journal = journal;
        parts.Storage.Calls.Journal = journal;
        parts.Account.Calls.Journal = journal;
        parts.Wsl.Calls.Journal = journal;
        parts.Installer.Calls.Prefix = "program ";
        parts.Storage.Calls.Prefix = "storage ";
        parts.Account.Calls.Prefix = "account ";
        parts.Wsl.Calls.Prefix = "wsl ";

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(@"program install from C:\Downloads\DbdLocalCi.exe", journal[0]);
        Assert.Equal("storage create-data-folder", journal[1]);
        Assert.Equal("account ensure", journal[2]);
        Assert.Contains("run it from anywhere, it installs itself", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADifferentCopyThatReplacesTheInstalledOne_FirstStopsTheBackgroundTask_ThenStartsItAgain()
    {
        var parts = Build();
        parts.Installer.IsInstalled = true;
        parts.Installer.Outcome = InstallOutcome.Updated;

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(["stop", @"install C:\Program Files\DbdLocalCi\DbdLocalCi.exe", "start"], parts.Scheduler.Calls);
        Assert.Contains("Updated", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataFolderThatCannotBeClosed_StopsSetupBeforeTheAccountOrAnythingIsDownloaded()
    {
        var parts = Build();
        parts.Storage.DataFolderProblems.Add(@"C:\ProgramData\DbdLocalCi gives access to all users.");

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("all users", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Wsl.Calls);
        Assert.Empty(parts.Account.Calls);
        Assert.Empty(parts.Api.Calls);
    }

    [Fact]
    public async Task AnAccountThatCannotBeMade_StopsSetup_WithTheReason_AndNothingIsDownloaded()
    {
        var parts = Build();
        parts.Account.EnsureFailure = new InvalidOperationException("An account named dbd-ci-wsl already exists but it is in the group Administrators.");

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("already exists", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Wsl.Calls);
        Assert.Empty(parts.Api.Calls);
        Assert.False(parts.Wizard.ProvenOnThisPc);
    }

    [Fact]
    public async Task WhenWslDoesNotAnswerForTheAccount_ItIsInstalledFromHere_AndSetupGoesOnWhenItAnswersAfterwards()
    {
        var parts = Build();
        parts.Wsl.Usable = false;
        parts.WslInstaller.OnInstall = () => parts.Wsl.Usable = true;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Contains("install", parts.WslInstaller.Calls);
        Assert.Contains("answers for the account dbd-ci-wsl now", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenWslNeedsARestartAfterTheInstall_SetupAsksForOneAndStops()
    {
        var parts = Build();
        parts.Wsl.Usable = false;
        parts.WslInstaller.Restart = true;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.RestartNeeded, outcome);
        Assert.Contains("Restart your PC, then open DbdLocalCi again.", parts.Console.Lines);
        Assert.Empty(parts.Api.Calls);
        Assert.False(parts.Wizard.ProvenOnThisPc);
    }

    [Fact]
    public async Task WhenWslStillDoesNotStartForTheAccount_AfterTheInstall_SetupSaysItIsUnmeasured_AndStops()
    {
        var parts = Build();
        parts.Wsl.Usable = false;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("PENDING-ENV", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("dbd-ci-wsl", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Api.Calls);
    }

    /// <summary>First-run risk (round 3): WSL installed for the owner's account only (the Store version) cannot be used by the other Windows account; the message says so and what installs it for the whole PC.</summary>
    [Fact]
    public async Task WhenWslStillDoesNotStartForTheAccount_SetupNamesTheCommonCause_WslInstalledForTheOwnerOnly()
    {
        var parts = Build();
        parts.Wsl.Usable = false;

        await parts.Wizard.RunAsync(CancellationToken.None);

        var text = parts.Console.AllText;
        Assert.Contains("WSL can be installed for your own Windows account only", text, StringComparison.Ordinal);
        Assert.Contains("is another Windows user, so it cannot use that", text, StringComparison.Ordinal);
        Assert.Contains("installs WSL for the whole PC", text, StringComparison.Ordinal);
        Assert.Contains("wsl --update", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWslThatCannotBeStartedAtAll_StopsSetupWithItsMessage()
    {
        var parts = Build();
        parts.Wsl.UsableFailure = new InvalidOperationException("Windows would not start wsl.exe as dbd-ci-wsl: The Windows service 'Secondary Logon' is switched off.");

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("Secondary Logon", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.WslInstaller.Calls);
    }

    [Fact]
    public async Task TheAccountsSettings_AreWrittenAndReadBackBeforeAnythingIsBuilt_AndTheOwnersWslIsNeverTouched()
    {
        var parts = Build();

        await parts.Wizard.RunAsync(CancellationToken.None);

        var written = Assert.Single(parts.Settings.Writes);
        Assert.Equal(WslSettingsPolicy.ExpectedText(Sandboxes.Facts), written);
        Assert.Contains("Nothing of yours (your own Linux, your .wslconfig) was touched", parts.Console.AllText, StringComparison.Ordinal);
        Assert.True(parts.Settings.Holds >= 1);
        Assert.Equal(parts.Settings.Holds, parts.Settings.Releases);
    }

    [Fact]
    public async Task SettingsThatCannotBeWrittenOrChangedAfterWriting_StopSetup_BeforeTheImageIsBuilt()
    {
        var parts = Build();
        parts.Settings.HeldTextOverride = "[wsl2]\nnetworkingMode=mirrored\n";

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("not as required", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(parts.Wsl.Calls, c => c.StartsWith("import", StringComparison.Ordinal));
        Assert.Empty(parts.Api.Calls);
    }

    [Fact]
    public async Task ASetupThatWorks_BuildsTheImage_ChecksTheTokenBeforeSavingIt_SetsTheOneVariableLast_InstallsTheTask_AndProvesItOnThisPc()
    {
        var parts = Build();

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.True(parts.Wizard.ProvenOnThisPc);
        Assert.Contains("import dbd-ci-base", parts.Wsl.Calls);
        Assert.Equal(["save"], parts.Store.Calls);
        Assert.Equal(new Dictionary<string, string> { ["LINUX_RUNNER"] = "dbd-linux" }, parts.Api.Variables);
        Assert.Equal([@"install C:\Program Files\DbdLocalCi\DbdLocalCi.exe", "start"], parts.Scheduler.Calls);
        Assert.Contains("dispatch local-ci-probe.yml on main", parts.Api.Calls);
        Assert.Contains("The test ran on your PC and passed.", parts.Console.Lines);
        Assert.Contains("now runs the checks of this repository on your PC", parts.Console.AllText, StringComparison.Ordinal);
        var calls = parts.Api.Calls.ToList();
        Assert.True(calls.IndexOf("repo") < calls.IndexOf("set LINUX_RUNNER"));
        Assert.True(calls.IndexOf("release") < calls.IndexOf("repo"));
        Assert.True(calls.IndexOf("set LINUX_RUNNER") < calls.IndexOf("dispatch local-ci-probe.yml on main"));
        Assert.DoesNotContain(calls, c => c.StartsWith("delete-variable", StringComparison.Ordinal));
        Assert.NotNull(parts.State.Load().LastBaseImageBuild);
        Assert.NotNull(parts.State.Load().TokenExpiresAt);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("failed-on-pc")]
    [InlineData("passed-on-github")]
    public async Task WhenTheTestRunDidNotPassOnThisPc_SetupSaysWhatIsNotProven_TakesTheVariableAway_AndDoesNotClaimSuccess(string mode)
    {
        var parts = Build();
        var time = TestTime.New();
        parts.Api.RunStates.Clear();
        parts.Api.Jobs.Clear();
        switch (mode)
        {
            case "refused":
                parts.Api.RunStates.Add(new WorkflowRunInfo(77, "completed", "failure", time.GetUtcNow()));
                parts.Api.Jobs.Add(new WorkflowJobInfo(5, "build", "completed", "failure", null, 0, ["self-hosted", "dbd-linux"]));
                break;
            case "failed-on-pc":
                parts.Api.RunStates.Add(new WorkflowRunInfo(77, "completed", "failure", time.GetUtcNow()));
                parts.Api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "failure", "dbd-linux-20261029120000", 9, ["self-hosted", "dbd-linux"]));
                break;
            default:
                parts.Api.RunStates.Add(new WorkflowRunInfo(77, "completed", "success", time.GetUtcNow()));
                parts.Api.Jobs.Add(new WorkflowJobInfo(1, "build", "completed", "success", "GitHub Actions 3", 9, ["ubuntu-24.04"]));
                break;
        }

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.False(parts.Wizard.ProvenOnThisPc);
        Assert.DoesNotContain("now runs the checks of this repository on your PC", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("NOT PROVEN", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Api.Variables);
        Assert.Contains("delete-variable LINUX_RUNNER", parts.Api.Calls);
        Assert.Contains("I turned LINUX_RUNNER off again", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheTestCouldNotBeRunAtAll_ThatIsNotProof_AndTheVariableGoes()
    {
        var parts = Build();
        parts.Api.RunStates.Clear();
        parts.Api.Jobs.Clear();
        parts.Api.DispatchedRun = null;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.False(parts.Wizard.ProvenOnThisPc);
        Assert.Empty(parts.Api.Variables);
        Assert.Contains("NOT PROVEN", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfTheVariableCannotBeTakenAwayAgain_TheOwnerIsToldToDeleteItByHand()
    {
        var parts = Build();
        parts.Api.RunStates.Clear();
        parts.Api.Jobs.Clear();
        parts.Api.RunStates.Add(new WorkflowRunInfo(77, "completed", "failure", TestTime.New().GetUtcNow()));
        parts.Api.Jobs.Add(new WorkflowJobInfo(5, "build", "completed", "failure", null, 0, ["self-hosted", "dbd-linux"]));
        parts.Api.DeleteVariableFailure = new GitHubApiException("Resource not accessible", 403, isTransient: false);

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.False(parts.Wizard.ProvenOnThisPc);
        Assert.Contains("I could not turn LINUX_RUNNER off again. Delete it on GitHub by hand now", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstalledCopyThatAnOrdinaryUserCouldChange_StopsSetupAtOnce_BeforeAnythingElseIsDone()
    {
        var parts = Build();
        parts.Installer.LocationProblems.Add(@"C:\Program Files\DbdLocalCi lets every user change the program.");

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(parts.Scheduler.Calls);
        Assert.Empty(parts.Api.Calls);
        Assert.Empty(parts.Wsl.Calls);
        Assert.Empty(parts.Account.Calls);
        Assert.Contains("not in a safe place", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("lets every user change the program", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSignInTask_IsNeverRegistered_ForAProgramAnOrdinaryUserCouldChange_EvenIfItChangedSinceTheInstall_AndNoVariableIsLeftPointingAtThePc()
    {
        var parts = Build();
        parts.Installer.LocationProblems.Add(@"C:\Program Files\DbdLocalCi lets every user change the program.");
        parts.Installer.ProblemsFromCheck = 2;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(parts.Scheduler.Calls);
        Assert.Contains("lets every user change the program", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("Nothing was registered", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Api.Variables);
        Assert.False(parts.Wizard.ProvenOnThisPc);
    }

    [Fact]
    public async Task ASavedTokenThatStillWorks_IsKept_WithoutAskingForANewOne()
    {
        var parts = Build(secrets: []);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Empty(parts.Store.Calls);
        Assert.Contains("still works", parts.Console.AllText, StringComparison.Ordinal);
    }

    /// <summary>The documented way to renew: when the saved token ends within the warning time, set-up takes the new one (checked before it is saved) and keeps the old one on Enter.</summary>
    [Fact]
    public async Task ASavedTokenThatEndsSoon_IsRenewedWhenTheOwnerPastesANewOne_AfterGitHubAcceptsIt()
    {
        var parts = Build(secrets: ["github_pat_11NEWNEWNEWNEWNEWNEW_abcdefghijklmnopqrstuvwxyz"]);
        parts.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(3), IsPrivate: true);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Equal(["save"], parts.Store.Calls);
        Assert.Equal("github_pat_11NEWNEWNEWNEWNEWNEW_abcdefghijklmnopqrstuvwxyz", parts.Store.Load()!.Reveal());
        Assert.Contains("still works but ends on 2026-11-01", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("Administration = Read and write", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("github_pat_11NEWNEW", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedTokenThatEndsSoon_IsKeptOnEnter_WithoutAnyChange()
    {
        var parts = Build(secrets: []);
        parts.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(7), IsPrivate: true);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Empty(parts.Store.Calls);
        Assert.Contains("Kept the saved token", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Equal(GoodToken, parts.Store.Load()!.Reveal());
    }

    [Theory]
    [InlineData(401, false, "did not accept that token")]
    [InlineData(0, true, "could not be reached")]
    public async Task ARenewalTokenThatCannotBeChecked_IsNeverSaved_AndTheSavedTokenStays(int status, bool transient, string expected)
    {
        var parts = Build(secrets: ["github_pat_11BADBADBADBADBADBAD_abcdefghijklmnopqrstuvwxyz"]);
        parts.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(2), IsPrivate: true);
        parts.Api.RepoFailure = new GitHubApiException("Bad credentials", status, transient);
        parts.Api.RepoCallsBeforeFailure = 1;
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Empty(parts.Store.Calls);
        Assert.Equal(GoodToken, parts.Store.Load()!.Reveal());
        Assert.Contains(expected, parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("saved token was kept", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenThatGitHubRefuses_IsNeverSaved_AndNothingOnGitHubIsChanged()
    {
        var parts = Build(secrets: ["github_pat_bad1", "github_pat_bad2", "github_pat_bad3"]);
        parts.Api.RepoFailure = new GitHubApiException("Bad credentials", 401, isTransient: false);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(parts.Store.Exists());
        Assert.Empty(parts.Api.Variables);
        Assert.Empty(parts.Scheduler.Calls);
        Assert.Equal(3, parts.Api.Calls.Count(c => c == "repo"));
    }

    /// <summary>An answer with no status at all (nothing that came from GitHub) is "no answer", not "the token is bad", even when it is not marked as transient.</summary>
    [Fact]
    public async Task AnAnswerWithNoStatusAtAll_WhenCheckingASavedToken_IsNoAnswer_NotABadToken()
    {
        var parts = Build(secrets: []);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();
        parts.Api.RepoFailure = new GitHubApiException("The answer had no status", 0, isTransient: false);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.True(parts.Store.Exists());
        Assert.Contains("could not be reached", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("did not accept", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenGitHubCannotBeReachedToCheckASavedToken_TheTokenIsKept_AndSetupStopsWithoutAskingForANewOne()
    {
        var parts = Build(secrets: []);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();
        parts.Api.RepoFailure = new GitHubApiException("No answer", 0, isTransient: true);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(parts.Store.Calls);
        Assert.True(parts.Store.Exists());
        Assert.Contains("could not be reached", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WINDOWS_RUNNER")]
    [InlineData("SECRETS_LINUX_RUNNER")]
    [InlineData("SECRETS_WINDOWS_RUNNER")]
    public async Task AVariableThisProgramNeverServes_IsSaidPlainly_AndDeletedOnlyWhenTheOwnerAgrees(string name)
    {
        var accepts = Build(answers: [true]);
        accepts.Api.Variables[name] = "something";
        var declines = Build(answers: [false]);
        declines.Api.Variables[name] = "something";

        await accepts.Wizard.RunAsync(CancellationToken.None);
        await declines.Wizard.RunAsync(CancellationToken.None);

        Assert.Contains($"{name} is set on GitHub (to 'something')", accepts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("only Linux checks", accepts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("delete-variable " + name, accepts.Api.Calls);
        Assert.DoesNotContain(name, accepts.Api.Variables.Keys);
        Assert.DoesNotContain("delete-variable " + name, declines.Api.Calls);
        Assert.Equal("something", declines.Api.Variables[name]);
        Assert.Contains("Left " + name, declines.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetupNeverSetsAVariableOtherThanLinuxRunner()
    {
        var parts = Build();

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(parts.Api.Calls, c => c.StartsWith("set ", StringComparison.Ordinal) && c != "set LINUX_RUNNER");
        Assert.Equal(["LINUX_RUNNER"], LocalCiDefaults.Variables.Keys);
    }

    /// <summary>The documented way to renew the token ("choose 1 again, it asks only for the token") must not rebuild the whole Linux base image each time (QA T14).</summary>
    [Fact]
    public async Task AnotherRunOfSetup_SkipsTheBaseImageThatIsAlreadyBuilt()
    {
        var first = Build();
        Assert.Equal(SetupOutcome.Done, await first.Wizard.RunAsync(CancellationToken.None));
        Assert.Contains("import dbd-ci-base", first.Wsl.Calls);
        var second = Build(secrets: []);
        second.Store.Save(new SecretText(GoodToken));

        var outcome = await second.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.DoesNotContain("import dbd-ci-base", second.Wsl.Calls);
        Assert.Contains("keeping it", second.Console.AllText, StringComparison.Ordinal);
    }

    /// <summary>After the first set-up the program itself uses disk: a re-run for a new token is not refused by the 25 GB free check of step 1 (QA T15).</summary>
    [Fact]
    public async Task ARerunToRenewTheToken_IsNotBlockedByTheDiskTheProgramItselfUses()
    {
        var first = Build();
        await first.Wizard.RunAsync(CancellationToken.None);
        var again = Build(secrets: []);
        again.Store.Save(new SecretText(GoodToken));
        again.Machine.FreeBytes = 20L * 1024 * 1024 * 1024;

        var outcome = await again.Wizard.RunAsync(CancellationToken.None);

        Assert.NotEqual(SetupOutcome.Stopped, outcome);
    }

    [Fact]
    public async Task ABaseImageFromAnotherRecipe_IsRebuiltOnARerun()
    {
        var first = Build();
        await first.Wizard.RunAsync(CancellationToken.None);
        first.State.Update(s => s.BaseImageRecipe = "an older recipe");
        var again = Build(secrets: []);
        again.Store.Save(new SecretText(GoodToken));

        await again.Wizard.RunAsync(CancellationToken.None);

        Assert.Contains("import dbd-ci-base", again.Wsl.Calls);
    }

    // ---- red-team L9: only a fine-grained token (github_pat_)

    [Fact]
    public async Task ATokenThatIsNotFineGrained_IsRefusedWithoutAskingGitHub_NeverSaved_AndNeverEchoed()
    {
        var classic = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var oauth = "gho_abcdefghijklmnopqrstuvwxyz0123456789";
        var parts = Build(secrets: [classic, oauth, "yes"]);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(parts.Store.Exists());
        Assert.DoesNotContain("repo", parts.Api.Calls);
        Assert.Equal(3, Regex.Count(parts.Console.AllText, "not a fine-grained token"));
        Assert.Contains("must start with github_pat_", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(classic, parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(oauth, parts.Console.AllText, StringComparison.Ordinal);
        Assert.Empty(parts.Api.Variables);
    }

    [Fact]
    public async Task AClassicTokenFollowedByAFineGrainedOne_IsAcceptedOnTheSecondTry()
    {
        var parts = Build(secrets: ["ghp_abcdefghijklmnopqrstuvwxyz0123456789", GoodToken]);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Equal(GoodToken, parts.Store.Load()!.Reveal());
        var calls = parts.Api.Calls.ToList();
        Assert.Equal(1, calls.Take(calls.IndexOf("set LINUX_RUNNER")).Count(c => c == "repo"));
    }

    [Fact]
    public async Task ARenewalTokenThatIsNotFineGrained_IsRefused_AndTheSavedTokenStays()
    {
        var parts = Build(secrets: ["ghp_abcdefghijklmnopqrstuvwxyz0123456789"]);
        parts.Api.Repo = new RepoInfo("main", TestTime.New().GetUtcNow().AddDays(2), IsPrivate: true);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Store.Calls.Clear();

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Empty(parts.Store.Calls);
        Assert.Equal(GoodToken, parts.Store.Load()!.Reveal());
        Assert.Contains("not a fine-grained token", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("saved token was kept", parts.Console.AllText, StringComparison.Ordinal);

        // GitHub was asked once about the saved token and never about the typed one (the test check asks once more, later).
        var calls = parts.Api.Calls.ToList();
        Assert.Equal(1, calls.Take(calls.IndexOf("set LINUX_RUNNER")).Count(c => c == "repo"));
    }

    [Fact]
    public async Task ASavedClassicToken_IsReportedAsNoLongerUsable_AndANewFineGrainedOneIsAsked()
    {
        var parts = Build(secrets: [GoodToken]);
        parts.Store.Save(new SecretText("ghp_abcdefghijklmnopqrstuvwxyz0123456789"));
        parts.Api.RepoFailure = new GitHubApiException("GitHub says this token is a classic token (its answers list scopes).", 403, isTransient: false) { IsClassicToken = true };
        parts.Api.RepoCallsBeforeFailure = 0;
        parts.Api.RepoFailureEndsAfter = 1;

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Done, outcome);
        Assert.Contains("classic token", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Equal(GoodToken, parts.Store.Load()!.Reveal());
    }

    // ---- red-team M5: only a private repository

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ARepositoryThatIsNotKnownToBePrivate_StopsSetup_BeforeAnythingSendsChecksToThisPc(bool? isPrivate)
    {
        var parts = Build();
        parts.Api.Repo = new RepoInfo("main", null, isPrivate);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.False(parts.Wizard.ProvenOnThisPc);
        Assert.Empty(parts.Api.Variables);
        Assert.DoesNotContain(parts.Api.Calls, c => c.StartsWith("set ", StringComparison.Ordinal));
        Assert.DoesNotContain(parts.Api.Calls, c => c.StartsWith("dispatch", StringComparison.Ordinal));
        Assert.Contains(isPrivate is false ? "is not private" : "did not say whether the repository", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("Nothing was set to send checks to this PC", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(parts.Scheduler.Calls, c => c.StartsWith("install", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASavedTokenOfAPublicRepository_StopsSetupToo()
    {
        var parts = Build(secrets: []);
        parts.Store.Save(new SecretText(GoodToken));
        parts.Api.Repo = new RepoInfo("main", null, IsPrivate: false);

        var outcome = await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Empty(parts.Api.Variables);
        Assert.Contains("is not private", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivateRepository_IsRecordedForTheStatus()
    {
        var parts = Build();

        await parts.Wizard.RunAsync(CancellationToken.None);

        Assert.True(parts.State.Load().RepositoryPrivate);
        Assert.NotNull(parts.State.Load().RepositoryCheckedAt);
    }

    [Fact]
    public void TheTokenInstructions_NameTheRepositoryTheShortExpiryThePermissionsAndWhatTheTokenCanDo()
    {
        var text = string.Join("\n", TokenGuide.Steps);

        Assert.Contains("UponAnonymous/dbd-presence", text, StringComparison.Ordinal);
        Assert.Contains("30 days", text, StringComparison.Ordinal);
        Assert.DoesNotContain("90 days", text, StringComparison.Ordinal);
        Assert.Contains("Administration = Read and write", text, StringComparison.Ordinal);
        Assert.Contains("Variables = Read and write", text, StringComparison.Ordinal);
        Assert.Contains("Actions = Read and write", text, StringComparison.Ordinal);
        Assert.Contains("Metadata = Read-only", text, StringComparison.Ordinal);
        Assert.Contains("Only select repositories", text, StringComparison.Ordinal);
        Assert.Contains("change that repository's settings", text, StringComparison.Ordinal);
        Assert.Contains("LINUX_RUNNER", text, StringComparison.Ordinal);
    }
}

public sealed class UninstallTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private sealed record Parts(
        Uninstaller Uninstaller,
        FakeConsole Console,
        List<string> Journal,
        FakeGitHubApi Api,
        FakeInstaller Installer,
        FakeWsl Wsl,
        FakeWslAccount Account,
        FakeSecretStore Store);

    private Parts Build(bool[]? answers = null, bool hasToken = true)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var journal = new List<string>();
        var console = new FakeConsole(answers: answers ?? [true]);
        var api = new FakeGitHubApi();
        api.Calls.Journal = journal;
        api.Runners.Add(new RunnerInfo(5, "dbd-linux-20261029120000", "online", false));
        api.Runners.Add(new RunnerInfo(6, "someone-elses-runner", "online", false));
        var scheduler = new FakeScheduler { TaskExists = true };
        scheduler.Calls.Journal = journal;
        scheduler.Calls.Prefix = "task ";
        var store = new FakeSecretStore();
        if (hasToken)
        {
            store.Save(new SecretText("x"));
        }

        store.Calls.Clear();
        store.Calls.Journal = journal;
        store.Calls.Prefix = "token ";
        var wsl = new FakeWsl();
        wsl.Calls.Journal = journal;
        wsl.Calls.Prefix = "wsl ";
        var account = new FakeWslAccount();
        account.Calls.Journal = journal;
        account.Calls.Prefix = "account ";
        var settings = new FakeWslSettingsFile();
        settings.Calls.Journal = journal;
        settings.Calls.Prefix = "settings ";
        var host = new FakeJobHost();
        host.Calls.Journal = journal;
        Directory.CreateDirectory(_folder.Paths.RunnerCache);
        File.WriteAllText(Path.Combine(_folder.Paths.RunnerCache, "x"), "x");
        File.WriteAllText(Path.Combine(_folder.Paths.Machine, "state.json"), "{}");
        var installer = new FakeInstaller { IsInstalled = true };
        installer.Calls.Journal = journal;
        installer.Calls.Prefix = "program ";
        var sandbox = Sandboxes.New(wsl, account, settings, log, redactor, TestTime.New());
        var uninstaller = new Uninstaller(console, api, scheduler, host, wsl, account, sandbox, store, installer, _folder.Paths, log, redactor);
        return new Parts(uninstaller, console, journal, api, installer, wsl, account, store);
    }

    [Fact]
    public async Task WhenTheOwnerSaysNo_NothingHappens_AndItIsNotDone()
    {
        var parts = Build(answers: [false]);

        var confirmed = await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.False(confirmed);
        Assert.False(parts.Uninstaller.Completed);
        Assert.Empty(parts.Journal);
        Assert.Single(parts.Console.Questions);
        Assert.True(Directory.Exists(_folder.Paths.RunnerCache));
        Assert.True(parts.Installer.IsInstalled);
    }

    [Fact]
    public async Task TheVariableIsDeletedFirst_ThenTheTask_TheRunners_TheLeftovers_EveryLinuxMachineOfTheAccount_TheAccount_TheCaches_TheTokenLast_AndThenTheInstalledProgram()
    {
        var parts = Build();
        parts.Wsl.Distributions.Add("dbd-ci-job-20261029120000");
        parts.Wsl.Distributions.Add("Ubuntu-in-the-account");

        var confirmed = await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.True(confirmed);
        Assert.True(parts.Uninstaller.Completed);
        Assert.Equal(
            [
                "delete-variable LINUX_RUNNER",
                "task stop",
                "task remove",
                "delete-runner 5",
                "cleanup leftovers",
                "account clear-lockout",
                "wsl shutdown",
                "account end-programs",
                "settings write",
                "settings hold",
                "wsl terminate dbd-ci-job-20261029120000",
                "wsl unregister dbd-ci-job-20261029120000",
                "wsl terminate Ubuntu-in-the-account",
                "wsl unregister Ubuntu-in-the-account",
                "wsl shutdown",
                "account end-programs",
                "settings release",
                "account remove",
                "token delete",
                "program remove",
            ],
            parts.Journal);
        Assert.False(Directory.Exists(_folder.Paths.Machine));
    }

    [Fact]
    public async Task OnlyExactRunnerNamesAreRemoved_NotAnOwnersOwnRunner_NotANearMiss()
    {
        var parts = Build();
        parts.Api.Runners.Add(new RunnerInfo(7, "dbd-presence-build-box", "online", false));
        parts.Api.Runners.Add(new RunnerInfo(8, "dbd-linux-1", "online", false));
        parts.Api.Runners.Add(new RunnerInfo(9, "dbd-linux-202610291200000", "online", false));
        parts.Api.Runners.Add(new RunnerInfo(10, "xdbd-linux-20261029120000", "online", false));
        parts.Api.Runners.Add(new RunnerInfo(11, "dbd-linux-20261029120001", "offline", false));

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.Equal([5L, 11L], parts.Api.DeletedRunners.Order().ToArray());
    }

    [Fact]
    public async Task EveryLinuxMachineOfTheAccountIsRemoved_WhateverItIsCalled_BecauseNobodyElseCanHaveOneThere()
    {
        var parts = Build();
        foreach (var name in new[] { "Ubuntu", "dbd-ci-job-1", "mine" })
        {
            parts.Wsl.Distributions.Add(name);
        }

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.Empty(parts.Wsl.Distributions);
        Assert.True(parts.Uninstaller.Completed);
    }

    [Fact]
    public async Task TheAccountIsRemovedOnPurpose_ExactlyOnce_AfterItsLinuxMachinesAndTheirSessionAreGone()
    {
        var parts = Build();
        parts.Wsl.Distributions.Add("dbd-ci-job-20261029120000");

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.Single(parts.Journal, j => j == "account remove");
        var removeAt = parts.Journal.IndexOf("account remove");
        Assert.True(removeAt > parts.Journal.IndexOf("wsl unregister dbd-ci-job-20261029120000"));
        Assert.True(removeAt > parts.Journal.LastIndexOf("wsl shutdown"));
        Assert.True(removeAt > parts.Journal.IndexOf("settings release"));
    }

    [Fact]
    public async Task TheLogFolderStays_AndTheOwnerIsToldWhereItIs()
    {
        var parts = Build();
        Directory.CreateDirectory(_folder.Paths.LogDirectory);
        File.WriteAllText(Path.Combine(_folder.Paths.LogDirectory, "localci-2026-10-29.log"), "line");

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_folder.Paths.LogDirectory, "localci-2026-10-29.log")));
        Assert.False(Directory.Exists(_folder.Paths.RunnerCache));
        Assert.False(File.Exists(Path.Combine(_folder.Paths.Machine, "state.json")));
        Assert.Contains("log files stay", parts.Console.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WINDOWS_RUNNER", "dbd-windows")]
    [InlineData("SECRETS_LINUX_RUNNER", "dbd-linux-secrets")]
    [InlineData("SECRETS_WINDOWS_RUNNER", "dbd-windows")]
    public async Task VariablesThisProgramNeverSets_AreNeverTouchedByUninstall_WhateverTheyHold(string variable, string value)
    {
        var parts = Build();
        parts.Api.Variables[variable] = value;

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.DoesNotContain("delete-variable " + variable, parts.Journal);
        Assert.Equal(value, parts.Api.Variables[variable]);
    }

    [Theory]
    [InlineData("dbd-linux", true)]
    [InlineData("my-own-linux-machine", false)]
    public async Task TheVariableThisProgramSet_IsDeletedOnlyWhileItStillHoldsThisProgramsLabel(string value, bool deleted)
    {
        var parts = Build();
        parts.Api.Variables["LINUX_RUNNER"] = value;

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.Equal(deleted, parts.Journal.Contains("delete-variable LINUX_RUNNER"));
        Assert.True(parts.Uninstaller.Completed, "a variable that points somewhere else is not this program's to delete; nothing is left for the owner to do");
        if (!deleted)
        {
            Assert.Contains("left alone", parts.Console.AllText, StringComparison.Ordinal);
            Assert.Equal(value, parts.Api.Variables["LINUX_RUNNER"]);
        }
    }

    [Fact]
    public async Task WithoutAToken_TheOwnerIsToldWhichVariableToDeleteByHand_TheRestStillHappens_AndItIsNotFullyDone()
    {
        var parts = Build(hasToken: false);

        var confirmed = await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.True(confirmed);
        Assert.False(parts.Uninstaller.Completed);
        Assert.Contains("LINUX_RUNNER", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("NOT FULLY DONE", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(parts.Journal, j => j.StartsWith("delete-variable", StringComparison.Ordinal));
        Assert.Contains("task remove", parts.Journal);
        Assert.Contains("account remove", parts.Journal);
    }

    /// <summary>QA T4 / B3: an expired token cannot delete the variable. Nothing else is removed, the token and the program stay for a retry, and the owner is told exactly what to do by hand.</summary>
    [Fact]
    public async Task WhenTheVariableCannotBeDeleted_NothingElseIsRemoved_TheTokenAndProgramStay_AndTheOwnerIsToldWhatToDoByHand()
    {
        var parts = Build(answers: [true, false]);
        parts.Api.DeleteVariableFailure = new GitHubApiException("GitHub refused the token (wrong, expired or revoked).", 401, isTransient: false);

        var confirmed = await parts.Uninstaller.RunAsync(CancellationToken.None);

        var text = parts.Console.AllText;
        Assert.True(confirmed);
        Assert.False(parts.Uninstaller.Completed);
        Assert.True(parts.Store.Exists(), "the token was deleted although the variable could not be removed, so a retry is impossible");
        Assert.True(parts.Installer.IsInstalled);
        Assert.Contains("NOT DONE", text, StringComparison.Ordinal);
        Assert.Contains("Delete it on GitHub by hand now: Settings, Secrets and variables, Actions, Variables", text, StringComparison.Ordinal);
        Assert.Contains("LINUX_RUNNER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("task stop", parts.Journal);
        Assert.DoesNotContain("account remove", parts.Journal);
        Assert.DoesNotContain("token delete", parts.Journal);
        Assert.Equal(2, parts.Console.Questions.Count);
    }

    /// <summary>
    /// QA M-2 (ADV16): the owner says "yes, I deleted it by hand". The rest is removed, but the program could not check it (the token it would ask with is the one GitHub refused), so the end says so
    /// honestly: not "turned off and removed", not completed, and the program exits with an error.
    /// </summary>
    [Fact]
    public async Task WhenTheOwnerSaysTheVariableWasDeletedByHand_TheRestIsRemoved_ButTheEndSaysItWasNotChecked_AndIsNotDone()
    {
        var parts = Build(answers: [true, true]);
        parts.Api.DeleteVariableFailure = new GitHubApiException("GitHub refused the token (wrong, expired or revoked).", 401, isTransient: false);

        var confirmed = await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.True(confirmed);
        Assert.Contains("account remove", parts.Journal);
        Assert.Contains("token delete", parts.Journal);
        Assert.False(parts.Installer.IsInstalled);
        Assert.False(parts.Uninstaller.Completed);
        Assert.Equal(1, ExitCodes.ForUninstall(parts.Uninstaller.Completed));
        var end = parts.Console.Lines[^1];
        Assert.Contains("NOT FULLY DONE", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("you said you deleted it by hand; this program could not delete it itself, so it cannot confirm that it is gone", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("LINUX_RUNNER", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("is turned off and removed", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("is turned off and removed", end, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheOwnerSaysNoToTheByHandQuestion_NothingElseChanges_AndItIsNotDone()
    {
        var parts = Build(answers: [true, false]);
        parts.Api.DeleteVariableFailure = new GitHubApiException("GitHub refused the token (wrong, expired or revoked).", 401, isTransient: false);

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.False(parts.Uninstaller.Completed);
        Assert.Contains("The token and the program were kept", parts.Console.AllText, StringComparison.Ordinal);
        Assert.True(parts.Installer.IsInstalled);
        Assert.Equal(1, ExitCodes.ForUninstall(parts.Uninstaller.Completed));
    }

    /// <summary>The by-hand answer is only asked for when a variable could not be deleted: an uninstall that works ends with the plain success line and exit code 0.</summary>
    [Fact]
    public async Task AnUninstallThatWorks_EndsWithTheSuccessLine_AndExitCodeZero()
    {
        var parts = Build();

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.True(parts.Uninstaller.Completed);
        Assert.Equal(0, ExitCodes.ForUninstall(parts.Uninstaller.Completed));
        Assert.Contains("DBD Local CI is turned off and removed.", parts.Console.Lines[^1], StringComparison.Ordinal);
        Assert.Single(parts.Console.Questions);
    }

    [Fact]
    public async Task AStepThatFails_IsSaidAloud_TheNextStepsStillRun_ButTheEndIsNotFullyDone()
    {
        var parts = Build();
        parts.Account.RemoveFailure = new InvalidOperationException("the account dbd-ci-wsl is only partly removed. Not done: delete the profile of the account");

        var confirmed = await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.True(confirmed);
        Assert.False(parts.Uninstaller.Completed);
        Assert.Contains("only partly removed", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("NOT FULLY DONE", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("token delete", parts.Journal);
        Assert.Equal("program remove", parts.Journal[^1]);
        Assert.False(parts.Installer.IsInstalled);
    }

    /// <summary>QA T13: a step that fails with an exception type nobody listed (a program that cannot be started, a damaged answer, a timeout) must not stop the rest.</summary>
    [Theory]
    [InlineData("win32")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task AFailingStepOfAnyKind_IsSaidAloud_AndTheRestStillRuns(string kind)
    {
        var parts = Build();
        parts.Wsl.Distributions.Add("dbd-ci-job-20261029120000");
        parts.Wsl.UnregisterFailure = kind switch
        {
            "win32" => new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"),
            "json" => new System.Text.Json.JsonException("bad"),
            _ => new TimeoutException("too slow"),
        };

        var ex = await Record.ExceptionAsync(() => parts.Uninstaller.RunAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.False(parts.Installer.IsInstalled);
        Assert.False(parts.Store.Exists());
        Assert.False(parts.Uninstaller.Completed);
        Assert.Contains("account remove", parts.Journal);
    }

    /// <summary>
    /// QA M-7 (ADV8, U6): the exception is thrown by the step itself and not swallowed deeper (the unregister failure above is caught inside the sandbox before the uninstaller sees it), so this
    /// proves the uninstaller's own handling: an exception of a kind nobody listed is said aloud and the later steps still run.
    /// </summary>
    [Theory]
    [InlineData("win32")]
    [InlineData("json")]
    [InlineData("timeout")]
    [InlineData("argument")]
    [InlineData("unauthorized")]
    public async Task AStepThatThrowsAnyKindItself_IsSaidAloud_AndTheRestStillRuns(string kind)
    {
        var parts = Build();
        parts.Account.RemoveFailure = kind switch
        {
            "win32" => new System.ComponentModel.Win32Exception(5, "Access is denied"),
            "json" => new System.Text.Json.JsonException("bad"),
            "timeout" => new TimeoutException("too slow"),
            "argument" => new ArgumentException("bad name"),
            _ => new UnauthorizedAccessException("not allowed"),
        };

        var ex = await Record.ExceptionAsync(() => parts.Uninstaller.RunAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.False(parts.Installer.IsInstalled);
        Assert.False(parts.Store.Exists());
        Assert.False(parts.Uninstaller.Completed);
        Assert.Contains("NOT FULLY DONE", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("could not remove the Windows account dbd-ci-wsl", parts.Console.AllText, StringComparison.Ordinal);
        Assert.Equal("program remove", parts.Journal[^1]);
    }

    [Fact]
    public async Task TheOwnersCancel_IsNotTurnedIntoAFailedStep()
    {
        var parts = Build();
        using var cts = new CancellationTokenSource();
        parts.Account.RemoveFailure = new OperationCanceledException(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parts.Uninstaller.RunAsync(cts.Token));
    }

    /// <summary>Exact names, the other direction: the persistent work folder of the account is not "a folder moved aside"; a folder moved aside is reported.</summary>
    [Fact]
    public async Task FoldersMovedAside_AreReported_ButTheAccountsOwnWorkFolderIsNot()
    {
        var parts = Build();
        Directory.CreateDirectory(_folder.Paths.WslWorkFolder);
        Directory.CreateDirectory(_folder.Paths.WslWorkFolder + ".untrusted-20261029120000");

        await parts.Uninstaller.RunAsync(CancellationToken.None);

        Assert.Contains("DbdLocalCi-wsl.untrusted-20261029120000", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("moved aside earlier because they were not closed to ordinary users (look at them, then delete them yourself): DbdLocalCi-wsl,", parts.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("DbdLocalCi-wsl, ", parts.Console.AllText, StringComparison.Ordinal);
    }
}
