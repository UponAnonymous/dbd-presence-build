using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>
/// wsl.exe as the dedicated account sees it (local CI round 2, red-team F2, L1): every command is started through <see cref="IAccountProcess"/>, which runs it as that standard user inside a Job Object,
/// never under this program's administrator token. Names are checked before they reach a command line, every command has a time limit of its own (also the steps that run a script in a Linux machine, round 3 L2), a listing that cannot be read throws (it is never an
/// empty list), and a failed terminate or unregister only counts as done when the list shows the Linux machine gone. PENDING-ENV: no wsl.exe has run as another user yet; the first-run checks in
/// docs/owner/free-ci.md measure it.
/// </summary>
public sealed class WslClient : IWsl
{
    private const int KeptLines = 200;
    private const int MaximumListBytes = 64 * 1024;

    private static readonly TimeSpan s_quickLimit = WslLimits.Quick;
    private static readonly TimeSpan s_transferLimit = WslLimits.Transfer;

    private readonly IAccountProcess _process;
    private readonly string _wsl;
    private readonly TimeProvider _time;
    private readonly ICiLog _log;

    /// <param name="process">Runs a program as the dedicated account.</param>
    /// <param name="wslPath">The full path of wsl.exe (from the Windows system folder).</param>
    /// <param name="time">The clock for the time limits.</param>
    /// <param name="log">The log.</param>
    public WslClient(IAccountProcess process, string wslPath, TimeProvider time, ICiLog log)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(wslPath);
        _process = process;
        _wsl = wslPath;
        _time = time;
        _log = log;
    }

    public async Task<bool> IsUsableAsync(CancellationToken cancellationToken)
    {
        var version = await WithLimitAsync(s_quickLimit, "asking for its version", ct => RunTextAsync(["--version"], null, ct), cancellationToken).ConfigureAwait(false);
        if (version.Exit != 0)
        {
            _log.Info($"'wsl --version' as the account ended with exit code {version.Exit}: {WslOutput.LastLine(version.Text)}");
            return false;
        }

        _log.Info("WSL as the account: " + (WslOutput.StripNuls(version.Text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no version line"));
        var list = await WithLimitAsync(s_quickLimit, "listing the Linux machines", RunListAsync, cancellationToken).ConfigureAwait(false);
        if (list.Exit != 0)
        {
            _log.Info($"'wsl --list' as the account ended with exit code {list.Exit}: {WslOutput.LastLine(list.Errors.Length > 0 ? list.Errors : list.Text)}");
            return false;
        }

        return true;
    }

    public async Task<string?> VersionAsync(CancellationToken cancellationToken)
    {
        var version = await WithLimitAsync(s_quickLimit, "asking for its version", ct => RunTextAsync(["--version"], null, ct), cancellationToken).ConfigureAwait(false);
        if (version.Exit != 0)
        {
            _log.Info($"'wsl --version' as the account ended with exit code {version.Exit}: {WslOutput.LastLine(version.Text)}");
            return null;
        }

        return WslOutput.VersionText(version.Text);
    }

    public async Task<IReadOnlyList<string>> ListDistributionsAsync(CancellationToken cancellationToken)
    {
        var list = await WithLimitAsync(s_quickLimit, "listing the Linux machines", RunListAsync, cancellationToken).ConfigureAwait(false);
        if (list.Exit != 0)
        {
            throw new InvalidOperationException($"Asking WSL for the Linux machines failed (exit code {list.Exit}): {WslOutput.LastLine(list.Errors.Length > 0 ? list.Errors : list.Text)}");
        }

        return WslOutput.ParseDistributions(list.Text);
    }

    public async Task ImportAsync(string name, string installDirectory, Stream archive, CancellationToken cancellationToken)
    {
        WslNames.RequireDistribution(name);
        WslNames.RequireInstallDirectory(installDirectory);
        ArgumentNullException.ThrowIfNull(archive);
        var result = await WithLimitAsync(s_transferLimit, $"importing {name}", ct => RunTextAsync(["--import", name, installDirectory, "-", "--version", "2"], archive, ct), cancellationToken).ConfigureAwait(false);
        if (result.Exit != 0)
        {
            throw new InvalidOperationException($"Importing {name} failed (exit code {result.Exit}): {WslOutput.LastLine(result.Text)}");
        }
    }

    public async Task ExportAsync(string name, Stream destination, CancellationToken cancellationToken)
    {
        WslNames.RequireDistribution(name);
        ArgumentNullException.ThrowIfNull(destination);
        var messages = new LineCollector(KeptLines);
        var exit = await WithLimitAsync(
            s_transferLimit,
            $"exporting {name}",
            ct => _process.RunAsync(new AccountCommand(_wsl, ["--export", name, "-"], Encoding.Unicode, null, destination, messages.Add), ct),
            cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException($"Exporting {name} failed (exit code {exit}): {WslOutput.LastLine(messages.Text)}");
        }
    }

    public async Task TerminateAsync(string name, CancellationToken cancellationToken)
    {
        WslNames.RequireDistribution(name);
        var result = await WithLimitAsync(s_quickLimit, $"stopping {name}", ct => RunTextAsync(["--terminate", name], null, ct), cancellationToken).ConfigureAwait(false);
        if (result.Exit != 0 && await ExistsAsync(name, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Stopping {name} failed (exit code {result.Exit}): {WslOutput.LastLine(result.Text)}");
        }
    }

    public async Task UnregisterAsync(string name, CancellationToken cancellationToken)
    {
        WslNames.RequireDistribution(name);
        var result = await WithLimitAsync(s_quickLimit, $"removing {name}", ct => RunTextAsync(["--unregister", name], null, ct), cancellationToken).ConfigureAwait(false);
        if (result.Exit != 0 && await ExistsAsync(name, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Removing {name} failed (exit code {result.Exit}): {WslOutput.LastLine(result.Text)}");
        }
    }

    public Task<int> ExecuteAsync(string distribution, string user, string script, Stream? standardInput, Action<string>? onLine, TimeSpan limit, CancellationToken cancellationToken)
    {
        WslNames.RequireDistribution(distribution);
        WslNames.RequireLinuxUser(user);
        ArgumentException.ThrowIfNullOrEmpty(script);
        if (limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A step needs a time limit above zero.");
        }

        // The script goes in through standard input in front of the data (the command line of another user's program is limited to 1,023 characters); the loader on the command line is short.
        var framed = ScriptUpload.Frame(script, standardInput);
        var command = new AccountCommand(
            _wsl,
            ["-d", distribution, "-u", user, "--cd", "/", "--exec", "/bin/bash", "-c", ScriptUpload.Loader],
            Encoding.UTF8,
            framed,
            null,
            line => onLine?.Invoke(WslOutput.StripNuls(line)));
        return WithLimitAsync(limit, $"a step in {distribution}", ct => RunDisposingAsync(framed, command, ct), cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var result = await WithLimitAsync(s_quickLimit, "shutting down", ct => RunTextAsync(["--shutdown"], null, ct), cancellationToken).ConfigureAwait(false);
        if (result.Exit != 0)
        {
            throw new InvalidOperationException($"Stopping the Linux machine failed (exit code {result.Exit}): {WslOutput.LastLine(result.Text)}");
        }
    }

    private async Task<int> RunDisposingAsync(Stream framed, AccountCommand command, CancellationToken cancellationToken)
    {
        await using (framed.ConfigureAwait(false))
        {
            return await _process.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        (await ListDistributionsAsync(cancellationToken).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase);

    private async Task<TextResult> RunTextAsync(IReadOnlyList<string> arguments, Stream? standardInput, CancellationToken cancellationToken)
    {
        var lines = new LineCollector(KeptLines);
        var exit = await _process.RunAsync(new AccountCommand(_wsl, arguments, Encoding.Unicode, standardInput, null, lines.Add), cancellationToken).ConfigureAwait(false);
        return new TextResult(exit, lines.Text);
    }

    /// <summary>Runs <c>wsl --list --quiet --all</c> (every distribution, also one that is being installed or removed: <c>--quiet</c> alone hides those). The listing is read as bytes and decoded from what they are (UTF-16 or UTF-8), so a wrong guess can never turn names into an empty list.</summary>
    private async Task<ListResult> RunListAsync(CancellationToken cancellationToken)
    {
        var errors = new LineCollector(KeptLines);
        var sink = new CappedMemoryStream(MaximumListBytes);
        await using (sink.ConfigureAwait(false))
        {
            var exit = await _process.RunAsync(new AccountCommand(_wsl, ["--list", "--quiet", "--all"], Encoding.Unicode, null, sink, errors.Add), cancellationToken).ConfigureAwait(false);
            return new ListResult(exit, WslOutput.Decode(sink.ToArray()), errors.Text);
        }
    }

    /// <summary>Runs <paramref name="work"/> with a time limit on top of the caller's token; the limit running out is a <see cref="TimeoutException"/>, the caller's cancel stays a cancel.</summary>
    private async Task<T> WithLimitAsync<T>(TimeSpan limit, string what, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(limit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timer.IsCancellationRequested)
        {
            throw new TimeoutException($"WSL did not finish {what} within {limit.TotalMinutes:0} minutes, so it was ended.");
        }
    }

    private sealed record TextResult(int Exit, string Text);

    private sealed record ListResult(int Exit, string Text, string Errors);
}
