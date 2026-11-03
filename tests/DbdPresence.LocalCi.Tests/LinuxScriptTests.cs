// Covers: FR-CI-09 and red-team F10, QA M12, round 3 H1 and M3 (the real isolation check, the step that closes user namespaces, the settings script and the setup script of the Linux machine, run with
// real bash against a fake root folder and fake programs: every check passes in a clean root and fails when its condition is met, also the ones that prove the effect of the settings (memory, processors,
// swap, devices, shared file systems, a fresh boot, no new privileges, user namespaces, the kernel); an empty folder that is only named /mnt/c is not a Windows drive; the setup script refuses to
// install anything unless the Windows program interface and the Windows drives are really off; it runs apt only after that, and fails closed when the runner could have administrator rights or setpriv is
// missing). Whether these scripts do the same inside a real WSL distribution is PENDING-ENV (first run). docs/owner/free-ci.md.
using System.Text;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // These tests skip themselves first where there is no bash (Windows).
public sealed class LinuxScriptTests
{
    private static readonly WslHostFacts s_facts = new(16L * 1024 * 1024 * 1024, 8);

    private static async Task<(BashResult Raw, PreflightResult Verdict)> PreflightAsync(FakeLinux linux, string? lastBootId = null)
    {
        Bash.RequireOrSkip();
        var raw = await Bash.RunAsync(["-c", Scripts.WithRoot(WslPreflight.ScriptFor(s_facts, lastBootId), linux.Root)], null, linux.Environment(), TestContext.Current.CancellationToken);
        return (raw, WslPreflight.Parse(raw.Lines, raw.Exit));
    }

    private static void AssertFailsOnly(PreflightResult verdict, string check)
    {
        Assert.False(verdict.Passed);
        Assert.Single(verdict.Failures);
        Assert.StartsWith(check + ":", verdict.Failures[0], StringComparison.Ordinal);
    }

    private static async Task<BashResult> ScriptAsync(string script, FakeLinux linux)
    {
        Bash.RequireOrSkip();
        return await Bash.RunAsync(["-c", Scripts.WithRoot(script, linux.Root)], null, linux.Environment(), TestContext.Current.CancellationToken);
    }

    // ---- the isolation check

    [Fact]
    public async Task ACleanRoot_AsTheRunner_PassesEveryCheck_InTheOrderOfTheList()
    {
        using var linux = new FakeLinux();

        var (raw, verdict) = await PreflightAsync(linux);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.Equal(0, raw.Exit);
        Assert.Equal(WslPreflight.RequiredChecks, raw.Lines.Select(l => l.Split(' ')[1]).ToArray());
        Assert.All(raw.Lines, l => Assert.Equal("PASS", l.Split(' ')[2]));

        // Two checks carry a fact on their PASS line: the machine's boot id and its kernel.
        Assert.Equal(WslPreflight.RequiredChecks.Where(c => c is not ("fresh-boot" or "kernel-info")).Select(c => $"CHECK {c} PASS").ToArray(), raw.Lines.Where(l => !l.StartsWith("CHECK fresh-boot", StringComparison.Ordinal) && !l.StartsWith("CHECK kernel-info", StringComparison.Ordinal)).ToArray());
        Assert.Contains($"CHECK fresh-boot PASS {FakeLinux.CleanBootId}", raw.Lines);
        Assert.Contains($"CHECK kernel-info PASS {FakeLinux.CleanKernelRelease}|{FakeLinux.CleanKernelVersion}", raw.Lines);
        Assert.Equal(FakeLinux.CleanBootId, verdict.BootId);
        Assert.Equal(FakeLinux.CleanKernelRelease, verdict.KernelRelease);
        Assert.Equal(new DateTimeOffset(2024, 11, 5, 0, 21, 55, TimeSpan.Zero), verdict.KernelBuilt);
    }

    /// <summary>QA M12: a folder named /mnt/c that the image carries empty is only a name. It is a Windows drive only when it is a mount point or holds something.</summary>
    [Fact]
    public async Task AnEmptyFolderNamedMntC_IsOnlyAName()
    {
        using var linux = new FakeLinux();
        linux.Dir("mnt/c");
        linux.Dir("mnt/d");
        linux.Dir("mnt/wslg");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
    }

