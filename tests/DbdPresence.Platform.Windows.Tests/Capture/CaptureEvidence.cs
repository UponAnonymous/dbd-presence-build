namespace DbdPresence.Platform.Windows.Tests.Capture;

/// <summary>
/// Appends facts observed on the CI runner (capture support, border behaviour, OCR languages) to a text file that the
/// workflow uploads with the other evidence, so ADR-0003 records what was measured and not what was assumed.
/// </summary>
internal static class CaptureEvidence
{
    private static readonly object s_lock = new();

    public static string FilePath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("DBDP_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(Path.GetTempPath(), "dbdp-capture-evidence");
            }

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "capture-evidence.txt");
        }
    }

    public static void Record(string line)
    {
        lock (s_lock)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }

        Console.WriteLine("[capture-evidence] " + line);
    }
}
