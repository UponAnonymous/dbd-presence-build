using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace DbdPresence.Platform.Windows.Tests.Capture;

/// <summary>
/// Plain-text results of the detection-speed measurements (docs/evidence/detection-speed-and-confidence.md, section Measurements).
/// Written to detection-benchmark.txt next to capture-evidence.txt, so the workflow uploads it in the windows-evidence artifact.
/// The first line written adds a header with the runner's OS build and CPU name.
/// </summary>
internal static class BenchmarkReport
{
    private static readonly object s_lock = new();
    private static bool s_headerWritten;

    public static string FilePath => Path.Combine(Path.GetDirectoryName(CaptureEvidence.FilePath)!, "detection-benchmark.txt");

    public static void Line(string line)
    {
        lock (s_lock)
        {
            if (!s_headerWritten)
            {
                s_headerWritten = true;
                File.AppendAllText(FilePath, Header() + Environment.NewLine);
            }

            File.AppendAllText(FilePath, line + Environment.NewLine);
        }

        Console.WriteLine("[detection-benchmark] " + line);
    }

    public static string Header() => string.Join(
        Environment.NewLine,
        "=== DETECTION BENCHMARK: a measurement, not a pass/fail check. The tests assert only that something was measured. ===",
        "Target: this test process's own WPF window (never the game). Values are from this one run on this one machine.",
        $"OS: {OsDescription()}",
        $"CPU: {CpuName()} ({Environment.ProcessorCount} logical processors)",
        $"Runtime: {Environment.Version}; 64-bit process: {Environment.Is64BitProcess}",
        string.Create(CultureInfo.InvariantCulture, $"Started (UTC): {DateTimeOffset.UtcNow:O}"),
        "---");

    private static string OsDescription()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "UNKNOWN";
            var display = key?.GetValue("DisplayVersion") as string ?? "UNKNOWN";
            var build = key?.GetValue("CurrentBuildNumber") as string ?? "UNKNOWN";
            var revision = key?.GetValue("UBR") ?? "UNKNOWN";
            return string.Create(CultureInfo.InvariantCulture, $"{product} {display}; build {build}.{revision}; Environment.OSVersion {Environment.OSVersion.Version}");
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return $"UNKNOWN ({ex.GetType().Name}); Environment.OSVersion {Environment.OSVersion.Version}";
        }
    }

    private static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "UNKNOWN";
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return $"UNKNOWN ({ex.GetType().Name})";
        }
    }
}
