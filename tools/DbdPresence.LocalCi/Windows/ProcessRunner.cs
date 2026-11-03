using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace DbdPresence.LocalCi.Windows;

/// <summary>A program the tool needs could not be started (it is missing or not allowed to run).</summary>
internal sealed class ProgramNotStartedException : InvalidOperationException
{
    public ProgramNotStartedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>What a finished command printed and returned.</summary>
internal sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Starts the Windows commands the program needs under its own administrator token (schtasks, taskkill for the dedicated account on uninstall, and wsl.exe only to install WSL itself) and waits for them.
/// The one place that starts those programs: only by a full path that <see cref="ProgramPaths"/> approves, from the Windows folder, with a short environment (<see cref="ChildEnvironment"/>) and a
/// temporary folder only Administrators can write to. Output is read as bytes in bounded pieces (a program that prints a gigabyte without a line break cannot make this program grow; local CI round 2,
/// red-team F5), the waits after the program has ended have time limits, and the start itself holds the window-station gate so it can never land on a private window station.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessRunner
{
    private const int KeptOutputLines = 200;

    private static readonly TimeSpan s_pumpLimit = TimeSpan.FromSeconds(10);
    private static readonly Redactor s_noSecrets = new();

    private static string? s_temporaryDirectory;

    /// <summary>Sets the protected temporary folder the started programs use (the data folder's <c>tmp</c>). Without it, or while it does not exist, Windows' own temporary folder is used.</summary>
    public static void Configure(string temporaryDirectory) => s_temporaryDirectory = temporaryDirectory;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Encoding? outputEncoding,
        Stream? standardInput,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var encoding = outputEncoding ?? Encoding.UTF8;
        var start = new ProcessStartInfo(ProgramPaths.Require(fileName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        start.Environment.Clear();
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Path.GetDirectoryName(Environment.SystemDirectory)!;
        var temporary = s_temporaryDirectory is { } configured && Directory.Exists(configured) ? configured : Path.Combine(systemRoot, "Temp");
        foreach (var (name, value) in ChildEnvironment.Build(Environment.GetEnvironmentVariable, systemRoot, temporary))
        {
            start.Environment[name] = value;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            // A private window station is made by another thread of this program by switching the whole process to it for a moment, under this lock; a program started in that moment would start there.
            lock (PrivateDesktop.ProcessStationGate)
            {
                process.Start();
            }
        }
        catch (Win32Exception ex)
        {
            throw new ProgramNotStartedException($"Windows could not start {fileName}: {ex.Message}", ex);
        }

        var lines = new LineCollector(KeptOutputLines);
        void Collect(string line)
        {
            lines.Add(line);
            onLine?.Invoke(line);
        }

        using var pumps = new PumpSet(cancellationToken);
        pumps.Start(ct => AccountStreams.PumpInputAsync(process.StandardInput.BaseStream, standardInput, ct));
        pumps.Start(ct => AccountStreams.PumpOutputAsync(process.StandardOutput.BaseStream, null, encoding, Collect, s_noSecrets, ct));
        pumps.Start(ct => AccountStreams.PumpOutputAsync(process.StandardError.BaseStream, null, encoding, Collect, s_noSecrets, ct));
        try
        {
            await process.WaitForExitAsync(pumps.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = EndTree(process);
            if (!cancellationToken.IsCancellationRequested && pumps.Failure is { } failure)
            {
                throw new IOException($"Handling the output or input of {Path.GetFileName(fileName)} failed, so it was ended: {failure.Message}", failure);
            }

            throw;
        }

        // What the program printed before it ended is still in the pipes; read it to the end, but never wait for ever for a pipe that something it started still holds open.
        _ = await pumps.WhenAllAsync(s_pumpLimit, TimeProvider.System).ConfigureAwait(false);
        if (pumps.Failure is { } late)
        {
            throw new IOException($"Handling the output or input of {Path.GetFileName(fileName)} failed: {late.Message}", late);
        }

        return new ProcessResult(process.ExitCode, lines.Text);
    }

    /// <summary>Ends the process and everything it started (waiting a short, fixed time); false when it had already ended on its own.</summary>
    public static bool EndTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(10_000);
            return true;
        }
        catch (InvalidOperationException)
        {
            // hygiene: allow-swallow the process ended between the check and the kill, so nothing is left to end
            return false;
        }
    }
}
