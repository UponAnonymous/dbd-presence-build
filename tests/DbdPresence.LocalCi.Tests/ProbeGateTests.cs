// Covers: local CI round 4, red-team F5 and QA M-3 (no Linux check starts unless the isolation probe was accepted for the current setup and the WSL that is installed now: never run, not accepted,
// another setup, an unknown version and a changed version each refuse, with the reason in plain words; the job host asks before a runner is registered and again once the session of a job has read the
// WSL version; the weekly WSL update runs only with the owner's yes). docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class ProbeGateTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private StateStore State() => new(_folder.Paths.StateFile);

    // ---- the rule itself

    [Fact]
    public void AnAcceptedProbeForTheCurrentSetupAndWsl_Holds()
    {
        var state = AcceptedProbe.In(State());

        Assert.Null(ProbeValidity.Problem(state.Load(), AcceptedProbe.Recipe));
        Assert.Null(AcceptedProbe.Gate(state).Problem());
    }

    [Fact]
    public void WithoutAProbe_NothingStarts()
    {
        Assert.Equal("the isolation probe has not been run on this PC yet (choose 5 in DBD Local CI)", ProbeValidity.Problem(new CiState(), AcceptedProbe.Recipe));
    }

    [Fact]
    public void AProbeThatWasNotAccepted_IsNotEnough()
    {
        var state = AcceptedProbe.In(State(), accepted: false);

        Assert.Equal("you have not accepted the result of the isolation probe (choose 5 in DBD Local CI and decide)", AcceptedProbe.Gate(state).Problem());
    }

    [Fact]
    public void AProbeForAnotherSetup_SaysNothingAboutThisOne()
    {
        var state = AcceptedProbe.In(State());

        var problem = AcceptedProbe.Gate(state, recipe: "recipe-2").Problem();

        Assert.NotNull(problem);
        Assert.Contains("run for an older setup of the Linux machine", problem, StringComparison.Ordinal);
        Assert.Contains("run it again (choose 5 in DBD Local CI)", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "WSL version: 2.4.8.0")]
    [InlineData("", "WSL version: 2.4.8.0")]
    [InlineData("  ", "WSL version: 2.4.8.0")]
    [InlineData("WSL version: 2.4.8.0", null)]
    [InlineData("WSL version: 2.4.8.0", "")]
    [InlineData(null, null)]
    public void AWslVersionThatIsNotKnown_MeansTheProbeCannotBeShownToHold(string? probeVersion, string? installedVersion)
    {
        var state = AcceptedProbe.In(State(), probeVersion: probeVersion, installedVersion: installedVersion);

        var problem = AcceptedProbe.Gate(state).Problem();

        Assert.NotNull(problem);
        Assert.Contains("the WSL version is not known", problem, StringComparison.Ordinal);
        Assert.Contains("cannot be shown that the isolation probe you accepted still holds", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AWslThatChangedSinceTheProbe_RefusesAndNamesBothVersions()
    {
        var state = AcceptedProbe.In(State(), installedVersion: "WSL version: 2.5.1.0\nKernel version: 6.6.36.3-1");

        var problem = AcceptedProbe.Gate(state).Problem();

        Assert.Equal("WSL changed since the isolation probe you accepted (WSL version: 2.4.8.0 then, WSL version: 2.5.1.0 now); run the probe again (choose 5 in DBD Local CI)", problem);
    }

    [Fact]
    public void ADifferenceOnlyInTheKernelLine_IsAChange_AndTrailingSpaceIsNot()
    {
        var kernel = AcceptedProbe.In(State(), installedVersion: AcceptedProbe.Version.Replace("5.15.167.4-1", "5.15.167.5-1", StringComparison.Ordinal));
        Assert.Contains("WSL changed since the isolation probe", AcceptedProbe.Gate(kernel).Problem(), StringComparison.Ordinal);

        var spaced = AcceptedProbe.In(new StateStore(Path.Combine(_folder.Path, "ProgramData", "other.json")), installedVersion: "  " + AcceptedProbe.Version + "\n");
        Assert.Null(AcceptedProbe.Gate(spaced).Problem());
    }

    [Fact]
    public void TheFirstLine_OfAVersionTextOrOfNothing()
    {
        Assert.Equal("WSL version: 2.4.8.0", ProbeValidity.FirstLine(AcceptedProbe.Version));
        Assert.Equal("unknown", ProbeValidity.FirstLine(null));
        Assert.Equal("unknown", ProbeValidity.FirstLine("   "));
        Assert.Equal("a", ProbeValidity.FirstLine("  a  \nb"));
    }

    [Fact]
    public void BadArguments_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => ProbeValidity.Problem(null!, "x"));
        Assert.Throws<ArgumentException>(() => ProbeValidity.Problem(new CiState(), string.Empty));
    }

    // ---- the job host

    private const string Runner = "dbd-linux-20261029120000";

    private sealed class HostRig : IDisposable
    {
        private readonly TempFolder _folder = new();

        public HostRig(bool version = false)
        {
            var redactor = new Redactor();
            Log = new MemoryCiLog(redactor);
            var paths = _folder.Paths;
            Wsl = new FakeWsl();
            State = AcceptedProbe.In(new StateStore(paths.StateFile));
            var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
            Directory.CreateDirectory(paths.BaseDirectory);
            File.WriteAllText(paths.BaseArchive, "base");
            integrity.Record(paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("base"))));
            Directory.CreateDirectory(paths.WslDistrosFolder);
            var time = TestTime.New();
            IMachineRecord record = version ? new StateMachineRecord(State, time) : new FakeMachineRecord();
            var sandbox = Sandboxes.New(Wsl, new FakeWslAccount(), new FakeWslSettingsFile(), Log, redactor, time, record);
            Host = new LinuxJobHost(Wsl, sandbox, paths, Log, redactor, integrity, new FakeProtectedStorage(), new FakeDisk(), new ResidueRecorder(State), time, AcceptedProbe.Gate(State));
            var package = Path.Combine(paths.RunnerCache, "2.338.0", "actions-runner-linux-x64-2.338.0.tar.gz");
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            File.WriteAllBytes(package, Encoding.UTF8.GetBytes("package"));
            integrity.Record(package, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("package"))));
            Launch = new RunnerLaunch(Runner, new SecretText("CONFIG"), package);
        }

        public MemoryCiLog Log { get; }

        public FakeWsl Wsl { get; }

        public StateStore State { get; }

        public LinuxJobHost Host { get; }

        public RunnerLaunch Launch { get; }

        public void Dispose() => _folder.Dispose();
    }

    [Fact]
    public void TheHostIsReady_OnlyWhenTheProbeHolds()
    {
        using var rig = new HostRig();
        Assert.Null(rig.Host.NotReadyReason());

        AcceptedProbe.In(rig.State, accepted: false);
        Assert.Equal("no Linux check starts on this PC: you have not accepted the result of the isolation probe (choose 5 in DBD Local CI and decide)", rig.Host.NotReadyReason());

        rig.State.Update(s => s.Probe = null);
        Assert.Contains("has not been run on this PC yet", rig.Host.NotReadyReason(), StringComparison.Ordinal);

        AcceptedProbe.In(rig.State, installedVersion: "WSL version: 2.5.1.0");
        Assert.Contains("WSL changed since the isolation probe you accepted", rig.Host.NotReadyReason(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWslThatChangedBetweenTwoLooks_IsCaughtWhenTheSessionReadsItAgain_BeforeAnythingOfTheJobExists()
    {
        using var rig = new HostRig(version: true);
        Assert.Null(rig.Host.NotReadyReason());
        rig.Wsl.VersionText = "WSL version: 2.5.1.0\nKernel version: 6.6.36.3-1";

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => rig.Host.RunAsync(rig.Launch, CancellationToken.None));

        Assert.Contains("The isolation probe no longer allows a job: WSL changed since the isolation probe you accepted (WSL version: 2.4.8.0 then, WSL version: 2.5.1.0 now)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No job was started.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Wsl.Calls, c => c.StartsWith("import", StringComparison.Ordinal));
        Assert.Empty(rig.Wsl.ExecutedScripts);
        Assert.Equal(2, rig.Wsl.Shutdowns);
        Assert.Contains("WSL changed", rig.Host.NotReadyReason(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheProbeHolds_TheJobRunsAsBefore()
    {
        using var rig = new HostRig(version: true);

        var exit = await rig.Host.RunAsync(rig.Launch, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(rig.Wsl.Calls, c => c.StartsWith("import", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostIsNeverMadeWithoutAGate()
    {
        using var folder = new TempFolder();
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var sandbox = Sandboxes.New(new FakeWsl(), new FakeWslAccount(), new FakeWslSettingsFile(), log, redactor, TestTime.New());

        Assert.Throws<ArgumentNullException>(() => new LinuxJobHost(new FakeWsl(), sandbox, folder.Paths, log, redactor, new IntegrityStore(folder.Paths.IntegrityFile, folder.Paths.Machine), new FakeProtectedStorage(), new FakeDisk(), new ResidueRecorder(new StateStore(folder.Paths.StateFile)), TestTime.New(), null!));
    }

    // ---- the weekly update is the owner's choice

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, true, "the file no-wsl-update is in the data folder")]
    [InlineData(false, false, "you did not allow the weekly update at set-up (run set-up again to change that)")]
    [InlineData(false, true, "the file no-wsl-update is in the data folder")]
    [InlineData(null, false, "you have not been asked yet whether this program may update WSL (run set-up again)")]
    [InlineData(null, true, "the file no-wsl-update is in the data folder")]
    public void TheWeeklyUpdateRunsOnlyWithTheOwnersYes_AndTheSwitchFileStillTurnsItOff(bool? allowed, bool fileThere, string? reason)
    {
        Assert.Equal(reason, WslUpdatePolicy.OffBecause(allowed, fileThere));
    }

    [Fact]
    public async Task AnUpdateThatIsOffBecauseOfTheOwnersAnswer_IsNotRun_AndTheNoteSaysWhy()
    {
        using var rig = new MaintenanceRig(withWslUpdate: true);
        var update = new WeeklyWslUpdate(rig.Installer, rig.Processes, () => WslUpdatePolicy.OffBecause(false, false), rig.State, rig.Time, rig.Log, rig.Redactor);

        var result = await update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Skipped, result);
        Assert.Empty(rig.Installer.Calls);
        Assert.Equal("off: you did not allow the weekly update at set-up (run set-up again to change that), so WSL is not updated by this program", rig.State.Load().LastWslUpdateNote);
        Assert.Contains(rig.Log.Lines, l => l.Contains("WSL is not updated: the update is off (you did not allow the weekly update at set-up", StringComparison.Ordinal));
    }
}
