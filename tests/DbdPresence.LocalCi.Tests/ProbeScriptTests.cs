// Covers: local CI round 3, red-team H1 (the real isolation probe script, run with real bash against a fake root folder and fake programs: a Windows drive that cannot be mounted, a drive that can be
// mounted and listed and written to, the Windows program interface with and without its server, the GPU and virtualization devices, shared mounts, and the host's network: each attempt is BLOCKED or POSSIBLE with
// its evidence, the evidence names no person's account or file, and the marker name is checked before it goes into the script). Whether the script does the same inside a real WSL machine is PENDING-ENV
// (first run). docs/owner/free-ci.md.
using System.Net.Sockets;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // These tests skip themselves first where there is no bash (Windows).
public sealed class ProbeScriptTests
{
    private const string Marker = "dbd-ci-probe-0123456789ab.txt";
    private const string RouteHeader = "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT\n";

    private static async Task<(BashResult Raw, IReadOnlyList<ProbeAttempt> Attempts)> RunAsync(FakeLinux linux, params (string Name, string Value)[] environment)
    {
        Bash.RequireOrSkip();
        var raw = await Bash.RunAsync(["-c", Scripts.WithRoot(IsolationProbe.ScriptFor(Marker), linux.Root)], null, linux.Environment(environment), TestContext.Current.CancellationToken);
        return (raw, IsolationProbe.Parse(raw.Lines, raw.Exit));
    }

