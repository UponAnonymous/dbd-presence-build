// Covers: local CI round 3, M3 (the boot id of the last job's machine and its kernel are kept in the state file between runs, so the next job's machine can be proven to be another one), L12 (the WSL
// version text is kept for the status), L5 (a leftover that was removed is taken off the list and nothing else is), L11 (the lock-out flag is the only flag the loop clears). docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class MachineRecordTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = TestTime.New();

    public void Dispose() => _folder.Dispose();

    private StateStore NewState() => new(_folder.Paths.StateFile);

    [Fact]
    public void TheBootIdAndKernel_SurviveARestart_BecauseTheyAreInTheStateFile()
    {
        var record = new StateMachineRecord(NewState(), _time);
        Assert.Null(record.LastBootId);

        record.RecordBoot("6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab", "5.15.167.4-microsoft-standard-WSL2", new DateTimeOffset(2024, 11, 5, 0, 21, 55, TimeSpan.Zero));

        var after = new StateMachineRecord(NewState(), _time);
        Assert.Equal("6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab", after.LastBootId);
        var state = NewState().Load();
        Assert.Equal("5.15.167.4-microsoft-standard-WSL2", state.KernelRelease);
        Assert.Equal(new DateTimeOffset(2024, 11, 5, 0, 21, 55, TimeSpan.Zero), state.KernelBuilt);
        Assert.Equal(_time.GetUtcNow(), state.KernelSeenAt);
    }

    [Fact]
    public void ANewBoot_ReplacesTheOldOne_AndAKernelWithoutADateKeepsNoOldDate()
    {
        var record = new StateMachineRecord(NewState(), _time);
        record.RecordBoot("11111111-1111-4111-8111-111111111111", "5.15", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _time.Advance(TimeSpan.FromHours(1));

        record.RecordBoot("22222222-2222-4222-8222-222222222222", "6.6", null);

        var state = NewState().Load();
        Assert.Equal("22222222-2222-4222-8222-222222222222", state.LastBootId);
        Assert.Equal("6.6", state.KernelRelease);
        Assert.Null(state.KernelBuilt);
        Assert.Equal(_time.GetUtcNow(), state.KernelSeenAt);
    }

    [Fact]
    public void TheWslVersionText_IsKeptWithTheTimeItWasRead()
    {
        var record = new StateMachineRecord(NewState(), _time);

        record.RecordVersion("WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1");

        var state = NewState().Load();
        Assert.Equal("WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1", state.WslVersionText);
        Assert.Equal(_time.GetUtcNow(), state.WslVersionAt);
    }

    [Fact]
    public void ASecretInTheKeptTexts_NeverReachesTheFile_WhenTheStoreHasARedactor()
    {
        var redactor = new Redactor();
        redactor.Register("Registered-Value-ZYX-0123456789");
        var state = new StateStore(_folder.Paths.StateFile, redactor);
        var record = new StateMachineRecord(state, _time);

        record.RecordVersion("WSL version: Registered-Value-ZYX-0123456789");
        record.RecordBoot("6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab", "kernel-Registered-Value-ZYX-0123456789", null);

        var text = File.ReadAllText(_folder.Paths.StateFile);
        Assert.DoesNotContain("Registered-Value-ZYX-0123456789", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsRecordedForNoText()
    {
        var record = new StateMachineRecord(NewState(), _time);

        Assert.Throws<ArgumentException>(() => record.RecordVersion(string.Empty));
        Assert.Throws<ArgumentException>(() => record.RecordBoot(string.Empty, null, null));
        Assert.Null(NewState().Load().LastBootId);
    }

    // ---- the list of leftovers

    [Fact]
    public void AResolvedLeftover_IsTakenOffTheList_OnlyTheEntryOfThatKindAndExactlyThatSubject_AndNothingElse()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);
        residue.Sweep([Left.Machine("dbd-ci-job-20261029110000"), Left.Folder("C:\\x\\dbd-ci-job-20261029110000"), Left.Machine("dbd-ci-job-20261028090000")]);

        residue.Resolve(ResidueKinds.LinuxMachine, "dbd-ci-job-20261029110000");

        Assert.Equal(["the folder C:\\x\\dbd-ci-job-20261029110000 is not empty (delete it by hand when no Linux machine uses it)", "the Linux machine dbd-ci-job-20261028090000 could not be removed"], state.Load().Residue);
    }

    [Fact]
    public void ASubjectThatIsOnNoEntry_ChangesNothing_AndAnEmptySubjectIsRefused()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);
        residue.Sweep([Left.Machine("a")]);

        residue.Resolve(ResidueKinds.LinuxMachine, "somewhere-else");

        Assert.Equal(["the Linux machine a could not be removed"], state.Load().Residue);
        Assert.Throws<ArgumentException>(() => residue.Resolve(ResidueKinds.LinuxMachine, string.Empty));
        Assert.Throws<ArgumentException>(() => residue.Resolve(string.Empty, "a"));
    }

    [Fact]
    public void AnEntryIsNeverTakenOffBecauseItsTextContainsTheSubject_OnlyBecauseItsSubjectIsIt()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);
        residue.Sweep([Left.Machine("dbd-ci-probe"), Left.Folder("C:\\distros\\dbd-ci-probe-20261030"), Left.Marker("dbd-ci-probe-0123456789ab.txt")]);

        residue.Resolve(ResidueKinds.LinuxMachine, "dbd-ci-probe");

        Assert.Equal(
            [(ResidueKinds.Folder, "C:\\distros\\dbd-ci-probe-20261030"), (ResidueKinds.MarkerFile, "dbd-ci-probe-0123456789ab.txt")],
            state.Load().ResidueItems.Select(i => (i.Kind, i.Subject)).ToArray());
    }

    [Fact]
    public void TheSameSubjectOfAnotherKind_IsAnotherEntry_AndNamesAreMatchedWithoutRegardToCase()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);
        residue.Sweep([Left.Machine("Dbd-CI-Job-1"), Left.Folder("dbd-ci-job-1")]);

        residue.Resolve(ResidueKinds.LinuxMachine, "DBD-ci-job-1");

        Assert.Equal(ResidueKinds.Folder, Assert.Single(state.Load().ResidueItems).Kind);
    }

    [Fact]
    public void AnEntryAddedAgainForTheSameSubject_ReplacesTheOldOne_AndIsNotRepeated()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);

        residue.Add(ResidueKinds.Folder, "C:\\x", "the folder C:\\x could not be removed");
        residue.Add(ResidueKinds.Folder, "c:\\X", "the folder C:\\x is not empty");
        residue.Add(ResidueKinds.LinuxMachine, "C:\\x", "a machine with that name");

        Assert.Equal(["the folder C:\\x is not empty", "a machine with that name"], state.Load().Residue);
        Assert.Throws<ArgumentException>(() => residue.Add(ResidueKinds.Folder, "C:\\x", string.Empty));
        Assert.Throws<ArgumentException>(() => residue.Add(ResidueKinds.Folder, string.Empty, "text"));
        Assert.Throws<ArgumentException>(() => residue.Add(string.Empty, "C:\\x", "text"));
    }

    [Fact]
    public void ASweep_ReplacesTheEntriesAboutMachinesFoldersAndChecks_ButKeepsTheOnesAboutFilesOnWindows()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);
        residue.Sweep([Left.Machine("old"), Left.Folder("C:\\old"), Left.Words("an old check"), Left.Marker("dbd-ci-probe-0123456789ab.txt")]);

        residue.Sweep([Left.Machine("new")]);

        Assert.Equal(
            [(ResidueKinds.MarkerFile, "dbd-ci-probe-0123456789ab.txt"), (ResidueKinds.LinuxMachine, "new")],
            state.Load().ResidueItems.Select(i => (i.Kind, i.Subject)).ToArray());
        Assert.Throws<ArgumentNullException>(() => residue.Sweep(null!));
    }

    [Fact]
    public void WithoutAMarker_TheEntriesAboutFilesOnWindowsAreLeftAsTheyAre()
    {
        var state = NewState();
        var residue = new ResidueRecorder(state);
        residue.Add(ResidueKinds.MarkerFile, "dbd-ci-probe-0123456789ab.txt", "the marker file is there");

        residue.RecheckMarkerFiles();

        Assert.Single(state.Load().ResidueItems);
    }

    [Fact]
    public void ASubjectThatIsNotTheNameOfAMarkerFile_IsNeverLookedFor_StaysOnTheList_AndIsLogged()
    {
        var state = NewState();
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var residue = new ResidueRecorder(state, new ProbeMarkerFile([_folder.Path], log), log);
        residue.Add(ResidueKinds.MarkerFile, "..\\something-else.txt", "an entry somebody edited");

        residue.RecheckMarkerFiles();

        Assert.Single(state.Load().ResidueItems);
        Assert.Contains(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("not the name of a probe marker file", StringComparison.Ordinal));
    }

    // ---- the account's flags

    [Fact]
    public void TheLockOut_IsTheOnlyFlagTheLoopClears()
    {
        const uint All = WslAccountPolicy.FlagScript | WslAccountPolicy.FlagAccountDisable | WslAccountPolicy.FlagLockout | WslAccountPolicy.FlagPasswordCannotChange | WslAccountPolicy.FlagDontExpirePassword;

        var cleared = WslAccountPolicy.WithoutLockout(All);

        Assert.Equal(All & ~WslAccountPolicy.FlagLockout, cleared);
        Assert.True((cleared & WslAccountPolicy.FlagAccountDisable) != 0, "a switched-off account stays switched off");
        Assert.False(WslAccountPolicy.IsLockedOut(cleared));
        Assert.True(WslAccountPolicy.IsLockedOut(All));
        Assert.Equal(WslAccountPolicy.DesiredFlags, WslAccountPolicy.WithoutLockout(WslAccountPolicy.DesiredFlags));
        Assert.False(WslAccountPolicy.IsLockedOut(0));
    }
}
