using System.ComponentModel;
using System.Diagnostics;
using DbdPresence.Core.Images;
using DbdPresence.Core.Pictures;

namespace DbdPresence.Platform.Windows.Images;

/// <summary>
/// Runs <c>DbdPresence.ImageHelper.exe</c> on an untrusted image and returns its clean PNG (FR-PACK-10, ADR-0013).
/// This is the one place in the app allowed to call <c>Process.Start</c>, and only for the image helper; the
/// architecture tests enforce that. The helper gets a time limit and is killed when it runs over. No job object
/// limits are applied: the project has no job-object interop, and adding one would extend the P/Invoke allow-list.
/// The helper is crash and parser isolation, not a privilege boundary (ADR-0013). Its output is not trusted blindly: it is walked
/// chunk by chunk (<see cref="StrictPng"/>) before it is returned. Network paths are refused before the helper starts. Result details
/// carry only a status and an exception type name: never paths, user names or exception messages.
/// </summary>
public sealed class SafeImageLoader : ISafeImageLoader, IPictureFitter
{
    /// <summary>The longest side of an accepted helper output (the helper scales to fit 1024).</summary>
    public const int MaxOutputDimension = 1024;

    /// <summary>The default time limit for one helper run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const string HelperFileName = "DbdPresence.ImageHelper.exe";
    private const long MaxOutputBytes = 16L * 1024 * 1024;
    private static readonly TimeSpan s_killGrace = TimeSpan.FromSeconds(5);

    private readonly string _helperDirectory;
    private readonly string _workRoot;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a loader.</summary>
    /// <param name="helperDirectory">The folder containing the helper; normally the app folder.</param>
    /// <param name="workRoot">A folder for short-lived working files, one sub-folder per call.</param>
    /// <param name="timeout">How long the helper may run before it is killed.</param>
    public SafeImageLoader(string helperDirectory, string workRoot, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        _helperDirectory = helperDirectory;
        _workRoot = workRoot;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public Task<SafeImageResult> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains("://", StringComparison.Ordinal))
        {
            return Task.FromResult(SafeImageResult.Failure(SafeImageStatus.BadInput, "remote locations are never loaded"));
        }