    /// <summary>A real Unix domain socket (a file of the socket kind), made the way a server makes one: by binding. It exists for as long as the returned socket is open.</summary>
    private static Socket MakeSocket(string path)
    {
        Assert.SkipUnless(Encoding.UTF8.GetByteCount(path) < 100, "the path of a Unix socket may not be longer than about 108 bytes, and the temp folder of this system is too long for one");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            throw;
        }
    }

    private static ProbeAttempt Get(IReadOnlyList<ProbeAttempt> attempts, string id) => Assert.Single(attempts, a => a.Id == id);

    private static void WithDefaultRoute(FakeLinux linux) => linux.Write("proc/net/route", RouteHeader + "eth0\t00000000\t0100A8C0\t0003\t0\t0\t0\t00000000\t0\t0\t0\n" + "eth0\t0000A8C0\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0\n");

    /// <summary>A mount that works: it makes the drive's folders in the mount point, with a fake cmd.exe that answers as the account.</summary>
    private static void MountWorks(FakeLinux linux, bool publicFolder = true)
    {
        const string Body = """
            if [ "$2" = "drvfs" ]; then
              mkdir -p "$4/Users/Someone" "$4/Windows/System32"
              __PUBLIC__
              cat > "$4/Windows/System32/cmd.exe" <<'EOS'
            #!/bin/sh
            printf '%s\n' 'PC\dbd-ci-wsl'
            EOS
              chmod +x "$4/Windows/System32/cmd.exe"
              exit 0
            fi
            exit 32
            """;
        linux.Tool("mount", Body.Replace("__PUBLIC__", publicFolder ? "mkdir -p \"$4/Users/Public\"" : "true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnAMachineWhereEveryWayOutIsClosed_EveryAttemptIsBlocked_AndTheScriptIsQuiet()
    {
        using var linux = new FakeLinux();
        WithDefaultRoute(linux);

        var (raw, attempts) = await RunAsync(linux);

        Assert.Equal(0, raw.Exit);
        Assert.Equal(IsolationProbe.AttemptIds, attempts.Select(a => a.Id).ToArray());
        Assert.All(attempts, a => Assert.Equal(ProbeVerdict.Blocked, a.Verdict));
        Assert.All(raw.Lines, l => Assert.StartsWith("ATTEMPT ", l, StringComparison.Ordinal));
        Assert.Equal(string.Empty, raw.Error);
        Assert.Contains("mount -t drvfs failed", Get(attempts, "mount-windows-drive").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADriveThatRootCanMount_IsPossible_WithTheCountOfEntriesAndNoNames()
    {
        using var linux = new FakeLinux();
        MountWorks(linux);
        WithDefaultRoute(linux);

        var (raw, attempts) = await RunAsync(linux);

        var mount = Get(attempts, "mount-windows-drive");
        Assert.Equal(ProbeVerdict.Possible, mount.Verdict);
        Assert.Contains("listed 2 entries in C:\\Users", mount.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("Someone", raw.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Public", Get(attempts, "mount-windows-drive").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMarkerIsWrittenIntoThePublicFolder_WhenTheDriveIsMounted_AndTheScriptSaysSo()
    {
        using var linux = new FakeLinux();
        MountWorks(linux);

        var (_, attempts) = await RunAsync(linux);

        var write = Get(attempts, "write-windows-public");
        Assert.Equal(ProbeVerdict.Possible, write.Verdict);
        Assert.Contains(Marker, write.Evidence, StringComparison.Ordinal);
        Assert.True(File.Exists(linux.PathOf("mnt/dbd-probe/Users/Public/" + Marker)), "the marker was not written into the fake Public folder");
    }

    [Fact]
    public async Task ADriveWhosePublicFolderCannotBeWritten_IsBlockedForTheWrite_ButNotForTheMount()
    {
        using var linux = new FakeLinux();
        MountWorks(linux, publicFolder: false);

        var (_, attempts) = await RunAsync(linux);

        Assert.Equal(ProbeVerdict.Possible, Get(attempts, "mount-windows-drive").Verdict);
        Assert.Equal(ProbeVerdict.Blocked, Get(attempts, "write-windows-public").Verdict);
        Assert.Contains("could not be written", Get(attempts, "write-windows-public").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAMount_ThereIsNothingToWriteInto_AndTheMarkerIsNeverCreated()
    {
        using var linux = new FakeLinux();

        var (_, attempts) = await RunAsync(linux);

        var write = Get(attempts, "write-windows-public");
        Assert.Equal(ProbeVerdict.Blocked, write.Verdict);
        Assert.Contains("nothing to write into", write.Evidence, StringComparison.Ordinal);
        Assert.False(Directory.Exists(linux.PathOf("mnt/dbd-probe")) && Directory.EnumerateFileSystemEntries(linux.PathOf("mnt/dbd-probe")).Any());
    }

    [Fact]
    public async Task AWindowsProgramThatRuns_IsPossible_AndSaysWhoAnswered()
    {
        using var linux = new FakeLinux();
        MountWorks(linux);

        var (_, attempts) = await RunAsync(linux);

        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.Possible, programs.Verdict);
        Assert.Contains("whoami said: PC\\dbd-ci-wsl", programs.Evidence, StringComparison.Ordinal);
    }

    // ---- a Windows program that is found but gives no account (local CI round 4, red-team F6): only a refusal of the system is a closed door; no answer proves nothing

    [Fact]
    public async Task AWindowsProgramFoundOnThePath_ThatTheSystemRefusesToRun_IsBlocked_WithTheExitCodeOfTheRefusal()
    {
        using var linux = new FakeLinux();
        linux.Tool("cmd.exe", "echo 'cannot execute binary file: Exec format error' >&2; exit 126");

        var (_, attempts) = await RunAsync(linux);

        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.Blocked, programs.Verdict);
        Assert.Contains("the system refused to run it (exit code 126: cannot execute binary file: Exec format error)", programs.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("echo 'cannot execute binary file'", 0)]
    [InlineData("echo 'something else entirely'; exit 3", 3)]
    [InlineData("exit 0", 0)]
    [InlineData("exit 137", 137)]
    [InlineData("exit 127", 127)]
    public async Task AWindowsProgramFoundOnThePath_ThatGivesNoAccount_IsNotTried_NeverBlocked(string body, int exit)
    {
        using var linux = new FakeLinux();
        linux.Tool("cmd.exe", body);

        var (_, attempts) = await RunAsync(linux);

        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.NotTried, programs.Verdict);
        Assert.Contains("does not show that it cannot run", programs.Evidence, StringComparison.Ordinal);
        Assert.Contains($"(exit code {exit}:", programs.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowsProgramThatTheTimeLimitStopped_IsNotTried_NotBlocked()
    {
        using var linux = new FakeLinux();
        linux.Tool("cmd.exe", "echo 'would answer, but late'");
        linux.Tool("timeout", "shift; case \"$*\" in *whoami*) exit 124 ;; *'/dev/tcp/'*) exit 1 ;; esac; exec \"$@\"");

        var (_, attempts) = await RunAsync(linux);

        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.NotTried, programs.Verdict);
        Assert.Contains("(exit code 124:", programs.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowsProgramAnswersAfterAnExitCode_IsStillPossible_BecauseTheAnswerCounts()
    {
        using var linux = new FakeLinux();
        linux.Tool("cmd.exe", "echo 'PC\\dbd-ci-wsl'; exit 1");

        var (_, attempts) = await RunAsync(linux);

        Assert.Equal(ProbeVerdict.Possible, Get(attempts, "windows-programs").Verdict);
    }

    [Fact]
    public async Task TheInterfaceBroughtBackWithItsServerAnswering_IsPossible_WithoutItsServerItIsBlocked()
    {
        using var served = new FakeLinux();
        served.Write("proc/sys/fs/binfmt_misc/register", string.Empty);
        served.Write("proc/sys/fs/binfmt_misc/WSLInterop", "enabled\n");
        served.Write("run/WSL/8_interop", string.Empty);
        using var silent = new FakeLinux();
        silent.Write("proc/sys/fs/binfmt_misc/register", string.Empty);
        silent.Write("proc/sys/fs/binfmt_misc/WSLInterop", "enabled\n");

        var (_, a) = await RunAsync(served);
        var (_, b) = await RunAsync(silent);

        Assert.Equal(ProbeVerdict.Possible, Get(a, "windows-programs").Verdict);
        Assert.Contains("probably be started", Get(a, "windows-programs").Evidence, StringComparison.Ordinal);
        Assert.Equal(ProbeVerdict.Blocked, Get(b, "windows-programs").Verdict);
        Assert.Contains("interface registered: 1, interop server socket: 0", Get(b, "windows-programs").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerSocketUnderAnyNameInRunWsl_IsTheInteropServer_AFileThatIsNotASocketIsNot()
    {
        using var socketed = new FakeLinux();
        socketed.Write("proc/sys/fs/binfmt_misc/register", string.Empty);
        socketed.Write("proc/sys/fs/binfmt_misc/WSLInterop", "enabled\n");
        using var server = MakeSocket(socketed.PathOf("run/WSL/relay"));
        using var plain = new FakeLinux();
        plain.Write("proc/sys/fs/binfmt_misc/register", string.Empty);
        plain.Write("proc/sys/fs/binfmt_misc/WSLInterop", "enabled\n");
        plain.Write("run/WSL/notes.txt", "not a socket");

        var (_, a) = await RunAsync(socketed);
        var (_, b) = await RunAsync(plain);

        Assert.Equal(ProbeVerdict.Possible, Get(a, "windows-programs").Verdict);
        Assert.Equal(ProbeVerdict.Blocked, Get(b, "windows-programs").Verdict);
        Assert.Contains("interop server socket: 0", Get(b, "windows-programs").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServerTheEnvironmentNames_IsTheServer_EvenWhereRunWslHasNothing()
    {
        using var linux = new FakeLinux();
        linux.Write("proc/sys/fs/binfmt_misc/register", string.Empty);
        linux.Write("proc/sys/fs/binfmt_misc/WSLInterop", "enabled\n");
        linux.Write("elsewhere/interop-server", string.Empty);

        var (_, named) = await RunAsync(linux, ("WSL_INTEROP", "/elsewhere/interop-server"));
        var (_, missing) = await RunAsync(linux, ("WSL_INTEROP", "/elsewhere/not-there"));
        var (_, unset) = await RunAsync(linux);

        Assert.Equal(ProbeVerdict.Possible, Get(named, "windows-programs").Verdict);
        Assert.Equal(ProbeVerdict.Blocked, Get(missing, "windows-programs").Verdict);
        Assert.Equal(ProbeVerdict.Blocked, Get(unset, "windows-programs").Verdict);
    }

    [Theory]
    [InlineData("dev/dxg", "gpu-device")]
    [InlineData("dev/kvm", "virtualization-device")]
    public async Task ADeviceThatExists_IsPossible(string device, string attempt)
    {
        using var linux = new FakeLinux();
        linux.Write(device, string.Empty);

        var (_, attempts) = await RunAsync(linux);

        Assert.Equal(ProbeVerdict.Possible, Get(attempts, attempt).Verdict);
        Assert.Single(attempts, a => a.Verdict == ProbeVerdict.Possible);
    }

    [Fact]
    public async Task ASharedMount_IsPossible_AndListed()
    {
        using var linux = new FakeLinux();
        linux.Mounts("proc /proc proc rw 0 0\nC:\\ /mnt/c 9p rw 0 0\nshare /somewhere virtiofs rw 0 0\n");

        var (_, attempts) = await RunAsync(linux);

        var shared = Get(attempts, "shared-mounts");
        Assert.Equal(ProbeVerdict.Possible, shared.Verdict);
        Assert.Contains("/mnt/c(9p)", shared.Evidence, StringComparison.Ordinal);
        Assert.Contains("/somewhere(virtiofs)", shared.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePcsAddressThatAnswersOnCommonPorts_IsPossible_WithThePortsAndTheAddress()
    {
        using var linux = new FakeLinux();
        WithDefaultRoute(linux);
        linux.Write("proc/net/arp", "IP address       HW type     Flags       HW address            Mask     Device\n192.168.0.1      0x1         0x2         00:15:5d:00:00:01     *        eth0\n");
        var open = linux.PathOf("open-ports");
        File.WriteAllText(open, "135\n445\n");
        linux.Tool("timeout", $"shift; case \"$*\" in *'/dev/tcp/'*) port=\"${{*##*/}}\"; grep -qx \"$port\" '{open}'; exit $? ;; esac; exec \"$@\"");

        var (_, attempts) = await RunAsync(linux);

        var network = Get(attempts, "host-network");
        Assert.Equal(ProbeVerdict.Possible, network.Verdict);
        Assert.Contains("192.168.0.1", network.Evidence, StringComparison.Ordinal);
        Assert.Contains("port(s): 135 445", network.Evidence, StringComparison.Ordinal);
        Assert.Contains("neighbours in the table: 1", network.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePcsAddressThatAnswersNowhere_IsBlocked()
    {
        using var linux = new FakeLinux();
        WithDefaultRoute(linux);

        var (_, attempts) = await RunAsync(linux);

        var network = Get(attempts, "host-network");
        Assert.Equal(ProbeVerdict.Blocked, network.Verdict);
        Assert.Contains("192.168.0.1 answered on none", network.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutADefaultRoute_TheNetworkAttemptCannotBeMade_AndIsNotTried()
    {
        using var linux = new FakeLinux();

        var (_, attempts) = await RunAsync(linux);

        var network = Get(attempts, "host-network");
        Assert.Equal(ProbeVerdict.NotTried, network.Verdict);
        Assert.Contains("no default route", network.Evidence, StringComparison.Ordinal);
    }

    // ---- the route table, the answer of whoami and the path of cmd.exe (local CI round 4, QA M-4, R14)

    private static void Route(FakeLinux linux, params string[] rows) => linux.Write("proc/net/route", RouteHeader + string.Concat(rows.Select(r => r + "\n")));

    private static string Row(string destination, string gateway) => $"eth0\t{destination}\t{gateway}\t0003\t0\t0\t0\t00000000\t0\t0\t0";

    [Fact]
    public async Task TheDefaultRoute_IsTheRowWithDestinationZero_AnywhereInTheTable_AndItsGatewayIsReadByteByByteInReverse()
    {
        using var linux = new FakeLinux();
        Route(linux, Row("0000A8C0", "00000000"), Row("00000000", "0200140A"), Row("00000000", "0100A8C0"));

        var (_, attempts) = await RunAsync(linux);

        var network = Get(attempts, "host-network");
        Assert.Equal(ProbeVerdict.Blocked, network.Verdict);
        Assert.Contains("the PC's address 10.20.0.2 answered on none", network.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0100A8C0", "192.168.0.1")]
    [InlineData("010011AC", "172.17.0.1")]
    [InlineData("FE01A8C0", "192.168.1.254")]
    public async Task TheGatewayAddress_IsBuiltFromTheFourBytesInTheRightOrder(string gateway, string address)
    {
        using var linux = new FakeLinux();
        Route(linux, Row("00000000", gateway));

        var (_, attempts) = await RunAsync(linux);

        Assert.Contains($"the PC's address {address} answered on none", Get(attempts, "host-network").Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARouteTableWithOnlyTheHeaderOrNoDefaultRoute_GivesNoAddress_AndTheAttemptIsNotTried()
    {
        using var headerOnly = new FakeLinux();
        Route(headerOnly);
        using var noDefault = new FakeLinux();
        Route(noDefault, Row("0000A8C0", "00000000"));
        using var shortGateway = new FakeLinux();
        Route(shortGateway, Row("00000000", "0100A8"));

        foreach (var linux in new[] { headerOnly, noDefault, shortGateway })
        {
            var (_, attempts) = await RunAsync(linux);

            Assert.Equal(ProbeVerdict.NotTried, Get(attempts, "host-network").Verdict);
            Assert.Contains("no default route", Get(attempts, "host-network").Evidence, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AWindowsProgramOnThePath_ThatAnswersWithAnAccountAndADomain_IsPossible()
    {
        using var linux = new FakeLinux();
        linux.Tool("cmd.exe", "echo 'WORKGROUP\\someone'");

        var (_, attempts) = await RunAsync(linux);

        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.Possible, programs.Verdict);
        Assert.Contains("whoami said: WORKGROUP\\someone", programs.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, ProbeVerdict.NotTried)]
    [InlineData(126, ProbeVerdict.Blocked)]
    public async Task OnlyTheFirstLineOfWhatWhoamiSaid_IsUsed_AndAnAnswerWithoutADomainIsNotTakenForAnAccount(int exit, ProbeVerdict expected)
    {
        using var linux = new FakeLinux();
        linux.Tool("cmd.exe", $"printf 'cannot execute binary file\\nPC\\\\other\\n'; exit {exit}");

        var (_, attempts) = await RunAsync(linux);

        var programs = Get(attempts, "windows-programs");
        Assert.Equal(expected, programs.Verdict);
        Assert.DoesNotContain("other", programs.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAMountedDriveWithoutCmdExe_NoProgramIsFound_AndTheInterfaceDecides()
    {
        using var linux = new FakeLinux();
        linux.Tool("mount", "if [ \"$2\" = \"drvfs\" ]; then mkdir -p \"$4/Users/Public\"; exit 0; fi; exit 32");

        var (_, attempts) = await RunAsync(linux);

        Assert.Equal(ProbeVerdict.Possible, Get(attempts, "mount-windows-drive").Verdict);
        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.Blocked, programs.Verdict);
        Assert.Contains("interface registered: 0, interop server socket: 0", programs.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProgramLeftInTheMountPoint_WithoutAMount_IsNeverRun()
    {
        using var linux = new FakeLinux();
        var witness = linux.PathOf("witness");
        linux.Program("mnt/dbd-probe/Windows/System32/cmd.exe", $"#!/bin/sh\ntouch '{witness}'\necho 'PC\\planted'\n");

        var (_, attempts) = await RunAsync(linux);

        Assert.False(File.Exists(witness), "the probe ran a program that was not on a mounted drive");
        var programs = Get(attempts, "windows-programs");
        Assert.Equal(ProbeVerdict.Blocked, programs.Verdict);
        Assert.Contains("no Windows program could be started (interface registered: 0, interop server socket: 0)", programs.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dbd-ci-probe-0123456789ab")]
    [InlineData("dbd-ci-probe-0123456789AB.txt")]
    [InlineData("dbd-ci-probe-0123456789a.txt")]
    [InlineData("../dbd-ci-probe-0123456789ab.txt")]
    [InlineData("dbd-ci-probe-0123456789ab.txt'; echo hacked #")]
    [InlineData("x.txt")]
    public void AMarkerNameThatIsNotTheShapeOfOne_IsNeverPutIntoTheScript(string name)
    {
        Assert.Throws<ArgumentException>(() => IsolationProbe.ScriptFor(name));
        Assert.False(ProbeMarkerNames.IsMarker(name));
    }

    [Fact]
    public void ANewMarkerName_HasTheShape_AndNeverRepeats()
    {
        var names = Enumerable.Range(0, 50).Select(_ => ProbeMarkerNames.New()).ToList();

        Assert.All(names, n => Assert.True(ProbeMarkerNames.IsMarker(n), n));
        Assert.Equal(50, names.Distinct(StringComparer.Ordinal).Count());
        Assert.StartsWith($"MARKER='{names[0]}'\n", IsolationProbe.ScriptFor(names[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void TheScript_IsPlainTextForBash_NamesEveryAttempt_AndHasItsRootLineOnce()
    {
        Assert.DoesNotContain('\r', IsolationProbe.Script);
        Assert.DoesNotContain('\0', IsolationProbe.Script);
        foreach (var id in IsolationProbe.AttemptIds)
        {
            Assert.Contains($"say {id} ", IsolationProbe.Script, StringComparison.Ordinal);
        }

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(IsolationProbe.Script, "R=\"\""));
        Assert.Contains("mount -t drvfs", IsolationProbe.Script, StringComparison.Ordinal);
        Assert.Contains("cmd.exe", IsolationProbe.Script, StringComparison.Ordinal);
        Assert.Contains("/dev/tcp/", IsolationProbe.Script, StringComparison.Ordinal);
    }
}
