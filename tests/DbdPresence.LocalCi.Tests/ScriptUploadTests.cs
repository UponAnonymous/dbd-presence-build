// Covers: red-team F2 (a script reaches the Linux machine through standard input in front of its data because another user's command line is limited to 1,023 characters: the loader reads the byte
// count, reads exactly that many bytes as the script and runs it with the rest of the input untouched), FR-CI-06 (the runner's configuration is only ever data after the script), checked with real bash.
// The wsl.exe side of this is PENDING-ENV. docs/owner/free-ci.md.
using System.Text;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // The tests that make files executable skip themselves first where there is no bash (Windows).
public sealed class ScriptUploadTests
{
    private static async Task<BashResult> RunThroughTheLoaderAsync(string script, Stream? data, IReadOnlyDictionary<string, string> environment)
    {
        Bash.RequireOrSkip();
        await using var framed = ScriptUpload.Frame(script, data);
        return await Bash.RunAsync(["-c", ScriptUpload.Loader], framed, environment, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Plain(FakeLinux linux) => linux.Environment();

    [Fact]
    public void TheFrame_IsTheByteCount_ALineFeed_TheScript_ThenTheData()
    {
        using var data = new MemoryStream("DATA"u8.ToArray());
        using var framed = ScriptUpload.Frame("echo hi\r\necho é", data);
        using var copy = new MemoryStream();
        framed.CopyTo(copy);

        // "echo hi\necho é" is 7 + 1 + 6 + 1 = 15 bytes (the carriage return is dropped; é is two bytes).
        Assert.Equal("15\necho hi\necho éDATA", Encoding.UTF8.GetString(copy.ToArray()));
    }

    [Fact]
    public async Task AScriptWithNoData_ReadsAnEmptyInput()
    {
        using var linux = new FakeLinux();

        var result = await RunThroughTheLoaderAsync("echo start; cat | wc -c", null, Plain(linux));

        Assert.Equal(0, result.Exit);
        Assert.Equal(["start", "0"], result.Lines);
    }

    [Fact]
    public async Task TheScriptGetsExactlyItsBytes_AndTheDataIsLeftForIt()
    {
        using var linux = new FakeLinux();
        using var data = new MemoryStream("first line\nsecond line\n"u8.ToArray());

        var result = await RunThroughTheLoaderAsync("IFS= read -r LINE; echo \"got:$LINE\"; cat | wc -c", data, Plain(linux));

        Assert.Equal(0, result.Exit);
        Assert.Equal(["got:first line", "12"], result.Lines);
    }

    [Fact]
    public async Task BinaryDataGoesThroughUnchanged_EveryByteValue_NoLineEndingChange()
    {
        using var linux = new FakeLinux();
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Enumerable.Range(0, 200_000).Select(i => (byte)((i * 31) % 256))).ToArray();
        using var data = new MemoryStream(bytes);

        var result = await RunThroughTheLoaderAsync("cat | od -An -v -tx1 | tr -d ' \\n'", data, Plain(linux));

        Assert.Equal(0, result.Exit);
        Assert.Equal(Convert.ToHexStringLower(bytes), result.Output.Trim());
    }

    [Fact]
    public async Task AScriptWithQuotesDollarsBackticksHeredocsAndMultiByteText_ArrivesWhole()
    {
        using var linux = new FakeLinux();
        const string Script = """
            set -eu
            A='single "double" $notexpanded `nor this`'
            B="double 'single' $((6*7))"
            cat <<'EOF'
            heredoc $x `y` \z
            EOF
            printf '%s|%s|%s\n' "$A" "$B" "japan: 日本語 é"
            """;

        var result = await RunThroughTheLoaderAsync(Script, null, Plain(linux));

        Assert.Equal(0, result.Exit);
        Assert.Contains("heredoc $x `y` \\z", result.Lines);
        Assert.Contains("single \"double\" $notexpanded `nor this`|double 'single' 42|japan: 日本語 é", result.Lines);
    }

    [Fact]
    public async Task TheScriptsExitCode_IsTheExitCodeOfTheWholeThing()
    {
        using var linux = new FakeLinux();

        var failed = await RunThroughTheLoaderAsync("echo before; exit 7", null, Plain(linux));

        Assert.Equal(7, failed.Exit);
        Assert.Equal(["before"], failed.Lines);
    }

    [Fact]
    public async Task AScriptThatEndsEarly_WithoutReadingItsData_IsNotAProblemOfTheLoader()
    {
        using var linux = new FakeLinux();
        using var data = new MemoryStream(new byte[1_000_000]);

        var result = await RunThroughTheLoaderAsync("echo done", data, Plain(linux));

        Assert.Equal(0, result.Exit);
        Assert.Equal(["done"], result.Lines);
    }

    [Fact]
    public async Task TheRealPreflightScript_RunsThroughTheLoader_AndPasses_InACleanFakeRoot()
    {
        using var linux = new FakeLinux();

        var result = await RunThroughTheLoaderAsync(Scripts.WithRoot(WslPreflight.ScriptFor(new WslHostFacts(16L * 1024 * 1024 * 1024, 8), null), linux.Root), null, Plain(linux));

        var verdict = WslPreflight.Parse(result.Lines, result.Exit);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Failures) + result.Error);
    }

    [Fact]
    public async Task TheRunScriptsFirstLine_BecomesTheRunnersConfiguration_AndIsOnNoCommandLine()
    {
        using var linux = new FakeLinux();
        linux.Program("home/runner/actions-runner/run.sh", "#!/bin/sh\nprintf '%s' \"$ACTIONS_RUNNER_INPUT_JITCONFIG\"\n");
        var setprivLog = linux.PathOf("setpriv.log");
        linux.Tool("setpriv", $"echo \"$*\" >> '{setprivLog}'; [ \"${{1:-}}\" = \"--no-new-privs\" ] || exit 2; shift; exec \"$@\"");

        // The real run script, with only its folder redirected into the fake root; it must export the configuration from the first line of its input.
        var script = LinuxJobHost.RunScript.Replace("cd /home/runner/actions-runner", "cd \"$FAKE_LINUX_ROOT/home/runner/actions-runner\"", StringComparison.Ordinal);
        using var data = new MemoryStream("SECRET-JIT-CONFIG\n"u8.ToArray());

        var result = await RunThroughTheLoaderAsync(script, data, Plain(linux));

        Assert.Equal(0, result.Exit);
        Assert.Equal("SECRET-JIT-CONFIG", result.Output);

        // Round 3, H1: the runner is started through setpriv with no new privileges, and the configuration is on none of the command lines.
        Assert.Equal(["--no-new-privs ./run.sh"], await File.ReadAllLinesAsync(setprivLog, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SECRET-JIT-CONFIG", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-JIT-CONFIG", ScriptUpload.Loader, StringComparison.Ordinal);
    }

    // ---- the framing itself

    [Fact]
    public void ARefusedScript_IsNeverFramed()
    {
        Assert.Throws<ArgumentException>(() => ScriptUpload.Frame(string.Empty, null));
        Assert.Throws<ArgumentException>(() => ScriptUpload.Frame("a\0b", null));
        Assert.Throws<ArgumentException>(() => ScriptUpload.Frame(new string('x', ScriptUpload.MaximumScriptBytes + 1), null));
        using var largest = ScriptUpload.Frame(new string('x', ScriptUpload.MaximumScriptBytes), null);
        Assert.NotNull(largest);
    }

    [Fact]
    public async Task TheJoinedStream_ReadsTheFirstThenTheSecond_AndDisposesNeither()
    {
        var first = new MemoryStream([1, 2]);
        var second = new MemoryStream([3, 4, 5]);
        await using var joined = new ConcatenatedStream(first, second);

        using var copy = new MemoryStream();
        await joined.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4, 5], copy.ToArray());
        Assert.True(first.CanRead);
        Assert.True(second.CanRead);
        Assert.Equal(0, joined.Read(new byte[4], 0, 4));
        Assert.Equal(0, joined.Read(Span<byte>.Empty));
        Assert.True(joined.CanRead);
        Assert.False(joined.CanSeek);
        Assert.False(joined.CanWrite);
        Assert.Throws<NotSupportedException>(() => joined.Length);
        Assert.Throws<NotSupportedException>(() => joined.Position);
        Assert.Throws<NotSupportedException>(() => joined.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => joined.SetLength(0));
        Assert.Throws<NotSupportedException>(() => joined.Write([1], 0, 1));
        joined.Flush();
    }

    [Fact]
    public void TheJoinedStream_NeedsBothStreams()
    {
        Assert.Throws<ArgumentNullException>(() => new ConcatenatedStream(null!, Stream.Null));
        Assert.Throws<ArgumentNullException>(() => new ConcatenatedStream(Stream.Null, null!));
    }

    [Fact]
    public void TheLoader_IsShort_AndHasNoNewlineOrSingleQuote()
    {
        Assert.True(ScriptUpload.Loader.Length < 120);
        Assert.DoesNotContain('\n', ScriptUpload.Loader);
        Assert.DoesNotContain('\'', ScriptUpload.Loader);
        Assert.DoesNotContain('\\', ScriptUpload.Loader);
    }
}
