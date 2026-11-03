// Covers: red-team F2, L1 (every wsl.exe command goes through the account's process runner with exactly the arguments below and never under the loop's own token; names are checked before they reach a
// command line; a listing that cannot be read throws and is never an empty list; a failed terminate or unregister counts as done only when the list shows the machine gone; commands have time
// limits; the script travels in front of its data through standard input and the command line stays under the logon limit), QA M5. docs/owner/free-ci.md.
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

public sealed class WslClientTests
{
    private static readonly string s_wslPath = Path.Combine(Path.GetTempPath(), "windows", "System32", "wsl.exe");
    private static readonly TimeSpan s_stepLimit = TimeSpan.FromMinutes(1);
    private static readonly string s_installFolder = Path.Combine(Path.GetTempPath(), "DbdLocalCi-wsl", "distros", "dbd-ci-job-20261029120000");

    private readonly FakeTimeProvider _time = TestTime.New();
    private readonly FakeAccountProcess _process = new();
    private readonly MemoryCiLog _log = new(new Redactor());
    private readonly WslClient _wsl;

    public WslClientTests()
    {
        _wsl = new WslClient(_process, s_wslPath, _time, _log);
    }

    private static string Joined(AccountCommand command) => string.Join(' ', command.Arguments);

    /// <summary>A wsl.exe that lists these machines (as UTF-16 bytes, as Windows writes them) for <c>--list --quiet --all</c> and exits 0 for anything else.</summary>
    private void Lists(params string[] names)
    {
        _process.OnRun = async (command, token) =>
        {
            if (command.Arguments.SequenceEqual(["--list", "--quiet", "--all"]))
            {
                var bytes = Encoding.Unicode.GetBytes(string.Concat(names.Select(n => n + "\r\n")));
                await command.RawOutput!.WriteAsync(bytes, token);
            }

            return 0;
        };
    }

