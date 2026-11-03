// Covers: FR-CI-07 (the status in plain words) and local CI round 3: red-team H1 (the status shows the last isolation probe in plain words: when it ran, what worked, whether the owner accepted it, that it
// is out of date when the setup or WSL changed, and that checks are sent here although it was not accepted; a PC that never ran one says UNKNOWN), L12 (the WSL version, the age of the Linux kernel of the
// last job, and the .NET runtime and build date of the program), FR-CI-10-style honesty (an age that is not known is not guessed). docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class StatusProbeTests : IDisposable
{
    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private StatusFacts Facts(ProbeRecord? probe = null, string? currentRecipe = null, WslFacts? wsl = null, BuildFacts? build = null, string? linuxRunner = "dbd-linux") =>
        new(
            _time.GetUtcNow(),
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
            Variables: new Dictionary<string, string?> { ["LINUX_RUNNER"] = linuxRunner },
            Probe: probe,
            CurrentProbeRecipe: currentRecipe,
            Wsl: wsl,
            Build: build);

    private ProbeRecord Probe(string outcome = "AllBlocked", bool accepted = true, string recipe = "recipe-1", params (string Id, string Verdict, string Evidence)[] open) =>
        new()
        {
            At = _time.GetUtcNow().AddDays(-2),
            Outcome = outcome,
            Recipe = recipe,
            Accepted = accepted,
            AcceptedAt = accepted ? _time.GetUtcNow().AddDays(-2) : null,
            Attempts = [.. IsolationProbe.AttemptIds.Select(id => open.FirstOrDefault(o => o.Id == id) is { Id: not null } o ? new ProbeAttemptRecord { Id = o.Id, Verdict = o.Verdict, Evidence = o.Evidence } : new ProbeAttemptRecord { Id = id, Verdict = "Blocked", Evidence = "closed" })],
        };

    // ---- the isolation probe

    [Fact]
    public void ANeverRunProbe_IsUnknown_WhenChecksAreNotSentHere_AndAProblemWhenTheyAre()
    {
        var off = StatusReport.Build(Facts(linuxRunner: null));
        var on = StatusReport.Build(Facts());

        Assert.Contains("Isolation probe: NOT RUN. Nobody has tried to get out of the Linux machine on this PC yet, so it is UNKNOWN how well it holds. Choose 5 (Isolation probe).", off, StringComparison.Ordinal);
        Assert.Contains("Isolation probe: PROBLEM. Nobody has tried to get out", on, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAcceptedProbeWhereEverythingWasStopped_IsShownWithItsDateAndAcceptance()
    {
        var text = StatusReport.Build(Facts(Probe(), currentRecipe: "recipe-1"));

        Assert.Contains("Isolation probe: at ", text, StringComparison.Ordinal);
        Assert.Contains("every attempt was stopped; you accepted it at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PROBLEM", text.Split('\n').Single(l => l.StartsWith("Isolation probe:", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("  POSSIBLE:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("older setup", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttemptThatWorkedOrCouldNotBeTried_IsListedWithItsEvidence()
    {
        var probe = Probe("SomePossible", true, "recipe-1", ("gpu-device", "Possible", "the GPU device /dev/dxg exists"), ("host-network", "NotTried", "no default route"));

        var text = StatusReport.Build(Facts(probe, currentRecipe: "recipe-1"));

        Assert.Contains("at least one attempt WORKED", text, StringComparison.Ordinal);
        Assert.Contains("  POSSIBLE: Use the GPU device (/dev/dxg) (the GPU device /dev/dxg exists)", text, StringComparison.Ordinal);
        Assert.Contains("  NOT TRIED: Reach this PC over the network (common ports) (no default route)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mount a Windows drive", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeTheOwnerDidNotAccept_WhileChecksAreSentHere_IsAProblem()
    {
        var text = StatusReport.Build(Facts(Probe(accepted: false), currentRecipe: "recipe-1"));

        Assert.Contains("you have NOT accepted it", text, StringComparison.Ordinal);
        Assert.Contains("PROBLEM: checks are sent to this PC although you have not accepted the result of the probe", text, StringComparison.Ordinal);

        var off = StatusReport.Build(Facts(Probe(accepted: false), currentRecipe: "recipe-1", linuxRunner: null));
        Assert.DoesNotContain("although you have not accepted", off, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeForAnotherSetup_IsOutOfDate()
    {
        var text = StatusReport.Build(Facts(Probe(recipe: "recipe-1"), currentRecipe: "recipe-2"));

        Assert.Contains("PROBLEM: the probe was run for an older setup of the Linux machine", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeBeforeAWslUpdate_IsOutOfDate()
    {
        var probe = Probe();
        probe.WslVersion = "WSL version: 2.4.8.0";
        var wsl = new WslFacts("WSL version: 2.5.1.0", _time.GetUtcNow(), null, null, null);

        var text = StatusReport.Build(Facts(probe, currentRecipe: "recipe-1", wsl: wsl));

        Assert.Contains("PROBLEM: WSL was updated or changed since the probe was run", text, StringComparison.Ordinal);
        var same = StatusReport.Build(Facts(probe, currentRecipe: "recipe-1", wsl: new WslFacts("WSL version: 2.4.8.0", _time.GetUtcNow(), null, null, null)));
        Assert.DoesNotContain("WSL was updated", same, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWritablePlaces_AreCounted_WithAnExample()
    {
        var probe = Probe();
        probe.WritableLocations = ["C:\\Tools (folder in the system PATH): the account may make files", "C:\\ (drive root): the account may make folders"];

        var text = StatusReport.Build(Facts(probe, currentRecipe: "recipe-1"));

        Assert.Contains("Places the Windows account dbd-ci-wsl may write to: 2, for example C:\\Tools", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteAudit_IsUnknown_NeverNone_AndTheOutcomeWordSaysSo()
    {
        var probe = Probe("AuditIncomplete");
        probe.AuditPlacesLooked = 90;
        probe.AuditGapCount = 3;
        probe.AuditGaps = [@"C:\System Volume Information (top-level folder): denied", "D:\\ (removable drive): not ready", "x"];

        var text = StatusReport.Build(Facts(probe, currentRecipe: "recipe-1"));

        Assert.Contains("every attempt was stopped, but the audit of the places the account may write to is INCOMPLETE", text, StringComparison.Ordinal);
        Assert.Contains(@"UNKNOWN: the audit of the places the account may write to is INCOMPLETE: 3 places could not be checked, for example C:\System Volume Information (top-level folder): denied. Look at the list the probe printed (choose 5 to run it again).", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Places the Windows account dbd-ci-wsl may write to: 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OneGap_IsSaidInTheSingular_AndACompleteAuditSaysNothingOfGaps()
    {
        var one = Probe();
        one.AuditGapCount = 1;
        one.AuditGaps = ["C:\\x (top-level folder): denied"];

        Assert.Contains("INCOMPLETE: 1 place could not be checked, for example C:\\x (top-level folder): denied.", StatusReport.Build(Facts(one, currentRecipe: "recipe-1")), StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", StatusReport.Build(Facts(Probe(), currentRecipe: "recipe-1")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteProbe_SaysNothingIsShownToBeClosed()
    {
        var text = StatusReport.Build(Facts(Probe("Incomplete", false), currentRecipe: "recipe-1"));

        Assert.Contains("it could not be completed, so nothing is shown to be closed", text, StringComparison.Ordinal);
    }

    // ---- WSL

    [Fact]
    public void WithoutAnyReading_WslIsNotSeenYet()
    {
        Assert.Contains("Windows Subsystem for Linux: not seen yet", StatusReport.Build(Facts()), StringComparison.Ordinal);
        Assert.Contains("Windows Subsystem for Linux: not seen yet", StatusReport.Build(Facts(wsl: new WslFacts(null, null, null, null, null))), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWslVersionOutput_IsShownLineByLine_WithTheTimeItWasRead()
    {
        var wsl = new WslFacts("WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1\nWindows version: 10.0.22631.4460", _time.GetUtcNow().AddHours(-3), null, null, null);

        var text = StatusReport.Build(Facts(wsl: wsl));

        Assert.Contains("Windows Subsystem for Linux (read at ", text, StringComparison.Ordinal);
        Assert.Contains("\n  WSL version: 2.4.8.0\n", text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("  Kernel version: 5.15.167.4-1", text, StringComparison.Ordinal);
        Assert.Contains("  Windows version: 10.0.22631.4460", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(180, false)]
    [InlineData(181, true)]
    [InlineData(400, true)]
    public void TheKernelAge_IsInDays_AndAWarningComesAfterHalfAYear(int days, bool warns)
    {
        var wsl = new WslFacts(null, null, "5.15.167.4-microsoft-standard-WSL2", _time.GetUtcNow().AddDays(-days), _time.GetUtcNow());

        var text = StatusReport.Build(Facts(wsl: wsl));

        Assert.Contains($"Linux kernel of the last job's machine: 5.15.167.4-microsoft-standard-WSL2, built {_time.GetUtcNow().AddDays(-days).UtcDateTime:yyyy-MM-dd} ({days} days old).", text, StringComparison.Ordinal);
        Assert.Equal(warns, text.Contains("WARNING: that is old", StringComparison.Ordinal));
        if (warns)
        {
            Assert.Contains("wsl --update", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AKernelWithoutABuildDate_HasAnAgeThatIsNotKnown_NeverGuessed()
    {
        var wsl = new WslFacts(null, null, "6.6.36.3-microsoft-standard-WSL2", null, _time.GetUtcNow());

        var text = StatusReport.Build(Facts(wsl: wsl));

        Assert.Contains("6.6.36.3-microsoft-standard-WSL2 (its build date is not known, so its age is not known)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING: that is old", text, StringComparison.Ordinal);
    }

    // ---- the program

    [Fact]
    public void TheProgramsRuntimeAndBuildDate_AreShown_WithTheNoteThatWindowsUpdateDoesNotUpdateIt()
    {
        var text = StatusReport.Build(Facts(build: new BuildFacts(".NET 10.0.0", _time.GetUtcNow().AddDays(-20))));

        Assert.Contains("This program: built on 2026-10-09 (20 days ago), with the .NET 10.0.0 runtime inside the file.", text, StringComparison.Ordinal);
        Assert.Contains("Windows Update does not update that runtime", text, StringComparison.Ordinal);
        Assert.Contains("Getting and checking the program", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownBuildDate_IsSaidSo()
    {
        var text = StatusReport.Build(Facts(build: new BuildFacts(".NET 10.0.0", null)));

        Assert.Contains("This program: build date not known, with the .NET 10.0.0 runtime inside the file.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("This program:", StatusReport.Build(Facts()), StringComparison.Ordinal);
    }

    // ---- the collector fills them from the state

    [Fact]
    public async Task TheCollector_TakesTheProbeTheWslFactsAndTheBuildFromTheStateAndTheCaller()
    {
        var state = new StateStore(_folder.Paths.StateFile);
        state.Update(s =>
        {
            s.Probe = Probe();
            s.WslVersionText = "WSL version: 2.4.8.0";
            s.WslVersionAt = _time.GetUtcNow();
            s.KernelRelease = "5.15";
            s.KernelBuilt = _time.GetUtcNow().AddDays(-5);
            s.KernelSeenAt = _time.GetUtcNow();
        });
        var collector = new StatusCollector(new FakeSecretStore(), new FakeScheduler(), new FakeGitHubApi(), state, _folder.Paths, _time, new Redactor(), currentProbeRecipe: () => "recipe-9", build: new BuildFacts(".NET 10.0.1", null));

        var facts = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal("recipe-1", facts.Probe!.Recipe);
        Assert.Equal("recipe-9", facts.CurrentProbeRecipe);
        Assert.Equal("WSL version: 2.4.8.0", facts.Wsl!.VersionText);
        Assert.Equal("5.15", facts.Wsl.KernelRelease);
        Assert.Equal(".NET 10.0.1", facts.Build!.RuntimeVersion);
    }

    // ---- the edges the mutation run of round 3 found open

    [Fact]
    public void ASetUpThatLacksItsTokenOrItsTask_SaysWhichOne()
    {
        var both = StatusReport.Build(Facts());
        var noTask = StatusReport.Build(Facts() with { TaskExists = false });
        var noToken = StatusReport.Build(Facts() with { TokenSaved = false });

        Assert.Contains("Set up: yes.", both, StringComparison.Ordinal);
        Assert.Contains("Set up: NO. The sign-in task is missing. Choose 1 (Set up).", noTask, StringComparison.Ordinal);
        Assert.Contains("Set up: NO. No GitHub token is saved. Choose 1 (Set up).", noToken, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyNoteAboutTheWeeklyWslUpdate_IsNotShown_WhetherOrNotWslHasBeenSeen()
    {
        var notSeen = StatusReport.Build(Facts() with { WslUpdateNote = string.Empty });
        var seen = StatusReport.Build(Facts(wsl: new WslFacts("WSL version: 2.4.8.0", _time.GetUtcNow(), null, null, null)) with { WslUpdateNote = string.Empty });
        var withNote = StatusReport.Build(Facts(wsl: new WslFacts("WSL version: 2.4.8.0", _time.GetUtcNow(), null, null, null)) with { WslUpdateNote = "updated at 2026-10-29: fine" });

        Assert.DoesNotContain("Weekly WSL update", notSeen, StringComparison.Ordinal);
        Assert.DoesNotContain("Weekly WSL update", seen, StringComparison.Ordinal);
        Assert.Contains("Weekly WSL update: updated at 2026-10-29: fine.", withNote, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeThatNeverKnewTheWslVersion_IsNotCalledOutOfDateBecauseOfIt()
    {
        var probe = Probe();
        probe.WslVersion = string.Empty;
        var wsl = new WslFacts("WSL version: 2.5.1.0", _time.GetUtcNow(), null, null, null);

        var text = StatusReport.Build(Facts(probe, currentRecipe: "recipe-1", wsl: wsl));

        Assert.DoesNotContain("WSL was updated or changed", text, StringComparison.Ordinal);
    }
}