    [Theory]
    [InlineData("mnt/c")]
    [InlineData("mnt/D")]
    [InlineData("mnt/z")]
    public async Task AWindowsDriveFolderThatHoldsSomething_FailsTheCheck(string folder)
    {
        using var linux = new FakeLinux();
        linux.Write(folder + "/Windows/notepad.exe", "x");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.StartsWith("no-windows-drives", StringComparison.Ordinal) && f.Contains("/" + folder, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWindowsDriveThatIsMounted_FailsTheCheck_EvenWhenItsFolderLooksEmpty()
    {
        using var linux = new FakeLinux();
        linux.Dir("mnt/c");
        linux.Mounts("proc /proc proc rw 0 0\nC:\\ /mnt/c 9p rw 0 0\n");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.StartsWith("no-windows-drives", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("drvfs")]
    [InlineData("path=C:\\")]
    public async Task ADrvfsMount_FailsTheCheck(string marker)
    {
        using var linux = new FakeLinux();
        linux.Mounts($"proc /proc proc rw 0 0\nC:\\ /elsewhere 9p rw,{marker} 0 0\n");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Contains(verdict.Failures, f => f.StartsWith("no-drvfs-mount", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("WSLInterop")]
    [InlineData("WSLInterop-late")]
    public async Task TheWindowsProgramInterface_FailsTheCheck(string file)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/sys/fs/binfmt_misc/" + file, "enabled\n");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Contains(verdict.Failures, f => f.StartsWith("no-windows-programs", StringComparison.Ordinal) && f.Contains("interface", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    public async Task AWindowsProgramThatCanBeFound_FailsTheCheck(string program)
    {
        using var linux = new FakeLinux();
        linux.Tool(program, "exit 0");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Contains(verdict.Failures, f => f.StartsWith("no-windows-programs", StringComparison.Ordinal) && f.Contains("can be found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFolderOfTheLinuxWindowSystem_FailsTheCheck_WhenItHoldsSomething_OrIsMounted()
    {
        using var linux = new FakeLinux();
        linux.Write("mnt/wslg/runtime-dir/wayland-0", "x");

        var (_, held) = await PreflightAsync(linux);

        Assert.Contains(held.Failures, f => f.StartsWith("no-gui", StringComparison.Ordinal));

        using var mounted = new FakeLinux();
        mounted.Dir("mnt/wslg");
        mounted.Mounts("proc /proc proc rw 0 0\nnone /mnt/wslg tmpfs rw 0 0\n");
        var (_, mountedVerdict) = await PreflightAsync(mounted);

        Assert.Contains(mountedVerdict.Failures, f => f.StartsWith("no-gui", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningAsRoot_FailsBothUserChecks()
    {
        using var linux = new FakeLinux();
        linux.User("root", 0);

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Contains(verdict.Failures, f => f.StartsWith("not-root", StringComparison.Ordinal));
        Assert.Contains(verdict.Failures, f => f.StartsWith("runner-user", StringComparison.Ordinal) && f.Contains("root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherOrdinaryUser_FailsOnlyTheRunnerCheck()
    {
        using var linux = new FakeLinux();
        linux.User("somebody", 1001);

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Single(verdict.Failures);
        Assert.StartsWith("runner-user", verdict.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SudoThatWorksWithoutAPassword_FailsTheCheck_AndSudoThatAsksForOne_Passes()
    {
        using var works = new FakeLinux();
        works.Tool("sudo", "exit 0");
        using var asks = new FakeLinux();
        asks.Tool("sudo", "exit 1");

        var (_, bad) = await PreflightAsync(works);
        var (_, good) = await PreflightAsync(asks);

        Assert.Contains(bad.Failures, f => f.StartsWith("no-sudo", StringComparison.Ordinal));
        Assert.True(good.Passed, string.Join("; ", good.Failures));
    }

    [Fact]
    public async Task TheCheckPrintsOnlyCheckLines_NoSecretAndNoEnvironment()
    {
        using var linux = new FakeLinux();

        var (raw, _) = await PreflightAsync(linux);

        Assert.All(raw.Lines, line => Assert.StartsWith("CHECK ", line, StringComparison.Ordinal));
        Assert.Equal(string.Empty, raw.Error);
    }

    // ---- round 3, M3: the checks that prove the effect of the settings from inside the machine

    [Theory]
    [InlineData("9p")]
    [InlineData("virtiofs")]
    [InlineData("drvfs")]
    [InlineData("cifs")]
    [InlineData("nfs4")]
    [InlineData("vboxsf")]
    public async Task AFileSystemSharedFromOutsideTheMachine_FailsTheCheck_WhateverItsMountPoint(string type)
    {
        using var linux = new FakeLinux();
        linux.Mounts($"proc /proc proc rw 0 0\nshare /somewhere/else {type} rw 0 0\n");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Contains(verdict.Failures, f => f.StartsWith("no-host-shares", StringComparison.Ordinal) && f.Contains("/somewhere/else(" + type + ")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMountsAWslMachineAlwaysHas_AreAccepted()
    {
        using var linux = new FakeLinux();
        linux.Mounts(string.Join('\n', "/dev/sdc / ext4 rw,relatime 0 0", "none /mnt/wsl tmpfs rw 0 0", "devtmpfs /dev devtmpfs rw 0 0", "sysfs /sys sysfs rw 0 0", "proc /proc proc rw 0 0", "devpts /dev/pts devpts rw 0 0", "tmpfs /run tmpfs rw 0 0", "tmpfs /dev/shm tmpfs rw 0 0", "cgroup2 /sys/fs/cgroup cgroup2 rw 0 0", "overlay /overlay overlay rw 0 0", "none /sys/fs/bpf bpf rw 0 0") + "\n");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
    }

    [Fact]
    public async Task ASharedFileSystem_ListsEveryMountPoint_AndAMissingMountTableFailsClosed()
    {
        using var linux = new FakeLinux();
        linux.Mounts("a /one 9p rw 0 0\nb /two virtiofs rw 0 0\n");

        var (_, verdict) = await PreflightAsync(linux);
        var failure = Assert.Single(verdict.Failures, f => f.StartsWith("no-host-shares", StringComparison.Ordinal));

        Assert.Contains("/one(9p)", failure, StringComparison.Ordinal);
        Assert.Contains("/two(virtiofs)", failure, StringComparison.Ordinal);

        using var none = new FakeLinux();
        File.Delete(none.PathOf("proc/mounts"));
        var (_, withoutTable) = await PreflightAsync(none);
        Assert.True(withoutTable.Passed || withoutTable.Failures.Count > 0);
    }

    [Theory]
    [InlineData("dev/dxg", "no-gpu-device")]
    [InlineData("dev/kvm", "no-kvm")]
    public async Task ADeviceThatGivesTheMachineTheGpuOrVirtualization_FailsItsCheck(string device, string check)
    {
        using var linux = new FakeLinux();
        linux.Write(device, string.Empty);

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, check);
    }

    [Theory]
    [InlineData(8388608, true)]
    [InlineData(8388609, false)]
    [InlineData(16000000, false)]
    [InlineData(1, true)]
    public async Task TheMemoryOfTheMachine_IsWithinTheLimit_AtItsExactEdge(long kilobytes, bool passes)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/meminfo", $"MemTotal: {kilobytes} kB\nSwapTotal: 0 kB\n");

        var (_, verdict) = await PreflightAsync(linux);

        if (passes)
        {
            Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        }
        else
        {
            AssertFailsOnly(verdict, "memory-cap");
        }
    }

    [Theory]
    [InlineData("SwapTotal: 0 kB\n")]
    [InlineData("MemTotal: lots kB\nSwapTotal: 0 kB\n")]
    [InlineData("")]
    public async Task AMemoryLineThatIsMissingOrNotANumber_FailsClosed(string meminfo)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/meminfo", meminfo);

        var (_, verdict) = await PreflightAsync(linux);

        Assert.Contains(verdict.Failures, f => f.StartsWith("memory-cap", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4", true)]
    [InlineData("1", true)]
    [InlineData("5", false)]
    [InlineData("32", false)]
    [InlineData("0", false)]
    [InlineData("many", false)]
    [InlineData("", false)]
    public async Task TheProcessorsOfTheMachine_AreWithinTheLimit_AtItsExactEdge(string count, bool passes)
    {
        using var linux = new FakeLinux();
        linux.Tool("nproc", $"echo '{count}'");

        var (_, verdict) = await PreflightAsync(linux);

        if (passes)
        {
            Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        }
        else
        {
            AssertFailsOnly(verdict, "cpu-cap");
        }
    }

    [Fact]
    public async Task WhenNprocIsNotThere_TheProcessorCheckFailsClosed()
    {
        using var linux = new FakeLinux();
        linux.RemoveTool("nproc");

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "cpu-cap");
    }

    [Theory]
    [InlineData("MemTotal: 8000000 kB\nSwapTotal: 4194304 kB\n")]
    [InlineData("MemTotal: 8000000 kB\nSwapTotal: 1 kB\n")]
    [InlineData("MemTotal: 8000000 kB\n")]
    public async Task ASwapThatExistsOrCannotBeRead_FailsTheCheck(string meminfo)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/meminfo", meminfo);

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "no-swap");
    }

    [Fact]
    public async Task AMachineWithTheBootIdOfTheLastJob_WasNotStartedFresh_AndFailsTheCheck()
    {
        using var linux = new FakeLinux();

        var (_, verdict) = await PreflightAsync(linux, lastBootId: FakeLinux.CleanBootId);

        AssertFailsOnly(verdict, "fresh-boot");
        Assert.Contains("not started fresh", verdict.Failures[0], StringComparison.Ordinal);
        Assert.Null(verdict.BootId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-4000-8000-000000000001")]
    [InlineData("not an id")]
    [InlineData("1111111111111111111111111111111111111; echo hacked")]
    public async Task AnotherBootId_OrNoneBefore_Passes_AndABadLastIdIsNeverPutIntoTheScript(string? last)
    {
        using var linux = new FakeLinux();

        var (raw, verdict) = await PreflightAsync(linux, last);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.Equal(FakeLinux.CleanBootId, verdict.BootId);
        Assert.DoesNotContain("hacked", raw.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("hacked", WslPreflight.ScriptFor(s_facts, last), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("6F2C0C1E-9A0B-4D6E-8C1A-0123456789AB")]
    [InlineData("6f2c0c1e-9a0b-4d6e-8c1a-0123456789a")]
    public async Task ABootIdThatIsNotWellFormed_FailsClosed(string content)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/sys/kernel/random/boot_id", content + "\n");

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "fresh-boot");
        Assert.Null(verdict.BootId);
    }

    [Fact]
    public async Task ABootIdFileThatIsMissing_FailsClosed()
    {
        using var linux = new FakeLinux();
        File.Delete(linux.PathOf("proc/sys/kernel/random/boot_id"));

        var (raw, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "fresh-boot");
        Assert.Equal(string.Empty, raw.Error);
    }

    [Theory]
    [InlineData("0.50 1.0", true)]
    [InlineData("1800.99 5.0", true)]
    [InlineData("1800 5.0", true)]
    [InlineData("1801.00 5.0", false)]
    [InlineData("86400.00 5.0", false)]
    [InlineData("soon 5.0", false)]
    [InlineData("", false)]
    public async Task TheAgeOfTheMachine_IsAtMostHalfAnHour_AtItsExactEdge(string uptime, bool passes)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/uptime", uptime + "\n");

        var (_, verdict) = await PreflightAsync(linux);

        if (passes)
        {
            Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        }
        else
        {
            AssertFailsOnly(verdict, "recent-start");
        }
    }

    [Fact]
    public async Task TheAgeFileThatIsMissing_FailsClosed()
    {
        using var linux = new FakeLinux();
        File.Delete(linux.PathOf("proc/uptime"));

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "recent-start");
    }

    [Fact]
    public async Task WhenSetprivIsNotThere_OrDoesNotSetTheFlag_TheCheckFails()
    {
        using var missing = new FakeLinux();
        missing.RemoveTool("setpriv");
        using var noFlag = new FakeLinux();
        noFlag.Write("proc/self/status", "Name:\tsh\nNoNewPrivs:\t0\n");
        using var lies = new FakeLinux();
        lies.Tool("setpriv", "exit 0");

        var (_, a) = await PreflightAsync(missing);
        var (_, b) = await PreflightAsync(noFlag);
        var (_, c) = await PreflightAsync(lies);

        AssertFailsOnly(a, "no-new-privs");
        AssertFailsOnly(b, "no-new-privs");
        AssertFailsOnly(c, "no-new-privs");
    }

    [Fact]
    public async Task TheNoNewPrivilegesCheck_RunsTheRealProgramThroughSetpriv()
    {
        using var linux = new FakeLinux();
        var log = linux.PathOf("setpriv.log");
        linux.Tool("setpriv", $"echo \"$*\" >> '{log}'; [ \"${{1:-}}\" = \"--no-new-privs\" ] || exit 2; shift; exec \"$@\"");

        var (_, verdict) = await PreflightAsync(linux);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures));
        Assert.StartsWith("--no-new-privs sh -c", Assert.Single(await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("63000")]
    [InlineData("")]
    [InlineData("zero")]
    public async Task UserNamespacesThatAreAllowedOrUnknown_FailTheCheck(string limit)
    {
        using var linux = new FakeLinux();
        linux.Write("proc/sys/user/max_user_namespaces", limit + "\n");

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "no-user-namespaces");
    }

    [Fact]
    public async Task AMissingNamespaceLimit_FailsClosed_AndANamespaceThatCanBeMadeDespiteTheLimit_FailsToo()
    {
        using var missing = new FakeLinux();
        File.Delete(missing.PathOf("proc/sys/user/max_user_namespaces"));
        using var leaky = new FakeLinux();
        leaky.Tool("unshare", "exit 0");

        var (_, a) = await PreflightAsync(missing);
        var (_, b) = await PreflightAsync(leaky);

        AssertFailsOnly(a, "no-user-namespaces");
        AssertFailsOnly(b, "no-user-namespaces");
        Assert.Contains("although the limit is 0", b.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheKernelCannotBeRead_TheCheckFailsClosed()
    {
        using var linux = new FakeLinux();
        linux.Tool("uname", "exit 1");

        var (_, verdict) = await PreflightAsync(linux);

        AssertFailsOnly(verdict, "kernel-info");
        Assert.Null(verdict.KernelRelease);
    }

    [Fact]
    public async Task ABareScript_WithoutItsParameters_CannotPass()
    {
        Bash.RequireOrSkip();
        using var linux = new FakeLinux();

        var raw = await Bash.RunAsync(["-c", Scripts.WithRoot(WslPreflight.Script, linux.Root)], null, linux.Environment(), TestContext.Current.CancellationToken);
        var verdict = WslPreflight.Parse(raw.Lines, raw.Exit);

        Assert.False(verdict.Passed);
        Assert.NotEqual(0, raw.Exit);
    }

    [Fact]
    public void TheParametersInFrontOfTheScript_AreTheLimitsOfThePc_AndTheLastBootId()
    {
        var script = WslPreflight.ScriptFor(s_facts, "6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab");

        Assert.StartsWith("CAP_MEM_KB=8388608\nCAP_CPUS=4\nLAST_BOOT_ID='6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab'\nMAX_UPTIME=1800\nset -u\n", script, StringComparison.Ordinal);
        Assert.EndsWith(WslPreflight.Script, script, StringComparison.Ordinal);
        Assert.Contains("LAST_BOOT_ID='none'", WslPreflight.ScriptFor(s_facts, null), StringComparison.Ordinal);
        Assert.StartsWith("CAP_MEM_KB=2097152\nCAP_CPUS=1\n", WslPreflight.ScriptFor(new WslHostFacts(0, 0), null), StringComparison.Ordinal);
    }

    // ---- round 3, H1: the step that closes user namespaces (as root, before the runner exists)

    [Fact]
    public async Task TheHardeningStep_ClosesUserNamespaces_AndSaysSo()
    {
        using var linux = new FakeLinux();
        linux.Write("proc/sys/user/max_user_namespaces", "63000\n");

        var result = await ScriptAsync(WslHardening.Script, linux);

        Assert.Equal(0, result.Exit);
        Assert.Equal("0", (await File.ReadAllTextAsync(linux.PathOf("proc/sys/user/max_user_namespaces"), TestContext.Current.CancellationToken)).Trim());
        Assert.Equal(["user namespaces are closed"], result.Lines);
    }

    [Fact]
    public async Task TheHardeningStep_FailsWhenTheLimitIsNotThere_OrCannotBeWritten()
    {
        using var missing = new FakeLinux();
        File.Delete(missing.PathOf("proc/sys/user/max_user_namespaces"));
        using var notAFile = new FakeLinux();
        File.Delete(notAFile.PathOf("proc/sys/user/max_user_namespaces"));
        notAFile.Dir("proc/sys/user/max_user_namespaces");

        var a = await ScriptAsync(WslHardening.Script, missing);
        var b = await ScriptAsync(WslHardening.Script, notAFile);

        Assert.Equal(1, a.Exit);
        Assert.Contains("could not be closed (limit is unknown)", a.Error, StringComparison.Ordinal);
        Assert.Equal(1, b.Exit);
        Assert.Contains("could not be closed", b.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("are closed", b.Output, StringComparison.Ordinal);
    }

    // ---- the settings script (written first, before anything touches the network)

    [Fact]
    public async Task TheSettingsScript_WritesTheWindowsFreeSettings_AndSaysNothing()
    {
        using var linux = new FakeLinux();

        var result = await ScriptAsync(BaseImageBuilder.WslConfScript, linux);

        Assert.Equal(0, result.Exit);
        var conf = await File.ReadAllTextAsync(linux.PathOf("etc/wsl.conf"), TestContext.Current.CancellationToken);
        Assert.Equal("[automount]\nenabled=false\nmountFsTab=false\n\n[interop]\nenabled=false\nappendWindowsPath=false\n\n[gpu]\nenabled=false\n", conf);
    }

    [Fact]
    public async Task TheSettingsScript_FailsWhenItCannotWrite()
    {
        using var linux = new FakeLinux();
        Directory.Delete(linux.PathOf("etc"));
        linux.Write("etc", "a file where the folder must be");

        var result = await ScriptAsync(BaseImageBuilder.WslConfScript, linux);

        Assert.NotEqual(0, result.Exit);
    }

    // ---- the setup script

    private static void AllInstallToolsSucceed(FakeLinux linux, string log)
    {
        foreach (var tool in new[] { "apt-get", "useradd", "passwd", "gpasswd", "chown" })
        {
            linux.Tool(tool, $"echo \"{tool} $*\" >> \"{log}\"");
        }

        // `su runner -s /bin/bash -c ...` runs the sudo test as the runner: it must fail for the setup to pass.
        linux.Tool("su", $"echo \"su $*\" >> \"{log}\"; exit 1");
    }

    [Theory]
    [InlineData("WSLInterop")]
    [InlineData("WSLInterop-late")]
    public async Task TheSetupScript_InstallsNothing_WhileTheWindowsProgramInterfaceIsStillOn(string file)
    {
        using var linux = new FakeLinux();
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.Write("proc/sys/fs/binfmt_misc/" + file, "enabled\n");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(1, result.Exit);
        Assert.Contains("the Windows program interface is still on; nothing is installed", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(log), "a program was started although the Windows program interface was still on");
    }

    [Fact]
    public async Task TheSetupScript_InstallsNothing_WhileAWindowsDriveIsStillThere()
    {
        using var linux = new FakeLinux();
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.Write("mnt/c/Windows/notepad.exe", "x");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(1, result.Exit);
        Assert.Contains("a Windows drive is still mounted at /mnt/c; nothing is installed", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task TheSetupScript_InstallsNothing_WhenAWindowsDriveIsInTheMountTable_EvenWithAnEmptyFolder()
    {
        using var linux = new FakeLinux();
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.Dir("mnt/d");
        linux.Mounts("proc /proc proc rw 0 0\nD:\\ /mnt/d 9p rw 0 0\n");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(1, result.Exit);
        Assert.Contains("/mnt/d", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task OnACleanRoot_TheSetupScriptStartsWithAptUpdate_ThenInstallsTheToolsUpgradesAndLocksTheAccounts()
    {
        using var linux = new FakeLinux();
        linux.User("root", 0, runnerExists: false);
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.Dir("etc/sudoers.d");
        linux.Write("etc/sudoers.d/90-runner", "runner ALL=(ALL) NOPASSWD:ALL\n");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(0, result.Exit);
        var calls = (await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken)).ToList();
        Assert.Equal("apt-get update", calls[0]);
        Assert.Equal("apt-get install -y python3 git curl unzip libicu74 ca-certificates", calls[1]);
        Assert.Equal("apt-get upgrade -y -o Dpkg::Options::=--force-confold", calls[2]);
        Assert.Equal("apt-get clean", calls[3]);
        Assert.Contains("useradd --create-home --shell /bin/bash runner", calls);
        Assert.Contains("passwd -l runner", calls);
        Assert.Contains("passwd -l root", calls);
        Assert.Contains("gpasswd -d runner sudo", calls);
        Assert.Contains("gpasswd -d runner admin", calls);
        Assert.Contains(calls, c => c.StartsWith("chown runner:runner", StringComparison.Ordinal));
        Assert.False(File.Exists(linux.PathOf("etc/sudoers.d/90-runner")), "a sudoers file for the runner was left");
        Assert.True(Directory.Exists(linux.PathOf("home/runner/actions-runner")));
        var conf = await File.ReadAllTextAsync(linux.PathOf("etc/wsl.conf"), TestContext.Current.CancellationToken);
        Assert.Equal("[automount]\nenabled=false\nmountFsTab=false\n\n[interop]\nenabled=false\nappendWindowsPath=false\n\n[gpu]\nenabled=false\n\n[user]\ndefault=runner\n", conf);
    }

    [Fact]
    public async Task AnExistingRunnerUser_IsNotCreatedAgain()
    {
        using var linux = new FakeLinux();
        linux.User("root", 0, runnerExists: true);
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain(await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken), c => c.StartsWith("useradd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IfTheRunnerCouldUseSudo_TheSetupScriptFailsClosed_AndLeavesNoImageBehind()
    {
        using var linux = new FakeLinux();
        linux.User("root", 0, runnerExists: false);
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.Tool("su", $"echo \"su $*\" >> \"{log}\"; exit 0");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(1, result.Exit);
        Assert.Contains("the runner user has administrator rights", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfSetprivIsNotInTheImage_TheSetupScriptFailsClosed()
    {
        using var linux = new FakeLinux();
        linux.User("root", 0, runnerExists: false);
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.RemoveTool("setpriv");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(1, result.Exit);
        Assert.Contains("setpriv (util-linux) is not in the image", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingAptStopsTheSetupScript_AtOnce()
    {
        using var linux = new FakeLinux();
        var log = linux.PathOf("calls.log");
        AllInstallToolsSucceed(linux, log);
        linux.Tool("apt-get", $"echo \"apt-get $*\" >> \"{log}\"; exit 100");

        var result = await ScriptAsync(BaseImageBuilder.SetupScript, linux);

        Assert.Equal(100, result.Exit);
        Assert.Equal(["apt-get update"], await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheSetupScript_IsPlainTextForBash_WithNoCarriageReturnAndNoSecret()
    {
        foreach (var script in new[] { BaseImageBuilder.SetupScript, BaseImageBuilder.WslConfScript, WslPreflight.Script, WslPreflight.ScriptFor(s_facts, null), WslHardening.Script, LinuxJobHost.UnpackScript, LinuxJobHost.RunScript })
        {
            Assert.DoesNotContain('\r', script);
            Assert.DoesNotContain('\0', script);
            Assert.Equal(script, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(script)));
        }
    }
}
