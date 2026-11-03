// Covers: local CI round 3, red-team H1 (the isolation probe: it imports the base image as the dedicated account into a throwaway machine inside a session, runs its fixed script as root there with a
// time limit, removes the machine, and looks for its marker file on the Windows side whatever happened, removing that exact file; every attempt is BLOCKED, POSSIBLE or NOT TRIED with evidence; a probe
// that cannot run is a result, not a crash, and is never read as "closed"; what cannot be removed is listed as a leftover; the places the account may write to are listed first and a failure of that audit
// does not stop the probe). docs/owner/free-ci.md.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

/// <summary>The isolation probe built from fakes, with every part within reach of a test.</summary>
internal sealed class ProbeRig : IDisposable
{
    private readonly TempFolder _folder = new();

    public ProbeRig(bool withBaseImage = true, FakeConsole? console = null)
    {
        Time = TestTime.New();
        Redactor = new Redactor();
        Log = new MemoryCiLog(Redactor);
        Wsl = new FakeWsl();
        Wsl.Calls.Journal = Journal;
        Wsl.Calls.Prefix = "wsl ";
        Settings = new FakeWslSettingsFile();
        Settings.Calls.Journal = Journal;
        Settings.Calls.Prefix = "settings ";
        Account = new FakeWslAccount();
        State = new StateStore(_folder.Paths.StateFile);
        Integrity = new IntegrityStore(_folder.Paths.IntegrityFile, _folder.Paths.Machine);
        Residue = new ResidueRecorder(State);
        Marker = new FakeProbeMarker();
        Locations = new FakeWritableLocations();
        Sandbox = Sandboxes.New(Wsl, Account, Settings, Log, Redactor, Time);
        if (withBaseImage)
        {
            Directory.CreateDirectory(Paths.BaseDirectory);
            File.WriteAllText(Paths.BaseArchive, "base");
            Integrity.Record(Paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("base"))));
        }

        Directory.CreateDirectory(Paths.WslDistrosFolder);
        Console = console ?? new FakeConsole();
        Probe = new IsolationProbe(Wsl, Sandbox, Paths, Integrity, Marker, Locations, Residue, Log, Redactor, Time);
        Review = new ProbeReview(Probe, State, Console, () => "recipe-1", Time, Log);
    }

    public List<string> Journal { get; } = [];

    public FakeTimeProvider Time { get; }

    public Redactor Redactor { get; }

    public MemoryCiLog Log { get; }

    public FakeWsl Wsl { get; }

    public FakeWslSettingsFile Settings { get; }

    public FakeWslAccount Account { get; }

    public StateStore State { get; }

    public IntegrityStore Integrity { get; }

    public ResidueRecorder Residue { get; }

    public FakeProbeMarker Marker { get; }

    public FakeWritableLocations Locations { get; }

    public WslSandbox Sandbox { get; }

    public FakeConsole Console { get; }

    public IsolationProbe Probe { get; }

    public ProbeReview Review { get; }

    public LocalCiPaths Paths => _folder.Paths;

    public void Dispose() => _folder.Dispose();
}

public sealed class IsolationProbeTests : IDisposable
{
    private const string Machine = LocalCiDefaults.ProbeDistribution;

    private readonly ProbeRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task TheProbeImportsTheBaseImageAsAThrowawayMachine_RunsItsScriptAsRoot_AndRemovesEverything()
    {
        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Equal(
            ["wsl shutdown", "settings write", "settings hold", "wsl import " + Machine, "wsl exec " + Machine + " as root", "wsl terminate " + Machine, "wsl unregister " + Machine, "wsl shutdown", "settings release"],
            _rig.Journal);
        Assert.Equal(Encoding.UTF8.GetBytes("base"), Assert.Single(_rig.Wsl.Imported));
        Assert.Equal(["root"], _rig.Wsl.ProbeUsers);
        Assert.Empty(_rig.Wsl.Distributions);
        Assert.False(Directory.Exists(_rig.Paths.WslInstallDirectory(LocalCiDefaults.ProbeFolderFor(_rig.Time.GetUtcNow()))));
        Assert.True(report.AllBlocked);
        Assert.Equal("AllBlocked", report.Outcome);
        Assert.Null(report.Failure);
        Assert.Empty(_rig.State.Load().Residue);
    }

