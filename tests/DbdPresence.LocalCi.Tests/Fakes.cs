using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

/// <summary>A temporary folder that deletes itself.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = Directory.CreateTempSubdirectory("localci-test-").FullName;

        // The data folder is only ever created by setup in the real program; here it exists from the start.
        Directory.CreateDirectory(Paths.Machine);
    }

    public string Path { get; }

    public LocalCiPaths Paths => new(System.IO.Path.Combine(Path, "ProgramData"));

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class TestTime
{
    public static FakeTimeProvider New() => new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
}

/// <summary>A clock whose wall-clock reading can be made to jump (as a wrongly set PC clock does) while its monotonic part keeps running as the fake clock does.</summary>
internal sealed class SkewedTime : TimeProvider
{
    private readonly FakeTimeProvider _inner;

    public SkewedTime(FakeTimeProvider inner)
    {
        _inner = inner;
    }

    /// <summary>Added to the wall-clock reading (negative: the clock was set back).</summary>
    public TimeSpan Skew { get; set; }

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + Skew;

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _inner.CreateTimer(callback, state, dueTime, period);
}

internal static class TestWait
{
    /// <summary>The longest a test waits for something that must happen without any time passing: a safety limit, so a test that would hang fails instead.</summary>
    public static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    public static Task OrFail(this Task task) => task.WaitAsync(Safety);

    public static Task<T> OrFail<T>(this Task<T> task) => task.WaitAsync(Safety);
}

/// <summary>A list of calls that can also write each call, with a prefix, into a journal shared by several fakes (to check the order across them).</summary>
internal sealed class CallLog : System.Collections.ObjectModel.Collection<string>
{
    public List<string>? Journal { get; set; }

    public string Prefix { get; set; } = string.Empty;

    protected override void InsertItem(int index, string item)
    {
        base.InsertItem(index, item);
        Journal?.Add(Prefix + item);
    }
}

