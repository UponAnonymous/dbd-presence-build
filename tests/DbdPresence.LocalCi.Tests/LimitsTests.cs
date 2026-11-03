// Covers: FR-CI-02, FR-CI-03, FR-CI-04, FR-CI-10 and local CI round 4 QA M-4 (R12): the limits and sizes the guide names are pinned as numbers, not as the constants themselves, so a changed value
// fails here and in the guide's table at once; the edges of a runner's output (the 5,000th line is kept, the 5,001st is not, and the warning comes once), the exact edge of the 14-hour rule, of the
// 5-second disk look and of the 3 GB stop. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class LimitsTests
{
    [Fact]
    public void TheTimesTheGuideNames_AreTheseNumbers()
    {
        Assert.Equal(TimeSpan.FromMinutes(120), LocalCiDefaults.JobTimeout);
        Assert.Equal(TimeSpan.FromHours(12), LocalCiDefaults.MaximumWait);
        Assert.Equal(TimeSpan.FromSeconds(20), LocalCiDefaults.PollInterval);
        Assert.Equal(TimeSpan.FromDays(1), LocalCiDefaults.RunnerCheckInterval);
        Assert.Equal(TimeSpan.FromDays(7), LocalCiDefaults.BaseImageMaximumAge);
        Assert.Equal(TimeSpan.FromDays(7), LocalCiDefaults.TokenWarning);
        Assert.Equal(TimeSpan.FromMinutes(30), LocalCiDefaults.SelfTestTimeout);
        Assert.Equal(TimeSpan.FromDays(180), LocalCiDefaults.KernelOldAfter);
        Assert.Equal(TimeSpan.FromDays(7), WeeklyWslUpdate.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), LinuxJobHost.DiskCheckEvery);
    }

    [Fact]
    public void TheSizesAndCountsTheGuideNames_AreTheseNumbers()
    {
        Assert.Equal(5000, LocalCiDefaults.RunnerOutputLinesKept);
        Assert.Equal(600L * 1024 * 1024, LocalCiDefaults.MaximumRunnerPackageBytes);
        Assert.Equal(25L * 1024 * 1024 * 1024, LocalCiDefaults.MinimumFreeDiskBytes);
        Assert.Equal(3L * 1024 * 1024 * 1024, LinuxJobHost.MinimumFreeBytesDuringAJob);
        Assert.Equal(8L * 1024 * 1024, GitHubRestClient.MaximumAnswerBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), GitHubRestClient.DefaultReadInactivityLimit);
        Assert.Equal(14, LocalCiDefaults.LogRetentionDays);
        Assert.Equal(2, LocalCiDefaults.RunnerVersionsKept);
        Assert.Equal(19041, LocalCiDefaults.MinimumWindowsBuild);
        Assert.Equal(1, LocalCiDefaults.RunnerGroupId);
    }

    [Fact]
    public void TheNamesOfTheProgramsOfTheGame_AreTheThreeTheGuideNames()
    {
        Assert.Equal(["DeadByDaylight-Win64-Shipping", "DeadByDaylight-EGS-Shipping", "DeadByDaylight"], LocalCiDefaults.GameProcessNames);
    }

    // ---- a runner's output: the 5,000th line is kept, the 5,001st is not

    [Theory]
    [InlineData(4999, 4999, 0)]
    [InlineData(5000, 5000, 0)]
    [InlineData(5001, 5000, 1)]
    [InlineData(12000, 5000, 1)]
    public void ARunnersOutput_KeepsFiveThousandLines_AndSaysOnceWhenItStops(int printed, int kept, int warnings)
    {
        var redactor = new Redactor();
        var log = new MemoryCiLog(redactor);
        var output = new RunnerOutput(log);

        for (var i = 0; i < printed; i++)
        {
            output.Line("line " + i);
        }

        Assert.Equal(kept, log.Lines.Count(l => l.Contains("runner: line ", StringComparison.Ordinal)));
        Assert.Equal(warnings, log.Lines.Count(l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("printed more than 5000 lines; the rest is not logged", StringComparison.Ordinal)));
        if (printed > 0)
        {
            Assert.Contains(log.Lines, l => l.EndsWith("runner: line 0", StringComparison.Ordinal));
        }
    }
}