        return Task.Run(() => Load(path, null, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<SafeImageResult> LoadBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var copy = data.ToArray();
        return Task.Run(() => Load(null, copy, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<SafeImageResult> FitFileAsync(string path, int maxEdge, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsFitEdge(maxEdge))
        {
            return Task.FromResult(SafeImageResult.Failure(SafeImageStatus.BadInput, "unsupported picture size"));
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return Task.FromResult(SafeImageResult.Failure(SafeImageStatus.BadInput, "remote locations are never loaded"));
        }

        return Task.Run(() => Load(path, null, cancellationToken, maxEdge), cancellationToken);
    }

    /// <inheritdoc />
    public Task<SafeImageResult> FitBytesAsync(ReadOnlyMemory<byte> data, int maxEdge, CancellationToken cancellationToken)
    {
        if (!IsFitEdge(maxEdge))
        {
            return Task.FromResult(SafeImageResult.Failure(SafeImageStatus.BadInput, "unsupported picture size"));
        }

        var copy = data.ToArray();
        return Task.Run(() => Load(null, copy, cancellationToken, maxEdge), cancellationToken);
    }

    private static bool IsFitEdge(int maxEdge) => maxEdge is 512 or 1024;

    private SafeImageResult Load(string? inputPath, byte[]? inputBytes, CancellationToken cancellationToken, int? fitEdge = null)
    {
        var workDir = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        SafeImageResult result;
        try
        {
            string input;
            if (inputBytes is null)
            {
                input = Path.GetFullPath(inputPath!);
                if (!ImagePathPolicy.IsLocalPath(input, root => new DriveInfo(root).DriveType))
                {
                    return SafeImageResult.Failure(SafeImageStatus.BadInput, "network and non-local paths are never loaded");
                }
            }
            else
            {
                Directory.CreateDirectory(workDir);
                input = Path.Combine(workDir, "in.bin");
                File.WriteAllBytes(input, inputBytes);
            }

            Directory.CreateDirectory(workDir);
            result = RunHelper(input, Path.Combine(workDir, "out.png"), fitEdge, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result = SafeImageResult.Failure(SafeImageStatus.BadInput, $"working files failed: {ex.GetType().Name}");
        }

        var cleaned = RemoveWorkDirectory(workDir, result);

        // A cancelled load has stopped the helper (RunHelper) and removed its files; the caller is told by the exception, not by a result.
        cancellationToken.ThrowIfCancellationRequested();
        return cleaned;
    }

    private SafeImageResult RunHelper(string input, string output, int? fitEdge, CancellationToken cancellationToken)
    {
        var helperPath = Path.Combine(_helperDirectory, HelperFileName);
        if (!File.Exists(helperPath))
        {
            return SafeImageResult.Failure(SafeImageStatus.HelperUnavailable, "helper executable not found");
        }

        var startInfo = new ProcessStartInfo(helperPath)
        {
            CreateNoWindow = true,
            WorkingDirectory = _helperDirectory,
        };
        if (fitEdge is { } edge)
        {
            startInfo.ArgumentList.Add("fit");
            startInfo.ArgumentList.Add(edge.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        startInfo.ArgumentList.Add(input);
        startInfo.ArgumentList.Add(output);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return SafeImageResult.Failure(SafeImageStatus.HelperUnavailable, $"start failed: {ex.GetType().Name}");
        }

        if (process is null)
        {
            return SafeImageResult.Failure(SafeImageStatus.HelperUnavailable, "start returned no process");
        }

        using (process)
        {
            // Cancelling stops the helper at once (the caller gave up on this picture); the timeout below still bounds an uncancelled run.
            using var registration = cancellationToken.Register(() => _ = TryKill(process));
            if (!process.WaitForExit(_timeout))
            {
                return StopHelper(process);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return SafeImageResult.Failure(SafeImageStatus.BadInput, "cancelled");
            }

            return process.ExitCode == SafeImageResult.ExitOk ? ReadOutput(output, fitEdge) : SafeImageResult.FromExitCode(process.ExitCode);
        }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            return false; // the process had already exited, or the kill was refused: the wait in RunHelper ends either way
        }
    }

    private SafeImageResult StopHelper(Process process)
    {
        var stopped = $"helper stopped after {_timeout.TotalSeconds:0.###} s";
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // The process may have exited on its own just now, or the kill was refused; either way the result is a timeout.
            return SafeImageResult.Failure(SafeImageStatus.TimedOut, $"{stopped}; kill failed: {ex.GetType().Name}");
        }

        return process.WaitForExit(s_killGrace)
            ? SafeImageResult.Failure(SafeImageStatus.TimedOut, stopped)
            : SafeImageResult.Failure(SafeImageStatus.TimedOut, $"{stopped}; it did not exit after the kill");
    }

    private static SafeImageResult ReadOutput(string outputPath, int? fitEdge)
    {
        FileInfo info = new(outputPath);
        if (!info.Exists || info.Length == 0 || info.Length > MaxOutputBytes)
        {
            return SafeImageResult.Failure(SafeImageStatus.DecodeFailed, "helper reported success without a usable output file");
        }

        if (fitEdge is not null && info.Length >= PictureLimits.MaxFileBytes)
        {
            return SafeImageResult.Failure(SafeImageStatus.OutputTooLarge, "helper output is 2 MB or more");
        }

        var png = File.ReadAllBytes(outputPath);
        var edgeCap = fitEdge ?? MaxOutputDimension;
        if (!StrictPng.TryValidate(png, out var width, out var height, out var failure) || width > edgeCap || height > edgeCap)
        {
            return SafeImageResult.Failure(SafeImageStatus.DecodeFailed, $"helper output rejected: {failure ?? "dimension cap"}");
        }

        return SafeImageResult.Success(png, width, height);
    }

    private static SafeImageResult RemoveWorkDirectory(string workDir, SafeImageResult result)
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result with { Detail = $"{result.Detail}; working folder not removed: {ex.GetType().Name}".TrimStart(';', ' ') };
        }
    }
}
