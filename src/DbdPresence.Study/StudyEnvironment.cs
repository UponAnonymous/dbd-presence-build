using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbdPresence.Study;

/// <summary>Everything the labelling code needs from the machine, so it runs on Linux in tests with fakes. It holds no recogniser: the evaluator tool adds that in its own subclass.</summary>
internal class StudyEnvironment
{
    /// <summary>Gets the standard output.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Gets the error output.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>Gets the clock.</summary>
    public required TimeProvider Time { get; init; }

    /// <summary>Gets the study tool's output folder (Documents\DBD Presence Study); the labelling files are written inside its session folders.</summary>
    public required string StudyRoot { get; init; }

    /// <summary>Gets the folder where the browser saves downloads, or null when unknown.</summary>
    public string? DownloadsFolder { get; init; }

    /// <summary>Gets the folder where the app keeps its data (%LOCALAPPDATA%\DBDPresence.Data), where it looks for <c>status-templates.json</c>; null when unknown.</summary>
    public string? AppDataFolder { get; init; }

    /// <summary>Gets the privacy words printed when a review file is made (the study tool's own by default; the Study page of the app says where the pictures go).</summary>
    public string PrivacyNotice { get; init; } = ReviewFiles.ToolPrivacyWarning;

    /// <summary>Gets the function that waits for the owner to press Enter and returns the typed line (null at end of input).</summary>
    public required Func<string?> ReadLine { get; init; }

    /// <summary>Gets a value indicating whether preparing a recording also makes its pictures smaller (FR-DET-24). Always on for the owner; tests that are
    /// not about the compaction switch it off, because re-encoding every picture made the test run take longer than CI allows.</summary>
    public bool CompactPictures { get; init; } = true;
}

/// <summary>Small JSON helpers: one compact line per record, UTF-8 without a byte order mark, newline-terminated files.</summary>
internal static class JsonText
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes one compact JSON value with the given writer callback.</summary>
    public static string Compact(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes lines to a file, each ending in a newline.</summary>
    public static void WriteLines(string path, IEnumerable<string> lines)
    {
        var text = string.Concat(lines.Select(l => l + "\n"));
        File.WriteAllText(path, text, s_utf8);
    }

    /// <summary>Formats a timestamp the way the dataset format expects (UTC, milliseconds).</summary>
    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses JSON text into a document; a syntax error becomes a <see cref="FormatException"/>.</summary>
    public static JsonDocument ReadDocumentFromText(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The file is not valid JSON.", ex);
        }
    }

    /// <summary>Reads a JSON file into a document.</summary>
    public static JsonDocument ReadDocument(string path, long maxBytes = 64L * 1024 * 1024)
    {
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
        {
            throw new FormatException($"{info.Name} is larger than {maxBytes / (1024 * 1024)} MB.");
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{info.Name} is not valid JSON: {ex.Message}", ex);
        }
    }
}
