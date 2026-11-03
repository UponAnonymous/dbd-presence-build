// Covers: FR-CI-02 (every job runs in a fresh throwaway environment: a new WSL distribution per Linux job, with its own name, inside a session of the dedicated account's own Linux machine that is
// shut down before and after), FR-CI-06 (the runner configuration travels on standard input, never on a command line or in a script), FR-CI-09 (the Linux environment is checked before the runner starts
// and the job stops when the check fails; the base image and the runner package are hashed again before every use; everything is removed afterwards, also after an error, a timeout or a cancel; what cannot
// be removed is recorded and no job starts over it), red-team F2, L1, L2, QA M6, round 3 H1 (the one step that runs as root closes user namespaces before the isolation check; the runner starts
// through setpriv), M3 (each job's machine reports a boot id that must differ from the last job's; the id and kernel are remembered), L2 (every step has a time limit), L5 (a leftover that was removed
// after all is taken off the list), M1 (the room left on the drive that holds the Linux machines is looked at every 5 seconds while a job runs; below 3 GB the job is stopped, its machine removed and
// the owner told in plain words). docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class LinuxJobHostTests : IDisposable
{
    private const string Runner = "dbd-linux-20261029120000";
    private const string Machine = "dbd-ci-job-20261029120000";

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly Redactor _redactor = new();
    private readonly MemoryCiLog _log;
    private readonly FakeProtectedStorage _storage = new();
    private readonly IntegrityStore _integrity;
    private readonly FakeWsl _wsl = new();
    private readonly FakeWslAccount _account = new();
    private readonly FakeWslSettingsFile _settings = new();
    private readonly FakeDisk _disk = new();
    private readonly FakeMachineRecord _record = new();
    private readonly StateStore _state;
    private readonly List<string> _journal = [];

    public LinuxJobHostTests()
    {
        _log = new MemoryCiLog(_redactor);
        _integrity = new IntegrityStore(_folder.Paths.IntegrityFile, _folder.Paths.Machine);
        _state = AcceptedProbe.In(new StateStore(_folder.Paths.StateFile));
        _wsl.Calls.Journal = _journal;
        _wsl.Calls.Prefix = "wsl ";
        _settings.Calls.Journal = _journal;
        _settings.Calls.Prefix = "settings ";
    }

    public void Dispose() => _folder.Dispose();

    private string Cached(string name, byte[] content)
    {
        var path = Path.Combine(_folder.Paths.RunnerCache, "2.338.0", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        _integrity.Record(path, Convert.ToHexStringLower(SHA256.HashData(content)));
        return path;
    }

    private string MakeLinuxPackage() => Cached("actions-runner-linux-x64-2.338.0.tar.gz", Encoding.UTF8.GetBytes("package"));

    private LinuxJobHost NewHost(bool withBaseImage = true, IProbeMarker? marker = null)
    {
        var paths = _folder.Paths;
        if (withBaseImage)
        {
            Directory.CreateDirectory(paths.BaseDirectory);
            File.WriteAllText(paths.BaseArchive, "base");
            _integrity.Record(paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("base"))));
        }

        Directory.CreateDirectory(paths.WslDistrosFolder);
        var residue = new ResidueRecorder(_state, marker, _log);
        var sandbox = Sandboxes.New(_wsl, _account, _settings, _log, _redactor, _time, _record, residue: residue);
        return new LinuxJobHost(_wsl, sandbox, paths, _log, _redactor, _integrity, _storage, _disk, residue, _time, AcceptedProbe.Gate(_state));
    }

    private static RunnerLaunch Launch(string package, string name = Runner, string config = "SECRET-CONFIG") => new(name, new SecretText(config), package);

    [Fact]
    public void TheDistributionNameFollowsTheRunnerNamesTimestamp()
    {
        Assert.Equal(Machine, LinuxJobHost.DistributionFor(Runner));
    }

    [Fact]
    public void LinuxIsNotReady_UntilTheBaseImageExistsAndIsRecorded()
    {
        var host = NewHost(withBaseImage: false);

        Assert.Contains("not been built", host.NotReadyReason(), StringComparison.Ordinal);

        Directory.CreateDirectory(_folder.Paths.BaseDirectory);
        File.WriteAllText(_folder.Paths.BaseArchive, "planted without a checksum");
        Assert.NotNull(host.NotReadyReason());
    }

    [Fact]
    public void LinuxIsNotReady_WhenTheBaseImageCannotBeTrusted()
    {
        var host = NewHost();
        Assert.Null(host.NotReadyReason());

        _storage.PathProblems[_folder.Paths.BaseDirectory] = "it is owned by an ordinary user";

        Assert.Contains("cannot be trusted", host.NotReadyReason(), StringComparison.Ordinal);
    }

    /// <summary>QA T16: a base image that no longer matches its record is noticed before a runner is registered.</summary>
    [Fact]
    public void LinuxIsNotReady_WhenTheBaseImageNoLongerMatchesItsRecord()
    {
        var host = NewHost();
        File.WriteAllText(_folder.Paths.BaseArchive, "the new image");

        Assert.Contains("does not match the checksum", host.NotReadyReason(), StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxIsNotReady_WhenTheAccountIsNotAsItMustBe_AndSaysWhy()
    {
        var host = NewHost();
        _account.Problems.Add("the account dbd-ci-wsl is switched off");

        var reason = host.NotReadyReason();

        Assert.Contains("dbd-ci-wsl", reason, StringComparison.Ordinal);
        Assert.Contains("switched off", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxIsNotReady_WhenTheDriveHoldingTheMachinesIsNearlyFull()
    {
        var host = NewHost();
        _disk.Free = 4L * 1024 * 1024 * 1024;

        Assert.Contains("less than 8 GB", host.NotReadyReason(), StringComparison.Ordinal);
        _disk.Free = 6L * 1024 * 1024 * 1024;
        Assert.Contains("less than 8 GB", host.NotReadyReason(), StringComparison.Ordinal);
        _disk.Free = 9L * 1024 * 1024 * 1024;
        Assert.Null(host.NotReadyReason());
    }

    [Fact]
    public async Task ALinuxJob_RunsInASessionOfTheAccount_ImportsAFreshDistribution_ChecksIt_UnpacksTheRunner_StartsItAsTheRunnerUser_AndRemovesEverything()
    {
        var host = NewHost();
        var package = MakeLinuxPackage();
        var configurationSeen = string.Empty;
        var packageSeen = Array.Empty<byte>();
        _wsl.OnExecute = async (_, user, script, input, _, token) =>
        {
            Assert.Equal("runner", user);
            using var copy = new MemoryStream();
            await input!.CopyToAsync(copy, token);
            if (script == LinuxJobHost.UnpackScript)
            {
                packageSeen = copy.ToArray();
            }
            else
            {
                configurationSeen = Encoding.UTF8.GetString(copy.ToArray());
            }

            return 0;
        };

        var exit = await host.RunAsync(Launch(package), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "wsl shutdown",
                "settings write",
                "settings hold",
                "wsl import " + Machine,
                "wsl exec " + Machine + " as root",
                "wsl exec " + Machine + " as runner",
                "wsl exec " + Machine + " as runner",
                "wsl exec " + Machine + " as runner",
                "wsl terminate " + Machine,
                "wsl unregister " + Machine,
                "wsl shutdown",
                "settings release",
            ],
            _journal);
        Assert.Equal(4, _wsl.ExecutedScripts.Count);
        Assert.Equal(WslHardening.Script, _wsl.ExecutedScripts[0]);
        Assert.Equal(WslPreflight.ScriptFor(Sandboxes.Facts, null), _wsl.ExecutedScripts[1]);
        Assert.Equal([LinuxJobHost.UnpackScript, LinuxJobHost.RunScript], _wsl.ExecutedScripts.Skip(2));

        // Round 3, L2: every step has its own time limit.
        Assert.Equal([WslLimits.Harden, WslLimits.Preflight, WslLimits.Unpack, WslLimits.Run], _wsl.ExecutedLimits);
        Assert.Equal(Encoding.UTF8.GetBytes("package"), packageSeen);
        Assert.Equal("SECRET-CONFIG\n", configurationSeen);
        Assert.Equal(Encoding.UTF8.GetBytes("base"), Assert.Single(_wsl.Imported));
        Assert.False(Directory.Exists(_folder.Paths.WslInstallDirectory(Machine)));
        Assert.Empty(_state.Load().Residue);
    }

    [Fact]
    public async Task TheRunnersConfiguration_IsInNoScript_AndNoCommandLine_OnlyInTheInputOfTheStartScript()
    {
        var host = NewHost();

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.DoesNotContain(_wsl.ExecutedScripts, s => s.Contains("SECRET-CONFIG", StringComparison.Ordinal));
        Assert.Contains("ACTIONS_RUNNER_INPUT_JITCONFIG", LinuxJobHost.RunScript, StringComparison.Ordinal);
        Assert.Contains("read -r ACTIONS_RUNNER_INPUT_JITCONFIG", LinuxJobHost.RunScript, StringComparison.Ordinal);
        Assert.DoesNotContain(_journal, j => j.Contains("SECRET-CONFIG", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("SECRET-CONFIG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAccountsSettingsAreHeldForTheWholeJob_AndLetGoAtTheEnd()
    {
        var host = NewHost();
        var heldDuringTheRun = false;
        _wsl.OnExecute = (_, _, script, _, _, _) =>
        {
            if (script == LinuxJobHost.RunScript)
            {
                heldDuringTheRun = _settings.Holds == 1 && _settings.Releases == 0;
            }

            return Task.FromResult(0);
        };

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.True(heldDuringTheRun);
        Assert.Equal(1, _settings.Releases);
        Assert.Equal(2, _wsl.Shutdowns);
    }

    [Fact]
    public async Task TheLinuxEnvironmentIsChecked_BeforeTheRunnerIsUnpacked_AndAFailedCheckStopsTheJobAndRemovesTheEnvironment()
    {
        _wsl.PreflightLines.Remove("CHECK no-sudo PASS");
        _wsl.PreflightLines.Add("CHECK no-sudo FAIL sudo works without a password");
        var host = NewHost();
        var started = false;
        _wsl.OnExecute = (_, _, _, _, _, _) =>
        {
            started = true;
            return Task.FromResult(0);
        };

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.False(started);
        Assert.Contains("no-sudo", ex.Message, StringComparison.Ordinal);
        Assert.Equal([WslHardening.Script, WslPreflight.ScriptFor(Sandboxes.Facts, null)], _wsl.ExecutedScripts);
        Assert.Empty(_wsl.Distributions);
        Assert.Equal("wsl shutdown", _journal[^2]);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("no-sudo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACheckThatDoesNotReport_OrAScriptThatFails_AlsoStopsTheJob()
    {
        var host = NewHost();
        _wsl.PreflightLines.Clear();
        await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));
        Assert.Equal(2, _wsl.ExecutedScripts.Count);

        _wsl.ExecutedScripts.Clear();
        _wsl.PreflightLines.AddRange(FakeWsl.CleanPreflightLines());
        _wsl.PreflightExit = 2;
        await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage(), "dbd-linux-20261029120001"), CancellationToken.None));
        Assert.Equal(2, _wsl.ExecutedScripts.Count);
    }

    // ---- round 3, H1: closing user namespaces as root, before the isolation check

    [Fact]
    public async Task OnlyTheHardeningStepRunsAsRoot_AndItRunsBeforeTheIsolationCheck()
    {
        var host = NewHost();

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        var executions = _journal.Where(j => j.StartsWith("wsl exec ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["wsl exec " + Machine + " as root", "wsl exec " + Machine + " as runner", "wsl exec " + Machine + " as runner", "wsl exec " + Machine + " as runner"], executions);
        Assert.Equal(WslHardening.Script, _wsl.ExecutedScripts[0]);
        Assert.Contains("max_user_namespaces", WslHardening.Script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHardeningStepThatFails_RefusesTheJob_BeforeTheIsolationCheckAndRemovesTheMachine()
    {
        _wsl.HardenExit = 1;
        _wsl.HardenLines.Clear();
        _wsl.HardenLines.Add("user namespaces could not be closed (limit is unknown)");
        var host = NewHost();

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Contains("User namespaces could not be closed", ex.Message, StringComparison.Ordinal);
        Assert.Equal([WslHardening.Script], _wsl.ExecutedScripts);
        Assert.Empty(_wsl.Distributions);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("could not be closed (limit is unknown)", StringComparison.Ordinal));
    }

    // ---- round 3, M3: each job's machine must report another boot id than the last job's

    [Fact]
    public async Task TheBootIdAndKernelOfEachMachine_AreRemembered_AndTheNextJobsCheckIsToldTheLastBootId()
    {
        var host = NewHost();

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.Equal("00000000-0000-4000-8000-000000000001", _record.LastBootId);
        Assert.Equal("5.15.167.4-microsoft-standard-WSL2", _record.KernelRelease);
        Assert.Equal(new DateTimeOffset(2024, 11, 5, 0, 21, 55, TimeSpan.Zero), _record.KernelBuilt);
        Assert.Contains("LAST_BOOT_ID='none'", _wsl.PreflightScripts[0], StringComparison.Ordinal);

        await host.RunAsync(Launch(MakeLinuxPackage(), "dbd-linux-20261029120001"), CancellationToken.None);

        Assert.Contains("LAST_BOOT_ID='00000000-0000-4000-8000-000000000001'", _wsl.PreflightScripts[1], StringComparison.Ordinal);
        Assert.Equal("00000000-0000-4000-8000-000000000002", _record.LastBootId);
        Assert.Equal(["00000000-0000-4000-8000-000000000001", "00000000-0000-4000-8000-000000000002"], _record.BootIds);
    }

    [Fact]
    public async Task TheLimitsOfThePcAreGivenToTheCheck_AsTheSettingsFileHasThem()
    {
        var host = NewHost();

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.StartsWith("CAP_MEM_KB=8388608\nCAP_CPUS=4\n", _wsl.PreflightScripts[0], StringComparison.Ordinal);
        Assert.Equal(WslPreflight.ScriptFor(Sandboxes.Facts, null), _wsl.PreflightScripts[0]);
    }

    [Fact]
    public async Task AMachineThatFailsTheFreshBootCheck_IsRefused_AndItsBootIdIsStillKnown()
    {
        _wsl.NewBootIdEachTime = false;
        _wsl.PreflightLines.RemoveAll(l => l.StartsWith("CHECK fresh-boot", StringComparison.Ordinal));
        _wsl.PreflightLines.Add("CHECK fresh-boot FAIL the machine has the boot id of the last job's machine, so it was not started fresh");
        var host = NewHost();

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Contains("fresh-boot", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not started fresh", ex.Message, StringComparison.Ordinal);
        Assert.Null(_record.LastBootId);
    }

    [Fact]
    public async Task TheBootIdIsRememberedEvenWhenAnotherCheckFails_BecauseThatMachineDidBoot()
    {
        _wsl.PreflightLines.RemoveAll(l => l.StartsWith("CHECK no-sudo", StringComparison.Ordinal));
        _wsl.PreflightLines.Add("CHECK no-sudo FAIL sudo works without a password");
        var host = NewHost();

        await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Equal("00000000-0000-4000-8000-000000000001", _record.LastBootId);
    }

    // ---- round 3, L5: a leftover that was removed after all is taken off the list

    [Fact]
    public async Task AnEarlierLeftoverThatIsRemovedNow_IsTakenOffTheList_WhileOthersStay()
    {
        var host = NewHost();
        const string Earlier = "dbd-ci-job-20261029110000";
        _wsl.Distributions.Add(Earlier);
        _state.Update(s => s.ResidueItems = [Left.Machine(Earlier), Left.Machine("dbd-ci-job-20261028090000")]);

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.Equal(["the Linux machine dbd-ci-job-20261028090000 could not be removed"], _state.Load().Residue);
    }

    [Fact]
    public async Task AJobsOwnFailedRemoval_IsListed_AndTakenOffAgainWhenTheNextJobRemovesIt()
    {
        var host = NewHost();
        _wsl.UnregisterFailure = new InvalidOperationException("in use");
        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);
        Assert.Contains(_state.Load().Residue, r => r.Contains(Machine, StringComparison.Ordinal));

        _wsl.UnregisterFailure = null;
        await host.RunAsync(Launch(MakeLinuxPackage(), "dbd-linux-20261029120001"), CancellationToken.None);

        Assert.Empty(_state.Load().Residue);
    }

    // ---- round 3, M1: the room left on the drive is watched while a job runs

    private const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>A run that goes on until it is cancelled, so the watchdog is what decides how it ends.</summary>
    private (Task<int> Running, TaskCompletionSource Reached) StartAHangingRun(CancellationToken cancellationToken = default)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wsl.OnExecute = async (_, _, script, input, _, token) =>
        {
            if (input is not null)
            {
                using var drain = new MemoryStream();
                await input.CopyToAsync(drain, token);
            }

            if (script == LinuxJobHost.RunScript)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return 0;
        };
        var host = NewHost();
        return (Task.Run(() => host.RunAsync(Launch(MakeLinuxPackage()), cancellationToken)), reached);
    }

    [Fact]
    public async Task AJobWhoseDriveStaysRoomy_IsNeverStopped_HoweverLongItRuns()
    {
        using var cts = new CancellationTokenSource();
        var (running, reached) = StartAHangingRun(cts.Token);
        await reached.Task.OrFail();

        _time.Advance(TimeSpan.FromHours(3));
        await Task.Yield();

        Assert.False(running.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
    }

    [Theory]
    [InlineData(3L * Gigabyte, false)]
    [InlineData(3L * Gigabyte - 1, true)]
    [InlineData(2L * Gigabyte, true)]
    [InlineData(0L, true)]
    public async Task TheJobIsStopped_WhenTheFreeRoomFallsBelowThreeGigabytes_AtItsExactEdge(long free, bool stops)
    {
        using var cts = new CancellationTokenSource();
        var (running, reached) = StartAHangingRun(cts.Token);
        await reached.Task.OrFail();
        _disk.Free = free;

        _time.Advance(LinuxJobHost.DiskCheckEvery);

        if (stops)
        {
            var ex = await Assert.ThrowsAsync<LowDiskException>(() => running.OrFail());
            Assert.Contains("less than 3 GB", ex.Message, StringComparison.Ordinal);
            Assert.Contains("stopped and its Linux machine removed", ex.Message, StringComparison.Ordinal);
            Assert.Contains(_folder.Paths.WslWorkFolder, ex.Message, StringComparison.Ordinal);
            Assert.Empty(_wsl.Distributions);
            Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("less than 3 GB", StringComparison.Ordinal));
            Assert.Equal("settings release", _journal[^1]);
        }
        else
        {
            await Task.Yield();
            Assert.False(running.IsCompleted);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        }
    }

    [Fact]
    public async Task TheRoomIsLookedAtEverySecond_NotOftenerAndNotRarer()
    {
        var (running, reached) = StartAHangingRun(TestContext.Current.CancellationToken);
        await reached.Task.OrFail();
        _disk.Free = Gigabyte;

        _time.Advance(LinuxJobHost.DiskCheckEvery - TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.False(running.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<LowDiskException>(() => running.OrFail());
        Assert.Equal(TimeSpan.FromSeconds(1), LinuxJobHost.DiskCheckEvery);
        Assert.Equal(3L * Gigabyte, LinuxJobHost.MinimumFreeBytesDuringAJob);
    }

    // ---- round 4, F15: the watch begins before the import and covers the import, the hardening and the isolation check

    [Fact]
    public async Task AnImportThatEatsTheRoom_IsStopped_BeforeAnythingElseOfTheJobRuns()
    {
        _wsl.OnImport = async token =>
        {
            _disk.Free = Gigabyte;
            _time.Advance(LinuxJobHost.DiskCheckEvery);
            await Task.Delay(Timeout.Infinite, token);
        };

        var host = NewHost();
        var running = Task.Run(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<LowDiskException>(() => running.OrFail());

        Assert.Contains("less than 3 GB", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_wsl.ExecutedScripts);
        Assert.Empty(_wsl.Distributions);
    }

    [Theory]
    [InlineData("harden")]
    [InlineData("preflight")]
    public async Task TheHardeningAndTheIsolationCheckThatEatTheRoom_AreStoppedToo_BeforeTheRunnerExists(string step)
    {
        _wsl.BeforeExecute = async (script, token) =>
        {
            var isStep = step == "harden" ? script == WslHardening.Script : script.EndsWith(WslPreflight.Script, StringComparison.Ordinal);
            if (isStep)
            {
                _disk.Free = Gigabyte;
                _time.Advance(LinuxJobHost.DiskCheckEvery);
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        var host = NewHost();
        var running = Task.Run(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<LowDiskException>(() => running.OrFail());

        Assert.DoesNotContain(LinuxJobHost.RunScript, _wsl.ExecutedScripts);
        Assert.DoesNotContain(LinuxJobHost.UnpackScript, _wsl.ExecutedScripts);
        Assert.Empty(_wsl.Distributions);
    }

    [Fact]
    public async Task WhatTheImportTookIsSaidInTheLog_ForTheFirstRunToMeasure()
    {
        _disk.Free = 20L * Gigabyte;
        _wsl.OnImport = _ =>
        {
            _disk.Free = 17L * Gigabyte + Gigabyte / 2;
            return Task.CompletedTask;
        };

        await NewHost().RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.Contains(_log.Lines, l => l.StartsWith("INFO", StringComparison.Ordinal) && l.Contains($"Free room on the drive that holds the Linux machines: 20.0 GB before the machine {Machine} was imported, 17.5 GB after.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadingOfTheRoomThatFailsAroundTheImport_IsSaid_AndTheJobGoesOn()
    {
        var failing = new FailingDisk { Failure = new IOException("the drive is not ready") };
        Directory.CreateDirectory(_folder.Paths.BaseDirectory);
        File.WriteAllText(_folder.Paths.BaseArchive, "base");
        _integrity.Record(_folder.Paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("base"))));
        Directory.CreateDirectory(_folder.Paths.WslDistrosFolder);
        var sandbox = Sandboxes.New(_wsl, _account, _settings, _log, _redactor, _time, _record);
        var host = new LinuxJobHost(_wsl, sandbox, _folder.Paths, _log, _redactor, _integrity, _storage, failing, new ResidueRecorder(_state), _time, AcceptedProbe.Gate(_state));

        var exit = await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(_log.Lines, l => l.StartsWith("INFO", StringComparison.Ordinal) && l.Contains($"Free room on the drive that holds the Linux machines: unknown (IOException: the drive is not ready) before the machine {Machine} was imported, unknown (IOException: the drive is not ready) after.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOwnersCancel_OfARunningJob_StaysACancel_EvenWhenTheDriveIsFullAtTheSameTime()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wsl.OnExecute = async (_, _, script, input, _, token) =>
        {
            if (input is not null)
            {
                using var drain = new MemoryStream();
                await input.CopyToAsync(drain, token);
            }

            if (script == LinuxJobHost.RunScript)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return 0;
        };
        using var cts = new CancellationTokenSource();
        var running = Task.Run(() => NewHost().RunAsync(Launch(MakeLinuxPackage()), cts.Token));
        await reached.Task.OrFail();

        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => running.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<LowDiskException>(ex);
        Assert.Empty(_wsl.Distributions);
    }

    [Fact]
    public async Task ALookAtTheDiskThatFails_IsLoggedOnce_AndTheJobGoesOn()
    {
        var failing = new FailingDisk();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wsl.OnExecute = async (_, _, script, input, _, token) =>
        {
            if (input is not null)
            {
                using var drain = new MemoryStream();
                await input.CopyToAsync(drain, token);
            }

            if (script == LinuxJobHost.RunScript)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return 0;
        };
        Directory.CreateDirectory(_folder.Paths.BaseDirectory);
        File.WriteAllText(_folder.Paths.BaseArchive, "base");
        _integrity.Record(_folder.Paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("base"))));
        Directory.CreateDirectory(_folder.Paths.WslDistrosFolder);
        var sandbox = Sandboxes.New(_wsl, _account, _settings, _log, _redactor, _time, _record);
        using var cts = new CancellationTokenSource();
        var host = new LinuxJobHost(_wsl, sandbox, _folder.Paths, _log, _redactor, _integrity, _storage, failing, new ResidueRecorder(_state), _time, AcceptedProbe.Gate(_state));
        var running = Task.Run(() => host.RunAsync(Launch(MakeLinuxPackage()), cts.Token));
        await reached.Task.OrFail();

        failing.Failure = new IOException("the drive is not ready");
        _time.Advance(LinuxJobHost.DiskCheckEvery);
        await Task.Yield();
        _time.Advance(LinuxJobHost.DiskCheckEvery);
        await Task.Yield();

        Assert.False(running.IsCompleted);
        Assert.Single(_log.Lines, l => l.Contains("Could not look at the free room", StringComparison.Ordinal));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
    }

    [Fact]
    public async Task AJobThatEndsOnItsOwn_StopsTheWatching_SoNothingFiresAfterwards()
    {
        var host = NewHost();

        var exit = await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);
        _disk.Free = 0;
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, exit);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("less than 3 GB", StringComparison.Ordinal));
    }

    private sealed class FailingDisk : IDiskSpace
    {
        public Exception? Failure { get; set; }

        public long FreeBytes(string path) => Failure is null ? 500L * 1024 * 1024 * 1024 : throw Failure;
    }

    [Fact]
    public async Task ABaseImageThatChangedSinceItWasChecked_IsNeverImported()
    {
        var host = NewHost();
        await File.WriteAllTextAsync(_folder.Paths.BaseArchive, "tampered", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IntegrityException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.DoesNotContain("import " + Machine, _wsl.Calls);
        Assert.Empty(_wsl.Distributions);
        Assert.Equal(2, _wsl.Shutdowns);
    }

    [Fact]
    public async Task ARunnerPackageThatChangedSinceItWasChecked_IsNeverUnpacked()
    {
        var host = NewHost();
        var package = MakeLinuxPackage();
        await File.WriteAllTextAsync(package, "swapped for another package", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IntegrityException>(() => host.RunAsync(Launch(package), CancellationToken.None));

        Assert.DoesNotContain(_wsl.ExecutedScripts, s => s == LinuxJobHost.UnpackScript);
        Assert.Empty(_wsl.Distributions);
    }

    [Fact]
    public async Task AnUnpackThatFails_StopsTheJob_BeforeTheRunnerStarts()
    {
        var host = NewHost();
        _wsl.OnExecute = (_, _, script, _, _, _) => Task.FromResult(script == LinuxJobHost.UnpackScript ? 2 : 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Contains("exit code 2", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_wsl.ExecutedScripts, s => s == LinuxJobHost.RunScript);
        Assert.Empty(_wsl.Distributions);
    }

    [Fact]
    public async Task TheRunnersOutput_IsCappedLineByLine_AndStopsAfterTheBudget()
    {
        var host = NewHost();
        _wsl.OnExecute = (_, _, script, _, onLine, _) =>
        {
            if (script == LinuxJobHost.RunScript)
            {
                onLine!("normal line");
                onLine("fake log line\n2099-01-01 00:00:00Z INFO forged \u001b[2J");
                for (var i = 0; i < LocalCiDefaults.RunnerOutputLinesKept + 20; i++)
                {
                    onLine("noise");
                }
            }

            return Task.FromResult(0);
        };

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        var runnerLines = _log.Lines.Where(l => l.Contains("runner: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(LocalCiDefaults.RunnerOutputLinesKept, runnerLines.Count);
        Assert.DoesNotContain(_log.Lines, l => l.Contains('\u001b', StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains('\n', StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("more than", StringComparison.Ordinal) && l.Contains("lines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALinuxJob_WhenTheRunnerCrashes_StillCleansUp_AndTheSessionStillEnds()
    {
        var host = NewHost();
        _wsl.OnExecute = (_, _, script, _, _, _) =>
            script == LinuxJobHost.RunScript ? throw new InvalidOperationException("boom") : Task.FromResult(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Equal(["wsl unregister " + Machine, "wsl shutdown", "settings release"], _journal.Skip(_journal.Count - 3));
        Assert.Empty(_wsl.Distributions);
        Assert.False(Directory.Exists(_folder.Paths.WslInstallDirectory(Machine)));
    }

    [Fact]
    public async Task ALinuxJob_WhenItIsCancelled_StopsTheRunnerAndStillCleansUp()
    {
        var host = NewHost();
        var reachedTheRunner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wsl.OnExecute = async (_, _, script, _, _, token) =>
        {
            if (script == LinuxJobHost.RunScript)
            {
                reachedTheRunner.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return 0;
        };
        using var cts = new CancellationTokenSource();
        var running = host.RunAsync(Launch(MakeLinuxPackage()), cts.Token);
        await reachedTheRunner.Task.OrFail();

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Contains("terminate " + Machine, _wsl.Calls);
        Assert.Equal("unregister " + Machine, _wsl.Calls[^2]);
        Assert.Equal("shutdown", _wsl.Calls[^1]);
        Assert.Empty(_wsl.Distributions);
    }

    [Fact]
    public async Task ALinuxJob_WhenTheImportFails_StillTriesToRemoveWhatWasStarted()
    {
        var host = NewHost();
        _wsl.ImportFailure = new InvalidOperationException("no disk");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Contains("unregister " + Machine, _wsl.Calls);
        Assert.False(Directory.Exists(_folder.Paths.WslInstallDirectory(Machine)));
        Assert.Equal("shutdown", _wsl.Calls[^1]);
    }

    [Fact]
    public async Task ALinuxJob_WhenAFirstCleanupStepFails_TheNextStepsStillRun_AndTheFailureIsLogged()
    {
        var host = NewHost();
        _wsl.TerminateFailure = new InvalidOperationException("stuck");

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.Contains("unregister " + Machine, _wsl.Calls);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("stuck", StringComparison.Ordinal));
        Assert.Empty(_state.Load().Residue);
    }

    /// <summary>Red-team L2: what cannot be removed is recorded for the status and the daily notice, not just logged.</summary>
    [Fact]
    public async Task AMachineThatCannotBeRemoved_IsRecordedAsALeftover_AndTheNextJobIsRefusedUntilItIsGone()
    {
        var host = NewHost();
        _wsl.UnregisterFailure = new InvalidOperationException("in use");

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        var left = Assert.Single(_state.Load().Residue);
        Assert.Contains(Machine, left, StringComparison.Ordinal);
        Assert.Contains("could not be removed", left, StringComparison.Ordinal);

        // The next job finds the leftover, and cannot remove it either: it does not start.
        _wsl.Calls.Clear();
        var next = await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage(), "dbd-linux-20261029130000"), CancellationToken.None));
        Assert.Contains("no new job was started", next.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("import dbd-ci-job-20261029130000", _wsl.Calls);

        // Once it can be removed, the job runs.
        _wsl.UnregisterFailure = null;
        var exit = await host.RunAsync(Launch(MakeLinuxPackage(), "dbd-linux-20261029130000"), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(Machine, _wsl.Distributions);
    }

    [Fact]
    public async Task AFolderThatIsNotEmptyAfterTheUnregister_IsRecordedAsALeftover_AndNeverDeletedFromHere()
    {
        var host = NewHost();
        _wsl.UnregisterLeavesFolder = true;

        await host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);

        Assert.Contains(_state.Load().Residue, r => r.Contains("is not empty", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_folder.Paths.WslInstallDirectory(Machine), "ext4.vhdx")));
    }

    [Fact]
    public async Task OnlyOneSessionExistsAtATime_ASecondJobIsRefusedWhileTheFirstRuns()
    {
        var host = NewHost();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wsl.OnExecute = async (_, _, script, _, _, _) =>
        {
            if (script == LinuxJobHost.RunScript)
            {
                reached.SetResult();
                await release.Task;
            }

            return 0;
        };
        var first = host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None);
        await reached.Task.OrFail();

        var second = await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage(), "dbd-linux-20261029130000"), CancellationToken.None));
        release.SetResult();
        await first;

        Assert.Contains("Another task is using the Linux machine", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheAccountIsNotAsItMustBe_NoJobStarts_AndNoWslCommandRuns()
    {
        var host = NewHost();
        _account.Problems.Add("the saved password of the account dbd-ci-wsl cannot be read (run set-up)");

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => host.RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.Contains("password", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_wsl.Calls);
        Assert.Empty(_settings.Writes);
    }

    // ---- the sweep

    [Fact]
    public async Task TheSweep_RemovesOnlyMachinesWithTheExactShapeOfThisProgramsOwn_AndLeavesEverythingElse()
    {
        var host = NewHost();
        foreach (var name in new[]
                 {
                     Machine, "dbd-ci-base", // the program's own
                     "Ubuntu", "dbd-ci-job-1", "dbd-ci-job-202610291200000", "xdbd-ci-job-20261029120000", "dbd-ci-base2", "dbd-ci-base-20261029120000", "dbd-ci-wsl", "dbd-linux-20261029120000",
                 })
        {
            _wsl.Distributions.Add(name);
        }

        await host.CleanupLeftoversAsync(CancellationToken.None);

        Assert.Equal(
            ["Ubuntu", "dbd-ci-base-20261029120000", "dbd-ci-base2", "dbd-ci-job-1", "dbd-ci-job-202610291200000", "dbd-ci-wsl", "dbd-linux-20261029120000", "xdbd-ci-job-20261029120000"],
            _wsl.Distributions.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(_state.Load().Residue);
    }

    [Fact]
    public async Task TheSweep_RemovesEmptyFoldersWithTheExactNames_AndRecordsOnesThatAreNotEmpty()
    {
        var host = NewHost();
        var empty = _folder.Paths.WslInstallDirectory("dbd-ci-job-20261029110000");
        var build = _folder.Paths.WslInstallDirectory("dbd-ci-base-20261029110000");
        var full = _folder.Paths.WslInstallDirectory("dbd-ci-job-20261029100000");
        var other = _folder.Paths.WslInstallDirectory("somebody-elses");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(build);
        Directory.CreateDirectory(full);
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(full, "ext4.vhdx"), "disk", TestContext.Current.CancellationToken);

        await host.CleanupLeftoversAsync(CancellationToken.None);

        Assert.False(Directory.Exists(empty));
        Assert.False(Directory.Exists(build));
        Assert.True(Directory.Exists(full));
        Assert.True(Directory.Exists(other));
        var left = Assert.Single(_state.Load().Residue);
        Assert.Contains("is not empty", left, StringComparison.Ordinal);
        Assert.Contains("dbd-ci-job-20261029100000", left, StringComparison.Ordinal);
    }

    /// <summary>Red-team L1: a listing that cannot be read is never an empty list; the sweep says it could not look.</summary>
    [Fact]
    public async Task WhenTheListingFails_TheSweepSaysItCouldNotLook_NeverThatNothingWasLeft()
    {
        var host = NewHost();
        _wsl.ListFailure = new InvalidOperationException("wsl said no");

        await host.CleanupLeftoversAsync(CancellationToken.None);

        var left = Assert.Single(_state.Load().Residue);
        Assert.Contains("could not be done", left, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("wsl said no", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACleanSweep_ClearsWhatAnEarlierSweepRecorded()
    {
        var host = NewHost();
        _state.Update(s => s.ResidueItems = [Left.Machine("dbd-ci-job-20260101000000")]);

        await host.CleanupLeftoversAsync(CancellationToken.None);

        Assert.Empty(_state.Load().Residue);
    }

    // ---- round 4, F7: the sweep keeps the entries about files on Windows and looks at the files again

    private const string EarlierMarker = "dbd-ci-probe-0123456789ab.txt";

    [Fact]
    public async Task TheSweep_KeepsTheEntryOfAMarkerFileThatIsStillOnWindows_WhateverItFindsOfMachinesAndFolders()
    {
        var marker = new FakeProbeMarker { Finding = ProbeMarkerFinding.FoundButNotRemoved };
        var host = NewHost(marker: marker);
        _state.Update(s => s.ResidueItems = [Left.Machine("dbd-ci-job-20260101000000"), Left.Marker(EarlierMarker)]);

        await host.CleanupLeftoversAsync(CancellationToken.None);

        var left = Assert.Single(_state.Load().ResidueItems);
        Assert.Equal((ResidueKinds.MarkerFile, EarlierMarker), (left.Kind, left.Subject));
        Assert.Equal([EarlierMarker], marker.Looked);
    }

    [Fact]
    public async Task TheSweep_KeepsTheEntryThatItsOwnSessionRecordedWhenItCouldNotEnd_SoTheOwnerIsToldAtOnce()
    {
        var host = NewHost();
        _wsl.OnList = () => _wsl.ShutdownFailure = new InvalidOperationException("stuck");

        await host.CleanupLeftoversAsync(CancellationToken.None);

        var left = Assert.Single(_state.Load().ResidueItems);
        Assert.Equal((ResidueKinds.Session, WslSandbox.SessionEndSubject), (left.Kind, left.Subject));
    }

    [Theory]
    [InlineData(ProbeMarkerFinding.NotThere, false)]
    [InlineData(ProbeMarkerFinding.FoundAndRemoved, true)]
    public async Task AMarkerFileThatIsGoneOrCanBeRemovedAtTheNextSweep_IsTakenOffTheList(ProbeMarkerFinding finding, bool removedNow)
    {
        var host = NewHost(marker: new FakeProbeMarker { Finding = finding });
        _state.Update(s => s.ResidueItems = [Left.Marker(EarlierMarker)]);

        await host.CleanupLeftoversAsync(CancellationToken.None);

        Assert.Empty(_state.Load().ResidueItems);
        Assert.Equal(removedNow, _log.Lines.Any(l => l.StartsWith("INFO", StringComparison.Ordinal) && l.Contains($"Removed the marker file {EarlierMarker}", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AMarkerFileThatCannotBeLookedAtAtTheNextSweep_StaysOnTheList_AndTheReasonIsLogged()
    {
        var host = NewHost(marker: new FakeProbeMarker { LookFailure = new IOException("the drive is not ready") });
        _state.Update(s => s.ResidueItems = [Left.Marker(EarlierMarker)]);

        var ex = await Record.ExceptionAsync(() => host.CleanupLeftoversAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.Equal(EarlierMarker, Assert.Single(_state.Load().ResidueItems).Subject);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("the drive is not ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSweep_StopsWhenTheLoopStops_ButNeverOtherwiseThrows()
    {
        var host = NewHost();
        _account.Problems.Add("the account dbd-ci-wsl does not exist (run set-up)");

        var ex = await Record.ExceptionAsync(() => host.CleanupLeftoversAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.Contains(_state.Load().Residue, r => r.Contains("could not be done", StringComparison.Ordinal));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _account.Problems.Clear();
        _wsl.ListFailure = new OperationCanceledException(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CleanupLeftoversAsync(cancelled.Token));
    }

    /// <summary>A step that is ended by something that is neither the owner nor the drive running low is not reported as a full drive.</summary>
    [Fact]
    public async Task ACancelThatIsNeitherTheOwnersNorTheDrives_IsNotTurnedIntoALowDiskStop()
    {
        _wsl.OnExecute = async (_, _, script, input, _, token) =>
        {
            if (input is not null)
            {
                using var drain = new MemoryStream();
                await input.CopyToAsync(drain, token);
            }

            if (script == LinuxJobHost.RunScript)
            {
                throw new OperationCanceledException("a step that something else ended");
            }

            return 0;
        };

        var ex = await Record.ExceptionAsync(() => NewHost().RunAsync(Launch(MakeLinuxPackage()), CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<LowDiskException>(ex);
        Assert.Empty(_wsl.Distributions);
    }
}
