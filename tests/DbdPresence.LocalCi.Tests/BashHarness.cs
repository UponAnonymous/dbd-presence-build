using System.Diagnostics;
using System.Text;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // These helpers only run where bash exists (Linux); every test that uses them skips itself first.

/// <summary>What a bash run printed and returned.</summary>
internal sealed record BashResult(int Exit, string Output, string Error)
{
    /// <summary>The lines of standard output.</summary>
    public IReadOnlyList<string> Lines => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Runs real bash for the tests of the scripts that go into the Linux machine. They are skipped where bash is not there (the Windows CI).</summary>
internal static class Bash
{
    public static bool Exists => OperatingSystem.IsLinux() && File.Exists("/bin/bash") && File.Exists("/bin/sh");

    public static void RequireOrSkip() => RequireOrSkip(Exists);

    /// <summary>Skips the running test unless <paramref name="available"/>. The one place that decides; <see cref="FakeLinux"/> calls it as the first thing it does (QA round 3, B-1).</summary>
    public static void RequireOrSkip(bool available) => Assert.SkipUnless(available, "bash is not available on this system; these tests run on Linux.");

    public static async Task<BashResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("bash did not start");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The script ended before it read all of its input; its exit code and output say why.
            await process.WaitForExitAsync(cancellationToken);
        }

        await process.WaitForExitAsync(cancellationToken);
        return new BashResult(process.ExitCode, await output, await error);
    }
}

/// <summary>
/// The two file calls the fake Linux needs that only work on Unix (a symbolic link, a file mode). They are behind an interface so that a test can prove they are never reached on a system without
/// bash: on Windows <c>File.SetUnixFileMode</c> throws <see cref="PlatformNotSupportedException"/>, which made 35 tests fail instead of skip (QA round 3, B-1).
/// </summary>
internal interface IUnixFiles
{
    void CreateSymbolicLink(string link, string target);

    void MakeExecutable(string path);
}

/// <summary>The real calls. Only used where bash exists, that is on Linux.</summary>
internal sealed class RealUnixFiles : IUnixFiles
{
    public void CreateSymbolicLink(string link, string target) => File.CreateSymbolicLink(link, target);

    public void MakeExecutable(string path) => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

/// <summary>
/// A fake Linux root folder and a folder of fake programs, for running the real scripts of the program against them. The scripts that are tested this way start with one line that sets the root
/// folder prefix (<c>R=""</c> in the shipped text, empty inside WSL); the harness replaces exactly that line and nothing else (<see cref="Scripts.WithRoot"/>), so the text that runs is the text that
/// ships and no environment variable can redirect it. The path holds only the fake programs and links to the few real tools a script needs, so nothing of the machine the test runs on leaks in.
/// </summary>
/// <remarks>The constructor skips the test first, before it makes a folder or calls anything that only works on Unix (QA round 3, B-1).</remarks>
internal sealed class FakeLinux : IDisposable
{
    private static readonly string[] s_toolFolders = ["/usr/bin", "/bin"];

    private static readonly string[] s_realTools = ["bash", "sh", "grep", "ls", "cat", "head", "od", "tr", "wc", "mkdir", "rmdir", "rm", "cut", "sort", "true", "false", "chmod", "sha256sum"];

    private readonly string _base;
    private readonly IUnixFiles _unix;

    public FakeLinux()
        : this(Bash.Exists, new RealUnixFiles())
    {
    }

    internal FakeLinux(bool bashAvailable, IUnixFiles unix, Func<string>? makeBase = null)
    {
        // First of all: where there is no bash nothing below may run (a skipped test must not leave a folder behind or call a Unix-only function).
        Bash.RequireOrSkip(bashAvailable);
        _unix = unix;
        _base = (makeBase ?? (() => Directory.CreateTempSubdirectory("fake-linux-").FullName))();
        Root = Path.Combine(_base, "root");
        Bin = Path.Combine(_base, "bin");
        Directory.CreateDirectory(Path.Combine(Root, "proc", "sys", "fs", "binfmt_misc"));
        Directory.CreateDirectory(Path.Combine(Root, "mnt"));
        Directory.CreateDirectory(Path.Combine(Root, "etc"));
        File.WriteAllText(Path.Combine(Root, "proc", "mounts"), "proc /proc proc rw 0 0\n/dev/sdc / ext4 rw 0 0\n");
        Directory.CreateDirectory(Bin);

        // A new, clean WSL 2 machine as it looks from inside: 8,000,000 kB of memory and no swap, started a moment ago with its own boot id, user namespaces closed, a flag that says no new privileges
        // can be set, a kernel that tells its release and build date. Tests change the one thing they are about.
        Write("proc/meminfo", "MemTotal:        8000000 kB\nMemFree:         7000000 kB\nSwapTotal:             0 kB\nSwapFree:              0 kB\n");
        Write("proc/uptime", "12.34 45.67\n");
        Write("proc/sys/kernel/random/boot_id", CleanBootId + "\n");
        Write("proc/sys/user/max_user_namespaces", "0\n");
        Write("proc/self/status", "Name:\tsh\nNoNewPrivs:\t1\nSeccomp:\t0\n");
        foreach (var tool in s_realTools)
        {
            var real = s_toolFolders.Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);
            if (real is not null)
            {
                _unix.CreateSymbolicLink(Path.Combine(Bin, tool), real);
            }
        }