    [Fact]
    public async Task TheScriptThatRuns_IsTheFixedProbeScript_WithAMarkerNameInFront_AndAnOwnTimeLimit()
    {
        await _rig.Probe.RunAsync(CancellationToken.None);

        var script = Assert.Single(_rig.Wsl.ProbeScripts);
        var marker = Assert.Single(_rig.Marker.Names);
        Assert.Equal(IsolationProbe.ScriptFor(marker), script);
        Assert.EndsWith(IsolationProbe.Script, script, StringComparison.Ordinal);
        Assert.Contains(WslLimits.Probe, _rig.Wsl.ExecutedLimits);
        Assert.Equal(WslLimits.Probe, _rig.Wsl.ExecutedLimits[^1]);
    }

    [Fact]
    public async Task TheMarkerIsLookedForOnTheWindowsSide_AfterTheScript_ByItsExactName()
    {
        await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Equal(_rig.Marker.Names, _rig.Marker.Looked);
    }

    [Fact]
    public async Task AnAttemptThatWorked_IsPossible_AndTheOutcomeSaysSo()
    {
        _rig.Wsl.ProbeLines.RemoveAll(l => l.StartsWith("ATTEMPT gpu-device", StringComparison.Ordinal));
        _rig.Wsl.ProbeLines.Add("ATTEMPT gpu-device POSSIBLE the GPU device /dev/dxg exists");

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(report.AnythingPossible);
        Assert.False(report.AllBlocked);
        Assert.Equal("SomePossible", report.Outcome);
        Assert.Equal(ProbeVerdict.Possible, report.Attempt("gpu-device")!.Verdict);
        Assert.Equal("the GPU device /dev/dxg exists", report.Attempt("gpu-device")!.Evidence);
    }

    [Fact]
    public async Task TheMarkerSeenOnWindows_MakesTheWriteAttemptPossible_EvenWhenTheScriptSaidBlocked()
    {
        _rig.Marker.Finding = ProbeMarkerFinding.FoundAndRemoved;

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        var write = report.Attempt("write-windows-public")!;
        Assert.Equal(ProbeVerdict.Possible, write.Verdict);
        Assert.Contains("appeared in C:\\Users\\Public on Windows (seen from Windows, and removed)", write.Evidence, StringComparison.Ordinal);
        Assert.Contains(_rig.Marker.Names[0], write.Evidence, StringComparison.Ordinal);
        Assert.Equal("SomePossible", report.Outcome);
    }

