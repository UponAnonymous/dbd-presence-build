// Covers: red-team F2 and F8 (every use of the account's Linux machine starts fresh and ends fresh: the machine is shut down first, the account is checked, the settings file is written when it differs
// and read back from the held file, anything but the exact settings stops the start, the file is held until the session ends, the machine is shut down again afterwards and the hold is let go even
// when that fails; only one session exists at a time), round 3 L3 (the file must be byte for byte what the program writes: an encoding mark, other line endings or a comment get it rewritten, and
// are refused when found in the held file), L11 (a locked-out account is unlocked, and only that), H1 (the programs of the account are ended and checked gone before a session and after it; a start
// next to programs that stay is refused), L12 (the WSL version is remembered for the status and never stops a start), L1 (removal counts as done only when the list shows the machine gone), L2 (a folder is
// only removed when empty), round 4 F14 (a session that could not shut the machine down or end the account's programs is listed as a leftover, so the status and the daily notice say so, and a session
// that ends cleanly takes the entry off again). docs/owner/free-ci.md.
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class WslSandboxTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly Redactor _redactor = new();
    private readonly MemoryCiLog _log;
    private readonly FakeWsl _wsl = new();
    private readonly FakeWslAccount _account = new();
    private readonly FakeWslSettingsFile _settings = new();
    private readonly FakeMachineRecord _record = new();
    private readonly WslSandbox _sandbox;
    private readonly List<string> _journal = [];

    public WslSandboxTests()
    {
        _log = new MemoryCiLog(_redactor);
        _wsl.Calls.Journal = _journal;
        _wsl.Calls.Prefix = "wsl ";
        _settings.Calls.Journal = _journal;
        _settings.Calls.Prefix = "settings ";
        _sandbox = Sandboxes.New(_wsl, _account, _settings, _log, _redactor, _time, _record);
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task ASessionStartsFresh_ChecksTheAccount_WritesTheSettings_HoldsThemAndEndsFresh()
    {
        var session = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.Equal(["wsl shutdown", "settings write", "settings hold"], _journal);
        Assert.Equal(WslSettingsPolicy.ExpectedText(Sandboxes.Facts), Assert.Single(_settings.Writes));

        await session.DisposeAsync();

        Assert.Equal(["wsl shutdown", "settings write", "settings hold", "wsl shutdown", "settings release"], _journal);
        Assert.Equal(1, _account.Checks);
    }

    [Fact]
    public async Task SettingsThatAlreadyAreExactlyRight_AreNotWrittenAgain_ButStillHeldAndChecked()
    {
        _settings.Bytes = WslSettingsPolicy.ExpectedBytes(Sandboxes.Facts);

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Empty(_settings.Writes);
        Assert.Equal(1, _settings.Holds);
    }

    [Theory]
    [InlineData("[wsl2]\nnetworkingMode=mirrored\n")]
    [InlineData("[wsl2]\nnetworkingMode=nat\nlocalhostForwarding=true\nguiApplications=false\nswap=0\nmemory=8GB\nprocessors=4\n")]
    [InlineData("[wsl2]\nnetworkingMode=nat\nlocalhostForwarding=false\nguiApplications=false\nswap=0\nmemory=8GB\nprocessors=4\nkernel=C:\\x\n")]
    [InlineData("")]
    public async Task SettingsThatDiffer_AreRewrittenBeforeTheyAreHeld(string existing)
    {
        _settings.Text = existing;

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Equal(WslSettingsPolicy.ExpectedText(Sandboxes.Facts), Assert.Single(_settings.Writes));
        Assert.True(_journal.IndexOf("settings write") < _journal.IndexOf("settings hold"));
        Assert.Contains(_log.Lines, l => l.Contains("Wrote the WSL settings of the account dbd-ci-wsl", StringComparison.Ordinal));
    }

    /// <summary>Red-team round 3, L3: settings that say the right things in other bytes are written again.</summary>
    [Theory]
    [InlineData("bom")]
    [InlineData("lf")]
    [InlineData("comment")]
    public async Task AFileWithTheRightSettingsInOtherBytes_IsRewrittenBeforeItIsHeld(string variant)
    {
        _settings.Bytes = OtherBytes(variant);

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Equal(WslSettingsPolicy.ExpectedBytes(Sandboxes.Facts), Assert.Single(_settings.WrittenBytes));
        Assert.Equal(WslSettingsPolicy.ExpectedBytes(Sandboxes.Facts), _settings.Bytes);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("lf")]
    [InlineData("comment")]
    public async Task AHeldFileWithTheRightSettingsInOtherBytes_StopsTheStart(string variant)
    {
        _settings.HeldBytesOverride = OtherBytes(variant);

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("byte for byte", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, _settings.Releases);
    }

    private static byte[] OtherBytes(string variant)
    {
        var text = WslSettingsPolicy.ExpectedText(Sandboxes.Facts);
        return variant switch
        {
            "bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "lf" => Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)),
            _ => Encoding.UTF8.GetBytes("# note\r\n" + text),
        };
    }

    /// <summary>What is checked is what WSL reads: the held file is read back, and a file that changed between the write and the hold stops the start.</summary>
    [Fact]
    public async Task AFileThatSaysSomethingElseWhenHeld_StopsTheStart_ReleasesTheHold_AndFreesTheSandbox()
    {
        _settings.HeldTextOverride = "[wsl2]\nnetworkingMode=mirrored\n" + WslSettingsPolicy.ExpectedText(Sandboxes.Facts).Replace("[wsl2]\r\n", string.Empty, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("not as required", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, _settings.Releases);

        _settings.HeldTextOverride = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AnAccountThatIsNotAsItMustBe_StopsTheStart_BeforeAnyCommandRuns()
    {
        _account.Problems.Add("the account dbd-ci-wsl is in the group S-1-5-32-544; it must be in no group but Users");

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("not as it must be", ex.Message, StringComparison.Ordinal);
        Assert.Contains("S-1-5-32-544", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_journal);
        Assert.Equal("the Windows account dbd-ci-wsl is not as it must be: the account dbd-ci-wsl is in the group S-1-5-32-544; it must be in no group but Users", _sandbox.NotReadyReason());
    }

    [Fact]
    public void WithAGoodAccount_TheSandboxIsReady()
    {
        Assert.Null(_sandbox.NotReadyReason());
        Assert.Equal(Sandboxes.Facts, _sandbox.Facts);
    }

    [Fact]
    public async Task AShutdownThatFailsAtTheStart_StopsTheStart_AndFreesTheSandbox()
    {
        _wsl.ShutdownFailure = new InvalidOperationException("the service did not answer");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Empty(_settings.Writes);
        _wsl.ShutdownFailure = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task SettingsThatCannotBeWritten_StopTheStart_AndFreeTheSandbox()
    {
        _settings.WriteFailure = new IOException("access denied");

        await Assert.ThrowsAsync<IOException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Equal(0, _settings.Holds);
        _settings.WriteFailure = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task SettingsThatCannotBeReadSafely_StopTheStart()
    {
        _settings.ReadFailure = new IOException("it is a link");

        var ex = await Assert.ThrowsAsync<IOException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("link", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_settings.Writes);
    }

    [Fact]
    public async Task OnlyOneSessionExists_AtATime_AndTheNextOneStartsWhenTheFirstEnds()
    {
        var first = await _sandbox.BeginAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => _sandbox.BeginAsync(CancellationToken.None));
        Assert.Contains("Another task is using the Linux machine", ex.Message, StringComparison.Ordinal);

        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var second = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task AShutdownThatFailsAtTheEnd_IsLogged_YetTheHoldIsLetGo_AndTheSandboxIsFree()
    {
        var session = await _sandbox.BeginAsync(CancellationToken.None);
        _wsl.ShutdownFailure = new InvalidOperationException("stuck");

        await session.DisposeAsync();

        Assert.Equal(1, _settings.Releases);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("shut down the Linux machine", StringComparison.Ordinal) && l.Contains("stuck", StringComparison.Ordinal));
        _wsl.ShutdownFailure = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    // ---- round 4, F14: a session that could not be ended cleanly is on the leftovers list, not only in the log

    private (WslSandbox Sandbox, StateStore State) SandboxWithLeftovers(StateStore? state = null)
    {
        state ??= new StateStore(_folder.Paths.StateFile);
        return (Sandboxes.New(_wsl, _account, _settings, _log, _redactor, _time, _record, residue: new ResidueRecorder(state, null, _log)), state);
    }

    [Fact]
    public async Task AShutdownThatFailsAtTheEnd_IsListedAsALeftover_WithWhatToDo()
    {
        var (sandbox, state) = SandboxWithLeftovers();
        var session = await sandbox.BeginAsync(CancellationToken.None);
        _wsl.ShutdownFailure = new InvalidOperationException("stuck");

        await session.DisposeAsync();

        var left = Assert.Single(state.Load().ResidueItems);
        Assert.Equal((ResidueKinds.Session, WslSandbox.SessionEndSubject), (left.Kind, left.Subject));
        Assert.Equal("the Linux machine of the account dbd-ci-wsl could not be shut down after its last use (see the log). The next Linux check will not start until that works; the program tries again before each check. If it keeps failing, sign out the Windows account dbd-ci-wsl or restart Windows", left.Text);
    }

    [Fact]
    public async Task ProgramsOfTheAccountThatCannotBeEndedAtTheEnd_AreListedAsALeftover()
    {
        var (sandbox, state) = SandboxWithLeftovers();
        var session = await sandbox.BeginAsync(CancellationToken.None);
        _account.EndProgramsFailure = new InvalidOperationException("access denied");

        await session.DisposeAsync();

        Assert.StartsWith("the programs of the account dbd-ci-wsl could not be ended after the last use of its Linux machine (see the log).", Assert.Single(state.Load().Residue), StringComparison.Ordinal);
        Assert.Equal(1, _settings.Releases);
    }

    [Fact]
    public async Task BothStepsThatFailAtTheEnd_AreSaidInOneEntry()
    {
        var (sandbox, state) = SandboxWithLeftovers();
        var session = await sandbox.BeginAsync(CancellationToken.None);
        _wsl.ShutdownFailure = new InvalidOperationException("stuck");
        _account.EndProgramsFailure = new InvalidOperationException("access denied");

        await session.DisposeAsync();

        Assert.StartsWith("the Linux machine of the account dbd-ci-wsl could not be shut down and its programs could not be ended after its last use (see the log).", Assert.Single(state.Load().Residue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionThatEndsCleanlyAfterwards_TakesTheEntryOff_AndLeavesTheOtherLeftoversAlone()
    {
        var (sandbox, state) = SandboxWithLeftovers();
        var first = await sandbox.BeginAsync(CancellationToken.None);
        _wsl.ShutdownFailure = new InvalidOperationException("stuck");
        await first.DisposeAsync();
        new ResidueRecorder(state).Add(ResidueKinds.Folder, "C:\\x", "the folder C:\\x is not empty");
        _wsl.ShutdownFailure = null;
        Assert.Equal(2, state.Load().ResidueItems.Count);

        await (await sandbox.BeginAsync(CancellationToken.None)).DisposeAsync();

        Assert.Equal(["the folder C:\\x is not empty"], state.Load().Residue);
    }

    [Fact]
    public async Task ACleanSession_ListsNothing()
    {
        var (sandbox, state) = SandboxWithLeftovers();

        await (await sandbox.BeginAsync(CancellationToken.None)).DisposeAsync();

        Assert.Empty(state.Load().ResidueItems);
    }

    [Fact]
    public async Task AStartThatIsRefusedBecauseProgramsStay_DoesNotTakeTheEntryOff()
    {
        var (sandbox, state) = SandboxWithLeftovers();
        var first = await sandbox.BeginAsync(CancellationToken.None);
        _account.EndProgramsFailure = new InvalidOperationException("access denied");
        await first.DisposeAsync();

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("from earlier are still running", ex.Message, StringComparison.Ordinal);
        Assert.Single(state.Load().ResidueItems);
    }

    [Fact]
    public async Task AStateFileThatIsBusy_DoesNotStopTheEndOfTheSession_TheFailureIsLogged()
    {
        var path = _folder.Paths.StateFile;
        var state = new StateStore(path, lockPatience: TimeSpan.Zero, lockStep: TimeSpan.FromMilliseconds(25), wait: _ => { });
        var (sandbox, _) = SandboxWithLeftovers(state);
        var session = await sandbox.BeginAsync(CancellationToken.None);
        _wsl.ShutdownFailure = new InvalidOperationException("stuck");
        using var busy = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);

        var ex = await Record.ExceptionAsync(async () => await session.DisposeAsync());

        Assert.Null(ex);
        Assert.Equal(1, _settings.Releases);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("Could not record how the last use of the Linux machine ended", StringComparison.Ordinal));
        _wsl.ShutdownFailure = null;
        await using var again = await sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public void AnEntryOfASessionThatEndedBadly_SurvivesTheSweepOfTheNextLoopStart()
    {
        var state = new StateStore(_folder.Paths.StateFile);
        var recorder = new ResidueRecorder(state);
        recorder.Add(ResidueKinds.Session, WslSandbox.SessionEndSubject, "the last use did not end cleanly");

        recorder.Sweep([Left.Machine("dbd-ci-job-20261029110000")]);

        Assert.Equal([(ResidueKinds.Session, WslSandbox.SessionEndSubject), (ResidueKinds.LinuxMachine, "dbd-ci-job-20261029110000")], state.Load().ResidueItems.Select(i => (i.Kind, i.Subject)).ToArray());
    }

    [Fact]
    public async Task ACancelledStart_FreesTheSandbox()
    {
        using var cts = new CancellationTokenSource();
        _wsl.ShutdownFailure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sandbox.BeginAsync(cts.Token));

        _wsl.ShutdownFailure = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    // ---- round 3: the account's lock-out, its programs, the WSL version

    private void AttachAccountToTheJournal()
    {
        _account.Calls.Journal = _journal;
        _account.Calls.Prefix = "account ";
    }

    [Fact]
    public async Task ASessionUnlocksTheAccount_ChecksIt_ThenShutsDown_EndsItsPrograms_AndOnlyThenWritesTheSettings()
    {
        AttachAccountToTheJournal();
        _account.LockedOut = true;

        var session = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.Equal(["account clear-lockout", "wsl shutdown", "account end-programs", "settings write", "settings hold"], _journal);
        await session.DisposeAsync();

        Assert.Equal(["account clear-lockout", "wsl shutdown", "account end-programs", "settings write", "settings hold", "wsl shutdown", "account end-programs", "settings release"], _journal);
        Assert.Equal(1, _account.LockoutsCleared);
        Assert.False(_account.LockedOut);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("was locked out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAccountThatIsNotLockedOut_IsNotTouched_AndNothingIsSaidAboutIt()
    {
        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Equal(0, _account.LockoutsCleared);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("locked out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALockOutThatCannotBeCleared_StopsTheStart_BeforeAnyCommandRuns()
    {
        _account.LockedOut = true;
        _account.LockoutStays = true;

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("locked out", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_journal);
        _account.LockedOut = false;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ProgramsOfTheAccountThatStay_StopTheStart_BeforeTheSettingsAreWritten_AndFreeTheSandbox()
    {
        _account.EndProgramsFailure = new InvalidOperationException("programs of the account dbd-ci-wsl are still running after they were ended 6 times");

        var ex = await Assert.ThrowsAsync<JobEnvironmentException>(() => _sandbox.BeginAsync(CancellationToken.None));

        Assert.Contains("still running", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no Linux machine was started", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Empty(_settings.Writes);
        Assert.Equal(0, _settings.Holds);
        _account.EndProgramsFailure = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ProgramsThatStayAtTheEnd_AreLogged_YetTheHoldIsLetGo_AndTheSandboxIsFree()
    {
        var session = await _sandbox.BeginAsync(CancellationToken.None);
        _account.EndProgramsFailure = new InvalidOperationException("still running");

        await session.DisposeAsync();

        Assert.Equal(1, _settings.Releases);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("end the programs of the account", StringComparison.Ordinal) && l.Contains("still running", StringComparison.Ordinal));
        _account.EndProgramsFailure = null;
        await using var again = await _sandbox.BeginAsync(CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AShutdownThatFailsAtTheEnd_StillEndsTheProgramsOfTheAccount_AndReleasesTheHold()
    {
        var session = await _sandbox.BeginAsync(CancellationToken.None);
        var endedBefore = _account.ProgramsEnded;
        _wsl.ShutdownFailure = new InvalidOperationException("stuck");

        await session.DisposeAsync();

        Assert.Equal(1, _settings.Releases);
        Assert.Equal(endedBefore + 1, _account.ProgramsEnded);
    }

    [Fact]
    public async Task TheWslVersion_IsRememberedForTheStatus_WhenASessionStarts()
    {
        _wsl.VersionText = "WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1";

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Equal("WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1", _record.Version);
        Assert.Equal(1, _wsl.VersionAsks);
    }

    [Fact]
    public async Task AWslVersionThatCannotBeRead_IsLogged_AndNeverStopsTheStart()
    {
        _wsl.VersionFailure = new InvalidOperationException("wsl.exe did not answer");

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Null(_record.Version);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("wsl.exe did not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAnswerForTheVersion_RecordsNothing()
    {
        _wsl.VersionText = null;

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Null(_record.Version);
    }

    [Fact]
    public void TheRecordOfTheMachine_IsReachableThroughTheSandbox()
    {
        Assert.Same(_record, _sandbox.Record);
    }

    // ---- removing a machine

    [Fact]
    public async Task ARemovedMachine_IsStopped_Unregistered_AndCheckedGoneFromTheList()
    {
        _wsl.Distributions.Add("dbd-ci-job-20261029120000");

        var gone = await _sandbox.RemoveDistributionAsync("dbd-ci-job-20261029120000");

        Assert.True(gone);
        Assert.Equal(["wsl terminate dbd-ci-job-20261029120000", "wsl unregister dbd-ci-job-20261029120000"], _journal);
        Assert.Empty(_wsl.Distributions);
    }

    [Fact]
    public async Task AMachineThatIsStillListed_IsNotGone_AndItIsLogged()
    {
        _wsl.Distributions.Add("dbd-ci-job-20261029120000");
        _wsl.UnregisterFailure = new InvalidOperationException("in use");

        var gone = await _sandbox.RemoveDistributionAsync("dbd-ci-job-20261029120000");

        Assert.False(gone);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("in use", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("is still registered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachRemovalStepRunsEvenWhenTheOneBeforeFailed()
    {
        _wsl.Distributions.Add("m");
        _wsl.TerminateFailure = new InvalidOperationException("cannot stop");

        var gone = await _sandbox.RemoveDistributionAsync("m");

        Assert.True(gone);
        Assert.Contains("wsl unregister m", _journal);
    }

    /// <summary>L1: when the list cannot be read afterwards, removal is not "verified gone".</summary>
    [Fact]
    public async Task AListThatCannotBeRead_CountsAsNotGone()
    {
        _wsl.Distributions.Add("m");
        _wsl.ListFailure = new InvalidOperationException("wsl said no");

        var gone = await _sandbox.RemoveDistributionAsync("m");

        Assert.False(gone);
        Assert.Contains(_log.Lines, l => l.Contains("could not check that m is gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARemovalStepThatNeverAnswers_IsEndedAfterThreeMinutes_AndTheNextStepsRun()
    {
        _wsl.Distributions.Add("m");
        _wsl.TerminateHangs = true;

        var removing = _sandbox.RemoveDistributionAsync("m");
        _time.Advance(TimeSpan.FromMinutes(3));
        var gone = await removing.OrFail();

        Assert.True(gone);
        Assert.Contains("wsl unregister m", _journal);
        Assert.Contains(_log.Lines, l => l.Contains("Clean-up could not stop m", StringComparison.Ordinal));
    }

    // ---- removing a folder

    [Fact]
    public void AFolderThatIsGoneOrEmpty_CountsAsRemoved()
    {
        var missing = Path.Combine(_folder.Path, "missing");
        var empty = Path.Combine(_folder.Path, "empty");
        Directory.CreateDirectory(empty);

        Assert.True(_sandbox.RemoveFolderIfEmpty(missing));
        Assert.True(_sandbox.RemoveFolderIfEmpty(empty));
        Assert.False(Directory.Exists(empty));
    }

    [Fact]
    public void AFolderWithAnythingInIt_IsNeverDeleted_AndIsLogged()
    {
        var full = Path.Combine(_folder.Path, "full");
        Directory.CreateDirectory(Path.Combine(full, "sub"));
        File.WriteAllText(Path.Combine(full, "sub", "ext4.vhdx"), "disk");

        Assert.False(_sandbox.RemoveFolderIfEmpty(full));

        Assert.True(File.Exists(Path.Combine(full, "sub", "ext4.vhdx")));
        Assert.Contains(_log.Lines, l => l.Contains("could not remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyAnswerForTheVersion_RecordsNothing()
    {
        _wsl.VersionText = string.Empty;

        await using var session = await _sandbox.BeginAsync(CancellationToken.None);

        Assert.Null(_record.Version);
    }
}
