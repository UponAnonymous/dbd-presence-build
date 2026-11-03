// Covers: local CI round 3, QA B-1 (35 tests used to fail instead of skip where there is no bash, because the fake Linux called File.SetUnixFileMode, which throws on Windows, before the test
// skipped itself), red-team round 3, M3 (no test hook in the shipped scripts). The guard tests here simulate "no bash" and a Windows that throws on every Unix-only call, on any system.
// FR-CI-09 (the scripts that go into the Linux machine are tested with real bash where bash exists, and skipped with a reason where it does not).
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // The harness only calls Unix-only functions behind the hook that these tests replace.
public sealed class BashHarnessTests
{
    /// <summary>Records every Unix-only call, and throws what Windows throws when asked to.</summary>
    private sealed class RecordingUnixFiles : IUnixFiles
    {
        public List<string> Links { get; } = [];

        public List<string> Executables { get; } = [];

        public Exception? Failure { get; set; }

        public int Calls => Links.Count + Executables.Count;

        public void CreateSymbolicLink(string link, string target)
        {
            Links.Add(link);
            if (Failure is not null)
            {
                throw Failure;
            }
        }

        public void MakeExecutable(string path)
        {
            Executables.Add(path);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private static FakeLinux Make(bool bashAvailable, IUnixFiles files, Func<string>? makeBase = null) => new(bashAvailable, files, makeBase);

    /// <summary>
    /// What the action throws. xUnit's own <c>Record.Exception</c> lets a skip pass through (it ends the running test as skipped), so this test must catch the skip itself to look at it.
    /// </summary>
#pragma warning disable CA1031 // The point of the helper is to hold any exception, a skip included, for the test to inspect.
    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
#pragma warning restore CA1031

    /// <summary>B-1: on a system without bash the very first thing the fake Linux does is skip the test. If anything Unix-only ran first the test would fail with PlatformNotSupportedException.</summary>
    [Fact]
    public void WithoutBash_TheFakeLinuxSkipsTheTest_BeforeItCallsAnythingThatOnlyWorksOnUnix()
    {
        var windows = new RecordingUnixFiles { Failure = new PlatformNotSupportedException("File.SetUnixFileMode is not supported on this platform.") };
        var expected = Catch(() => Bash.RequireOrSkip(false));
        Assert.NotNull(expected);

        var actual = Catch(() => Make(bashAvailable: false, windows));

        Assert.NotNull(actual);
        Assert.IsNotType<PlatformNotSupportedException>(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(0, windows.Calls);
    }

    /// <summary>A skipped test leaves no folder behind either: the folder is only made after the skip.</summary>
    [Fact]
    public void WithoutBash_NoFolderIsMade()
    {
        var made = 0;
        var windows = new RecordingUnixFiles();

        var skipped = Catch(() => Make(bashAvailable: false, windows, () =>
        {
            made++;
            return Directory.CreateTempSubdirectory("fake-linux-never-").FullName;
        }));

        Assert.NotNull(skipped);
        Assert.Equal(0, made);
    }

    /// <summary>The same hook carries every Unix-only call when bash is there: the symbolic links to the real tools and the file mode of each fake program.</summary>
    [Fact]
    public void WithBash_EveryUnixOnlyCallGoesThroughTheHook()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "PENDING-ENV: the fake Linux machine makes Linux links and file modes; GitHub's Windows machines have a bash but no such files.");
        var recorder = new RecordingUnixFiles();

        using var linux = Make(bashAvailable: true, recorder);
        linux.Tool("sudo", "exit 0");
        linux.Program("home/runner/run.sh", "#!/bin/sh\n");

        Assert.NotEmpty(recorder.Links);
        Assert.All(recorder.Links, link => Assert.StartsWith(linux.Bin, link, StringComparison.Ordinal));
        Assert.Contains(recorder.Executables, e => e.EndsWith("id", StringComparison.Ordinal));
        Assert.Contains(recorder.Executables, e => e.EndsWith("sudo", StringComparison.Ordinal));
        Assert.Contains(recorder.Executables, e => e.EndsWith("run.sh", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisposedFakeLinux_RemovesItsFolder()
    {
        var recorder = new RecordingUnixFiles();
        string root;
        using (var linux = Make(bashAvailable: true, recorder))
        {
            root = linux.Root;
            Assert.True(Directory.Exists(root));
        }

        Assert.False(Directory.Exists(root));
    }

    /// <summary>
    /// No test file but the harness calls a function that only works on Unix: such a call in a test body would fail on Windows before any skip can run (B-1 again). The harness hides them behind
    /// <see cref="IUnixFiles"/>, whose users skip first.
    /// </summary>
    [Fact]
    public void NoTestFileExceptTheHarnessCallsAUnixOnlyFileFunction()
    {
        var folder = TestSources.Folder();
        var offenders = Directory.GetFiles(folder, "*.cs")
            .Where(f => Path.GetFileName(f) is not ("BashHarness.cs" or "BashHarnessTests.cs"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"SetUnixFileMode|GetUnixFileMode|\bUnixFileMode\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "These test files call a Unix-only file function directly: " + string.Join(", ", offenders));
    }

    /// <summary>Red-team round 3, M3: nothing in a shipped script reads the old test hook from the environment, and the root prefix is a fixed empty string.</summary>
    [Fact]
    public void NoShippedScriptReadsATestHookFromTheEnvironment()
    {
        foreach (var script in new[] { WslPreflight.Script, BaseImageBuilder.WslConfScript, BaseImageBuilder.SetupScript, LinuxJobHost.UnpackScript, LinuxJobHost.RunScript })
        {
            Assert.DoesNotContain("TEST_ROOT", script, StringComparison.Ordinal);
            Assert.DoesNotContain("${DBD", script, StringComparison.Ordinal);
            Assert.DoesNotContain("printenv", script, StringComparison.Ordinal);
            Assert.All(Regex.Matches(script, "^R=.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant).Select(m => m.Value), line => Assert.Equal("R=\"\"", line));
        }
    }

    [Fact]
    public void TheRootWrapper_ReplacesTheOneRootLine_AndRefusesAScriptWithoutItOrWithTwo()
    {
        Assert.Equal("set -u\nR='/fake'\necho \"$R\"", Scripts.WithRoot("set -u\nR=\"\"\necho \"$R\"", "/fake"));
        Assert.ThrowsAny<Exception>(() => Scripts.WithRoot("echo none", "/fake"));
        Assert.ThrowsAny<Exception>(() => Scripts.WithRoot("R=\"\"\nR=\"\"", "/fake"));
        Assert.ThrowsAny<Exception>(() => Scripts.WithRoot("R=\"\"", "/it's"));
    }

    [Fact]
    public void EveryShippedScriptThatTheTestsRunAgainstAFakeRoot_HasItsRootLineExactlyOnce()
    {
        foreach (var script in new[] { WslPreflight.Script, BaseImageBuilder.WslConfScript, BaseImageBuilder.SetupScript })
        {
            Assert.Equal(1, Regex.Count(script, "^R=\"\"$", RegexOptions.Multiline | RegexOptions.CultureInvariant));
        }
    }
}

/// <summary>Finds the folders of this test project's and the tool's source files (the tests run from a build folder).</summary>
internal static class TestSources
{
    /// <summary>The folder of this test project's source files.</summary>
    public static string Folder() => Path.Combine(RepositoryRoot(), "tests", "DbdPresence.LocalCi.Tests");

    /// <summary>The folder of the tool's source files.</summary>
    public static string ToolFolder() => Path.Combine(RepositoryRoot(), "tools", "DbdPresence.LocalCi");

    /// <summary>The repository root (the folder that holds DbdPresence.slnx).</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DbdPresence.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