    [Fact]
    public async Task AMarkerThatCannotBeRemoved_IsSaid_AndListedAsALeftoverForTheOwner()
    {
        _rig.Marker.Finding = ProbeMarkerFinding.FoundButNotRemoved;

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Contains("could not be removed (delete it by hand)", report.Attempt("write-windows-public")!.Evidence, StringComparison.Ordinal);
        Assert.Contains(_rig.State.Load().Residue, r => r.Contains(_rig.Marker.Names[0], StringComparison.Ordinal) && r.Contains("delete it by hand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALookForTheMarkerThatFails_IsLogged_AndListedAsALeftover_NeverHidden()
    {
        _rig.Marker.LookFailure = new IOException("the drive is not ready");

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(report.AllBlocked);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("the drive is not ready", StringComparison.Ordinal));
        Assert.Contains(_rig.State.Load().Residue, r => r.Contains("may be in C:\\Users\\Public", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProbeThatFailsHalfway_IsAFailureNotACrash_EveryAttemptIsNotTried_TheMarkerIsStillLookedFor_AndTheMachineIsRemoved()
    {
        _rig.Wsl.ProbeFailure = new InvalidOperationException("wsl.exe stopped answering");

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.StartsWith("The isolation probe could not run: ", report.Failure, StringComparison.Ordinal);
        Assert.Contains("wsl.exe stopped answering", report.Failure, StringComparison.Ordinal);
        Assert.All(report.Attempts, a => Assert.Equal(ProbeVerdict.NotTried, a.Verdict));
        Assert.Equal(IsolationProbe.AttemptIds, report.Attempts.Select(a => a.Id).ToArray());
        Assert.False(report.AllBlocked);
        Assert.Equal("Incomplete", report.Outcome);
        Assert.Equal(_rig.Marker.Names, _rig.Marker.Looked);
        Assert.Empty(_rig.Wsl.Distributions);
        Assert.Equal("wsl shutdown", _rig.Journal[^2]);
    }

    // ---- a script that printed every line and still did not end with 0 (local CI round 4, red-team F6)

    [Theory]
    [InlineData(1)]
    [InlineData(137)]
    public async Task AScriptThatPrintedEveryLineButEndedWithAnExitCode_IsIncomplete_NeverAllBlocked_AndWhatItPrintedIsKept(int exit)
    {
        _rig.Wsl.ProbeExit = exit;

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Contains($"ended with exit code {exit} instead of 0", report.Failure, StringComparison.Ordinal);
        Assert.False(report.AllBlocked);
        Assert.False(report.AttemptsAllBlocked);
        Assert.Equal("Incomplete", report.Outcome);
        Assert.All(report.Attempts, a => Assert.Equal(ProbeVerdict.Blocked, a.Verdict));
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains($"exit code {exit}", StringComparison.Ordinal));
        Assert.Empty(_rig.Wsl.Distributions);
        Assert.Equal(_rig.Marker.Names, _rig.Marker.Looked);
    }

    [Fact]
    public async Task AScriptThatFoundAWayOutAndThenEndedWithAnExitCode_KeepsWhatItFound()
    {
        _rig.Wsl.ProbeLines[Array.IndexOf(IsolationProbe.AttemptIds.ToArray(), "gpu-device")] = "ATTEMPT gpu-device POSSIBLE the GPU device /dev/dxg exists";
        _rig.Wsl.ProbeExit = 137;

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(report.AnythingPossible);
        Assert.Equal(ProbeVerdict.Possible, report.Attempt("gpu-device")!.Verdict);
        Assert.NotNull(report.Failure);
        Assert.Equal("Incomplete", report.Outcome);
    }

    [Fact]
    public async Task AScriptThatEndedWithAnExitCode_AndLeftItsMarkerOnWindows_StillShowsTheWriteAsPossible()
    {
        _rig.Wsl.ProbeExit = 2;
        _rig.Marker.Finding = ProbeMarkerFinding.FoundAndRemoved;

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.NotNull(report.Failure);
        Assert.Equal(ProbeVerdict.Possible, report.Attempt("write-windows-public")!.Verdict);
        Assert.Equal(ProbeVerdict.Blocked, report.Attempt("mount-windows-drive")!.Verdict);
    }

    [Fact]
    public async Task AFailedProbeThatLeftTheMarkerOnWindows_StillSaysSo_ForTheWriteAttempt()
    {
        _rig.Wsl.ProbeFailure = new InvalidOperationException("stopped");
        _rig.Marker.Finding = ProbeMarkerFinding.FoundAndRemoved;

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.NotNull(report.Failure);
        Assert.Equal(ProbeVerdict.Possible, report.Attempt("write-windows-public")!.Verdict);
        Assert.Equal(ProbeVerdict.NotTried, report.Attempt("mount-windows-drive")!.Verdict);
    }

    [Fact]
    public async Task WithoutABaseImage_TheProbeCannotRun_AndSaysWhy_BeforeAnyMachineIsStarted()
    {
        using var rig = new ProbeRig(withBaseImage: false);

        var report = await rig.Probe.RunAsync(CancellationToken.None);

        Assert.Contains("base image has not been built", report.Failure, StringComparison.Ordinal);
        Assert.Empty(rig.Journal);
        Assert.False(report.AllBlocked);
    }

    [Fact]
    public async Task AnEarlierProbeMachineThatIsStillThere_IsRemovedFirst_AndIfItCannotBeTheProbeDoesNotRun()
    {
        _rig.Wsl.Distributions.Add(Machine);
        _rig.State.Update(s => s.ResidueItems = [new ResidueItem { Kind = ResidueKinds.LinuxMachine, Subject = Machine, Text = "the Linux machine dbd-ci-probe of an isolation probe could not be removed" }]);

        var first = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(first.AllBlocked);
        Assert.Empty(_rig.State.Load().Residue);

        _rig.Wsl.Distributions.Add(Machine);
        _rig.Wsl.UnregisterFailure = new InvalidOperationException("in use");
        var second = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Contains("of an earlier probe could not be removed", second.Failure, StringComparison.Ordinal);
        Assert.Empty(_rig.Wsl.ProbeScripts.Skip(1));
    }

    /// <summary>The reproduction of round 4, F7: a probe that ended well used to take off the list the entries of an earlier probe whose names merely contain the machine's name.</summary>
    [Fact]
    public async Task ASuccessfulProbe_LeavesTheEntriesOfAnEarlierProbesMarkerFileAndFolderOnTheList()
    {
        const string EarlierMarker = "dbd-ci-probe-aaaaaaaaaaaa.txt";
        var earlierFolder = Path.Combine(_rig.Paths.WslDistrosFolder, "dbd-ci-probe-20260101000000");
        _rig.State.Update(s => s.ResidueItems = [Left.Marker(EarlierMarker), Left.Folder(earlierFolder), Left.Machine(Machine)]);

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(report.AllBlocked);
        Assert.Equal(
            [(ResidueKinds.MarkerFile, EarlierMarker), (ResidueKinds.Folder, earlierFolder)],
            _rig.State.Load().ResidueItems.Select(i => (i.Kind, i.Subject)).ToArray());
    }

    [Fact]
    public async Task AProbeMachineThatCannotBeRemovedAfterwards_IsListedAsALeftover()
    {
        _rig.Wsl.UnregisterFailure = new InvalidOperationException("in use");

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(report.AllBlocked);
        Assert.Contains(_rig.State.Load().Residue, r => r.Contains("the Linux machine dbd-ci-probe of an isolation probe could not be removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderThatIsNotEmptyAfterwards_IsListedAsALeftover()
    {
        _rig.Wsl.UnregisterLeavesFolder = true;

        await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Contains(_rig.State.Load().Residue, r => r.Contains("is not empty", StringComparison.Ordinal) && r.Contains("isolation probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePlacesTheAccountMayWriteTo_AreInTheReport_AndAFailedAuditDoesNotStopTheProbe()
    {
        _rig.Locations.Places.Add("C:\\ (drive root): the account may make folders");

        var report = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.Equal(["C:\\ (drive root): the account may make folders"], report.WritableLocations);

        _rig.Locations.Failure = new UnauthorizedAccessException("access denied");
        var again = await _rig.Probe.RunAsync(CancellationToken.None);

        // The audit that fails is not an empty answer: it is an audit that could not be made (round 4, F1), so the probe is not "all blocked" any more, though every attempt was stopped.
        Assert.Empty(again.WritableLocations);
        Assert.False(again.Audit.Complete);
        Assert.Equal(1, again.Audit.GapCount);
        Assert.Contains("the audit could not be made at all: ", Assert.Single(again.Audit.Gaps), StringComparison.Ordinal);
        Assert.Contains("access denied", Assert.Single(again.Audit.Gaps), StringComparison.Ordinal);
        Assert.True(again.AttemptsAllBlocked);
        Assert.False(again.AllBlocked);
        Assert.Equal("AuditIncomplete", again.Outcome);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("places the account may write to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAuditWithGaps_MakesTheOutcomeAuditIncomplete_NotAllBlocked_AndNeverHidesAWorseOutcome()
    {
        _rig.Locations.Gaps.Add("C:\\System Volume Information (top-level folder): access denied");

        var gaps = await _rig.Probe.RunAsync(CancellationToken.None);

        Assert.True(gaps.AttemptsAllBlocked);
        Assert.False(gaps.AllBlocked);
        Assert.Equal("AuditIncomplete", gaps.Outcome);

        // A way out that is open is the more serious word; a probe that did not run at all is Incomplete whatever the audit says.
        _rig.Wsl.ProbeLines.RemoveAll(l => l.StartsWith("ATTEMPT gpu-device", StringComparison.Ordinal));
        _rig.Wsl.ProbeLines.Add("ATTEMPT gpu-device POSSIBLE open");
        Assert.Equal("SomePossible", (await _rig.Probe.RunAsync(CancellationToken.None)).Outcome);

        _rig.Wsl.ProbeFailure = new InvalidOperationException("stopped");
        Assert.Equal("Incomplete", (await _rig.Probe.RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task TheOwnersCancel_PassesThrough_YetTheMachineAndTheMarkerAreStillTakenCareOf()
    {
        using var cts = new CancellationTokenSource();
        _rig.Wsl.ProbeHang.On = true;
        var running = _rig.Probe.RunAsync(cts.Token);
        await _rig.Wsl.ProbeHang.StartedAsync();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        Assert.Empty(_rig.Wsl.Distributions);
        Assert.Equal(_rig.Marker.Names, _rig.Marker.Looked);
        Assert.Equal("settings release", _rig.Journal[^1]);
    }

    [Fact]
    public async Task ACancelBeforeAnythingStarted_IsACancel_NotAFailureOfTheProbe()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _rig.Locations.Failure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Probe.RunAsync(cts.Token));
    }

    // ---- reading what the script printed

    [Fact]
    public void AnAttemptThatDidNotReport_IsNotTried_WithTheExitCodeWhenTheScriptStoppedEarly()
    {
        var attempts = IsolationProbe.Parse(["ATTEMPT mount-windows-drive BLOCKED nope"], 1);

        Assert.Equal(IsolationProbe.AttemptIds, attempts.Select(a => a.Id).ToArray());
        Assert.Equal(ProbeVerdict.Blocked, attempts[0].Verdict);
        Assert.All(attempts.Skip(1), a =>
        {
            Assert.Equal(ProbeVerdict.NotTried, a.Verdict);
            Assert.Contains("exit code 1", a.Evidence, StringComparison.Ordinal);
        });
        Assert.Contains("did not report", IsolationProbe.Parse([], 0)[0].Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttemptReportedTwice_KeepsTheWorseReading_InEitherOrder()
    {
        var a = IsolationProbe.Parse(["ATTEMPT gpu-device BLOCKED fine", "ATTEMPT gpu-device POSSIBLE open"], 0).Single(x => x.Id == "gpu-device");
        var b = IsolationProbe.Parse(["ATTEMPT gpu-device POSSIBLE open", "ATTEMPT gpu-device BLOCKED fine"], 0).Single(x => x.Id == "gpu-device");
        var c = IsolationProbe.Parse(["ATTEMPT gpu-device BLOCKED fine", "ATTEMPT gpu-device NOTTRIED unknown"], 0).Single(x => x.Id == "gpu-device");

        Assert.Equal(ProbeVerdict.Possible, a.Verdict);
        Assert.Equal(ProbeVerdict.Possible, b.Verdict);
        Assert.Equal(ProbeVerdict.NotTried, c.Verdict);
    }

    /// <summary>Mutants 43 and 11 (adopted from the reviewer's scratch test D6): an attempt that was reported both as not tried and as possible counts as possible, whatever the order.</summary>
    [Theory]
    [InlineData("NOTTRIED", "POSSIBLE")]
    [InlineData("POSSIBLE", "NOTTRIED")]
    public void NotTriedAndPossible_InAnyOrder_ArePossible(string first, string second)
    {
        var attempts = IsolationProbe.Parse([$"ATTEMPT gpu-device {first} a", $"ATTEMPT gpu-device {second} b"], 0);

        Assert.Equal(ProbeVerdict.Possible, attempts.Single(a => a.Id == "gpu-device").Verdict);
    }

    [Theory]
    [InlineData("BLOCKED", "NOTTRIED", ProbeVerdict.NotTried)]
    [InlineData("NOTTRIED", "BLOCKED", ProbeVerdict.NotTried)]
    [InlineData("BLOCKED", "POSSIBLE", ProbeVerdict.Possible)]
    [InlineData("POSSIBLE", "BLOCKED", ProbeVerdict.Possible)]
    [InlineData("BLOCKED", "BLOCKED", ProbeVerdict.Blocked)]
    [InlineData("POSSIBLE", "POSSIBLE", ProbeVerdict.Possible)]
    [InlineData("NOTTRIED", "NOTTRIED", ProbeVerdict.NotTried)]
    public void TwoReadingsOfOneAttempt_KeepTheWorseOne_WhateverTheOrder(string first, string second, ProbeVerdict expected)
    {
        var attempts = IsolationProbe.Parse([$"ATTEMPT gpu-device {first} one", $"ATTEMPT gpu-device {second} two"], 0);

        Assert.Equal(expected, attempts.Single(a => a.Id == "gpu-device").Verdict);
    }

    [Fact]
    public void OtherOutput_NulCharactersAndCarriageReturns_AreHandled()
    {
        var lines = new[] { "some other output", "ATTEMPT mystery BLOCKED not one of ours", string.Join('\0', "ATTEMPT gpu-device POSSIBLE open\r".ToCharArray()) };

        var attempts = IsolationProbe.Parse(lines, 0);

        Assert.DoesNotContain(attempts, a => a.Id == "mystery");
        Assert.Equal(ProbeVerdict.Possible, attempts.Single(a => a.Id == "gpu-device").Verdict);
    }

    [Fact]
    public void AReportIsNeverAllBlocked_WithoutAttempts_OrWithAFailure()
    {
        var gap = new WritableAudit([], 5, 1, ["C:\\x (top-level folder): access denied"]);
        var blocked = new ProbeAttempt("gpu-device", ProbeVerdict.Blocked, string.Empty);
        Assert.False(new ProbeReport([], WritableAudit.Empty, null).AllBlocked);
        Assert.False(new ProbeReport([blocked], WritableAudit.Empty, "it did not run").AllBlocked);
        Assert.True(new ProbeReport([blocked], WritableAudit.Empty, null).AllBlocked);
        Assert.False(new ProbeReport([blocked], gap, null).AllBlocked);
        Assert.True(new ProbeReport([blocked], gap, null).AttemptsAllBlocked);
        Assert.False(new ProbeReport([blocked], WritableAudit.Empty, "it did not run").AttemptsAllBlocked);
        Assert.Equal("Incomplete", new ProbeReport([], WritableAudit.Empty, null).Outcome);
        Assert.Equal("AuditIncomplete", new ProbeReport([blocked], gap, null).Outcome);
        Assert.Null(new ProbeReport([], WritableAudit.Empty, null).Attempt("gpu-device"));
    }

    // ---- which setup a result is about

    [Fact]
    public void TheRecipeId_ChangesWithTheSettingsTheImageRecipeAndStaysTheSameOtherwise()
    {
        var facts = new WslHostFacts(16L * 1024 * 1024 * 1024, 8);
        var small = new WslHostFacts(4L * 1024 * 1024 * 1024, 4);

        var id = IsolationProbe.RecipeIdFor(facts, "image-1");

        Assert.Equal(16, id.Length);
        Assert.Equal(id, IsolationProbe.RecipeIdFor(facts, "image-1"));
        Assert.NotEqual(id, IsolationProbe.RecipeIdFor(small, "image-1"));
        Assert.NotEqual(id, IsolationProbe.RecipeIdFor(facts, "image-2"));
        Assert.Throws<ArgumentException>(() => IsolationProbe.RecipeIdFor(facts, string.Empty));
    }
}