/// <summary>A call that never answers on its own (a wedged program or connection); only its token ends it. The test can wait until the call has started.</summary>
internal sealed class Hang
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set to make the next calls hang.</summary>
    public bool On { get; set; }

    /// <summary>Completes when a call has begun to hang (a safety limit stops a test that would wait for ever).</summary>
    public Task StartedAsync() => _started.Task.WaitAsync(TestWait.Safety);

    /// <summary>Waits for ever (until <paramref name="cancellationToken"/> ends it) when switched on; otherwise returns at once.</summary>
    public async Task MaybeAsync(CancellationToken cancellationToken)
    {
        if (On)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>WSL as the dedicated account sees it, scripted. A distribution has a disk folder that WSL makes on import and removes on unregister, as the real one does.</summary>
internal sealed class FakeWsl : IWsl
{
    private readonly Dictionary<string, string> _folders = new(StringComparer.OrdinalIgnoreCase);

    public CallLog Calls { get; } = new();

    public HashSet<string> Distributions { get; } = [];

    public bool Usable { get; set; } = true;

    public Exception? UsableFailure { get; set; }

    public Exception? ListFailure { get; set; }

    public Exception? ImportFailure { get; set; }

    public Exception? ExportFailure { get; set; }

    public Exception? TerminateFailure { get; set; }

    public Exception? UnregisterFailure { get; set; }

    public Exception? ShutdownFailure { get; set; }

    /// <summary>When set, a failed unregister leaves the disk folder behind (WSL does that when a file is in use).</summary>
    public bool UnregisterLeavesFolder { get; set; }

    /// <summary>The lines the isolation check prints; the default is a clean pass.</summary>
    public List<string> PreflightLines { get; } = [.. WslPreflight.RequiredChecks.Select(DefaultLine)];

    public int PreflightExit { get; set; }

    /// <summary>True (the default): every isolation check run reports a boot id of its own, as a machine that was started fresh does; false keeps the boot id written in <see cref="PreflightLines"/>.</summary>
    public bool NewBootIdEachTime { get; set; } = true;

    /// <summary>The isolation scripts that were run, as the host sent them (the parameters in front, then the shipped text).</summary>
    public List<string> PreflightScripts { get; } = [];

    /// <summary>What the step that closes user namespaces answers: the exit code and the lines.</summary>
    public int HardenExit { get; set; }

    public List<string> HardenLines { get; } = ["user namespaces are closed"];

    /// <summary>The time limit each executed script was given, in order (the call log has the distribution and the user).</summary>
    public List<TimeSpan> ExecutedLimits { get; } = [];

    /// <summary>The lines the isolation probe prints; the default is every attempt stopped.</summary>
    public List<string> ProbeLines { get; } = [.. IsolationProbe.AttemptIds.Select(id => $"ATTEMPT {id} BLOCKED nothing got through")];

    public int ProbeExit { get; set; }

    /// <summary>The probe scripts that were run (the marker line in front, then the shipped text) and the user each ran as.</summary>
    public List<string> ProbeScripts { get; } = [];

    public List<string> ProbeUsers { get; } = [];

    /// <summary>When set, the probe script does not answer on its own (a probe that hangs); only its token ends it.</summary>
    public Hang ProbeHang { get; } = new();

    /// <summary>Thrown by the probe script instead of answering.</summary>
    public Exception? ProbeFailure { get; set; }

    /// <summary>What <c>wsl --version</c> answers; null means no answer.</summary>
    public string? VersionText { get; set; } = "WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1\nWindows version: 10.0.22631.4460";

    public Exception? VersionFailure { get; set; }

    public int VersionAsks { get; private set; }

    private int _bootCounter;

    /// <summary>The lines of an isolation check that passes everything, as a fresh machine would print them.</summary>
    public static List<string> CleanPreflightLines() => [.. WslPreflight.RequiredChecks.Select(DefaultLine)];

    private static string DefaultLine(string check) => check switch
    {
        "fresh-boot" => "CHECK fresh-boot PASS 00000000-0000-4000-8000-000000000001",
        "kernel-info" => "CHECK kernel-info PASS 5.15.167.4-microsoft-standard-WSL2|#1 SMP Tue Nov  5 00:21:55 UTC 2024",
        _ => $"CHECK {check} PASS",
    };

    public Task<string?> VersionAsync(CancellationToken cancellationToken)
    {
        VersionAsks++;
        return VersionFailure is null ? Task.FromResult(VersionText) : Task.FromException<string?>(VersionFailure);
    }

    public Func<string, string, string, Stream?, Action<string>?, CancellationToken, Task<int>> OnExecute { get; set; } = (_, _, _, _, _, _) => Task.FromResult(0);

    public List<string> ExecutedScripts { get; } = [];

    /// <summary>The bytes of every archive that was imported, in order.</summary>
    public List<byte[]> Imported { get; } = [];

    /// <summary>The folder of each import, and whether it already held something when WSL was told to use it.</summary>
    public List<(string Directory, bool WasEmpty)> ImportFolders { get; } = [];

    public byte[] ExportBytes { get; set; } = Encoding.UTF8.GetBytes("exported");

    public int Shutdowns => Calls.Count(c => c == "shutdown");

    public Task<bool> IsUsableAsync(CancellationToken cancellationToken)
    {
        Calls.Add("usable?");
        return UsableFailure is null ? Task.FromResult(Usable) : Task.FromException<bool>(UsableFailure);
    }

    /// <summary>Runs when the list of machines is asked for, before the answer: what changes while a session is open (a test makes the end of the session fail with it).</summary>
    public Action? OnList { get; set; }

    public Task<IReadOnlyList<string>> ListDistributionsAsync(CancellationToken cancellationToken)
    {
        OnList?.Invoke();
        return ListFailure is null ? Task.FromResult<IReadOnlyList<string>>([.. Distributions]) : Task.FromException<IReadOnlyList<string>>(ListFailure);
    }

    /// <summary>Runs in the middle of an import, with the token of the import: a slow import (a test lets the drive fill up while it runs).</summary>
    public Func<CancellationToken, Task>? OnImport { get; set; }

    /// <summary>Runs first in every script that is executed, with the script and the token: a script that takes long (a test lets the drive fill up while it runs).</summary>
    public Func<string, CancellationToken, Task>? BeforeExecute { get; set; }

    public async Task ImportAsync(string name, string installDirectory, Stream archive, CancellationToken cancellationToken)
    {
        Calls.Add("import " + name);
        using var copy = new MemoryStream();
        await archive.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        Imported.Add(copy.ToArray());
        if (OnImport is not null)
        {
            await OnImport(cancellationToken).ConfigureAwait(false);
        }

        ImportFolders.Add((installDirectory, !Directory.Exists(installDirectory) || !Directory.EnumerateFileSystemEntries(installDirectory).Any()));
        if (ImportFailure is not null)
        {
            throw ImportFailure;
        }

        Directory.CreateDirectory(installDirectory);
        await File.WriteAllTextAsync(Path.Combine(installDirectory, "ext4.vhdx"), "disk", cancellationToken).ConfigureAwait(false);
        _folders[name] = installDirectory;
        Distributions.Add(name);
    }

    public async Task ExportAsync(string name, Stream destination, CancellationToken cancellationToken)
    {
        Calls.Add("export " + name);
        if (ExportFailure is not null)
        {
            throw ExportFailure;
        }

        await destination.WriteAsync(ExportBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>When set, a terminate never answers on its own (wsl.exe that hangs); only its token ends it.</summary>
    public bool TerminateHangs { get; set; }

    public async Task TerminateAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add("terminate " + name);
        if (TerminateHangs)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        if (TerminateFailure is not null)
        {
            throw TerminateFailure;
        }
    }

    public Task UnregisterAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add("unregister " + name);
        if (UnregisterFailure is not null)
        {
            return Task.FromException(UnregisterFailure);
        }

        Distributions.Remove(name);

        // WSL deletes the disk and then the folder if it is empty.
        if (_folders.Remove(name, out var folder) && !UnregisterLeavesFolder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async Task<int> ExecuteAsync(string distribution, string user, string script, Stream? standardInput, Action<string>? onLine, TimeSpan limit, CancellationToken cancellationToken)
    {
        Calls.Add($"exec {distribution} as {user}");
        ExecutedScripts.Add(script);
        ExecutedLimits.Add(limit);
        if (BeforeExecute is not null)
        {
            await BeforeExecute(script, cancellationToken).ConfigureAwait(false);
        }

        if (script.EndsWith(WslPreflight.Script, StringComparison.Ordinal))
        {
            PreflightScripts.Add(script);
            foreach (var line in PreflightLines)
            {
                var shown = NewBootIdEachTime && line.StartsWith("CHECK fresh-boot PASS ", StringComparison.Ordinal)
                    ? $"CHECK fresh-boot PASS 00000000-0000-4000-8000-{Interlocked.Increment(ref _bootCounter):D12}"
                    : line;
                onLine?.Invoke(shown);
            }

            return PreflightExit;
        }

        if (script.EndsWith(IsolationProbe.Script, StringComparison.Ordinal))
        {
            ProbeScripts.Add(script);
            ProbeUsers.Add(user);
            await ProbeHang.MaybeAsync(cancellationToken).ConfigureAwait(false);
            if (ProbeFailure is not null)
            {
                throw ProbeFailure;
            }

            foreach (var line in ProbeLines)
            {
                onLine?.Invoke(line);
            }

            return ProbeExit;
        }

        if (string.Equals(script, WslHardening.Script, StringComparison.Ordinal))
        {
            foreach (var line in HardenLines)
            {
                onLine?.Invoke(line);
            }

            return HardenExit;
        }

        return await OnExecute(distribution, user, script, standardInput, onLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes <c>wsl --shutdown</c> hang (the start of every session, so also the start of a base image build).</summary>
    public Hang ShutdownHang { get; } = new();

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Calls.Add("shutdown");
        await ShutdownHang.MaybeAsync(cancellationToken).ConfigureAwait(false);
        if (ShutdownFailure is not null)
        {
            throw ShutdownFailure;
        }
    }
}

internal sealed class FakeWslInstaller : IWslInstaller
{
    public CallLog Calls { get; } = new();

    /// <summary>What the installer answers: true = Windows needs a restart before WSL works.</summary>
    public bool Restart { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>Called when the install runs (a test makes WSL usable from here on).</summary>
    public Action? OnInstall { get; set; }

    public Task<bool> InstallWithoutDistributionAsync(CancellationToken cancellationToken)
    {
        Calls.Add("install");
        OnInstall?.Invoke();
        return Failure is null ? Task.FromResult(Restart) : Task.FromException<bool>(Failure);
    }

    /// <summary>The last line <c>wsl --update</c> prints.</summary>
    public string UpdateText { get; set; } = "WSL was updated to version 2.5.1.0.";

    public Exception? UpdateFailure { get; set; }

    /// <summary>When set, the update does not end on its own (an installer that hangs); only its token ends it.</summary>
    public Hang UpdateHang { get; } = new();

    /// <summary>Runs when the update starts, before it answers (a test looks at what the program had written down by then).</summary>
    public Action? OnUpdate { get; set; }

    public async Task<string> UpdateAsync(CancellationToken cancellationToken)
    {
        Calls.Add("update");
        OnUpdate?.Invoke();
        await UpdateHang.MaybeAsync(cancellationToken).ConfigureAwait(false);
        return UpdateFailure is null ? UpdateText : throw UpdateFailure;
    }
}

/// <summary>The dedicated account, scripted: nothing is wrong unless a test says so.</summary>
internal sealed class FakeWslAccount : IWslAccount
{
    public CallLog Calls { get; } = new();

    public string UserName => LocalCiDefaults.WslAccountName;

    /// <summary>The account's security identifier; null stands for an account that does not exist.</summary>
    public string? Sid { get; set; } = "S-1-5-21-1111-2222-3333-1099";

    public string? AccountSid() => Sid;

    public List<string> Problems { get; } = [];

    public Exception? EnsureFailure { get; set; }

    public Exception? RemoveFailure { get; set; }

    /// <summary>True while the account is locked out; <see cref="ClearLockout"/> clears it.</summary>
    public bool LockedOut { get; set; }

    /// <summary>When set, the lock-out cannot be cleared (Windows would not change the account).</summary>
    public bool LockoutStays { get; set; }

    public int LockoutsCleared { get; private set; }

    /// <summary>Thrown by <see cref="EndProgramsAsync"/>.</summary>
    public Exception? EndProgramsFailure { get; set; }

    public int ProgramsEnded { get; private set; }

    /// <summary>Runs when the account is made (a test cancels the owner's run from here).</summary>
    public Action? OnEnsure { get; set; }

    public int Checks { get; private set; }

    public IReadOnlyList<string> Check()
    {
        Checks++;
        return LockedOut ? [.. Problems, "the account dbd-ci-wsl is locked out"] : [.. Problems];
    }

    public bool ClearLockout()
    {
        Calls.Add("clear-lockout");
        if (!LockedOut || LockoutStays)
        {
            return false;
        }

        LockedOut = false;
        LockoutsCleared++;
        return true;
    }

    public Task EndProgramsAsync(CancellationToken cancellationToken)
    {
        Calls.Add("end-programs");
        ProgramsEnded++;
        return EndProgramsFailure is null ? Task.CompletedTask : Task.FromException(EndProgramsFailure);
    }

    public Task EnsureAsync(CancellationToken cancellationToken)
    {
        Calls.Add("ensure");
        OnEnsure?.Invoke();
        return EnsureFailure is null ? Task.CompletedTask : Task.FromException(EnsureFailure);
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        Calls.Add("remove");
        return RemoveFailure is null ? Task.CompletedTask : Task.FromException(RemoveFailure);
    }
}

/// <summary>The account's <c>.wslconfig</c>, in memory, as bytes (what is checked is what WSL reads); <see cref="Text"/> is the same bytes as UTF-8 text without a byte order mark, for tests that write words.</summary>
internal sealed class FakeWslSettingsFile : IWslSettingsFile
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public CallLog Calls { get; } = new();

    public string Location => @"C:\Users\dbd-ci-wsl\.wslconfig";

    /// <summary>The file's bytes; null when there is no file.</summary>
    public byte[]? Bytes { get; set; }

    public string? Text
    {
        get => Bytes is null ? null : s_utf8.GetString(Bytes);
        set => Bytes = value is null ? null : s_utf8.GetBytes(value);
    }

    /// <summary>The text of every write (decoded as UTF-8), in order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>The bytes of every write, in order.</summary>
    public List<byte[]> WrittenBytes { get; } = [];

    /// <summary>Thrown by <see cref="Read"/> (a link, a file that is too large).</summary>
    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    /// <summary>When set, the bytes the held file has, whatever was written (the file changed between the write and the hold).</summary>
    public byte[]? HeldBytesOverride { get; set; }

    /// <summary>When set, what the held file says as text (UTF-8, no byte order mark).</summary>
    public string? HeldTextOverride
    {
        get => HeldBytesOverride is null ? null : s_utf8.GetString(HeldBytesOverride);
        set => HeldBytesOverride = value is null ? null : s_utf8.GetBytes(value);
    }

    public int Holds { get; private set; }

    public int Releases { get; private set; }

    public byte[]? Read() => ReadFailure is null ? Bytes : throw ReadFailure;

    public void Write(byte[] content)
    {
        Calls.Add("write");
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        WrittenBytes.Add(content);
        Writes.Add(s_utf8.GetString(content));
        Bytes = content;
    }

    public IDisposable OpenHeld(out byte[] content)
    {
        Calls.Add("hold");
        content = HeldBytesOverride ?? Bytes ?? throw new IOException(Location + " is not there.");
        Holds++;
        return new Release(this);
    }

    private sealed class Release : IDisposable
    {
        private readonly FakeWslSettingsFile _owner;
        private bool _released;

        public Release(FakeWslSettingsFile owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                _owner.Releases++;
                _owner.Calls.Add("release");
            }
        }
    }
}

/// <summary>Runs programs "as the account" by calling a function; records every command.</summary>
internal sealed class FakeAccountProcess : IAccountProcess
{
    public List<AccountCommand> Commands { get; } = [];

    /// <summary>What the program does: feed output to the command's sinks, read its input; returns the exit code. The default reads the input to its end and exits 0.</summary>
    public Func<AccountCommand, CancellationToken, Task<int>> OnRun { get; set; } = async (command, token) =>
    {
        if (command.StandardInput is not null)
        {
            await command.StandardInput.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
        }

        return 0;
    };

    public Task<int> RunAsync(AccountCommand command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return OnRun(command, cancellationToken);
    }
}

/// <summary>The session of the account's Linux machine, built from fakes.</summary>
/// <summary>The boot id, kernel and WSL version the program remembers, in memory.</summary>
internal sealed class FakeMachineRecord : IMachineRecord
{
    public string? LastBootId { get; set; }

    public string? Version { get; private set; }

    public string? KernelRelease { get; private set; }

    public DateTimeOffset? KernelBuilt { get; private set; }

    public List<string> BootIds { get; } = [];

    public void RecordVersion(string text) => Version = text;

    public void RecordBoot(string bootId, string? kernelRelease, DateTimeOffset? kernelBuilt)
    {
        LastBootId = bootId;
        BootIds.Add(bootId);
        KernelRelease = kernelRelease;
        KernelBuilt = kernelBuilt;
    }
}

/// <summary>The machine-wide session lock, in memory: a test makes another program hold it, and sees when this one takes and lets go of it.</summary>
internal sealed class FakeSessionLock : ISessionLock
{
    /// <summary>True while another program holds the lock: a session cannot begin.</summary>
    public bool HeldByAnotherProgram { get; set; }

    /// <summary>How many times a session took the lock.</summary>
    public int Taken { get; private set; }

    /// <summary>How many of those are still held.</summary>
    public int Held { get; private set; }

    public CallLog Calls { get; } = new();

    public IDisposable? TryAcquire()
    {
        if (HeldByAnotherProgram)
        {
            Calls.Add("lock refused");
            return null;
        }

        Taken++;
        Held++;
        Calls.Add("lock taken");
        return new Release(this);
    }

    private sealed class Release : IDisposable
    {
        private readonly FakeSessionLock _owner;
        private bool _released;

        public Release(FakeSessionLock owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                _owner.Held--;
                _owner.Calls.Add("lock released");
            }
        }
    }
}

internal static class Sandboxes
{
    /// <summary>16 GB and 8 logical processors: the limits come out as 8 GB and 4.</summary>
    public static WslHostFacts Facts { get; } = new(16L * 1024 * 1024 * 1024, 8);

    public static WslSandbox New(FakeWsl wsl, FakeWslAccount account, FakeWslSettingsFile settings, MemoryCiLog log, Redactor redactor, TimeProvider time, IMachineRecord? record = null, ISessionLock? sessionLock = null, ResidueRecorder? residue = null) =>
        new(wsl, account, settings, Facts, record ?? new FakeMachineRecord(), log, redactor, time, sessionLock ?? new FakeSessionLock(), residue);
}

/// <summary>Entries of the leftovers list as the tests need them (local CI round 4, red-team F7: every entry is about one exact subject).</summary>
internal static class Left
{
    public static ResidueItem Machine(string name) => new() { Kind = ResidueKinds.LinuxMachine, Subject = name, Text = $"the Linux machine {name} could not be removed" };

    public static ResidueItem Folder(string path) => new() { Kind = ResidueKinds.Folder, Subject = path, Text = $"the folder {path} is not empty (delete it by hand when no Linux machine uses it)" };

    public static ResidueItem Marker(string name) => new() { Kind = ResidueKinds.MarkerFile, Subject = name, Text = $"the marker file {name} of an isolation probe is in C:\\Users\\Public (delete it by hand)" };

    /// <summary>Something left that a test only needs to see named: the words are both the subject and the text.</summary>
    public static ResidueItem Words(string words) => new() { Kind = ResidueKinds.Check, Subject = words, Text = words };
}

/// <summary>The marker file of the isolation probe on the Windows side, scripted: the file is not there unless a test says so.</summary>
internal sealed class FakeProbeMarker : IProbeMarker
{
    private int _made;

    /// <summary>What the Windows side finds after the probe.</summary>
    public ProbeMarkerFinding Finding { get; set; } = ProbeMarkerFinding.NotThere;

    public Exception? LookFailure { get; set; }

    public List<string> Names { get; } = [];

    public List<string> Looked { get; } = [];

    public string NewName()
    {
        var name = $"dbd-ci-probe-{Interlocked.Increment(ref _made):x12}.txt";
        Names.Add(name);
        return name;
    }

    public ProbeMarkerFinding FindAndRemove(string name)
    {
        Looked.Add(name);
        return LookFailure is null ? Finding : throw LookFailure;
    }
}

/// <summary>The places the account may write to, scripted.</summary>
internal sealed class FakeWritableLocations : IWritableLocations
{
    public List<string> Places { get; } = [];

    /// <summary>The places the audit could not check, each named with the reason; with any in it the audit is incomplete.</summary>
    public List<string> Gaps { get; } = [];

    /// <summary>How many places the audit says it read the permissions of.</summary>
    public int PlacesLooked { get; set; } = 12;

    public Exception? Failure { get; set; }

    public int Asked { get; private set; }

    public Task<WritableAudit> FindAsync(CancellationToken cancellationToken)
    {
        Asked++;
        return Failure is null ? Task.FromResult(new WritableAudit([.. Places], PlacesLooked, Gaps.Count, [.. Gaps])) : Task.FromException<WritableAudit>(Failure);
    }
}

internal sealed class FakeScheduler : IScheduler
{
    public CallLog Calls { get; } = new();

    public bool TaskExists { get; set; }

    /// <summary>Whether a loop is running. Starting the task starts it and ending the task ends it, as the real task does (a test can break either).</summary>
    public bool Running { get; set; }

    /// <summary>When set, ending the task does not stop the loop (a loop that cannot be ended).</summary>
    public bool StopLeavesRunning { get; set; }

    /// <summary>When false, a started task does not show as running (a loop that needs a moment to come up).</summary>
    public bool StartShowsAsRunning { get; set; } = true;

    public Exception? InstallFailure { get; set; }

    public Exception? StartFailure { get; set; }

    public Exception? StopFailure { get; set; }

    public Exception? RemoveFailure { get; set; }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Task.FromResult(TaskExists);

    public bool LoopIsRunning() => Running;

    public Task InstallAsync(string programPath, CancellationToken cancellationToken)
    {
        Calls.Add("install " + programPath);
        if (InstallFailure is not null)
        {
            return Task.FromException(InstallFailure);
        }

        TaskExists = true;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Calls.Add("start");
        if (StartFailure is not null)
        {
            return Task.FromException(StartFailure);
        }

        Running = StartShowsAsRunning;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Calls.Add("stop");
        if (StopFailure is not null)
        {
            return Task.FromException(StopFailure);
        }

        if (!StopLeavesRunning)
        {
            Running = false;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        Calls.Add("remove");
        if (RemoveFailure is not null)
        {
            return Task.FromException(RemoveFailure);
        }

        TaskExists = false;
        Running = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeSecretStore : ISecretStore
{
    private SecretText? _secret;

    public CallLog Calls { get; } = new();

    public bool Exists() => _secret is not null;

    public SecretText? Load() => _secret;

    public void Save(SecretText secret)
    {
        Calls.Add("save");
        _secret = secret;
    }

    public void Delete()
    {
        Calls.Add("delete");
        _secret = null;
    }
}

internal sealed class FakeProcesses : IProcessList
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, the question itself fails (the process list is unavailable).</summary>
    public Exception? Throw { get; set; }

    public bool IsAnyRunning(IReadOnlyCollection<string> processNames) => Throw is null ? processNames.Any(Running.Contains) : throw Throw;
}

internal sealed class FakeNotifier : INotifier
{
    public List<string> Messages { get; } = [];

    public void Notify(string message) => Messages.Add(message);
}

internal sealed class FakeConsole : IConsoleUi
{
    private readonly Queue<string> _secrets;
    private readonly Queue<bool> _answers;
    private readonly Queue<string> _typed;

    /// <param name="secrets">What is typed at the secret prompts.</param>
    /// <param name="answers">The answers to yes/no questions.</param>
    /// <param name="typed">The lines typed at the prompts that read a whole line (the acceptance of the isolation probe); when they run out the owner types "yes".</param>
    public FakeConsole(IEnumerable<string>? secrets = null, IEnumerable<bool>? answers = null, IEnumerable<string>? typed = null)
    {
        _secrets = new Queue<string>(secrets ?? []);
        _answers = new Queue<bool>(answers ?? []);
        _typed = new Queue<string>(typed ?? []);
    }

    public List<string> Lines { get; } = [];

    public List<string> Questions { get; } = [];

    public void WriteLine(string text) => Lines.Add(text);

    /// <summary>Runs when a whole line is read, before the answer is given: what another window of the program does while the owner is typing.</summary>
    public Action? OnReadLine { get; set; }

    public string? ReadLine()
    {
        OnReadLine?.Invoke();
        return _typed.Count > 0 ? _typed.Dequeue() : "yes";
    }

    public string? ReadSecret(string prompt) => _secrets.Count > 0 ? _secrets.Dequeue() : null;

    public bool Confirm(string question)
    {
        Questions.Add(question);
        return _answers.Count > 0 && _answers.Dequeue();
    }

    public string AllText => string.Join("\n", Lines);
}

internal sealed class FakeMachine : IMachine
{
    public int WindowsBuild { get; set; } = 22631;

    public long FreeBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    public bool? Virtualization { get; set; } = true;

    public bool Administrator { get; set; } = true;

    public long FreeDiskBytes(string path) => FreeBytes;

    public bool? VirtualizationEnabled() => Virtualization;

    public bool IsAdministrator() => Administrator;

    public int RelaunchElevated(string program, string arguments) => 0;
}

internal sealed class FakeDownloads : IDownloads
{
    public Dictionary<string, string> Texts { get; } = [];

    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Requested { get; } = [];

    public List<long> Limits { get; } = [];

    public Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
    {
        Requested.Add(url.ToString());
        return Texts.TryGetValue(url.ToString(), out var text) ? Task.FromResult(text) : throw new HttpRequestException("not found: " + url);
    }

    public async Task DownloadToFileAsync(Uri url, string destinationPath, long maximumBytes, CancellationToken cancellationToken)
    {
        Requested.Add(url.ToString());
        Limits.Add(maximumBytes);
        if (!Files.TryGetValue(url.ToString(), out var bytes))
        {
            throw new HttpRequestException("not found: " + url);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class FakeJobHost : IJobHost
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? NotReady { get; set; }

    public int ExitCode { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>Makes the failure from the launch it is given (a failure that quotes the runner's secret configuration).</summary>
    public Func<RunnerLaunch, Exception>? FailureFactory { get; set; }

    public bool BlockUntilReleased { get; set; }

    /// <summary>When true a cancelled run keeps going until <see cref="Release"/> (an environment that takes a while to end).</summary>
    public bool IgnoreCancellationUntilReleased { get; set; }

    public List<RunnerLaunch> Launches { get; } = [];

    public int Cleanups { get; private set; }

    public bool WasCancelled { get; private set; }

    public string? NotReadyReason() => NotReady;

    /// <summary>Completes when the service has started a runner on this host (a safety limit stops a test that would wait for ever).</summary>
    public Task WaitStartedAsync() => _started.Task.WaitAsync(TestWait.Safety);

    public void Release() => _release.TrySetResult();

    public async Task<int> RunAsync(RunnerLaunch launch, CancellationToken cancellationToken)
    {
        Launches.Add(launch);
        _started.TrySetResult();
        if (FailureFactory is not null)
        {
            throw FailureFactory(launch);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        if (BlockUntilReleased)
        {
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                if (IgnoreCancellationUntilReleased)
                {
                    await _release.Task.ConfigureAwait(false);
                }

                throw;
            }
        }

        return ExitCode;
    }

    public CallLog Calls { get; } = new();

    public Task CleanupLeftoversAsync(CancellationToken cancellationToken)
    {
        Cleanups++;
        Calls.Add("cleanup leftovers");
        return Task.CompletedTask;
    }
}

internal sealed class FakeMaintenance : IMaintenance
{
    public string? Package { get; set; } = "linux.tar.gz";

    public List<bool> Rounds { get; } = [];

    public Task RunDueAsync(bool linuxIdle, CancellationToken cancellationToken)
    {
        Rounds.Add(linuxIdle);
        return Task.CompletedTask;
    }

    public string? LatestPackage() => Package;
}

/// <summary>A delay the test releases by hand, so loops advance without real waiting.</summary>
internal sealed class ManualDelay
{
    private readonly object _gate = new();
    private readonly Queue<TaskCompletionSource> _pending = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan LastRequested { get; private set; }

    public Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        LastRequested = span;
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        lock (_gate)
        {
            _pending.Enqueue(source);
            _arrived.TrySetResult();
        }

        return source.Task;
    }

    /// <summary>Waits until a delay is waiting to be released: the loop that asked for it has finished processing the previous one.</summary>
    public async Task WaitForRequestAsync()
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (_pending.Any(p => !p.Task.IsCompleted))
                {
                    return;
                }
            }

            if (limit.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("No delay was asked for.");
            }

            await Task.Yield();
        }
    }

    /// <summary>Waits until a delay has been asked for, then completes it (a cancelled one is skipped).</summary>
    public async Task ReleaseNextAsync()
    {
        while (true)
        {
            Task arrived;
            TaskCompletionSource? next = null;
            lock (_gate)
            {
                while (_pending.Count > 0)
                {
                    var candidate = _pending.Dequeue();
                    if (!candidate.Task.IsCompleted)
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                arrived = _arrived.Task;
            }

            if (next is not null)
            {
                next.TrySetResult();
                return;
            }

            await arrived.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }
}

/// <summary>An HttpMessageHandler that answers from a list of canned replies and records the requests (no real network in tests).</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        _reply = reply;
    }

    public List<(HttpMethod Method, Uri Url, string? Body, string? Authorization)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add((request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString()));
        return _reply(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
}

internal sealed class FakeGitHubApi : IGitHubApi
{
    public CallLog Calls { get; } = new();

    public RepoInfo Repo { get; set; } = new("main", new DateTimeOffset(2027, 1, 27, 10, 0, 0, TimeSpan.Zero), IsPrivate: true);

    public Exception? RepoFailure { get; set; }

    /// <summary>Gives the token the program's GitHub client would use at the moment of a call (a test wires it to the token source of the program under test).</summary>
    public Func<string?>? TokenInUse { get; set; }

    /// <summary>The token in use at each repository call, in order (what the check of a pasted token really used).</summary>
    public List<string?> TokensSeen { get; } = [];

    public RunnerRelease? Release { get; set; }

    public List<(string Name, IReadOnlyList<string> Labels)> JitRequests { get; } = [];

    public Exception? JitFailure { get; set; }

    public List<RunnerInfo> Runners { get; } = [];

    public Dictionary<string, string> Variables { get; } = [];

    public List<string> DeletedVariables { get; } = [];

    public List<long> DeletedRunners { get; } = [];

    public long? DispatchedRun { get; set; } = 77;

    public List<WorkflowRunInfo> RunStates { get; } = [];

    public List<WorkflowJobInfo> Jobs { get; } = [];

    public List<long> CancelledRuns { get; } = [];

    public Action? OnRunPolled { get; set; }

    private int _nextRunner;

    /// <summary>How many calls to the repository answer before <see cref="RepoFailure"/> starts (0: it fails from the first call).</summary>
    public int RepoCallsBeforeFailure { get; set; }

    /// <summary>When set, the failure stops after this many failed calls (a token that is refused once, then a new one is accepted). 0: it never stops.</summary>
    public int RepoFailureEndsAfter { get; set; }

    private int _repoCalls;

    /// <summary>Makes the repository call (the token check) hang.</summary>
    public Hang RepoHang { get; } = new();

    /// <summary>Makes the call for the newest runner release (the daily download) hang.</summary>
    public Hang ReleaseHang { get; } = new();

    public async Task<RepoInfo> GetRepositoryAsync(CancellationToken cancellationToken)
    {
        await RepoHang.MaybeAsync(cancellationToken).ConfigureAwait(false);
        return Repository();
    }

    private RepoInfo Repository()
    {
        Calls.Add("repo");
        TokensSeen.Add(TokenInUse?.Invoke());
        _repoCalls++;
        var failing = RepoFailure is not null
                      && _repoCalls > RepoCallsBeforeFailure
                      && (RepoFailureEndsAfter == 0 || _repoCalls <= RepoCallsBeforeFailure + RepoFailureEndsAfter);
        return failing ? throw RepoFailure! : Repo;
    }

    public async Task<RunnerRelease> GetLatestRunnerReleaseAsync(CancellationToken cancellationToken)
    {
        Calls.Add("release");
        await ReleaseHang.MaybeAsync(cancellationToken).ConfigureAwait(false);
        return Release ?? throw new InvalidOperationException("no release configured");
    }

    public Task<JitRunner> GenerateJitConfigAsync(string name, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        lock (JitRequests)
        {
            Calls.Add("jit " + name);
            JitRequests.Add((name, labels));
        }

        if (JitFailure is not null)
        {
            return Task.FromException<JitRunner>(JitFailure);
        }

        return Task.FromResult(new JitRunner(Interlocked.Increment(ref _nextRunner), name, new SecretText("JITCONFIG-" + name)));
    }

    public Exception? RunnersFailure { get; set; }

    public Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync(CancellationToken cancellationToken) =>
        RunnersFailure is null ? Task.FromResult<IReadOnlyList<RunnerInfo>>([.. Runners]) : Task.FromException<IReadOnlyList<RunnerInfo>>(RunnersFailure);

    /// <summary>How often each variable was read (a test counts the reads to see how many times something looked).</summary>
    public Dictionary<string, int> VariableReads { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetVariableAsync(string name, CancellationToken cancellationToken)
    {
        VariableReads[name] = VariableReads.GetValueOrDefault(name) + 1;
        return Task.FromResult(Variables.TryGetValue(name, out var value) ? value : null);
    }

    public Task DeleteRunnerAsync(long runnerId, CancellationToken cancellationToken)
    {
        Calls.Add("delete-runner " + runnerId);
        DeletedRunners.Add(runnerId);
        return Task.CompletedTask;
    }

    /// <summary>Runs after a variable was set: what happens right after the request (a test lets the owner press Ctrl+C there).</summary>
    public Action<string>? AfterVariableSet { get; set; }

    public Exception? SetVariableFailure { get; set; }

    public Task SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        Calls.Add("set " + name);
        if (SetVariableFailure is not null)
        {
            return Task.FromException(SetVariableFailure);
        }

        Variables[name] = value;
        AfterVariableSet?.Invoke(name);
        return Task.CompletedTask;
    }

    public Exception? DeleteVariableFailure { get; set; }

    public Task DeleteVariableAsync(string name, CancellationToken cancellationToken)
    {
        Calls.Add("delete-variable " + name);
        DeletedVariables.Add(name);
        if (DeleteVariableFailure is not null)
        {
            return Task.FromException(DeleteVariableFailure);
        }

        Variables.Remove(name);
        return Task.CompletedTask;
    }

    /// <summary>Thrown by <see cref="DispatchWorkflowAsync"/> (GitHub answers 404 for a workflow that is not on the default branch yet).</summary>
    public Exception? DispatchFailure { get; set; }

    public Task<long?> DispatchWorkflowAsync(string workflowFile, string branch, CancellationToken cancellationToken)
    {
        Calls.Add($"dispatch {workflowFile} on {branch}");
        return DispatchFailure is null ? Task.FromResult(DispatchedRun) : Task.FromException<long?>(DispatchFailure);
    }

    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(string workflowFile, string? eventName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowRunInfo>>([.. RunStates]);

    public Task<WorkflowRunInfo> GetWorkflowRunAsync(long runId, CancellationToken cancellationToken)
    {
        OnRunPolled?.Invoke();
        var state = RunStates.Count > 1 ? RunStates[0] : RunStates[^1];
        if (RunStates.Count > 1)
        {
            RunStates.RemoveAt(0);
        }

        return Task.FromResult(state);
    }

    public Task CancelWorkflowRunAsync(long runId, CancellationToken cancellationToken)
    {
        CancelledRuns.Add(runId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowJobInfo>> ListWorkflowJobsAsync(long runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowJobInfo>>([.. Jobs]);
}

/// <summary>The data folder checks, scripted: nothing is wrong unless a test says so.</summary>
internal sealed class FakeProtectedStorage : IProtectedStorage
{
    public List<string> DataFolderProblems { get; } = [];

    /// <summary>Problems for paths that start with a key (for example a planted version folder).</summary>
    public Dictionary<string, string> PathProblems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CallLog Calls { get; } = new();

    public IReadOnlyList<string> CheckDataFolder() => [.. DataFolderProblems];

    public IReadOnlyList<string> CheckInsideDataFolder(string path) =>
        [.. PathProblems.Where(p => path.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value)];

    public void CreateOrRepairDataFolder() => Calls.Add("create-data-folder");
}

internal sealed class FakeInstaller : IProgramInstaller
{
    public string InstalledPath { get; set; } = @"C:\Program Files\DbdLocalCi\DbdLocalCi.exe";

    public bool IsInstalled { get; set; }

    public List<string> LocationProblems { get; } = [];

    /// <summary>The check at which the location problems start to be reported (1 = from the first check on).</summary>
    public int ProblemsFromCheck { get; set; } = 1;

    public InstallOutcome Outcome { get; set; } = InstallOutcome.Installed;

    public CallLog Calls { get; } = new();

    private int _checks;

    public bool IsInstalledCopy(string runningPath) => string.Equals(runningPath, InstalledPath, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> CheckInstalledLocation() => ++_checks >= ProblemsFromCheck ? [.. LocationProblems] : [];

    public Task<InstallOutcome> InstallAsync(string runningPath, CancellationToken cancellationToken)
    {
        Calls.Add("install from " + runningPath);
        IsInstalled = true;
        return Task.FromResult(Outcome);
    }

    public string Remove()
    {
        Calls.Add("remove");
        IsInstalled = false;
        return "The installed program was removed.";
    }
}

internal sealed class FakeDisk : IDiskSpace
{
    public long Free { get; set; } = 500L * 1024 * 1024 * 1024;

    public long FreeBytes(string path) => Free;
}

/// <summary>An isolation probe the owner accepted, written into a state file, and the gate over it: what a job host needs before it will start a runner (round 4, F5).</summary>
internal static class AcceptedProbe
{
    public const string Recipe = "recipe-1";
    public const string Version = "WSL version: 2.4.8.0\nKernel version: 5.15.167.4-1\nWindows version: 10.0.22631.4460";

    /// <summary>Writes the accepted result of a probe in which every attempt was stopped, for <see cref="Recipe"/> and <see cref="Version"/>, and the WSL version the machine reported at its last use.</summary>
    public static StateStore In(StateStore state, bool accepted = true, string recipe = Recipe, string? probeVersion = Version, string? installedVersion = Version)
    {
        state.Update(s =>
        {
            s.WslVersionText = installedVersion;
            s.Probe = new ProbeRecord
            {
                Id = "accepted-probe",
                At = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
                Outcome = "AllBlocked",
                Attempts = [.. IsolationProbe.AttemptIds.Select(id => new ProbeAttemptRecord { Id = id, Verdict = nameof(ProbeVerdict.Blocked), Evidence = "closed" })],
                Recipe = recipe,
                WslVersion = probeVersion,
                Accepted = accepted,
                AcceptedAt = accepted ? new DateTimeOffset(2026, 10, 28, 12, 1, 0, TimeSpan.Zero) : null,
            };
        });
        return state;
    }

    public static ProbeGate Gate(StateStore state, string recipe = Recipe) => new(state, () => recipe);
}

/// <summary>Security descriptions of paths, scripted; a path nobody described does not exist.</summary>
internal sealed class FakeProbe : IAuditSecurityProbe
{
    private readonly Dictionary<string, NodeSecurity> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NodeSecurity> _seenWithBackupRights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _stillRefused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path that was read with the backup right, in order.</summary>
    public List<string> BackupReads { get; } = [];

    public NodeSecurity Describe(string path)
    {
        if (_refused.TryGetValue(path, out var failure))
        {
            throw failure;
        }

        return _nodes.TryGetValue(path, out var node) ? node : NodeSecurity.Missing(path);
    }

    public NodeSecurity DescribeWithBackupRights(string path)
    {
        BackupReads.Add(path);
        if (_stillRefused.TryGetValue(path, out var failure))
        {
            throw failure;
        }

        return _seenWithBackupRights.TryGetValue(path, out var node) ? node : Describe(path);
    }

    public FakeProbe Set(NodeSecurity node)
    {
        _nodes[node.Path] = node;
        return this;
    }

    /// <summary>The ordinary read of this path fails with this (Windows refuses even an administrator the permissions of some folders).</summary>
    public FakeProbe Refuse(string path, Exception failure)
    {
        _refused[path] = failure;
        return this;
    }

    /// <summary>With the backup right the refused path can be read, and this is what it shows.</summary>
    public FakeProbe ShowWithBackupRights(NodeSecurity node)
    {
        _seenWithBackupRights[node.Path] = node;
        return this;
    }

    /// <summary>Even with the backup right the read of this path fails with this.</summary>
    public FakeProbe RefuseEvenWithBackupRights(string path, Exception failure)
    {
        _stillRefused[path] = failure;
        return this;
    }

    public NodeSecurity? Get(string path) => _nodes.GetValueOrDefault(path);
}

/// <summary>Builders for the security descriptions the access rules read.</summary>
internal static class Nodes
{
    private static readonly AceRecord[] s_adminOnly =
    [
        new(WellKnownSids.LocalSystem, true, FileRights.FullControl),
        new(WellKnownSids.Administrators, true, FileRights.FullControl),
    ];

    public static NodeSecurity ClosedFolder(string path, string? owner = null, bool protectedAcl = true, IEnumerable<AceRecord>? extra = null) =>
        new(path, true, true, false, owner ?? WellKnownSids.Administrators, protectedAcl, [.. s_adminOnly, .. extra ?? []]);

    public static NodeSecurity ClosedFile(string path, string? owner = null, IEnumerable<AceRecord>? extra = null) =>
        new(path, true, false, false, owner ?? WellKnownSids.Administrators, false, [.. s_adminOnly, .. extra ?? []]);

    /// <summary>A folder like ProgramData or Program Files above the program: trusted owner, others may read and add but not remove.</summary>
    public static NodeSecurity Ancestor(string path, string? owner = null, IEnumerable<AceRecord>? extra = null) =>
        new(
            path,
            true,
            true,
            false,
            owner ?? WellKnownSids.LocalSystem,
            false,
            [
                .. s_adminOnly,
                new AceRecord(WellKnownSids.Users, true, FileRights.ReadAndExecute),
                new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.AppendData),
                new AceRecord(WellKnownSids.CreatorOwner, true, FileRights.FullControl, InheritOnly: true),
                .. extra ?? [],
            ]);

    public static NodeSecurity ProgramFolder(string path, string? owner = null, IEnumerable<AceRecord>? extra = null) =>
        new(
            path,
            true,
            true,
            false,
            owner ?? WellKnownSids.Administrators,
            true,
            [.. s_adminOnly, new AceRecord(WellKnownSids.Users, true, FileRights.ReadAndExecute), .. extra ?? []]);

    public static NodeSecurity ProgramFile(string path, string? owner = null, IEnumerable<AceRecord>? extra = null) =>
        new(
            path,
            true,
            false,
            false,
            owner ?? WellKnownSids.Administrators,
            false,
            [.. s_adminOnly, new AceRecord(WellKnownSids.Users, true, FileRights.ReadAndExecute), .. extra ?? []]);

    public static NodeSecurity Link(string path, bool directory = true) => new(path, true, directory, true, null, false, []);
}
