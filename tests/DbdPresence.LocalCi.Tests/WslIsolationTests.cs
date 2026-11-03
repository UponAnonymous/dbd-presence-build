// Covers: FR-CI-09 and red-team F2/F8, round 3 H1, M1 and L3 (the account's .wslconfig has exactly the twelve required settings and nothing else, and the file is byte for byte what the program writes: the check
// refuses a missing file, a missing, changed or doubled setting, another section, another setting, a setting outside any section, a line that is not a setting, an encoding mark, other line endings, spacing
// and comments; the fail-closed isolation check inside every job distribution: not root, no Windows drive, no Windows programs, no password-less sudo, and a check that does not report counts as failed).
// docs/owner/free-ci.md.
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class WslIsolationTests
{
    private static readonly WslHostFacts s_facts = new(16L * 1024 * 1024 * 1024, 8);

    private static readonly string[] s_twelveKeys =
    [
        "networkingMode", "localhostForwarding", "guiApplications", "swap", "memory", "processors", "nestedVirtualization", "gpuSupport", "hostFileSystemAccess", "defaultVhdSize", "autoProxy", "firewall",
    ];

    private static string ExpectedLines() => WslSettingsPolicy.ExpectedText(s_facts);

    // ---- the account's .wslconfig

    [Theory]
    [InlineData(2L * 1024 * 1024 * 1024, 1, 2, 1)]
    [InlineData(4L * 1024 * 1024 * 1024, 2, 2, 1)]
    [InlineData(16L * 1024 * 1024 * 1024, 8, 8, 4)]
    [InlineData(64L * 1024 * 1024 * 1024, 32, 8, 4)]
    [InlineData(0L, 0, 2, 1)]
    public void TheLimits_AreHalfThePcWithinTheirBounds(long memory, int processors, int memoryGigabytes, int processorCount)
    {
        var facts = new WslHostFacts(memory, processors);

        Assert.Equal(memoryGigabytes, facts.MemoryGigabytes);
        Assert.Equal(processorCount, facts.Processors);
    }

    [Fact]
    public void TheExpectedFile_HasExactlyTheTwelveSettings_InOneSection()
    {
        var text = ExpectedLines();

        Assert.Equal(
            "[wsl2]\r\nnetworkingMode=nat\r\nlocalhostForwarding=false\r\nguiApplications=false\r\nswap=0\r\nmemory=8GB\r\nprocessors=4\r\n"
            + "nestedVirtualization=false\r\ngpuSupport=false\r\nhostFileSystemAccess=false\r\ndefaultVhdSize=40GB\r\nautoProxy=false\r\nfirewall=true\r\n",
            text);
        var parsed = WslConfigFile.Parse(text);
        Assert.Equal(12, parsed.Entries.Count);
        Assert.Equal(s_twelveKeys, parsed.Entries.Select(e => e.Key));
        Assert.All(parsed.Entries, e => Assert.Equal("wsl2", e.Section));
        Assert.Empty(WslSettingsPolicy.Problems(text, s_facts));
        Assert.Equal(s_twelveKeys, WslSettingsPolicy.Expected(s_facts).Select(e => e.Key));
    }

    [Fact]
    public void TheDiskLimitIs40GB_SoAJobCannotFillTheDrive()
    {
        Assert.Equal("40GB", WslSettingsPolicy.DiskLimit);
        Assert.Contains(WslSettingsPolicy.Expected(s_facts), e => e is { Key: "defaultVhdSize", Value: "40GB" });
    }

    [Fact]
    public void TheFileIsWrittenAsUtf8WithoutAMark_WithCarriageReturnLineFeedEnds()
    {
        var bytes = WslSettingsPolicy.ExpectedBytes(s_facts);

        Assert.Equal((byte)'[', bytes[0]);
        Assert.Equal(ExpectedLines(), new UTF8Encoding(false).GetString(bytes));
        Assert.Equal(Encoding.UTF8.GetByteCount(ExpectedLines()), bytes.Length);
        Assert.Equal(0, bytes.AsSpan().Count((byte)'\n') - bytes.AsSpan().Count((byte)'\r'));
        Assert.Empty(WslSettingsPolicy.BytesProblems(bytes, s_facts));
    }

    [Fact]
    public void AMissingFile_IsAProblem()
    {
        Assert.Contains("not there", Assert.Single(WslSettingsPolicy.Problems(null, s_facts)), StringComparison.Ordinal);
        Assert.Contains("not there", Assert.Single(WslSettingsPolicy.BytesProblems(null, s_facts)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("networkingMode", "mirrored")]
    [InlineData("networkingMode", "bridged")]
    [InlineData("localhostForwarding", "true")]
    [InlineData("guiApplications", "true")]
    [InlineData("swap", "4GB")]
    [InlineData("memory", "64GB")]
    [InlineData("processors", "32")]
    [InlineData("nestedVirtualization", "true")]
    [InlineData("gpuSupport", "true")]
    [InlineData("hostFileSystemAccess", "true")]
    [InlineData("defaultVhdSize", "1TB")]
    [InlineData("autoProxy", "true")]
    [InlineData("firewall", "false")]
    public void AChangedSetting_IsAProblem_AndNamesTheSetting(string key, string value)
    {
        var changed = string.Join(
            "\r\n",
            ExpectedLines().Split("\r\n").Select(l => l.StartsWith(key + "=", StringComparison.Ordinal) ? $"{key}={value}" : l));

        var problems = WslSettingsPolicy.Problems(changed, s_facts);

        Assert.Contains(problems, p => p.Contains(key, StringComparison.Ordinal) && p.Contains(value, StringComparison.Ordinal));
        Assert.NotEmpty(WslSettingsPolicy.BytesProblems(Encoding.UTF8.GetBytes(changed), s_facts));
    }

    [Theory]
    [InlineData("networkingMode")]
    [InlineData("localhostForwarding")]
    [InlineData("guiApplications")]
    [InlineData("swap")]
    [InlineData("memory")]
    [InlineData("processors")]
    [InlineData("nestedVirtualization")]
    [InlineData("gpuSupport")]
    [InlineData("hostFileSystemAccess")]
    [InlineData("defaultVhdSize")]
    [InlineData("autoProxy")]
    [InlineData("firewall")]
    public void AMissingSetting_IsAProblem(string key)
    {
        var without = string.Join("\r\n", ExpectedLines().Split("\r\n").Where(l => !l.StartsWith(key + "=", StringComparison.Ordinal)));

        Assert.Contains(WslSettingsPolicy.Problems(without, s_facts), p => p.Contains(key, StringComparison.Ordinal) && p.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void ADoubledSetting_IsAProblem_EvenWhenBothSayTheSame_AndWhenTheSecondOneUndoesTheFirst()
    {
        var same = ExpectedLines() + "swap=0\r\n";
        var undone = ExpectedLines() + "networkingMode=mirrored\r\n";
        var hardening = ExpectedLines() + "hostFileSystemAccess=true\r\n";

        Assert.Contains(WslSettingsPolicy.Problems(same, s_facts), p => p.Contains("swap is given 2 times", StringComparison.Ordinal));
        Assert.Contains(WslSettingsPolicy.Problems(undone, s_facts), p => p.Contains("networkingMode is given 2 times", StringComparison.Ordinal));
        Assert.Contains(WslSettingsPolicy.Problems(hardening, s_facts), p => p.Contains("hostFileSystemAccess is given 2 times", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kernel=C:\\evil\\bzImage\r\n", "kernel")]
    [InlineData("dnsTunneling=true\r\n", "dnsTunneling")]
    [InlineData("[experimental]\r\nsparseVhd=true\r\n", "sparseVhd")]
    [InlineData("[interop]\r\nenabled=true\r\n", "enabled")]
    public void AnythingElse_IsAProblem_AnotherSettingOrAnotherSection(string extra, string expected)
    {
        Assert.Contains(WslSettingsPolicy.Problems(ExpectedLines() + extra, s_facts), p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ASettingOutsideAnySection_IsAProblem()
    {
        Assert.Contains(WslSettingsPolicy.Problems("kernelCommandLine=init=/bin/sh\r\n" + ExpectedLines(), s_facts), p => p.Contains("outside any section", StringComparison.Ordinal));
    }

    [Fact]
    public void ALineThatIsNotASetting_IsAProblem_SoNothingHidesInIt()
    {
        var text = ExpectedLines() + "this is not a setting\r\n";

        Assert.Contains(WslSettingsPolicy.Problems(text, s_facts), p => p.Contains("not a setting", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[WSL2]\nNETWORKINGMODE = NAT\nLocalhostForwarding=False\nguiapplications=FALSE\nSWAP=0\nMEMORY=8gb\nPROCESSORS=4\nNestedVirtualization=False\nGPUSUPPORT=false\nhostfilesystemaccess=FALSE\nDefaultVhdSize=40gb\nautoproxy=False\nFirewall=TRUE\n")]
    [InlineData("# a comment\n[wsl2]\nnetworkingMode=nat # the default\nlocalhostForwarding=false\n; another\nguiApplications=\"false\"\nswap=0\nmemory=8GB\nprocessors=4\nnestedVirtualization=false\ngpuSupport=false\nhostFileSystemAccess=false\ndefaultVhdSize=40GB\nautoProxy=false\nfirewall=true\n\n")]
    public void ForTheSettingsAlone_CaseSpacingAndCommentsDoNotMatter(string text)
    {
        Assert.Empty(WslSettingsPolicy.Problems(text, s_facts));
        Assert.True(WslSettingsPolicy.SameSettings(text, s_facts));
    }

    /// <summary>Red-team round 3, L3: the file must be what the program writes; a file WSL would read the same but that has another encoding, line ends, spacing or a comment is refused.</summary>
    [Theory]
    [InlineData("bom")]
    [InlineData("lf")]
    [InlineData("comment")]
    [InlineData("trailing-space")]
    [InlineData("no-final-newline")]
    [InlineData("blank-line")]
    [InlineData("upper-case")]
    [InlineData("utf16")]
    [InlineData("spaces-around-equals")]
    public void TheByteCheck_RefusesAFileWithTheRightSettingsThatIsNotExactlyWhatTheProgramWrites(string variant)
    {
        var expected = ExpectedLines();
        var bytes = variant switch
        {
            "bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(expected)],
            "lf" => Encoding.UTF8.GetBytes(expected.Replace("\r\n", "\n", StringComparison.Ordinal)),
            "comment" => Encoding.UTF8.GetBytes("# my note\r\n" + expected),
            "trailing-space" => Encoding.UTF8.GetBytes(expected.Replace("swap=0\r\n", "swap=0 \r\n", StringComparison.Ordinal)),
            "no-final-newline" => Encoding.UTF8.GetBytes(expected[..^2]),
            "blank-line" => Encoding.UTF8.GetBytes(expected.Replace("swap=0\r\n", "swap=0\r\n\r\n", StringComparison.Ordinal)),
            "upper-case" => Encoding.UTF8.GetBytes(expected.Replace("networkingMode=nat", "networkingMode=NAT", StringComparison.Ordinal)),
            "utf16" => Encoding.Unicode.GetBytes(expected),
            _ => Encoding.UTF8.GetBytes(expected.Replace("swap=0", "swap = 0", StringComparison.Ordinal)),
        };

        Assert.NotEmpty(WslSettingsPolicy.BytesProblems(bytes, s_facts));
    }

    [Fact]
    public void TheByteCheck_SaysWhyWhenOnlyTheBytesDiffer()
    {
        var bytes = Encoding.UTF8.GetBytes(ExpectedLines().Replace("\r\n", "\n", StringComparison.Ordinal));

        var problem = Assert.Single(WslSettingsPolicy.BytesProblems(bytes, s_facts));

        Assert.Contains("byte for byte", problem, StringComparison.Ordinal);
        Assert.Contains("line endings", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheByteCheck_RefusesBytesThatAreNotUtf8()
    {
        var problem = Assert.Single(WslSettingsPolicy.BytesProblems([0xFF, 0xFE, 0x00, 0x80, 0xC3], s_facts));

        Assert.Contains("not valid UTF-8", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealLimitIsHeld_WhenThePcChanges_TheFileMustSayTheNewLimit()
    {
        var small = new WslHostFacts(4L * 1024 * 1024 * 1024, 4);
        var text = ExpectedLines();

        Assert.NotEmpty(WslSettingsPolicy.Problems(text, small));
        Assert.Empty(WslSettingsPolicy.Problems(WslSettingsPolicy.ExpectedText(small), small));
        Assert.Empty(WslSettingsPolicy.BytesProblems(WslSettingsPolicy.ExpectedBytes(small), small));
    }

    [Fact]
    public void TheParser_KeepsEverySection_EveryValue_AndTheUnreadableLines()
    {
        var file = WslConfigFile.Parse("a=1\n[x]\nk=v\nk=w\njunk\n[y y]\nz = \"q\"\n");

        Assert.Equal(["v", "w"], file.GetAll("x", "k"));
        Assert.Equal(["q"], file.GetAll("y y", "z"));
        Assert.Equal(["1"], file.GetAll(string.Empty, "a"));
        Assert.Equal(["junk"], file.UnreadableLines);
        Assert.Empty(WslConfigFile.Parse(null).Entries);
    }

    // ---- the isolation check inside a job's Linux

    private static List<string> AllPass() => FakeWsl.CleanPreflightLines();

    [Fact]
    public void WhenEveryRequiredCheckPasses_AndTheScriptEndedCleanly_TheEnvironmentPasses()
    {
        var result = WslPreflight.Parse(AllPass(), 0);

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Theory]
    [InlineData("not-root", "the job would run as root")]
    [InlineData("runner-user", "the user is somebody")]
    [InlineData("no-windows-drives", "/mnt holds a Windows drive folder")]
    [InlineData("no-drvfs-mount", "a Windows drive is mounted")]
    [InlineData("no-windows-programs", "the Windows program interface is switched on")]
    [InlineData("no-gui", "the folder of the Linux window system is mounted")]
    [InlineData("no-sudo", "sudo works without a password")]
    [InlineData("no-host-shares", "a file system shared from outside the machine is mounted: /x(9p)")]
    [InlineData("no-gpu-device", "the GPU device /dev/dxg exists")]
    [InlineData("no-kvm", "the virtualization device /dev/kvm exists")]
    [InlineData("memory-cap", "the machine has 16000000 kB of memory")]
    [InlineData("cpu-cap", "the machine has 8 processors")]
    [InlineData("no-swap", "the machine has swap (1 kB)")]
    [InlineData("fresh-boot", "the machine has the boot id of the last job's machine")]
    [InlineData("recent-start", "the machine has been running for 90000 seconds")]
    [InlineData("no-new-privs", "setpriv --no-new-privs is not available")]
    [InlineData("no-user-namespaces", "user namespaces are allowed (limit 63000)")]
    [InlineData("kernel-info", "the kernel version could not be read")]
    public void AnyFailingCheck_StopsTheJob_AndSaysWhy(string id, string reason)
    {
        var lines = AllPass();
        lines.RemoveAll(l => l.StartsWith($"CHECK {id} PASS", StringComparison.Ordinal));
        lines.Add($"CHECK {id} FAIL {reason}");

        var result = WslPreflight.Parse(lines, 0);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith(id, StringComparison.Ordinal) && f.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCheckTheScriptPrints_IsRequired_AndEveryRequiredCheckIsInTheScript()
    {
        var printed = System.Text.RegularExpressions.Regex.Matches(WslPreflight.Script, @"(?:pass|passinfo|fail) ([a-z][a-z-]+)\b")
            .Select(m => m.Groups[1].Value)
            .Where(id => id.Contains('-', StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(WslPreflight.RequiredChecks.Order(StringComparer.Ordinal), printed);
        Assert.Equal(WslPreflight.RequiredChecks.Count, WslPreflight.RequiredChecks.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ACheckThatDoesNotReport_CountsAsFailed()
    {
        var lines = AllPass();
        lines.Remove("CHECK no-sudo PASS");

        var result = WslPreflight.Parse(lines, 0);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("no-sudo", StringComparison.Ordinal) && f.Contains("did not report", StringComparison.Ordinal));
        Assert.False(WslPreflight.Parse([], 0).Passed);
    }

    [Fact]
    public void TheBootIdAndTheKernel_AreTakenFromThePassLines()
    {
        var result = WslPreflight.Parse(AllPass(), 0);

        Assert.True(result.Passed);
        Assert.Equal("00000000-0000-4000-8000-000000000001", result.BootId);
        Assert.Equal("5.15.167.4-microsoft-standard-WSL2", result.KernelRelease);
        Assert.Equal(new DateTimeOffset(2024, 11, 5, 0, 21, 55, TimeSpan.Zero), result.KernelBuilt);
    }

    [Fact]
    public void APassForTheFreshBootThatCarriesNoBootId_IsNotAPass()
    {
        var lines = AllPass();
        lines.RemoveAll(l => l.StartsWith("CHECK fresh-boot", StringComparison.Ordinal));
        lines.Add("CHECK fresh-boot PASS");

        var result = WslPreflight.Parse(lines, 0);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("fresh-boot", StringComparison.Ordinal) && f.Contains("without reporting the boot id", StringComparison.Ordinal));
        Assert.Null(result.BootId);
    }

    [Theory]
    [InlineData("not-a-boot-id")]
    [InlineData("00000000-0000-4000-8000-00000000000G")]
    [InlineData("00000000-0000-4000-8000-0000000000012")]
    public void ABootIdThatIsNotWellFormed_IsNotAPass(string id)
    {
        var lines = AllPass();
        lines.RemoveAll(l => l.StartsWith("CHECK fresh-boot", StringComparison.Ordinal));
        lines.Add("CHECK fresh-boot PASS " + id);

        var result = WslPreflight.Parse(lines, 0);

        Assert.False(result.Passed);
        Assert.Null(result.BootId);
    }

    [Fact]
    public void APassForTheKernelThatCarriesNothing_IsNotAPass()
    {
        var lines = AllPass();
        lines.RemoveAll(l => l.StartsWith("CHECK kernel-info", StringComparison.Ordinal));
        lines.Add("CHECK kernel-info PASS");

        var result = WslPreflight.Parse(lines, 0);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("kernel-info", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#1 SMP Tue Nov  5 00:21:55 UTC 2024", 2024, 11, 5, 0, 21, 55)]
    [InlineData("#1 SMP PREEMPT_DYNAMIC Thu Jun  5 18:30:46 UTC 2025", 2025, 6, 5, 18, 30, 46)]
    [InlineData("#1 SMP Fri Dec 31 23:59:59 GMT 2027", 2027, 12, 31, 23, 59, 59)]
    public void TheBuildDateOfTheKernel_IsReadFromItsVersionText(string version, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero), WslPreflight.ParseKernelBuilt(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#1 SMP no date at all")]
    [InlineData("#1 SMP Tue Nov  5 00:21:55 CET 2024")]
    [InlineData("#1 SMP Tue Foo  5 00:21:55 UTC 2024")]
    [InlineData("#1 SMP Tue Nov 35 00:21:55 UTC 2024")]
    [InlineData("#1 SMP Tue Nov  5 25:21:55 UTC 2024")]
    public void ADateThatIsMissingInAnotherZoneOrImpossible_IsNotGuessed(string? version)
    {
        Assert.Null(WslPreflight.ParseKernelBuilt(version));
    }

    [Fact]
    public void AScriptThatEndsWithAnError_FailsEvenWhenEveryCheckPrintedPass()
    {
        var result = WslPreflight.Parse(AllPass(), 127);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("exit code 127", StringComparison.Ordinal));
    }

    [Fact]
    public void APassAndAFailForTheSameCheck_IsAFailure_InEitherOrder()
    {
        var failFirst = AllPass();
        failFirst.Insert(0, "CHECK no-sudo FAIL late");
        var passFirst = AllPass();
        passFirst.Add("CHECK no-sudo FAIL late");

        Assert.False(WslPreflight.Parse(failFirst, 0).Passed);
        Assert.False(WslPreflight.Parse(passFirst, 0).Passed);
    }

    [Fact]
    public void AnUnknownCheckThatFails_AlsoStopsTheJob_AndOtherOutputIsIgnored()
    {
        var lines = AllPass();
        lines.Add("some other output");
        lines.Add("CHECK extra-thing PASS");
        Assert.True(WslPreflight.Parse(lines, 0).Passed);

        lines.Add("CHECK mystery FAIL it is odd");
        Assert.False(WslPreflight.Parse(lines, 0).Passed);
    }

    [Fact]
    public void TheOutputOfWsl_WithNulCharactersAndCarriageReturns_IsParsed()
    {
        var lines = AllPass().Select(l => string.Join('\0', l.ToCharArray()) + "\r");

        Assert.True(WslPreflight.Parse(lines, 0).Passed);
    }

    [Fact]
    public void TheScript_ChecksEverythingThePolicyNeeds_AndPrintsNoSecret()
    {
        var script = WslPreflight.Script;

        foreach (var check in WslPreflight.RequiredChecks)
        {
            Assert.Contains(check, script, StringComparison.Ordinal);
        }

        foreach (var needed in new[] { "9p|virtiofs|drvfs", "/dev/dxg", "/dev/kvm", "CAP_MEM_KB", "CAP_CPUS", "SwapTotal", "boot_id", "LAST_BOOT_ID", "/proc/uptime", "setpriv --no-new-privs", "max_user_namespaces", "unshare -U", "uname -r", "uname -v" })
        {
            Assert.Contains(needed, script, StringComparison.Ordinal);
        }

        Assert.Contains("id -u", script, StringComparison.Ordinal);
        Assert.Contains("/mnt/[A-Za-z]", script, StringComparison.Ordinal);
        Assert.Contains("drvfs", script, StringComparison.Ordinal);
        Assert.Contains("WSLInterop", script, StringComparison.Ordinal);
        Assert.Contains("cmd.exe", script, StringComparison.Ordinal);
        Assert.Contains("powershell.exe", script, StringComparison.Ordinal);
        Assert.Contains("sudo -n true", script, StringComparison.Ordinal);
        Assert.Contains("/mnt/wslg", script, StringComparison.Ordinal);
        Assert.DoesNotContain("JIT", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
    }

    // ---- the edges the mutation run of round 3 found open

    [Fact]
    public void AFreshBootCheckReportedBothWays_RecordsNoBootId_InEitherOrder()
    {
        var passFirst = AllPass();
        passFirst.Add("CHECK fresh-boot FAIL not fresh");
        var failFirst = AllPass();
        failFirst.Insert(0, "CHECK fresh-boot FAIL not fresh");

        var first = WslPreflight.Parse(passFirst, 0);
        var second = WslPreflight.Parse(failFirst, 0);

        Assert.False(first.Passed);
        Assert.False(second.Passed);
        Assert.Null(first.BootId);
        Assert.Null(second.BootId);
    }

    [Fact]
    public void TheKernelRelease_AndItsBuildDate_AreKept_EvenWhenTheReleaseIsEmpty()
    {
        var emptyRelease = AllPass();
        emptyRelease.RemoveAll(l => l.StartsWith("CHECK kernel-info", StringComparison.Ordinal));
        emptyRelease.Add("CHECK kernel-info PASS |#1 SMP Tue Nov  5 00:21:55 UTC 2024");

        var normal = WslPreflight.Parse(AllPass(), 0);
        var odd = WslPreflight.Parse(emptyRelease, 0);

        var built = new DateTimeOffset(2024, 11, 5, 0, 21, 55, TimeSpan.Zero);
        Assert.Equal("5.15.167.4-microsoft-standard-WSL2", normal.KernelRelease);
        Assert.Equal(built, normal.KernelBuilt);
        Assert.Null(odd.KernelRelease);
        Assert.Equal(built, odd.KernelBuilt);
    }

    [Fact]
    public void AnUnreadableLineOfTheSettingsFile_IsKeptWhole_UpTo80Characters_AndShortenedBeyond_AndEmptyBracketsAreNotASection()
    {
        var exactly = new string('x', 80);
        var longer = new string('y', 81);

        var file = WslConfigFile.Parse(exactly + "\n" + longer + "\n[]\n");

        Assert.Equal([exactly, new string('y', 80) + "...", "[]"], file.UnreadableLines);
        Assert.Empty(file.Entries);
    }
}
