// Covers: FR-CI-09 (the programs the elevated loop starts get a short environment of their own: no WSLENV, no search path or temporary folder the owner can write to; command lines are
// quoted by the C run time rules and respect the 1024-character limit of the logon API), FR-CI-02 (a runner name is exactly dbd-linux- and 14 digits), security review 2026-10-30 M3 M4.
// docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class EnvironmentAndCommandLineTests
{
    [Theory]
    [InlineData("dbd-linux-20261029120000", true)]
    [InlineData("dbd-linux-20261029120000\n", false)]
    [InlineData("dbd-linux-2026102912000", false)]
    [InlineData("dbd-linux-202610291200000", false)]
    [InlineData("dbd-windows-20261029120000", false)]
    [InlineData("dbd-presence-build-box", false)]
    [InlineData("dbd-linux-x0261029120000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ARunnerNameThisProgramMakes_IsExactlyDbdLinuxAndFourteenDigits(string? name, bool expected) => Assert.Equal(expected, LocalCiDefaults.IsRunnerName(name));

    // ---- the environment of the programs the elevated loop starts

    private static readonly Dictionary<string, string> s_owner = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["windir"] = @"C:\Windows",
        ["USERPROFILE"] = @"C:\Users\owner",
        ["LOCALAPPDATA"] = @"C:\Users\owner\AppData\Local",
        ["APPDATA"] = @"C:\Users\owner\AppData\Roaming",
        ["ProgramData"] = @"C:\ProgramData",
        ["ProgramFiles"] = @"C:\Program Files",
        ["PATH"] = @"C:\Users\owner\bin;C:\Windows\System32;C:\Tools",
        ["TEMP"] = @"C:\Users\owner\AppData\Local\Temp",
        ["TMP"] = @"C:\Users\owner\AppData\Local\Temp",
        ["WSLENV"] = "GITHUB_TOKEN/u:ACTIONS_RUNNER_INPUT_JITCONFIG",
        ["WSL_UTF8"] = "1",
        ["GITHUB_TOKEN"] = "github_pat_secret",
        ["HTTPS_PROXY"] = "http://proxy",
        ["DOTNET_ROOT"] = @"C:\Users\owner\.dotnet",
        ["PSModulePath"] = @"C:\Users\owner\Documents\PowerShell\Modules",
        ["Path_Extra"] = "x",
    };

    [Fact]
    public void TheChildEnvironment_DropsWslenvAndAnythingNotOnTheShortList()
    {
        var environment = ChildEnvironment.Build(name => s_owner.GetValueOrDefault(name), @"C:\Windows", @"C:\ProgramData\DbdLocalCi\tmp");

        Assert.DoesNotContain("WSLENV", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("WSL_UTF8", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTPS_PROXY", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOTNET_ROOT", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PSModulePath", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path_Extra", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Users\owner", environment["USERPROFILE"]);
        Assert.Equal(@"C:\Users\owner\AppData\Local", environment["LOCALAPPDATA"]);
        Assert.Equal(@"C:\Windows", environment["SystemRoot"]);
    }

    [Fact]
    public void TheChildSearchPath_IsTheWindowsFoldersOnly_AndTheTemporaryFolderIsTheProtectedOne()
    {
        var environment = ChildEnvironment.Build(name => s_owner.GetValueOrDefault(name), @"/win", "/data/tmp");

        var path = environment["PATH"].Split(Path.PathSeparator);
        Assert.Equal([Path.Combine("/win", "System32"), "/win", Path.Combine("/win", "System32", "Wbem")], path);
        Assert.DoesNotContain(environment["PATH"], @"C:\Users\owner\bin", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/data/tmp", environment["TEMP"]);
        Assert.Equal("/data/tmp", environment["TMP"]);
    }

    [Fact]
    public void AVariableThatIsNotSetOrEmpty_IsNotInventedInTheChildEnvironment()
    {
        var environment = ChildEnvironment.Build(name => name == "USERPROFILE" ? string.Empty : null, @"C:\Windows", @"C:\t");

        Assert.DoesNotContain("USERPROFILE", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, environment.Count);
    }

    // ---- command lines

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("two\\\\\"quotes", "\"two\\\\\\\\\\\"quotes\"")]
    public void AnArgument_IsQuotedByTheRunTimeRules(string argument, string expected)
    {
        var line = CommandLineQuoting.Join(@"C:\p.exe", [argument]);

        Assert.Equal("\"C:\\p.exe\" " + expected, line);
    }

    [Fact]
    public void TheProgramIsAlwaysQuoted_AndArgumentsAreSeparatedBySpaces()
    {
        var line = CommandLineQuoting.Join(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-File", @"C:\ProgramData\DbdLocalCiJob-x\start.ps1"]);

        Assert.Equal("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -File C:\\ProgramData\\DbdLocalCiJob-x\\start.ps1", line);
        Assert.True(line.Length < CommandLineQuoting.LogonCommandLineLimit);
        Assert.Equal(1024, CommandLineQuoting.LogonCommandLineLimit);
    }

    [Fact]
    public void ACommandLineWithANulCharacter_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => CommandLineQuoting.Join("p.exe", ["a\0b"]));
    }
}
