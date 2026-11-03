// Covers: local CI round 3, red-team H1 (the owner reviews the isolation probe before anything is sent to this PC: every attempt is shown in plain words with its evidence and, where it worked, what that
// means; only a typed "yes" accepts, never "y" or Enter; a probe that worked anywhere recommends against accepting and says so; a probe that could not run cannot be accepted; the result and the acceptance
// are kept in the state file; a result for another setup or another WSL is called out of date; the probe command takes the variable away again when the result is not accepted), FR-CI-07 (the status shows
// the probe). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ProbeReviewTests
{
    internal static ProbeRig Rig(params string[] typed) => new(console: new FakeConsole(typed: typed));

    private static void Replace(ProbeRig rig, string id, string verdict, string evidence)
    {
        rig.Wsl.ProbeLines.RemoveAll(l => l.StartsWith($"ATTEMPT {id} ", StringComparison.Ordinal));
        rig.Wsl.ProbeLines.Add($"ATTEMPT {id} {verdict} {evidence}");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("  Yes  ")]
    public async Task OnlyTheTypedWordYes_Accepts(string typed)
    {
        using var rig = Rig(typed);

        var outcome = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.Accepted, outcome);
        var record = rig.State.Load().Probe!;
        Assert.True(record.Accepted);
        Assert.Equal(rig.Time.GetUtcNow(), record.AcceptedAt);
        Assert.Contains("Accepted.", rig.Console.Lines);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("")]
    [InlineData("yes please")]
    [InlineData("no")]
    [InlineData("ye")]
    [InlineData("1")]
    public async Task AnythingElse_DoesNotAccept_AndSendsNothing(string typed)
    {
        using var rig = Rig(typed);

        var outcome = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.Declined, outcome);
        Assert.False(rig.State.Load().Probe!.Accepted);
        Assert.Null(rig.State.Load().Probe!.AcceptedAt);
        Assert.Contains("Not accepted. Nothing will be sent to this PC.", rig.Console.Lines);
    }

    // ---- round 4, F8: a "yes" is for the result that was shown, not for whatever result is saved when it is typed

    private static void AnotherWindowSavesItsOwnResult(ProbeRig rig, string id) => rig.State.Update(s => s.Probe = new ProbeRecord
    {
        Id = id,
        At = rig.Time.GetUtcNow().AddMinutes(1),
        Outcome = "SomePossible",
        Recipe = "recipe-1",
        Attempts = [new ProbeAttemptRecord { Id = "gpu-device", Verdict = nameof(ProbeVerdict.Possible), Evidence = "the GPU device /dev/dxg exists" }],
    });

    [Fact]
    public async Task AYesTypedWhileAnotherWindowSavedANewerResult_AcceptsNothing_AndSaysSo()
    {
        using var rig = Rig("yes");
        rig.Console.OnReadLine = () => AnotherWindowSavesItsOwnResult(rig, "the-other-window");

        var outcome = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.Superseded, outcome);
        var record = rig.State.Load().Probe!;
        Assert.Equal("the-other-window", record.Id);
        Assert.False(record.Accepted);
        Assert.Null(record.AcceptedAt);
        Assert.Contains("Nothing was accepted. Run the probe again and decide on that result.", rig.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Accepted.", rig.Console.Lines);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("so nothing was accepted", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("The owner accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AfterAYesThatFoundANewerResult_ADecisionOnTheNewRunIsWrittenOntoThatRun()
    {
        using var rig = Rig("yes", "yes");
        var once = true;
        rig.Console.OnReadLine = () =>
        {
            if (once)
            {
                once = false;
                AnotherWindowSavesItsOwnResult(rig, "the-other-window");
            }
        };

        Assert.Equal(ProbeOutcome.Superseded, await rig.Review.RunAsync(CancellationToken.None));
        var second = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.Accepted, second);
        var record = rig.State.Load().Probe!;
        Assert.NotEqual("the-other-window", record.Id);
        Assert.True(record.Accepted);
    }

    [Fact]
    public async Task EveryRunSavesItsResultUnderAnIdOfItsOwn()
    {
        using var rig = Rig("", "");

        await rig.Review.RunAsync(CancellationToken.None);
        var first = rig.State.Load().Probe!.Id;
        await rig.Review.RunAsync(CancellationToken.None);
        var second = rig.State.Load().Probe!.Id;

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ASupersededProbeInSetup_StopsSetup_AndTakesTheVariableAway()
    {
        using var rig = new SetupRig(typed: ["yes"]);
        rig.Console.OnReadLine = () => rig.State.Update(s => s.Probe = new ProbeRecord { Id = "the-other-window", At = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), Outcome = "AllBlocked", Recipe = "x" });

        var outcome = await rig.Wizard.RunAsync(CancellationToken.None);

        Assert.Equal(SetupOutcome.Stopped, outcome);
        Assert.Contains("because another window of this program saved a newer probe result while you were reading this one", rig.Console.AllText, StringComparison.Ordinal);
        Assert.False(rig.Api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.DoesNotContain("install", rig.Scheduler.Calls);
        Assert.False(rig.State.Load().Probe!.Accepted);
    }

    [Fact]
    public void NothingTyped_IsNotYes()
    {
        Assert.False(ProbeAcceptance.IsYes(null));
        Assert.False(ProbeAcceptance.IsYes(string.Empty));
        Assert.True(ProbeAcceptance.IsYes("yes"));
    }

    [Fact]
    public async Task WhenEveryAttemptWasStopped_TheQuestionSaysHowToGoOn_AndTheReportListsThePlaces()
    {
        using var rig = Rig("yes");
        rig.Locations.Places.Add("C:\\Tools (folder in the system PATH): the account may make files. A program placed here can be run by other programs of this PC");

        await rig.Review.RunAsync(CancellationToken.None);

        var text = rig.Console.AllText;
        Assert.Contains("Every attempt was stopped.", text, StringComparison.Ordinal);
        Assert.Contains("Type yes to send the repository's Linux checks to this PC.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("I recommend you do NOT accept", text, StringComparison.Ordinal);
        Assert.Contains("C:\\Tools (folder in the system PATH)", text, StringComparison.Ordinal);
        Assert.Contains("BLOCKED   Mount a Windows drive as root and list C:\\Users", text, StringComparison.Ordinal);
        Assert.Contains("PENDING-ENV", text, StringComparison.Ordinal);
        Assert.Contains("third-party code", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheDriveCanBeMountedByRoot_TheReportSaysWhatThatMeans_AndRecommendsNotTurningItOn()
    {
        using var rig = Rig("");
        Replace(rig, "mount-windows-drive", "POSSIBLE", "root mounted C: and listed 4 entries in C:\\Users");

        await rig.Review.RunAsync(CancellationToken.None);

        var text = rig.Console.AllText;
        Assert.Contains("POSSIBLE  Mount a Windows drive as root and list C:\\Users", text, StringComparison.Ordinal);
        Assert.Contains("evidence: root mounted C: and listed 4 entries in C:\\Users", text, StringComparison.Ordinal);
        Assert.Contains("could mount your Windows drive and read and change everything the Windows account dbd-ci-wsl can", text, StringComparison.Ordinal);
        Assert.Contains("I recommend NOT turning the Linux checks on for this PC", text, StringComparison.Ordinal);
        Assert.Contains("hostFileSystemAccess=false", text, StringComparison.Ordinal);
        Assert.Contains("wsl --update", text, StringComparison.Ordinal);
        Assert.Contains("1 of 7 attempts worked.", text, StringComparison.Ordinal);
        Assert.Contains("I recommend you do NOT accept this.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Type yes to send the repository's Linux checks to this PC.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("write-windows-public", "Files can be written to Windows")]
    [InlineData("windows-programs", "could start Windows programs as the account dbd-ci-wsl")]
    [InlineData("gpu-device", "flaw in the graphics driver")]
    [InlineData("virtualization-device", "Nested virtualization is on")]
    [InlineData("shared-mounts", "Files of this PC are shared")]
    [InlineData("host-network", "talk to services on this PC")]
    public async Task EveryWayOutThatIsOpen_IsExplainedInPlainWords(string id, string words)
    {
        using var rig = Rig("");
        Replace(rig, id, "POSSIBLE", "open");

        await rig.Review.RunAsync(CancellationToken.None);

        Assert.Contains(words, rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAttemptThatCouldNotBeTried_IsNotCalledClosed()
    {
        using var rig = Rig("");
        Replace(rig, "host-network", "NOTTRIED", "the machine has no default route, so there was no network to try");

        await rig.Review.RunAsync(CancellationToken.None);

        var text = rig.Console.AllText;
        Assert.Contains("NOT TRIED  Reach this PC over the network", text, StringComparison.Ordinal);
        Assert.Contains("not shown to be closed", text, StringComparison.Ordinal);
        Assert.Contains("1 could not be tried.", text, StringComparison.Ordinal);
        Assert.Contains("I recommend you do NOT accept this.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeThatCouldNotRun_CannotBeAccepted_AndNothingIsAsked()
    {
        using var rig = Rig("yes", "yes");
        rig.Wsl.ProbeFailure = new InvalidOperationException("wsl.exe stopped answering");

        var outcome = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.CouldNotRun, outcome);
        var record = rig.State.Load().Probe!;
        Assert.False(record.Accepted);
        Assert.Equal("Incomplete", record.Outcome);
        Assert.Contains("The probe could not run, so nothing is shown to be closed.", rig.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Type yes", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("Nothing can be accepted without a result", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptThatEndedWithAnExitCode_CannotBeAccepted_EvenWhenEveryLineSaidBlocked()
    {
        using var rig = Rig("yes", "yes");
        rig.Wsl.ProbeExit = 137;

        var outcome = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.CouldNotRun, outcome);
        var record = rig.State.Load().Probe!;
        Assert.False(record.Accepted);
        Assert.Equal("Incomplete", record.Outcome);
        Assert.Contains("ended with exit code 137 instead of 0", rig.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Type yes", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResultIsKept_WithItsAttempts_ThePlaces_TheSetupItIsAboutAndTheWslVersion()
    {
        using var rig = Rig("yes");
        rig.Locations.Places.Add("C:\\Users\\Public (public folder): the account may make files");
        Replace(rig, "gpu-device", "POSSIBLE", "the GPU device /dev/dxg exists");
        rig.State.Update(s => s.WslVersionText = "WSL version: 2.4.8.0");

        await rig.Review.RunAsync(CancellationToken.None);

        var record = rig.State.Load().Probe!;
        Assert.Equal(rig.Time.GetUtcNow(), record.At);
        Assert.Equal("SomePossible", record.Outcome);
        Assert.Equal("recipe-1", record.Recipe);
        Assert.Equal("WSL version: 2.4.8.0", record.WslVersion);
        Assert.Equal(IsolationProbe.AttemptIds, record.Attempts.Select(a => a.Id).ToArray());
        Assert.Equal(nameof(ProbeVerdict.Possible), record.Attempts.Single(a => a.Id == "gpu-device").Verdict);
        Assert.Equal("the GPU device /dev/dxg exists", record.Attempts.Single(a => a.Id == "gpu-device").Evidence);
        Assert.Equal(["C:\\Users\\Public (public folder): the account may make files"], record.WritableLocations);
    }

    [Fact]
    public async Task ANewRun_ReplacesTheOldAcceptance_SoAWorseResultMustBeAcceptedAgain()
    {
        using var rig = Rig("yes", "");

        Assert.Equal(ProbeOutcome.Accepted, await rig.Review.RunAsync(CancellationToken.None));
        Assert.True(rig.State.Load().Probe!.Accepted);

        Assert.Equal(ProbeOutcome.Declined, await rig.Review.RunAsync(CancellationToken.None));
        Assert.False(rig.State.Load().Probe!.Accepted);
    }

    [Fact]
    public async Task AnAcceptance_IsWrittenToTheLog()
    {
        using var rig = Rig("yes");

        await rig.Review.RunAsync(CancellationToken.None);

        Assert.Contains(rig.Log.Lines, l => l.Contains("The owner accepted the result of the isolation probe (AllBlocked)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSummary_SaysWhatWorkedAndWhatCouldNotBeTried()
    {
        var attempts = new List<ProbeAttempt>
        {
            new("gpu-device", ProbeVerdict.Possible, "x"),
            new("virtualization-device", ProbeVerdict.Possible, "x"),
            new("host-network", ProbeVerdict.NotTried, "x"),
            new("shared-mounts", ProbeVerdict.Blocked, "x"),
        };

        Assert.Equal("2 of 4 attempts worked and 1 could not be tried.", ProbeText.Summary(new ProbeReport(attempts, WritableAudit.Empty, null)));
        Assert.Equal("Every attempt was stopped.", ProbeText.Summary(new ProbeReport([attempts[3]], WritableAudit.Empty, null)));
        Assert.Equal("The probe could not run, so nothing is shown to be closed.", ProbeText.Summary(new ProbeReport([], WritableAudit.Empty, "no")));
        Assert.Equal(string.Empty, ProbeText.Meaning(attempts[3]));
    }

    [Fact]
    public void EveryAttemptHasAPlainName_AndAnUnknownOneKeepsItsId()
    {
        foreach (var id in IsolationProbe.AttemptIds)
        {
            Assert.NotEqual(id, ProbeText.What(id));
            Assert.NotEmpty(ProbeText.Meaning(new ProbeAttempt(id, ProbeVerdict.Possible, "x")));
        }

        Assert.Equal("made-up", ProbeText.What("made-up"));
        Assert.Contains("open", ProbeText.Meaning(new ProbeAttempt("made-up", ProbeVerdict.Possible, "x")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoPlacesToListAtAll_TheReportSaysSo()
    {
        using var rig = Rig("yes");

        await rig.Review.RunAsync(CancellationToken.None);

        Assert.Contains("The audit looked at 12 places on this PC and found none the Windows account dbd-ci-wsl may write to besides its own folders.", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("What the audit looks at: the roots of the drives and their top-level folders, the Public folder, the Windows temp folder, the folders directly in Program Files and ProgramData, and the folders of the system PATH. It does not look deeper, and it does not follow links.", rig.Console.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", rig.Console.AllText, StringComparison.Ordinal);
    }

    // ---- an audit that could not look at every place is never "none" (round 4, F1)

    [Fact]
    public async Task AnAuditWithGaps_NamesThem_IsNeverReportedAsNone_AndTheQuestionRecommendsAgainstAcceptingUntilTheyAreLookedAt()
    {
        using var rig = Rig("");
        rig.Locations.PlacesLooked = 140;
        rig.Locations.Gaps.Add("C:\\System Volume Information (top-level folder): Attempted to perform an unauthorized operation.; with the backup right: the program does not hold the backup right");
        rig.Locations.Gaps.Add("D:\\ (removable drive): the file system could not be read: The device is not ready.");

        var outcome = await rig.Review.RunAsync(CancellationToken.None);

        Assert.Equal(ProbeOutcome.Declined, outcome);
        var text = rig.Console.AllText;
        Assert.Contains("The audit found no place the Windows account dbd-ci-wsl may write to among the 140 places it could look at. That is NOT \"none\": see below.", text, StringComparison.Ordinal);
        Assert.Contains("The audit is INCOMPLETE: 2 places could not be checked, so it is UNKNOWN whether the account may write there:", text, StringComparison.Ordinal);
        Assert.Contains("  C:\\System Volume Information (top-level folder): Attempted to perform an unauthorized operation.; with the backup right: the program does not hold the backup right", text, StringComparison.Ordinal);
        Assert.Contains("  D:\\ (removable drive): the file system could not be read: The device is not ready.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("besides its own folders", text, StringComparison.Ordinal);
        Assert.Contains("Every attempt was stopped, but the audit of the places the account may write to is INCOMPLETE (2 could not be checked).", text, StringComparison.Ordinal);
        Assert.Contains("Every attempt was stopped, but the list of places the account may write to is INCOMPLETE (see above), so it is UNKNOWN what a job that got out could change there. I recommend you do NOT accept this until you have looked at those places.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Type yes to send the repository's Linux checks to this PC.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuditThatCouldNotLookAtAnyPlace_SaysItIsUnknownWhichPlacesAreOpen()
    {
        using var rig = Rig("");
        rig.Locations.Failure = new InvalidOperationException("the drive list is broken");

        await rig.Review.RunAsync(CancellationToken.None);

        var text = rig.Console.AllText;
        Assert.Contains("The audit could not look at any place, so it is UNKNOWN which places the Windows account dbd-ci-wsl may write to.", text, StringComparison.Ordinal);
        Assert.Contains("the audit could not be made at all: ", text, StringComparison.Ordinal);
        Assert.Contains("the drive list is broken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("besides its own folders", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResultKeepsTheGaps_SoTheStatusCanSayThemLater_AndAnIncompleteAuditHasItsOwnOutcomeWord()
    {
        using var rig = Rig("yes");
        rig.Locations.PlacesLooked = 90;
        rig.Locations.Gaps.Add("C:\\$Recycle.Bin (top-level folder): access denied");

        await rig.Review.RunAsync(CancellationToken.None);

        var record = rig.State.Load().Probe!;
        Assert.Equal("AuditIncomplete", record.Outcome);
        Assert.Equal(90, record.AuditPlacesLooked);
        Assert.Equal(1, record.AuditGapCount);
        Assert.Equal([@"C:\$Recycle.Bin (top-level folder): access denied"], record.AuditGaps);
        Assert.True(record.Accepted);
    }

    [Fact]
    public async Task AGapIsRedactedLikeEveryOtherTextInTheStateFile()
    {
        using var rig = Rig("");
        rig.Redactor.Register(new SecretText("SECRET-FOLDER-NAME-0123456789"));
        var state = new StateStore(rig.Paths.StateFile, rig.Redactor);
        state.Update(s => s.Probe = new ProbeRecord { AuditGapCount = 1, AuditGaps = ["C:\\SECRET-FOLDER-NAME-0123456789 (top-level folder): access denied"] });

        Assert.Equal(["C:\\[hidden] (top-level folder): access denied"], state.Load().Probe!.AuditGaps);
    }

    [Fact]
    public void TheSummary_OfAnIncompleteAudit_IsNotEveryAttemptWasStopped_Alone()
    {
        var blocked = new ProbeAttempt("gpu-device", ProbeVerdict.Blocked, "x");
        var one = new WritableAudit([], 10, 1, ["x"]);
        var many = new WritableAudit([], 10, 30, ["x"]);

        Assert.Equal("Every attempt was stopped, but the audit of the places the account may write to is INCOMPLETE (1 could not be checked).", ProbeText.Summary(new ProbeReport([blocked], one, null)));
        Assert.Equal("Every attempt was stopped, but the audit of the places the account may write to is INCOMPLETE (30 could not be checked).", ProbeText.Summary(new ProbeReport([blocked], many, null)));
        Assert.Equal("1 of 1 attempts worked.", ProbeText.Summary(new ProbeReport([new ProbeAttempt("gpu-device", ProbeVerdict.Possible, "x")], one, null)));
        Assert.Contains("1 place could not be checked", string.Join("\n", ProbeText.AuditLines(one)), StringComparison.Ordinal);
        Assert.Contains("30 places could not be checked", string.Join("\n", ProbeText.AuditLines(many)), StringComparison.Ordinal);
    }

    // ---- the probe command

    internal static (ProbeCommand Command, FakeGitHubApi Api, FakeSecretStore Store) Command(ProbeRig rig, string? variable = "dbd-linux", bool token = true, FakeScheduler? scheduler = null)
    {
        var api = new FakeGitHubApi();
        if (variable is not null)
        {
            api.Variables["LINUX_RUNNER"] = variable;
        }

        var store = new FakeSecretStore();
        if (token)
        {
            store.Save(new SecretText("github_pat_11GOODGOODGOODGOODGOOD_abcdefghijklmnopqrstuvwxyz"));
        }

        return (new ProbeCommand(rig.Review, new RoutingRemover(rig.Console, api, store, rig.Log, rig.Redactor), rig.Console, scheduler ?? new FakeScheduler(), api, rig.Log, rig.Redactor), api, store);
    }

    [Fact]
    public async Task TheProbeCommand_WhenTheOwnerAccepts_ChangesNothing_AndExitsWithZero()
    {
        using var rig = Rig("yes");
        var (command, api, _) = Command(rig);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(command.Accepted);
        Assert.Equal("dbd-linux", api.Variables["LINUX_RUNNER"]);
        Assert.Contains("Nothing was changed on GitHub or on this PC", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeCommand_WhenTheOwnerDoesNotAccept_TakesTheVariableAway_AndExitsWithAnError()
    {
        using var rig = Rig("no");
        var (command, api, _) = Command(rig);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(command.Accepted);
        Assert.False(api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("you did not accept what the isolation probe found", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeCommand_WhenTheProbeCouldNotRun_AlsoTakesTheVariableAway()
    {
        using var rig = Rig();
        rig.Wsl.ProbeFailure = new InvalidOperationException("stopped");
        var (command, api, _) = Command(rig);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("the isolation probe could not give a result", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeCommand_WhenAnotherWindowSavedANewerResult_AcceptsNothing_AndTakesTheVariableAway()
    {
        using var rig = Rig("yes");
        rig.Console.OnReadLine = () => AnotherWindowSavesItsOwnResult(rig, "the-other-window");
        var (command, api, _) = Command(rig);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(command.Accepted);
        Assert.False(api.Variables.ContainsKey("LINUX_RUNNER"));
        Assert.Contains("another window of this program saved a newer probe result while you were reading this one", rig.Console.AllText, StringComparison.Ordinal);
        Assert.False(rig.State.Load().Probe!.Accepted);
    }

    [Fact]
    public async Task AVariableWithAnotherValue_IsTheOwnersOwn_AndStays()
    {
        using var rig = Rig("");
        var (command, api, _) = Command(rig, variable: "ubuntu-latest");

        await command.RunAsync(CancellationToken.None);

        Assert.Equal("ubuntu-latest", api.Variables["LINUX_RUNNER"]);
        Assert.Contains("not to this program's 'dbd-linux', so it was left alone", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASavedToken_TheCommandSaysToDeleteTheVariableByHand()
    {
        using var rig = Rig("");
        var (command, api, _) = Command(rig, token: false);

        await command.RunAsync(CancellationToken.None);

        Assert.Equal("dbd-linux", api.Variables["LINUX_RUNNER"]);
        Assert.Contains("No GitHub token is saved", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains("delete it on GitHub by hand", rig.Console.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGitHubFailureWhileTakingTheVariableAway_IsSaidAloud_NotHidden()
    {
        using var rig = Rig("");
        var (command, api, _) = Command(rig);
        api.DeleteVariableFailure = new GitHubApiException("boom", 500, isTransient: false);

        var exit = await command.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("I could not check or turn LINUX_RUNNER off", rig.Console.AllText, StringComparison.Ordinal);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("take away the variable LINUX_RUNNER", StringComparison.Ordinal));
    }

    [Fact]
    public void TheProbeExitCode_IsZeroOnlyWhenAccepted()
    {
        Assert.Equal(0, ExitCodes.ForProbe(true));
        Assert.Equal(1, ExitCodes.ForProbe(false));
    }
}
