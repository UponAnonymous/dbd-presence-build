// Covers: local CI round 4, red-team F5 and QA M-3 (after the program updated WSL by itself it repeats the isolation probe without the owner and keeps serving only when nothing got worse than the result the
// owner accepted: a WSL with the same version is not probed again; no worse keeps the acceptance and counts the renewal; worse, not tried where it was blocked, a probe that cannot run, or a worse spot
// elsewhere withdraws the acceptance, deletes LINUX_RUNNER, starts no runner and tells the owner once a day, never while the game runs; the maintenance does this after every update that ran and for
// nothing else; the daily notice and log line when the gate refuses). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ProbeRenewalTests : IDisposable
{
    private const string NewVersion = "WSL version: 2.5.1.0\nKernel version: 6.6.36.3-1\nWindows version: 10.0.22631.4460";

    private readonly MaintenanceRig _rig = new(withDownloadableImage: true, withWslUpdate: true, withProbeRenewal: true);

    public ProbeRenewalTests()
    {
        _rig.Api.Variables["LINUX_RUNNER"] = LocalCiDefaults.LinuxLabel;
    }

    public void Dispose() => _rig.Dispose();

    private ProbeRenewal Renewal => _rig.Renewal!;

    private void WslIsUpdated() => _rig.Wsl.VersionText = NewVersion;

    private static void Open(FakeWsl wsl, string id, string verdict = "POSSIBLE")
    {
        wsl.ProbeLines.RemoveAll(l => l.StartsWith($"ATTEMPT {id} ", StringComparison.Ordinal));
        wsl.ProbeLines.Add($"ATTEMPT {id} {verdict} now {verdict}");
    }

    // ---- when nothing is to be renewed

    [Fact]
    public async Task WithoutAnAcceptedProbe_ThereIsNothingToRenew_AndNothingIsProbed()
    {
        _rig.State.Update(s => s.Probe!.Accepted = false);
        WslIsUpdated();

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.NothingToRenew, result);
        Assert.Empty(_rig.Wsl.ProbeScripts);
        Assert.Equal("dbd-linux", _rig.Api.Variables["LINUX_RUNNER"]);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("no accepted result for the current setup to renew", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProbeForAnotherSetup_IsNotRenewedByTheProgram_TheOwnerMustRunItAgain()
    {
        _rig.State.Update(s => s.Probe!.Recipe = "recipe-0");
        WslIsUpdated();

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.NothingToRenew, result);
        Assert.Empty(_rig.Wsl.ProbeScripts);
    }

    [Fact]
    public async Task WhenTheWslVersionIsTheSame_TheProbeStillHolds_AndIsNotRepeated()
    {
        var before = _rig.State.Load().Probe!;

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.StillValid, result);
        Assert.Empty(_rig.Wsl.ProbeScripts);
        Assert.Equal(before.At, _rig.State.Load().Probe!.At);
        Assert.Equal(0, _rig.State.Load().Probe!.Renewals);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("WSL has the same version as when you accepted the isolation probe", StringComparison.Ordinal));
    }

    // ---- nothing got worse

    [Fact]
    public async Task WhenNothingGotWorse_TheAcceptanceCarriesOver_WithTheNewTimeAndVersion_AndTheRenewalIsCounted()
    {
        var accepted = _rig.State.Load().Probe!;
        WslIsUpdated();
        _rig.Time.Advance(TimeSpan.FromDays(3));
        _rig.State.Update(s => s.Probe!.WritableLocations = ["C:\\Tools (folder in the system PATH): the account may make files"]);

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.Renewed, result);
        var record = _rig.State.Load().Probe!;
        Assert.True(record.Accepted);
        Assert.Equal(accepted.AcceptedAt, record.AcceptedAt);
        Assert.Equal(_rig.Time.GetUtcNow(), record.At);
        Assert.Equal(1, record.Renewals);
        Assert.Equal(_rig.Time.GetUtcNow(), record.LastRenewedAt);
        Assert.Equal(NewVersion, record.WslVersion);
        Assert.NotEmpty(record.Id);
        Assert.NotEqual(accepted.Id, record.Id);
        Assert.Equal("AllBlocked", record.Outcome);
        Assert.Equal(["C:\\Tools (folder in the system PATH): the account may make files"], record.WritableLocations);
        Assert.Equal(AcceptedProbe.Recipe, record.Recipe);
        Assert.Null(_rig.Gate!.Problem());
        Assert.Equal("dbd-linux", _rig.Api.Variables["LINUX_RUNNER"]);
        Assert.Empty(_rig.Notifier.Messages);
        Assert.Single(_rig.Wsl.ProbeScripts);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("gave a result no worse than the one you accepted (AllBlocked); Linux checks stay on this PC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRepeatedProbe_NeverReadsWindowsPermissions_TheLoopHasNoAudit()
    {
        WslIsUpdated();

        await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(0, _rig.Locations!.Asked);
        Assert.Equal(_rig.Marker!.Names, _rig.Marker.Looked);
    }

    [Fact]
    public async Task TheAuditOfTheResultTheOwnerLookedAt_IsKept_AndAnIncompleteOneKeepsItsWord()
    {
        _rig.State.Update(s =>
        {
            s.Probe!.Outcome = "AuditIncomplete";
            s.Probe.AuditPlacesLooked = 90;
            s.Probe.AuditGapCount = 1;
            s.Probe.AuditGaps = ["C:\\System Volume Information (top-level folder): denied"];
        });
        WslIsUpdated();

        await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        var record = _rig.State.Load().Probe!;
        Assert.Equal("AuditIncomplete", record.Outcome);
        Assert.Equal(90, record.AuditPlacesLooked);
        Assert.Equal(1, record.AuditGapCount);
        Assert.Equal(["C:\\System Volume Information (top-level folder): denied"], record.AuditGaps);
    }

    [Fact]
    public async Task EachRenewalCountsOn_AndTheStatusTellsThatTheProgramRepeatedTheAttempts()
    {
        WslIsUpdated();
        await Renewal.AfterWslUpdateAsync(CancellationToken.None);
        _rig.Wsl.VersionText = NewVersion.Replace("2.5.1.0", "2.6.0.0", StringComparison.Ordinal);
        await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        var record = _rig.State.Load().Probe!;
        Assert.Equal(2, record.Renewals);
        var text = StatusReport.Build(new StatusFacts(_rig.Time.GetUtcNow(), true, true, true, [], null, [], null, 0, null, true, Probe: record, CurrentProbeRecipe: AcceptedProbe.Recipe, Variables: new Dictionary<string, string?> { ["LINUX_RUNNER"] = "dbd-linux" }));
        Assert.Contains("you accepted it at ", text, StringComparison.Ordinal);
        Assert.Contains("(after its own WSL update the program repeated the attempts by itself 2 times, last at ", text, StringComparison.Ordinal);
        Assert.Contains("with a result no worse)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheOwnerKnowinglyAcceptedAsOpen_StaysNoWorse_AndAnotherOpenWayIsWorse()
    {
        // The owner accepted a result in which the GPU device was open.
        _rig.State.Update(s =>
        {
            s.Probe!.Outcome = "SomePossible";
            s.Probe.Attempts.Single(a => a.Id == "gpu-device").Verdict = nameof(ProbeVerdict.Possible);
        });
        Open(_rig.Wsl, "gpu-device");
        WslIsUpdated();

        Assert.Equal(RenewalResult.Renewed, await Renewal.AfterWslUpdateAsync(CancellationToken.None));
        Assert.Equal("SomePossible", _rig.State.Load().Probe!.Outcome);
        Assert.True(_rig.State.Load().Probe!.Accepted);

        Open(_rig.Wsl, "virtualization-device");
        _rig.Wsl.VersionText = NewVersion.Replace("2.5.1.0", "2.6.0.0", StringComparison.Ordinal);

        Assert.Equal(RenewalResult.Withdrawn, await Renewal.AfterWslUpdateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AWayThatWasNotTriedBefore_CanStayNotTried_ButNotBecomePossible()
    {
        _rig.State.Update(s => s.Probe!.Attempts.Single(a => a.Id == "host-network").Verdict = nameof(ProbeVerdict.NotTried));
        Open(_rig.Wsl, "host-network", "NOTTRIED");
        WslIsUpdated();

        Assert.Equal(RenewalResult.Renewed, await Renewal.AfterWslUpdateAsync(CancellationToken.None));
        Assert.Equal("Incomplete", _rig.State.Load().Probe!.Outcome);

        Open(_rig.Wsl, "host-network");
        _rig.Wsl.VersionText = NewVersion.Replace("2.5.1.0", "2.6.0.0", StringComparison.Ordinal);

        Assert.Equal(RenewalResult.Withdrawn, await Renewal.AfterWslUpdateAsync(CancellationToken.None));
    }

    // ---- something got worse, or the probe could not run

    [Fact]
    public async Task WhenAWayOutIsOpenNow_TheAcceptanceIsWithdrawn_TheVariableIsDeleted_NoRunnerStarts_AndTheOwnerIsTold()
    {
        Open(_rig.Wsl, "mount-windows-drive");
        WslIsUpdated();

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.Withdrawn, result);
        var record = _rig.State.Load().Probe!;
        Assert.False(record.Accepted);
        Assert.Null(record.AcceptedAt);
        Assert.Equal("SomePossible", record.Outcome);
        Assert.Equal(0, record.Renewals);
        Assert.Null(record.LastRenewedAt);
        Assert.Equal(NewVersion, record.WslVersion);
        Assert.False(_rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Equal("you have not accepted the result of the isolation probe (choose 5 in DBD Local CI and decide)", _rig.Gate!.Problem());
        var notice = Assert.Single(_rig.Notifier.Messages);
        Assert.Contains("DBD Local CI updated WSL and repeated the isolation probe by itself", notice, StringComparison.Ordinal);
        Assert.Contains("Mount a Windows drive as root and list C:\\Users: BLOCKED when you accepted it, POSSIBLE now", notice, StringComparison.Ordinal);
        Assert.Contains("It turned LINUX_RUNNER off, so checks run on GitHub's machines again, and it starts no Linux check on this PC.", notice, StringComparison.Ordinal);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("is worse than what you accepted, so the acceptance is withdrawn", StringComparison.Ordinal));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("I turned LINUX_RUNNER off (the isolation probe, repeated after the WSL update, is worse than what you accepted)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWayThatCouldNotBeTriedNow_WhereItWasBlocked_IsWorse()
    {
        Open(_rig.Wsl, "host-network", "NOTTRIED");
        WslIsUpdated();

        Assert.Equal(RenewalResult.Withdrawn, await Renewal.AfterWslUpdateAsync(CancellationToken.None));
        Assert.Contains("Reach this PC over the network (common ports): BLOCKED when you accepted it, NOT TRIED now", Assert.Single(_rig.Notifier.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeThatCannotRun_IsWorse_BecauseNothingIsShownToBeClosed()
    {
        _rig.Wsl.ProbeFailure = new InvalidOperationException("wsl.exe stopped answering");
        WslIsUpdated();

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.Withdrawn, result);
        Assert.False(_rig.State.Load().Probe!.Accepted);
        Assert.Equal("Incomplete", _rig.State.Load().Probe!.Outcome);
        Assert.False(_rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("wsl.exe stopped answering", Assert.Single(_rig.Notifier.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptThatPrintedEveryLineBlocked_ButEndedWithAnExitCode_IsWorse_BecauseItsResultIsNotComplete()
    {
        _rig.Wsl.ProbeExit = 137;
        WslIsUpdated();

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.Withdrawn, result);
        Assert.False(_rig.State.Load().Probe!.Accepted);
        Assert.Equal("Incomplete", _rig.State.Load().Probe!.Outcome);
        Assert.False(_rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("ended with exit code 137 instead of 0", Assert.Single(_rig.Notifier.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionThatCannotBeRead_CountsAsChanged_SoTheProbeIsRepeated()
    {
        _rig.Wsl.VersionFailure = new InvalidOperationException("wsl.exe --version failed");

        var result = await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal(RenewalResult.Renewed, result);
        Assert.Single(_rig.Wsl.ProbeScripts);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("Could not read the WSL version after the update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhileTheGameRuns_TheNoticeWaits_ButTheVariableIsStillDeleted_AndTheNoticeComesOncePerDay()
    {
        Open(_rig.Wsl, "gpu-device");
        WslIsUpdated();
        _rig.Processes.Running.Add("DeadByDaylight");

        await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Empty(_rig.Notifier.Messages);
        Assert.False(_rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.False(_rig.State.Load().Probe!.Accepted);

        // The next day, with the game closed, a second withdrawal is told; a third one within the same day is not.
        _rig.Processes.Running.Clear();
        AcceptedProbe.In(_rig.State);
        _rig.Wsl.VersionText = NewVersion.Replace("2.5.1.0", "2.6.0.0", StringComparison.Ordinal);
        _rig.State.Update(s => s.WslVersionText = AcceptedProbe.Version);
        _rig.Time.Advance(TimeSpan.FromDays(1));
        await Renewal.AfterWslUpdateAsync(CancellationToken.None);
        Assert.Single(_rig.Notifier.Messages);

        AcceptedProbe.In(_rig.State);
        _rig.Wsl.VersionText = NewVersion.Replace("2.5.1.0", "2.7.0.0", StringComparison.Ordinal);
        await Renewal.AfterWslUpdateAsync(CancellationToken.None);
        Assert.Single(_rig.Notifier.Messages);
    }

    [Fact]
    public async Task AVariableWithAnotherValue_IsTheOwnersOwn_AndStaysEvenWhenTheAcceptanceIsWithdrawn()
    {
        _rig.Api.Variables["LINUX_RUNNER"] = "ubuntu-latest";
        Open(_rig.Wsl, "gpu-device");
        WslIsUpdated();

        await Renewal.AfterWslUpdateAsync(CancellationToken.None);

        Assert.Equal("ubuntu-latest", _rig.Api.Variables["LINUX_RUNNER"]);
        Assert.False(_rig.State.Load().Probe!.Accepted);
    }

    [Fact]
    public async Task ACancel_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        WslIsUpdated();
        _rig.Wsl.ProbeHang.On = true;
        var running = Renewal.AfterWslUpdateAsync(cts.Token);
        await _rig.Wsl.ProbeHang.StartedAsync();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        Assert.True(_rig.State.Load().Probe!.Accepted);
    }

    [Fact]
    public void WorseAttempts_AreNamedWithWhatTheyWereAndWhatTheyAre()
    {
        var accepted = _rig.State.Load().Probe!;
        var now = new List<ProbeAttempt> { new("gpu-device", ProbeVerdict.Possible, "open"), new("shared-mounts", ProbeVerdict.Blocked, "closed"), new("host-network", ProbeVerdict.NotTried, "no route") };

        var worse = ProbeComparison.WorseThanAccepted(accepted, now);

        Assert.Equal(["Use the GPU device (/dev/dxg): BLOCKED when you accepted it, POSSIBLE now (open)", "Reach this PC over the network (common ports): BLOCKED when you accepted it, NOT TRIED now (no route)"], worse);
        Assert.Empty(ProbeComparison.WorseThanAccepted(accepted, [new("gpu-device", ProbeVerdict.Blocked, "x")]));
        Assert.Single(ProbeComparison.WorseThanAccepted(new ProbeRecord(), [new("gpu-device", ProbeVerdict.NotTried, "x")]));
    }

    [Fact]
    public void ALogConsole_ShowsEverythingInTheLog_AndAsksNothing()
    {
        var ui = new LogConsoleUi(_rig.Log);

        ui.WriteLine("hello owner");

        Assert.Contains(_rig.Log.Lines, l => l.Contains("hello owner", StringComparison.Ordinal));
        Assert.Null(ui.ReadLine());
        Assert.Null(ui.ReadSecret("token: "));
        Assert.False(ui.Confirm("delete?"));
    }

    // ---- in the maintenance round

    private void ReadyForAnUpdate()
    {
        _rig.EverythingJustDone();
        _rig.State.Update(s => s.LastWslUpdateAttempt = null);
    }

    [Fact]
    public async Task AfterAnUpdateThatRan_TheMaintenanceRepeatsTheProbe_ForAFailedOneToo()
    {
        ReadyForAnUpdate();
        _rig.Installer.OnUpdate = WslIsUpdated;

        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Equal(["update"], _rig.Installer.Calls.ToArray());
        Assert.Single(_rig.Wsl.ProbeScripts);
        Assert.Equal(1, _rig.State.Load().Probe!.Renewals);

        ReadyForAnUpdate();
        _rig.Installer.UpdateFailure = new InvalidOperationException("the download was refused");
        _rig.Installer.OnUpdate = () => _rig.Wsl.VersionText = NewVersion.Replace("2.5.1.0", "2.6.0.0", StringComparison.Ordinal);
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Equal(2, _rig.Wsl.ProbeScripts.Count);
        Assert.Equal(2, _rig.State.Load().Probe!.Renewals);
    }

    [Fact]
    public async Task WhenNoUpdateRan_NothingIsRepeated()
    {
        ReadyForAnUpdate();
        _rig.UpdateSwitchedOff = true;

        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        _rig.State.Update(s => s.LastWslUpdateAttempt = null);
        _rig.UpdateSwitchedOff = false;
        _rig.Processes.Running.Add("wslhost");
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Empty(_rig.Installer.Calls);
        Assert.Empty(_rig.Wsl.ProbeScripts);
    }

    [Fact]
    public async Task AnUpdateThatFindsAWorseResult_StopsTheLoopFromServing_ThroughTheGate()
    {
        ReadyForAnUpdate();
        _rig.Installer.OnUpdate = WslIsUpdated;
        Open(_rig.Wsl, "windows-programs");

        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.False(_rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.NotNull(_rig.Gate!.Problem());
    }

    // ---- the daily notice that no Linux check starts

    [Fact]
    public async Task WhenTheGateRefuses_TheOwnerGetsOneNoticeADay_AndTheLogSaysItOnce()
    {
        _rig.EverythingJustDone();
        _rig.State.Update(s => s.WslVersionText = NewVersion);

        for (var round = 0; round < 5; round++)
        {
            await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        }

        var notice = Assert.Single(_rig.Notifier.Messages);
        Assert.Contains("DBD Local CI sends no Linux check to this PC: WSL changed since the isolation probe you accepted", notice, StringComparison.Ordinal);
        Assert.Contains("turn LINUX_RUNNER off on GitHub, or run the probe and decide", notice, StringComparison.Ordinal);
        Assert.Single(_rig.Log.Lines, l => l.Contains("sends no Linux check to this PC", StringComparison.Ordinal));

        _rig.Time.Advance(TimeSpan.FromHours(25));
        _rig.EverythingJustDone();
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Equal(2, _rig.Notifier.Messages.Count);
        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("sends no Linux check to this PC", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ANewReasonIsLoggedAtOnce_AndWhenTheProbeHolds_NothingIsSaid()
    {
        _rig.EverythingJustDone();
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Empty(_rig.Notifier.Messages);
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("sends no Linux check", StringComparison.Ordinal));

        _rig.State.Update(s => s.Probe!.Accepted = false);
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        _rig.State.Update(s => s.Probe!.Accepted = true);
        _rig.State.Update(s => s.WslVersionText = NewVersion);
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("sends no Linux check to this PC", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WhileTheGameRuns_NoNoticeIsShown_ButTheLogSaysIt()
    {
        _rig.EverythingJustDone();
        _rig.State.Update(s => s.Probe!.Accepted = false);
        _rig.Processes.Running.Add("DeadByDaylight");

        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Empty(_rig.Notifier.Messages);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("you have not accepted the result of the isolation probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARenewalThatCannotWriteItsResult_NeverStopsTheRound_AndTheGateStillRefusesBecauseTheVersionDiffers()
    {
        using var rig = new MaintenanceRig(withWslUpdate: true, withProbeRenewal: true);
        rig.EverythingJustDone();
        rig.State.Update(s => s.LastWslUpdateAttempt = null);
        rig.Installer.OnUpdate = () => rig.Wsl.VersionText = NewVersion;
        rig.Wsl.ListFailure = new IOException("the list of Linux machines cannot be read");

        await rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The isolation probe", StringComparison.Ordinal));
        Assert.NotNull(rig.Gate!.Problem());
    }
}