        // The ordinary user of a job: the runner.
        User("runner", 1000);
        Tool("nproc", "echo 4");
        Tool("uname", "case \"$1\" in -r) echo " + CleanKernelRelease + " ;; -v) echo '" + CleanKernelVersion + "' ;; *) echo Linux ;; esac");
        Tool("setpriv", "[ \"${1:-}\" = \"--no-new-privs\" ] || exit 2; shift; exec \"$@\"");
        Tool("unshare", "exit 1");

        // The tools the isolation probe uses: a mount that fails, an unmount that works, and a connection test that reaches nothing. Tests that are about the probe replace them.
        Tool("mount", "echo 'mount: wrong fs type, bad option, bad superblock' >&2; exit 32");
        Tool("umount", "exit 0");
        Tool("timeout", "shift; case \"$*\" in *'/dev/tcp/'*) exit 1 ;; esac; exec \"$@\"");
    }

    /// <summary>The boot id of the clean machine.</summary>
    public const string CleanBootId = "6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab";

    public const string CleanKernelRelease = "5.15.167.4-microsoft-standard-WSL2";

    public const string CleanKernelVersion = "#1 SMP Tue Nov  5 00:21:55 UTC 2024";

    /// <summary>Takes a fake program off the path (it is then "not installed").</summary>
    public void RemoveTool(string name) => File.Delete(Path.Combine(Bin, name));

    /// <summary>The fake root folder.</summary>
    public string Root { get; }

    /// <summary>The only folder on the path.</summary>
    public string Bin { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative.TrimStart('/'));

    public void Dir(string relative) => Directory.CreateDirectory(PathOf(relative));

    public void Write(string relative, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(relative))!);
        File.WriteAllText(PathOf(relative), content);
    }

    public void Mounts(string text) => Write("proc/mounts", text);

    /// <summary>A fake program on the path: a shell script with this body.</summary>
    public void Tool(string name, string body)
    {
        var path = Path.Combine(Bin, name);
        File.Delete(path);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        _unix.MakeExecutable(path);
    }

    /// <summary>A program inside the fake root (for example the runner's <c>run.sh</c>): a file with this content that can be run.</summary>
    public void Program(string relative, string content)
    {
        Write(relative, content);
        _unix.MakeExecutable(PathOf(relative));
    }

    /// <summary>
    /// Makes <c>id</c> answer as this user: <c>id -u</c> the number, <c>id -un</c> the name, and <c>id -u runner</c> that the user <c>runner</c> exists only when <paramref name="runnerExists"/> says so.
    /// </summary>
    public void User(string name, int number, bool runnerExists = true)
    {
        const string Body = """
            case "$1" in
              -u) if [ -n "${2:-}" ]; then if [ "$2" = "runner" ] && [ "__EXISTS__" = "1" ]; then echo 1000; else exit 1; fi; else echo __NUMBER__; fi ;;
              -un) echo __NAME__ ;;
              *) exit 1 ;;
            esac
            """;
        Tool("id", Body.Replace("__NUMBER__", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("__NAME__", name, StringComparison.Ordinal).Replace("__EXISTS__", runnerExists ? "1" : "0", StringComparison.Ordinal));
    }

    public Dictionary<string, string> Environment(params (string Name, string Value)[] more)
    {
        var environment = new Dictionary<string, string> { ["PATH"] = Bin, ["FAKE_LINUX_ROOT"] = Root, ["HOME"] = _base };
        foreach (var (name, value) in more)
        {
            environment[name] = value;
        }

        return environment;
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);
}

/// <summary>
/// Puts a fake root folder into one of the program's scripts. Every script that is tested against a fake root carries the single line <c>R=""</c> (the root prefix of its paths, empty inside WSL);
/// this replaces exactly that line and fails when it is not there exactly once, so what runs is the shipped text and nothing but its root differs (red-team round 3, M3: no test hook in the
/// shipped scripts).
/// </summary>
internal static class Scripts
{
    private const string RootLine = "R=\"\"";

    public static string WithRoot(string script, string root)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrEmpty(root);
        Assert.DoesNotContain('\'', root);
        var first = script.IndexOf(RootLine, StringComparison.Ordinal);
        Assert.True(first >= 0 && script.IndexOf(RootLine, first + 1, StringComparison.Ordinal) < 0, "the script must contain its root line R=\"\" exactly once");
        return script.Replace(RootLine, "R='" + root + "'", StringComparison.Ordinal);
    }
}
