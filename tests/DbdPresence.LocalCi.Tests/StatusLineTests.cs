// Covers: FR-CI-07 (the status in plain words) and local CI round 3: QA M-1 (a PROBLEM line when the variable sends checks to this PC and no loop runs). docs/owner/free-ci.md.
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class StatusLineTests
{
    private readonly FakeTimeProvider _time = TestTime.New();

    private StatusFacts Facts(bool loop = true, bool task = true, string? linuxRunner = "dbd-linux") =>
        new(_time.GetUtcNow(), TokenSaved: true, TaskExists: task, LoopRunning: loop, [], null, [], null, 0, null, BaseImageBuilt: true, Variables: new Dictionary<string, string?> { ["LINUX_RUNNER"] = linuxRunner });

    [Fact]
    public void ChecksSentToThisPc_WhileTheLoopRuns_AreOn()
    {
        var text = StatusReport.Build(Facts());

        Assert.Contains("Checks on this PC: ON.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Checks on this PC: PROBLEM", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChecksSentToThisPc_WhileNoLoopRuns_AreAPlainProblem_NotOn()
    {
        var text = StatusReport.Build(Facts(loop: false));

        Assert.Contains("Checks on this PC: PROBLEM. LINUX_RUNNER is set, so the Linux checks are sent to this PC, but the background loop is not running, so they wait for a runner nobody serves.", text, StringComparison.Ordinal);
        Assert.Contains("It starts when you sign in", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Checks on this PC: ON", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheSignInTaskIsMissingToo_TheProblemLineSaysSoAndOffersTheVariable()
    {
        var text = StatusReport.Build(Facts(loop: false, task: false));

        Assert.Contains("The sign-in task is missing: choose 1 (Set up), or delete the variable LINUX_RUNNER on GitHub.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheVariableOff_ANotRunningLoopIsNoProblemOfChecks()
    {
        var text = StatusReport.Build(Facts(loop: false, linuxRunner: null));

        Assert.Contains("Checks on this PC: OFF.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("wait for a runner nobody serves", text, StringComparison.Ordinal);
    }
}
