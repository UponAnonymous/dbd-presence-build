// Covers: local CI round 3, QA M-4 (the exit codes of set-up, uninstall and the test check were unpinned: X1, X2, X3), FR-CI-01 (set-up succeeds only after a test job ran on this PC and passed),
// FR-CI-07 (the test check), FR-CI-08 (uninstall succeeds only when nothing is left to do by hand). The mapping is a platform-neutral class; the Windows start-up code only calls it.
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi.Tests;

public sealed class ExitCodeTests
{
    [Fact]
    public void TheCodes_AreZeroOneAndTwo_AsDocumented()
    {
        Assert.Equal(0, ExitCodes.Ok);
        Assert.Equal(1, ExitCodes.Failed);
        Assert.Equal(2, ExitCodes.Usage);
    }

    /// <summary>X1: set-up exits 0 only when it ended done AND a test job ran on this PC and passed.</summary>
    [Theory]
    [InlineData(SetupOutcome.Done, true, 0)]
    [InlineData(SetupOutcome.Done, false, 1)]
    [InlineData(SetupOutcome.Stopped, true, 1)]
    [InlineData(SetupOutcome.Stopped, false, 1)]
    [InlineData(SetupOutcome.RestartNeeded, true, 1)]
    [InlineData(SetupOutcome.RestartNeeded, false, 1)]
    public void Setup_ExitsZeroOnlyWhenItIsDoneAndProvenOnThisPc(SetupOutcome outcome, bool proven, int expected) =>
        Assert.Equal(expected, ExitCodes.ForSetup(outcome, proven));

    /// <summary>X2: uninstall exits 0 only when everything was removed; the owner saying "no" and a step left for the owner are errors.</summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Uninstall_ExitsZeroOnlyWhenNothingIsLeftForTheOwner(bool completed, int expected) =>
        Assert.Equal(expected, ExitCodes.ForUninstall(completed));

    /// <summary>X3: the test check exits 0 only when its job ran on this PC and passed; every other outcome, and no outcome at all, is an error.</summary>
    [Theory]
    [InlineData(SelfTestOutcome.PassedOnThisPc, 0)]
    [InlineData(SelfTestOutcome.PassedOnGitHub, 1)]
    [InlineData(SelfTestOutcome.Failed, 1)]
    [InlineData(SelfTestOutcome.FailedToStart, 1)]
    [InlineData(SelfTestOutcome.NotPickedUp, 1)]
    [InlineData(SelfTestOutcome.Cancelled, 1)]
    public void TheTestCheck_ExitsZeroOnlyWhenItsJobRanOnThisPcAndPassed(SelfTestOutcome outcome, int expected) =>
        Assert.Equal(expected, ExitCodes.ForSelfTest(outcome));

    [Fact]
    public void ATestCheckWithNoResult_IsAnError() => Assert.Equal(1, ExitCodes.ForSelfTest(null));

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void TheProbe_ExitsZeroOnlyWhenTheOwnerAcceptedWhatItFound(bool accepted, int expected) =>
        Assert.Equal(expected, ExitCodes.ForProbe(accepted));

    [Theory]
    [InlineData(LocalCiCommand.Help, 0)]
    [InlineData(LocalCiCommand.Unknown, 2)]
    public void WordsThatAreNotACommand_ExitWithUsage_ButTheHelpTextExitsZero(LocalCiCommand command, int expected) =>
        Assert.Equal(expected, ExitCodes.ForUnstarted(command));

    /// <summary>The Windows start-up code uses the mapping and carries no copy of it that could drift.</summary>
    [Fact]
    public void TheStartUpCode_UsesTheMapping_AndHoldsNoOwnCopyOfIt()
    {
        var application = File.ReadAllText(Path.Combine(TestSources.ToolFolder(), "Windows", "LocalCiApplication.cs"));

        Assert.Contains("ExitCodes.ForSetup(", application, StringComparison.Ordinal);
        Assert.Contains("ExitCodes.ForUninstall(", application, StringComparison.Ordinal);
        Assert.Contains("ExitCodes.ForSelfTest(", application, StringComparison.Ordinal);
        Assert.Contains("ExitCodes.ForUnstarted(", application, StringComparison.Ordinal);
        Assert.DoesNotContain("? Ok : Failed", application, StringComparison.Ordinal);
        Assert.DoesNotContain("? Failed : Ok", application, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(application, @"const int (Ok|Failed|Usage) = \d"), "the start-up code must not define its own exit numbers");
    }

    /// <summary>Round 4, QA M-1: the commands that take minutes show their log lines on the console as they happen; the test check (menu 3) used to be silent for up to 30 minutes.</summary>
    [Fact]
    public void TheCommandsThatTakeMinutes_ShowTheirLogOnTheConsole_AsItHappens()
    {
        var application = File.ReadAllText(Path.Combine(TestSources.ToolFolder(), "Windows", "LocalCiApplication.cs"));

        Assert.Contains("parsed.Command is LocalCiCommand.Setup or LocalCiCommand.SelfTest or LocalCiCommand.Probe ? Console.Out : null", application, StringComparison.Ordinal);
        Assert.Contains("merge", application[application.IndexOf("Starting one test check", StringComparison.Ordinal)..], StringComparison.OrdinalIgnoreCase);
    }
}
