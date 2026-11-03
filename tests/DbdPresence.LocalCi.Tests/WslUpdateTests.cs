// Covers: local CI round 3, red-team H1 (WSL's kernel is updated by wsl --update; the update is made once a week, before the weekly image build, only when it cannot get in the owner's way: the owner has
// not switched it off, no Linux machine of this program is up, no WSL process of anybody is running; a failure or a hang is logged and ended and never stops the round's other work; the status says what
// came of it), L2 (the update has a time limit of its own). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class WslUpdateTests : IDisposable
{
    private readonly MaintenanceRig _rig = new(withWslUpdate: true);

    public void Dispose() => _rig.Dispose();

    private WeeklyWslUpdate Update => _rig.WslUpdate!;

    private void Due()
    {
        _rig.EverythingJustDone();
        _rig.State.Update(s => s.LastWslUpdateAttempt = null);
    }

    // ---- the update itself

    [Fact]
    public async Task WhenNothingSpeaksAgainstIt_WslIsUpdated_AndTheAttemptAndTheOutcomeAreKept()
    {
        var result = await Update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Updated, result);
        Assert.Equal(["update"], _rig.Installer.Calls.ToArray());
        var state = _rig.State.Load();
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastWslUpdateAttempt);
        Assert.Equal("updated at 2026-10-29: WSL was updated to version 2.5.1.0.", state.LastWslUpdateNote);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("WSL update finished: WSL was updated to version 2.5.1.0.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheOwnerSwitchedItOff_NothingIsRun_ButTheAttemptIsKept_SoItIsNotAskedEveryRound()
    {
        _rig.UpdateSwitchedOff = true;

        var result = await Update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Skipped, result);
        Assert.Empty(_rig.Installer.Calls);
        var state = _rig.State.Load();
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastWslUpdateAttempt);
        Assert.Contains("off: the file no-wsl-update is in the data folder", state.LastWslUpdateNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vmmemWSL")]
    [InlineData("vmmem")]
    [InlineData("wslhost")]
    [InlineData("wsl")]
    [InlineData("wslrelay")]
    [InlineData("wslg")]
    public async Task WhenAWslProgramRuns_NothingIsUpdated_AndItIsTriedAgainLater(string process)
    {
        _rig.Processes.Running.Add(process);

        var result = await Update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Skipped, result);
        Assert.Empty(_rig.Installer.Calls);
        Assert.Null(_rig.State.Load().LastWslUpdateAttempt);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("a WSL program is running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProcessListThatCannotBeRead_MeansItIsNotKnownThatNobodyUsesWsl_SoNothingIsUpdated()
    {
        _rig.Processes.Throw = new InvalidOperationException("the process list is unavailable");

        var result = await Update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Skipped, result);
        Assert.Empty(_rig.Installer.Calls);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("it could not be seen whether anybody uses WSL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedUpdate_IsLogged_KeptAsTheOutcome_AndNotTriedAgainBeforeNextWeek()
    {
        _rig.Installer.UpdateFailure = new InvalidOperationException("Updating WSL failed (exit code 1): the download was refused");

        var result = await Update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Failed, result);
        var state = _rig.State.Load();
        Assert.Contains("failed at 2026-10-29: InvalidOperationException: Updating WSL failed (exit code 1)", state.LastWslUpdateNote, StringComparison.Ordinal);
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastWslUpdateAttempt);
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The WSL update failed", StringComparison.Ordinal));
        Assert.False(Update.Due(state));
    }

    [Fact]
    public async Task AnUpdateThatWasStoppedByItsOwnLimit_IsAFailure_NotACancelOfTheLoop()
    {
        _rig.Installer.UpdateFailure = new OperationCanceledException();

        var result = await Update.RunAsync(CancellationToken.None);

        Assert.Equal(WslUpdateResult.Failed, result);
        Assert.Equal("failed: the update was stopped", _rig.State.Load().LastWslUpdateNote);
    }

    [Fact]
    public async Task TheLoopsOwnCancel_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _rig.Installer.UpdateFailure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Update.RunAsync(cts.Token));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(30, true)]
    public void TheUpdateIsDueAWeekAfterTheLastAttempt(int daysAgo, bool due)
    {
        var state = new CiState { LastWslUpdateAttempt = _rig.Time.GetUtcNow().AddDays(-daysAgo).AddSeconds(-1) };

        Assert.Equal(due, Update.Due(state));
    }

    [Fact]
    public void NeverTried_OrWithATimeInTheFuture_IsDue()
    {
        Assert.True(Update.Due(new CiState()));
        Assert.True(Update.Due(new CiState { LastWslUpdateAttempt = _rig.Time.GetUtcNow().AddDays(3) }));
        Assert.Equal(TimeSpan.FromDays(7), WeeklyWslUpdate.Interval);
    }

    // ---- the limit

    [Fact]
    public async Task TheUpdateHasATimeLimitOfItsOwn_ThirtyMinutes()
    {
        var inner = new FakeWslInstaller();
        inner.UpdateHang.On = true;
        var limited = new LimitedWslInstaller(inner, WslLimits.Install, _rig.Time);

        var running = limited.UpdateAsync(CancellationToken.None);
        await inner.UpdateHang.StartedAsync();
        _rig.Time.Advance(TimeSpan.FromMinutes(30));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Equal("Updating WSL did not finish within 30 minutes, so it was ended.", ex.Message);
    }

    // ---- in the maintenance round

    [Fact]
    public async Task TheMaintenance_UpdatesWsl_WhenDueAndIdle_BeforeTheImageIsBuilt()
    {
        using var rig = new MaintenanceRig(withDownloadableImage: true, withWslUpdate: true);
        rig.EverythingJustDone();
        rig.State.Update(s =>
        {
            s.LastWslUpdateAttempt = null;
            s.LastBaseImageBuild = null;
            s.LastBaseImageAttempt = null;
        });
        var order = new List<string>();
        rig.Installer.Calls.Journal = order;
        rig.Installer.Calls.Prefix = "installer ";
        rig.Wsl.Calls.Journal = order;
        rig.Wsl.Calls.Prefix = "wsl ";

        await rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Equal("installer update", order[0]);
        Assert.Contains(order, o => o.StartsWith("wsl import ", StringComparison.Ordinal));
        Assert.True(order.IndexOf("installer update") < order.FindIndex(o => o.StartsWith("wsl import ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheMaintenance_DoesNotUpdateWsl_WhileALinuxMachineIsUp_OrWhenTheGameRuns_OrBeforeItIsDue()
    {
        Due();

        await _rig.Maintenance.RunDueAsync(linuxIdle: false, CancellationToken.None);
        Assert.Empty(_rig.Installer.Calls);

        _rig.Processes.Running.Add("DeadByDaylight");
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Empty(_rig.Installer.Calls);

        _rig.Processes.Running.Clear();
        _rig.State.Update(s => s.LastWslUpdateAttempt = _rig.Time.GetUtcNow().AddDays(-2));
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Empty(_rig.Installer.Calls);

        _rig.State.Update(s => s.LastWslUpdateAttempt = null);
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Equal(["update"], _rig.Installer.Calls.ToArray());
    }

    [Fact]
    public async Task AnUpdateThatFailsOrHangs_NeverKeepsTheRoundsOtherWorkFromRunning()
    {
        Due();
        _rig.State.Update(s =>
        {
            s.LastRunnerCheck = null;
            s.LastRunnerAttempt = null;
        });
        _rig.Installer.UpdateFailure = new InvalidOperationException("boom");
        _rig.Api.Release = new RunnerRelease("2.338.0", string.Empty, new Uri("https://github.com/a"));

        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);

        Assert.Equal(["update"], _rig.Installer.Calls.ToArray());
        Assert.Contains(_rig.Log.Lines, l => l.Contains("The WSL update failed", StringComparison.Ordinal));
        Assert.Contains("release", _rig.Api.Calls);
    }

    [Fact]
    public async Task AnUpdateThatHangs_IsStoppedAfterThirtyMinutes_AndTheRoundGoesOn()
    {
        Due();
        _rig.Installer.UpdateHang.On = true;
        var running = _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        await _rig.Installer.UpdateHang.StartedAsync();

        _rig.Time.Advance(WslLimits.Update);
        await running.OrFail();

        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("The WSL update failed: ", StringComparison.Ordinal) && l.Contains("Updating WSL did not finish within 30 minutes, so it was ended.", StringComparison.Ordinal));
    }

    // ---- an update that does not end is still an attempt (round 4, F2)

    [Fact]
    public async Task TheAttemptIsWrittenDown_BeforeTheUpdateStarts_SoAHangOrAStoppedPcCannotRepeatItEveryRound()
    {
        DateTimeOffset? seenAtTheStart = null;
        string? noteAtTheStart = null;
        _rig.Installer.OnUpdate = () =>
        {
            var state = _rig.State.Load();
            seenAtTheStart = state.LastWslUpdateAttempt;
            noteAtTheStart = state.LastWslUpdateNote;
        };

        await Update.RunAsync(CancellationToken.None);

        Assert.Equal(_rig.Time.GetUtcNow(), seenAtTheStart);
        Assert.Equal("started at 2026-10-29 12:00 UTC and has not reported an end (if it stays like this the update hung or the PC stopped)", noteAtTheStart);
    }

    [Fact]
    public async Task AnUpdateThatHangsPastItsLimit_IsOneAttempt_NotTriedAgainAtTheNextIdleRound_AndTheStatusSaysWhyItFailed()
    {
        Due();
        _rig.Installer.UpdateHang.On = true;
        var running = _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        await _rig.Installer.UpdateHang.StartedAsync();
        var startedAt = _rig.Time.GetUtcNow();
        _rig.Time.Advance(WslLimits.Update);
        await running.OrFail();

        var state = _rig.State.Load();
        Assert.Equal(startedAt, state.LastWslUpdateAttempt);
        Assert.Equal("failed at 2026-10-29: TimeoutException: Updating WSL did not finish within 30 minutes, so it was ended.", state.LastWslUpdateNote);
        Assert.False(Update.Due(state));

        // The next idle rounds (twenty seconds later, then a day later) do not start the update again.
        _rig.Installer.UpdateHang.On = false;
        _rig.Time.Advance(TimeSpan.FromSeconds(20));
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        _rig.Time.Advance(TimeSpan.FromDays(1));
        await _rig.Maintenance.RunDueAsync(linuxIdle: true, CancellationToken.None);
        Assert.Equal(["update"], _rig.Installer.Calls.ToArray());

        // A week after the attempt it is due again.
        _rig.Time.Advance(TimeSpan.FromDays(7));
        Assert.True(Update.Due(_rig.State.Load()));
    }

    [Fact]
    public async Task WithTheLoopsOwnCancelInTheMiddle_TheAttemptStaysWritten()
    {
        using var cts = new CancellationTokenSource();
        _rig.Installer.UpdateHang.On = true;
        var running = Update.RunAsync(cts.Token);
        await _rig.Installer.UpdateHang.StartedAsync();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.OrFail());
        Assert.Equal(_rig.Time.GetUtcNow(), _rig.State.Load().LastWslUpdateAttempt);
        Assert.StartsWith("started at ", _rig.State.Load().LastWslUpdateNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMessageThatWslIsInUse_IsSaidOnce_AndAgainOnlyAfterTheUpdateWasPossibleInBetween()
    {
        _rig.Processes.Running.Add("wslhost");

        for (var round = 0; round < 5; round++)
        {
            await Update.RunAsync(CancellationToken.None);
        }

        Assert.Single(_rig.Log.Lines, l => l.Contains("a WSL program is running", StringComparison.Ordinal));
        Assert.Equal("waiting: a WSL program is running, so WSL is not updated yet (it is tried again later)", _rig.State.Load().LastWslUpdateNote);
        Assert.Null(_rig.State.Load().LastWslUpdateAttempt);

        _rig.Processes.Running.Clear();
        Assert.Equal(WslUpdateResult.Updated, await Update.RunAsync(CancellationToken.None));
        _rig.Processes.Running.Add("wslhost");
        _rig.State.Update(s => s.LastWslUpdateAttempt = null);
        await Update.RunAsync(CancellationToken.None);

        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("a WSL program is running", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheMessageThatTheProcessListCannotBeRead_IsAlsoSaidOnce()
    {
        _rig.Processes.Throw = new InvalidOperationException("the process list is unavailable");

        for (var round = 0; round < 4; round++)
        {
            await Update.RunAsync(CancellationToken.None);
        }

        Assert.Single(_rig.Log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("it could not be seen whether anybody uses WSL", StringComparison.Ordinal));
        Assert.Empty(_rig.Installer.Calls);
    }

    [Fact]
    public async Task OnlyTheFirstOfTwoDifferentReasonsToWait_AndTheNextIsSaidWhenItChanges()
    {
        _rig.Processes.Running.Add("wsl");
        await Update.RunAsync(CancellationToken.None);
        _rig.Processes.Running.Clear();
        _rig.Processes.Throw = new InvalidOperationException("the process list is unavailable");
        await Update.RunAsync(CancellationToken.None);
        _rig.Processes.Throw = null;
        _rig.Processes.Running.Add("wsl");
        await Update.RunAsync(CancellationToken.None);

        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("a WSL program is running", StringComparison.Ordinal)));
        Assert.Single(_rig.Log.Lines, l => l.Contains("it could not be seen whether anybody uses WSL", StringComparison.Ordinal));
    }

    // ---- the status

    [Fact]
    public void TheStatus_SaysWhatCameOfTheLastUpdate()
    {
        var facts = new StatusFacts(
            _rig.Time.GetUtcNow(),
            TokenSaved: true,
            TaskExists: true,
            LoopRunning: true,
            [],
            null,
            [],
            null,
            0,
            null,
            BaseImageBuilt: true,
            Wsl: new WslFacts("WSL version: 2.5.1.0", _rig.Time.GetUtcNow(), null, null, null),
            WslUpdateNote: "failed at 2026-10-29: boom");

        var text = StatusReport.Build(facts);
        var notYetSeen = StatusReport.Build(facts with { Wsl = null });

        Assert.Contains("  Weekly WSL update: failed at 2026-10-29: boom.", text, StringComparison.Ordinal);
        Assert.Contains("  Weekly WSL update: failed at 2026-10-29: boom.", notYetSeen, StringComparison.Ordinal);
        Assert.DoesNotContain("Weekly WSL update", StatusReport.Build(facts with { WslUpdateNote = null }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateThatWasStopped_IsKeptAsAnAttempt_SoItIsNotTriedAgainBeforeNextWeek()
    {
        _rig.Installer.UpdateFailure = new OperationCanceledException();

        await Update.RunAsync(CancellationToken.None);

        var state = _rig.State.Load();
        Assert.Equal(_rig.Time.GetUtcNow(), state.LastWslUpdateAttempt);
        Assert.False(Update.Due(state));
    }
}
