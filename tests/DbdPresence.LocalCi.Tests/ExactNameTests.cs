// Covers: FR-CI-11 and the owner's rule for names (every sweep, uninstall and status matches exactly the names the current program creates: a runner is dbd-linux- and 14 ASCII digits, a job's Linux
// machine is dbd-ci-job- and 14 digits, the base image machine is dbd-ci-base, a base build's folder is dbd-ci-base- and 14 digits; the persistent account dbd-ci-wsl is never matched by any leftover
// pattern and no leftover name is ever taken for the account). Both directions are tested. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ExactNameTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("dbd-linux-20261029120000", true)]
    [InlineData("dbd-linux-20261029120000\n", false)]
    [InlineData(" dbd-linux-20261029120000", false)]
    [InlineData("dbd-linux-2026102912000", false)]
    [InlineData("dbd-linux-202610291200000", false)]
    [InlineData("dbd-linux-x0261029120000", false)]
    [InlineData("DBD-LINUX-20261029120000", false)]
    [InlineData("dbd-windows-20261029120000", false)]
    [InlineData("dbd-secrets-20261029120000", false)]
    [InlineData("my-dbd-linux-20261029120000", false)]
    [InlineData("dbd-presence-build-box", false)]
    [InlineData("dbd-linux-٢٠٢٦١٠٢٩١٢٠٠٠٠", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ARunnerNameIsExactlyDbdLinuxAndFourteenAsciiDigits(string? name, bool expected) => Assert.Equal(expected, LocalCiDefaults.IsRunnerName(name));

    [Theory]
    [InlineData("dbd-ci-job-20261029120000", true)]
    [InlineData("dbd-ci-job-20261029120000\n", false)]
    [InlineData("dbd-ci-job-2026102912000", false)]
    [InlineData("dbd-ci-job-202610291200000", false)]
    [InlineData("xdbd-ci-job-20261029120000", false)]
    [InlineData("dbd-ci-job-", false)]
    [InlineData("dbd-ci-job-1", false)]
    [InlineData("dbd-ci-base-20261029120000", false)]
    [InlineData("dbd-ci-wsl", false)]
    [InlineData("dbd-ci-job-٢٠٢٦١٠٢٩١٢٠٠٠٠", false)]
    [InlineData("Ubuntu", false)]
    [InlineData(null, false)]
    public void AJobsMachineIsExactlyDbdCiJobAndFourteenAsciiDigits(string? name, bool expected) => Assert.Equal(expected, LocalCiDefaults.IsJobDistributionName(name));

    [Theory]
    [InlineData("dbd-ci-base-20261029120000", true)]
    [InlineData("dbd-ci-base-20261029120000\n", false)]
    [InlineData("dbd-ci-base", false)]
    [InlineData("dbd-ci-base-1", false)]
    [InlineData("dbd-ci-job-20261029120000", false)]
    [InlineData("dbd-ci-wsl", false)]
    [InlineData(null, false)]
    public void ABaseBuildsFolderIsExactlyDbdCiBaseAndFourteenDigits(string? name, bool expected) => Assert.Equal(expected, LocalCiDefaults.IsBuildFolderName(name));

    [Theory]
    [InlineData("dbd-ci-base", true)]
    [InlineData("DBD-CI-BASE", false)]
    [InlineData("dbd-ci-base ", false)]
    [InlineData("dbd-ci-base2", false)]
    [InlineData("dbd-ci-base-20261029120000", false)]
    [InlineData("dbd-ci-wsl", false)]
    [InlineData(null, false)]
    public void TheBaseImageMachineIsExactlyDbdCiBase(string? name, bool expected) => Assert.Equal(expected, LocalCiDefaults.IsBaseDistributionName(name));

    [Theory]
    [InlineData("dbd-ci-wsl", true)]
    [InlineData("DBD-CI-WSL", true)]
    [InlineData("Dbd-Ci-Wsl", true)]
    [InlineData(" dbd-ci-wsl", false)]
    [InlineData("dbd-ci-wsl ", false)]
    [InlineData("dbd-ci-wsl\n", false)]
    [InlineData("dbd-ci-wsl2", false)]
    [InlineData("xdbd-ci-wsl", false)]
    [InlineData("dbd-ci-ws", false)]
    [InlineData("dbd-ci-wsl-20261029120000", false)]
    [InlineData("dbd-ci-job-20261029120000", false)]
    [InlineData("dbd-ci-base", false)]
    [InlineData("dbd-ci-20261029120000", false)]
    [InlineData("dbd-linux-20261029120000", false)]
    [InlineData("runner", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheAccountIsExactlyDbdCiWsl(string? name, bool expected) => Assert.Equal(expected, LocalCiDefaults.IsWslAccountName(name));

    /// <summary>The coordinator's rule, one direction: no leftover pattern ever matches the persistent account, so no sweep can take it for a disposable leftover.</summary>
    [Fact]
    public void NoLeftoverPattern_MatchesTheAccount()
    {
        var account = LocalCiDefaults.WslAccountName;

        Assert.False(LocalCiDefaults.IsJobDistributionName(account));
        Assert.False(LocalCiDefaults.IsBaseDistributionName(account));
        Assert.False(LocalCiDefaults.IsBuildFolderName(account));
        Assert.False(LocalCiDefaults.IsRunnerName(account));
        Assert.False(LocalCiDefaults.IsJobDistributionName(account.ToUpperInvariant()));
        Assert.Equal("dbd-ci-wsl", account);
    }

    /// <summary>The other direction: nothing the program creates for a job, a build or a runner is ever taken for the account.</summary>
    [Fact]
    public void NoNameTheProgramMakesForAJobIsTakenForTheAccount()
    {
        var made = new[]
        {
            LocalCiDefaults.RunnerName(s_now),
            LocalCiDefaults.JobDistributionFor(LocalCiDefaults.RunnerName(s_now)),
            LocalCiDefaults.BuildFolderFor(s_now),
            LocalCiDefaults.BaseDistribution,
            LocalCiDefaults.JobDistributionPrefix + "20261029120000",
        };

        Assert.All(made, name => Assert.False(LocalCiDefaults.IsWslAccountName(name), name));
    }

    [Fact]
    public void TheNamesTheProgramMakes_AreMatchedByTheirOwnPatternAndNoOther()
    {
        var runner = LocalCiDefaults.RunnerName(s_now);
        var job = LocalCiDefaults.JobDistributionFor(runner);
        var build = LocalCiDefaults.BuildFolderFor(s_now);

        Assert.Equal("dbd-linux-20261029120000", runner);
        Assert.Equal("dbd-ci-job-20261029120000", job);
        Assert.Equal("dbd-ci-base-20261029120000", build);
        Assert.True(LocalCiDefaults.IsRunnerName(runner));
        Assert.True(LocalCiDefaults.IsJobDistributionName(job));
        Assert.True(LocalCiDefaults.IsBuildFolderName(build));
        Assert.True(LocalCiDefaults.IsBaseDistributionName(LocalCiDefaults.BaseDistribution));
        Assert.False(LocalCiDefaults.IsJobDistributionName(runner));
        Assert.False(LocalCiDefaults.IsRunnerName(job));
        Assert.False(LocalCiDefaults.IsBuildFolderName(job));
        Assert.False(LocalCiDefaults.IsJobDistributionName(build));
        Assert.False(LocalCiDefaults.IsBaseDistributionName(build));
    }

    [Fact]
    public void TheTimeStampsAreUtc_SoAnOwnersOtherTimeZoneChangesNothing()
    {
        var local = new DateTimeOffset(2026, 10, 29, 15, 30, 45, TimeSpan.FromHours(3));

        Assert.Equal("dbd-linux-20261029123045", LocalCiDefaults.RunnerName(local));
        Assert.Equal("dbd-ci-base-20261029123045", LocalCiDefaults.BuildFolderFor(local));
    }

    [Fact]
    public void TheVariablesThisProgramSets_AndTheOnesItNeverServes_AreDisjoint()
    {
        Assert.Equal(["LINUX_RUNNER"], LocalCiDefaults.Variables.Keys);
        Assert.Equal("dbd-linux", LocalCiDefaults.Variables["LINUX_RUNNER"]);
        Assert.Equal(["WINDOWS_RUNNER", "SECRETS_LINUX_RUNNER", "SECRETS_WINDOWS_RUNNER"], LocalCiDefaults.NotServedVariables);
        Assert.Empty(LocalCiDefaults.NotServedVariables.Intersect(LocalCiDefaults.Variables.Keys));
    }
}