    [Fact]
    public async Task IsUsable_WhenTheVersionAndTheListingBothAnswer()
    {
        _process.OnRun = (command, _) =>
        {
            if (command.Arguments.SequenceEqual(["--version"]))
            {
                command.OnLine!("WSL version: 2.6.1.0");
            }

            return Task.FromResult(0);
        };

        Assert.True(await _wsl.IsUsableAsync(CancellationToken.None));

        Assert.Equal(["--version", "--list --quiet --all"], _process.Commands.Select(Joined).ToArray());
        Assert.All(_process.Commands, c => Assert.Equal(s_wslPath, c.FileName));
        Assert.Contains(_log.Lines, l => l.Contains("WSL version: 2.6.1.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsNotUsable_WhenTheVersionFails_AndTheListingIsNotAsked()
    {
        _process.OnRun = (command, _) =>
        {
            command.OnLine!("Invalid command line option: --version");
            return Task.FromResult(1);
        };

        Assert.False(await _wsl.IsUsableAsync(CancellationToken.None));

        Assert.Single(_process.Commands);
        Assert.Contains(_log.Lines, l => l.Contains("exit code 1", StringComparison.Ordinal) && l.Contains("Invalid command line option", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IsNotUsable_WhenTheListingFails()
    {
        _process.OnRun = (command, _) => Task.FromResult(command.Arguments.SequenceEqual(["--list", "--quiet", "--all"]) ? 1 : 0);

        Assert.False(await _wsl.IsUsableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AProgramThatCannotBeStartedAsTheAccount_IsNotHidden()
    {
        _process.OnRun = (_, _) => throw new InvalidOperationException("Windows would not start wsl.exe as dbd-ci-wsl");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.IsUsableAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.ShutdownAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheListing_IsReadFromUtf16Bytes_AndFromUtf8Bytes()
    {
        Lists("Ubuntu", "dbd-ci-job-20261029120000");
        Assert.Equal(["Ubuntu", "dbd-ci-job-20261029120000"], await _wsl.ListDistributionsAsync(CancellationToken.None));

        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(Encoding.UTF8.GetBytes("Ubuntu\ndbd-ci-base\n"), token);
            return 0;
        };
        Assert.Equal(["Ubuntu", "dbd-ci-base"], await _wsl.ListDistributionsAsync(CancellationToken.None));

        var bom = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Debian\r\n")).ToArray();
        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(bom, token);
            return 0;
        };
        Assert.Equal(["Debian"], await _wsl.ListDistributionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyListing_WithExitCodeZero_IsAnEmptyList()
    {
        Lists();

        Assert.Empty(await _wsl.ListDistributionsAsync(CancellationToken.None));
    }

    /// <summary>L1: a failed listing is a failure, never "nothing is installed".</summary>
    [Fact]
    public async Task AListingThatFails_Throws_AndSaysWhatWslSaid()
    {
        _process.OnRun = (command, _) =>
        {
            command.OnLine!("Wsl/Service/E_UNEXPECTED");
            return Task.FromResult(-1);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.ListDistributionsAsync(CancellationToken.None));

        Assert.Contains("exit code -1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("E_UNEXPECTED", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListingThatIsFarTooLong_IsRefused_NotKeptInMemory()
    {
        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(new byte[65 * 1024], token);
            return 0;
        };

        await Assert.ThrowsAsync<IOException>(() => _wsl.ListDistributionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ANoticeBetweenTheNames_IsNotAMachine()
    {
        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(Encoding.Unicode.GetBytes("Ubuntu\r\nWSL has a new version available\r\n\r\ndbd-ci-base\r\n"), token);
            return 0;
        };

        Assert.Equal(["Ubuntu", "dbd-ci-base"], await _wsl.ListDistributionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Import_PipesTheCheckedArchiveIntoStandardInput_WithTheExactArguments()
    {
        Stream? seen = null;
        _process.OnRun = (command, _) =>
        {
            seen = command.StandardInput;
            return Task.FromResult(0);
        };
        using var archive = new MemoryStream([1, 2, 3]);

        await _wsl.ImportAsync("dbd-ci-job-20261029120000", s_installFolder, archive, CancellationToken.None);

        var command = Assert.Single(_process.Commands);
        Assert.Equal(["--import", "dbd-ci-job-20261029120000", s_installFolder, "-", "--version", "2"], command.Arguments);
        Assert.Same(archive, seen);
        Assert.Null(command.RawOutput);
    }

    [Fact]
    public async Task AnImportThatFails_Throws_WithWhatWslSaid()
    {
        _process.OnRun = (command, _) =>
        {
            command.OnLine!("Error code: Wsl/Service/RegisterDistro/ERROR_ALREADY_EXISTS");
            return Task.FromResult(1);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.ImportAsync("m", s_installFolder, Stream.Null, CancellationToken.None));

        Assert.Contains("Importing m failed (exit code 1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ERROR_ALREADY_EXISTS", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--unregister")]
    [InlineData("-d")]
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("")]
    [InlineData("a\nb")]
    [InlineData("a;b")]
    [InlineData("é")]
    public async Task ANameThatCouldBeReadAsAnOptionOrSplitACommandLine_IsRefusedBeforeAnythingRuns(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ImportAsync(name, s_installFolder, Stream.Null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ExportAsync(name, Stream.Null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.TerminateAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.UnregisterAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ExecuteAsync(name, "runner", "true", null, null, s_stepLimit, CancellationToken.None));

        Assert.Empty(_process.Commands);
    }

    [Theory]
    [InlineData("relative/folder")]
    [InlineData("")]
    [InlineData("/x\" --version")]
    [InlineData("/x\ny")]
    [InlineData("/x|y")]
    public async Task AnInstallFolderThatIsNotAPlainFullPath_IsRefused(string directory)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ImportAsync("m", directory, Stream.Null, CancellationToken.None));

        Assert.Empty(_process.Commands);
    }

    [Theory]
    [InlineData("-evil")]
    [InlineData("Root")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task ALinuxUserThatIsNotAPlainName_IsRefused(string user)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ExecuteAsync("m", user, "true", null, null, s_stepLimit, CancellationToken.None));

        Assert.Empty(_process.Commands);
    }

    [Fact]
    public async Task Export_WritesTheArchiveToTheCallersStream_ThroughStandardOutput()
    {
        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(new byte[] { 9, 8, 7 }, token);
            return 0;
        };
        using var destination = new MemoryStream();

        await _wsl.ExportAsync("dbd-ci-base", destination, CancellationToken.None);

        var command = Assert.Single(_process.Commands);
        Assert.Equal(["--export", "dbd-ci-base", "-"], command.Arguments);
        Assert.Equal(new byte[] { 9, 8, 7 }, destination.ToArray());
        Assert.Null(command.StandardInput);
    }

    [Fact]
    public async Task AnExportThatFails_Throws_WithWhatWslSaid()
    {
        _process.OnRun = (command, _) =>
        {
            command.OnLine!("There is not enough space on the disk.");
            return Task.FromResult(1);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.ExportAsync("dbd-ci-base", Stream.Null, CancellationToken.None));

        Assert.Contains("not enough space", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminate_AndUnregister_UseTheirOwnCommands()
    {
        Lists();

        await _wsl.TerminateAsync("m", CancellationToken.None);
        await _wsl.UnregisterAsync("m", CancellationToken.None);

        Assert.Equal(["--terminate m", "--unregister m"], _process.Commands.Select(Joined).ToArray());
    }

    [Fact]
    public async Task AFailedUnregister_IsFine_WhenTheMachineWasNotThereAnyway()
    {
        _process.OnRun = (command, _) => Task.FromResult(command.Arguments.SequenceEqual(["--unregister", "m"]) ? 1 : 0);

        await _wsl.UnregisterAsync("m", CancellationToken.None);

        Assert.Equal(["--unregister m", "--list --quiet --all"], _process.Commands.Select(Joined).ToArray());
    }

    [Fact]
    public async Task AFailedUnregister_IsAFailure_WhenTheMachineIsStillListed()
    {
        _process.OnRun = async (command, token) =>
        {
            if (command.Arguments.SequenceEqual(["--list", "--quiet", "--all"]))
            {
                await command.RawOutput!.WriteAsync(Encoding.Unicode.GetBytes("m\r\n"), token);
                return 0;
            }

            command.OnLine!("The process cannot access the file because it is being used by another process.");
            return 1;
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.UnregisterAsync("m", CancellationToken.None));
        var stop = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.TerminateAsync("m", CancellationToken.None));

        Assert.Contains("Removing m failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("being used by another process", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stopping m failed", stop.Message, StringComparison.Ordinal);
    }

    /// <summary>L1 again: when a command failed and the list cannot be read to see whether the machine is gone, that is a failure.</summary>
    [Fact]
    public async Task AFailedUnregister_WhoseCheckCannotBeMade_IsAFailure_NotSuccess()
    {
        _process.OnRun = (_, _) => Task.FromResult(1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.UnregisterAsync("m", CancellationToken.None));

        Assert.Contains("Asking WSL for the Linux machines failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_RunsWslShutdown_AndAFailureThrows()
    {
        await _wsl.ShutdownAsync(CancellationToken.None);
        Assert.Equal("--shutdown", Joined(Assert.Single(_process.Commands)));

        _process.OnRun = (_, _) => Task.FromResult(1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _wsl.ShutdownAsync(CancellationToken.None));
        Assert.Contains("Stopping the Linux machine failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_SendsTheScriptInFrontOfItsData_ThroughStandardInput_WithAShortLoaderOnTheCommandLine()
    {
        byte[]? input = null;
        _process.OnRun = async (command, token) =>
        {
            using var copy = new MemoryStream();
            await command.StandardInput!.CopyToAsync(copy, token);
            input = copy.ToArray();
            command.OnLine!("hel\0l\0o\0");
            return 5;
        };
        using var data = new MemoryStream(Encoding.UTF8.GetBytes("THE-DATA"));
        var lines = new List<string>();

        var exit = await _wsl.ExecuteAsync("dbd-ci-job-20261029120000", "runner", "echo é", data, lines.Add, s_stepLimit, CancellationToken.None);

        var command = Assert.Single(_process.Commands);
        Assert.Equal(5, exit);
        Assert.Equal(["-d", "dbd-ci-job-20261029120000", "-u", "runner", "--cd", "/", "--exec", "/bin/bash", "-c", ScriptUpload.Loader], command.Arguments);
        Assert.Equal(Encoding.UTF8, command.OutputEncoding);
        Assert.Equal("7\necho \u00e9THE-DATA", Encoding.UTF8.GetString(input!));
        Assert.Equal(["hello"], lines);
    }

    [Fact]
    public async Task TheCommandLineOfAnyExecute_StaysUnderTheLimitOfTheLogonApi_EvenWithTheLongestNames()
    {
        var distribution = new string('d', WslNames.MaximumDistributionLength);
        var user = new string('u', 32);

        await _wsl.ExecuteAsync(distribution, user, "true", null, null, s_stepLimit, CancellationToken.None);

        var command = Assert.Single(_process.Commands);
        var line = CommandLineQuoting.Join(s_wslPath, command.Arguments);
        Assert.True(line.Length < CommandLineQuoting.LogonCommandLineLimit, $"the command line is {line.Length} characters");
        Assert.DoesNotContain("true", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptTooLargeToUpload_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ExecuteAsync("m", "runner", new string('x', ScriptUpload.MaximumScriptBytes + 1), null, null, s_stepLimit, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ExecuteAsync("m", "runner", "a\0b", null, null, s_stepLimit, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _wsl.ExecuteAsync("m", "runner", string.Empty, null, null, s_stepLimit, CancellationToken.None));

        Assert.Empty(_process.Commands);
    }

    [Fact]
    public async Task EveryScriptTheProgramRuns_FitsTheUploadLimit_AndHasNoNulCharacter()
    {
        foreach (var script in new[] { WslPreflight.Script, BaseImageBuilder.WslConfScript, BaseImageBuilder.SetupScript, LinuxJobHost.UnpackScript, LinuxJobHost.RunScript })
        {
            var stream = ScriptUpload.Frame(script, null);
            await using (stream)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
                Assert.True(copy.Length < ScriptUpload.MaximumScriptBytes);
            }
        }
    }

    // ---- time limits

    [Fact]
    public async Task AManagementCommandThatNeverEnds_IsEndedAfterThreeMinutes_WithATimeoutNotACancel()
    {
        _process.OnRun = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        };

        var running = _wsl.ShutdownAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(3));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Contains("did not finish shutting down within 3 minutes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImportGetsMoreTime_ButNotForEver()
    {
        _process.OnRun = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        };

        var running = _wsl.ImportAsync("m", s_installFolder, Stream.Null, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(60));
        await Task.Yield();
        Assert.False(running.IsCompleted);
        _time.Advance(TimeSpan.FromMinutes(30));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Contains("90 minutes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersOwnCancel_StaysACancel_NotATimeout()
    {
        _process.OnRun = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        };
        using var cts = new CancellationTokenSource();
        var running = _wsl.ShutdownAsync(cts.Token);

        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => running.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<TimeoutException>(ex);
    }

    /// <summary>Round 3, L2: every step that runs a script in a Linux machine has a limit of its own, the run of a job too (it used to have none).</summary>
    [Fact]
    public async Task AStepInTheLinuxMachine_IsEndedWhenItsOwnLimitRunsOut_WithAPlainMessage()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OnRun = async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        };
        var running = _wsl.ExecuteAsync("m", "runner", "true", null, null, s_stepLimit, CancellationToken.None);
        await reached.Task.OrFail();

        _time.Advance(s_stepLimit - TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.False(running.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => running.OrFail());
        Assert.Contains("did not finish a step in m within 1 minutes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepInTheLinuxMachine_EndsWhenTheCallerSaysSo_AsACancelAndNotATimeout()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OnRun = async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        };
        using var cts = new CancellationTokenSource();
        var running = _wsl.ExecuteAsync("m", "runner", "true", null, null, TimeSpan.FromHours(20), cts.Token);
        await reached.Task.OrFail();

        _time.Advance(TimeSpan.FromHours(10));
        Assert.False(running.IsCompleted);
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => running.OrFail());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AStepWithoutARealLimit_IsRefusedBeforeAnythingRuns(int seconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _wsl.ExecuteAsync("m", "runner", "true", null, null, TimeSpan.FromSeconds(seconds), CancellationToken.None));

        Assert.Empty(_process.Commands);
    }

    [Fact]
    public void TheLimitsOfTheSteps_AreSetAndTheRunOfAJobHasABackstopBeyondTheLoopsOwnLimits()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), WslLimits.Harden);
        Assert.Equal(TimeSpan.FromMinutes(2), WslLimits.Preflight);
        Assert.Equal(TimeSpan.FromMinutes(15), WslLimits.Unpack);
        Assert.Equal(TimeSpan.FromMinutes(5), WslLimits.BaseSettings);
        Assert.Equal(TimeSpan.FromMinutes(60), WslLimits.BaseSetup);
        Assert.Equal(TimeSpan.FromMinutes(5), WslLimits.Probe);
        Assert.Equal(TimeSpan.FromMinutes(30), WslLimits.Install);
        Assert.Equal(TimeSpan.FromMinutes(30), WslLimits.Update);
        Assert.Equal(TimeSpan.FromMinutes(90), WslLimits.ImageBuild);
        Assert.Equal(TimeSpan.FromMinutes(3), WslLimits.Quick);
        Assert.Equal(TimeSpan.FromMinutes(90), WslLimits.Transfer);
        Assert.True(WslLimits.Run > LocalCiDefaults.MaximumWait + LocalCiDefaults.JobTimeout);
    }

    [Fact]
    public async Task TheVersion_IsAskedAsTheAccount_CleanedAndBounded_AndAMissingAnswerIsNull()
    {
        _process.OnRun = (command, _) =>
        {
            if (command.Arguments.SequenceEqual(["--version"]))
            {
                foreach (var line in new[] { "W\0S\0L\0 version: 2.4.8.0\r", string.Empty, "Kernel version: 5.15.167.4-1", new string('x', 500), "Windows version: 10.0.22631.4460" })
                {
                    command.OnLine!(line);
                }
            }

            return Task.FromResult(0);
        };

        var text = await _wsl.VersionAsync(CancellationToken.None);

        Assert.Equal("WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1\nWindows version: 10.0.22631.4460", text);
        Assert.Equal(["--version"], _process.Commands.Select(Joined).ToArray());

        _process.OnRun = (_, _) => Task.FromResult(1);
        Assert.Null(await _wsl.VersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheListOfMachines_IncludesThoseBeingInstalledOrRemoved()
    {
        Lists("dbd-ci-job-20261029120000");

        var names = await _wsl.ListDistributionsAsync(CancellationToken.None);

        Assert.Equal(["dbd-ci-job-20261029120000"], names);
        Assert.Equal(["--list --quiet --all"], _process.Commands.Select(Joined).ToArray());
    }

    [Fact]
    public void BadConstructorArguments_AreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new WslClient(null!, s_wslPath, _time, _log));
        Assert.Throws<ArgumentException>(() => new WslClient(_process, string.Empty, _time, _log));
    }

    /// <summary>The listing is read up to 64 KiB exactly; one character more is refused (a program that prints that much is not listing machines).</summary>
    [Fact]
    public async Task AListingOfExactly64KiB_IsRead_AndOneCharacterMoreIsRefused()
    {
        const int Names = 8192; // "xx\r\n" is 8 bytes in UTF-16
        var exact = Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("xx\r\n", Names)));
        var tooMuch = Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("xx\r\n", Names)) + "y");
        Assert.Equal(64 * 1024, exact.Length);

        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(exact, token);
            return 0;
        };
        Assert.Equal(Names, (await _wsl.ListDistributionsAsync(CancellationToken.None)).Count);

        _process.OnRun = async (command, token) =>
        {
            await command.RawOutput!.WriteAsync(tooMuch, token);
            return 0;
        };
        var error = await Assert.ThrowsAsync<IOException>(() => _wsl.ListDistributionsAsync(CancellationToken.None));
        Assert.Contains("printed more than 65536 bytes", error.Message, StringComparison.Ordinal);
    }
}
