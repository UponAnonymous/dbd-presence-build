using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbdPresence.StudyCapture;

/// <summary>A session folder that was found but deliberately left alone, with the plain-language reason.</summary>
internal sealed record RetentionProblem(string FolderName, string Reason);

/// <summary>What one clean-up pass did: the folders it deleted, the ones it could not or would not delete, and how many it kept.</summary>
internal sealed record RetentionReport(IReadOnlyList<string> Deleted, IReadOnlyList<RetentionProblem> Problems, int Kept)
{
    /// <summary>Gets a report for a pass that found nothing to do.</summary>
    public static RetentionReport Empty { get; } = new([], [], 0);
}

/// <summary>
/// Study recordings delete themselves 30 days after they were made (PLAN_V2 D25). On every start the tool calls
/// <see cref="Sweep"/> before it records anything. It only ever touches a folder that sits directly in the output root, is named
/// like a session (<see cref="NameFormat"/>), is not a symlink, junction or other reparse point, and holds a <c>metadata.json</c> that
/// carries this tool's marker. Inside such a folder it deletes only the files the tool writes (<c>frame-NNNNN.png</c>,
/// <c>metadata.json</c>, the evaluator's labelling files and its <c>-review.zip</c> review file, whole or in pieces); if anything else is in there, nothing in that folder is deleted and the owner is told. Pure file logic and
/// clock-injected, so it is unit tested on Linux through a linked source file.
/// </summary>
internal static partial class SessionRetention
{
    /// <summary>The number of days a recording is kept.</summary>
    public const int Days = 30;

    /// <summary>The folder-name format of a session (local date and time).</summary>
    public const string NameFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>The value of the <c>Tool</c> field that marks a <c>metadata.json</c> as written by this tool.</summary>
    public const string ToolMarker = "DbdPresence.StudyCapture";

    private const long MaxMetadataBytes = 32L * 1024 * 1024;

    /// <summary>Gets how long a recording is kept.</summary>
    public static TimeSpan Period { get; } = TimeSpan.FromDays(Days);

    /// <summary>Gets the moment a recording started at <paramref name="startedUtc"/> is deleted.</summary>
    public static DateTimeOffset ExpiryOf(DateTimeOffset startedUtc) => startedUtc + Period;

    /// <summary>Gets the local calendar date (yyyy-MM-dd) on which a recording started at <paramref name="startedUtc"/> is deleted.</summary>
    public static string ExpiryDate(DateTimeOffset startedUtc) =>
        ExpiryOf(startedUtc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Deletes every expired session folder under <paramref name="root"/>.</summary>
    public static RetentionReport Sweep(string root, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(time);

        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            return RetentionReport.Empty;
        }

        if (IsReparsePoint(rootInfo))
        {
            return new RetentionReport([], [new RetentionProblem(rootInfo.Name, "the output folder is a link, so it was not followed and nothing was deleted")], 0);
        }

        List<DirectoryInfo> children;
        try
        {
            children = rootInfo.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RetentionReport([], [new RetentionProblem(rootInfo.Name, $"the output folder could not be listed: {ex.Message}")], 0);
        }

        var now = time.GetUtcNow();
        var deleted = new List<string>();
        var problems = new List<RetentionProblem>();
        var kept = 0;
        foreach (var folder in children)
        {
            if (!NameLooksLikeASession(folder.Name))
            {
                continue;
            }

            if (IsReparsePoint(folder))
            {
                problems.Add(new RetentionProblem(folder.Name, "it is a link, so it was not followed and not deleted"));
                continue;
            }

            if (!TryReadStart(folder, out var startedUtc, out var problem))
            {
                if (problem is not null)
                {
                    problems.Add(new RetentionProblem(folder.Name, problem));
                }

                continue;
            }

            if (now < ExpiryOf(startedUtc))
            {
                kept++;
                continue;
            }

            var failure = TryDelete(folder);
            if (failure is null)
            {
                deleted.Add(folder.Name);
            }
            else
            {
                problems.Add(new RetentionProblem(folder.Name, failure));
            }
        }

        return new RetentionReport(deleted, problems, kept);
    }

    /// <summary>
    /// Reads the recording start from <c>metadata.json</c>. Returns false with <paramref name="problem"/> null when the folder is
    /// simply not ours (no file, or no marker): such a folder is never mentioned and never touched.
    /// </summary>
    private static bool TryReadStart(DirectoryInfo folder, out DateTimeOffset startedUtc, out string? problem)
    {
        startedUtc = default;
        problem = null;
        var file = new FileInfo(System.IO.Path.Combine(folder.FullName, "metadata.json"));
        try
        {
            if (!file.Exists)
            {
                problem = HoldsPictures(folder) ? UnreadableMetadata : null;
                return false;
            }

            if (IsReparsePoint(file))
            {
                problem = "its metadata.json is a link, so the folder was not followed and not deleted";
                return false;
            }

            if (file.Length > MaxMetadataBytes)
            {
                problem = HoldsPictures(folder) ? UnreadableMetadata : null;
                return false;
            }

            using var stream = file.OpenRead();
            using var document = JsonDocument.Parse(stream);
            var obj = document.RootElement;
            if (obj.ValueKind != JsonValueKind.Object
                || !obj.TryGetProperty("Tool", out var tool)
                || tool.ValueKind != JsonValueKind.String
                || tool.GetString() != ToolMarker)
            {
                return false;
            }

            if (!obj.TryGetProperty("StartedUtc", out var started)
                || started.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(started.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out startedUtc))
            {
                problem = "it carries this tool's marker but no readable start time, so it was kept";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            problem = HoldsPictures(folder) ? UnreadableMetadata : null;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"its metadata.json could not be read, so it was kept: {ex.Message}";
            return false;
        }
    }

    /// <summary>Deletes the tool's own files and then the folder. Returns null on success, else why not everything was deleted.</summary>
    private static string? TryDelete(DirectoryInfo folder)
    {
        try
        {
            var files = new List<FileInfo>();
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (IsReparsePoint(entry))
                {
                    return $"it holds a link ({entry.Name}), which is never followed, so nothing in it was deleted";
                }

                if (entry is not FileInfo file || !IsOwnFileName(entry.Name))
                {
                    return $"it holds something this tool did not create ({entry.Name}), so nothing in it was deleted";
                }

                files.Add(file);
            }

            // metadata.json goes last: if the pass is interrupted the marker stays and the next start finishes the job.
            foreach (var file in files.OrderBy(f => f.Name == "metadata.json").ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                file.Delete();
            }

            folder.Delete(recursive: false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var reason = $"it could not be deleted: {ex.Message}";
            return reason;
        }
    }

    private static bool NameLooksLikeASession(string name) =>
        DateTime.TryParseExact(name, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>
    /// The files the evaluator (<c>tools/DbdPresence.Eval</c>) adds to a session folder for labelling. They are deleted with the pictures, so the 30-day
    /// promise covers them too: the review pages hold small copies of game pictures and the labels belong to the recording.
    /// </summary>
    private static readonly string[] s_evaluatorFiles =
    [
        "session.json", "frames.jsonl", "labels.jsonl", "review-plan.json", "review-answers.json", "review.html",
        "recheck-plan.json", "recheck.html", "recheck.json",
    ];

    private const string UnreadableMetadata = "it holds recording pictures but its metadata.json is missing or unreadable, so it cannot be dated and was kept; delete the folder yourself if you no longer need it";

    /// <summary>Whether the folder holds files named like the recorder's pictures (so a missing or damaged metadata.json is worth telling the owner about).</summary>
    private static bool HoldsPictures(DirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateFiles("frame-*.png").Any(f => FrameFileName().IsMatch(f.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed at all: say so rather than staying quiet about pictures that may be there.
            return ex is not null;
        }
    }

    private static bool IsOwnFileName(string name) =>
        name == "metadata.json" || name == MetadataTempName || FrameFileName().IsMatch(name) || s_evaluatorFiles.Contains(name, StringComparer.Ordinal) || ReviewFileName().IsMatch(name);

    /// <summary>The name metadata.json is written under before it is moved into place, so the marker file is never half written.</summary>
    public const string MetadataTempName = "metadata.json.tmp";

    /// <summary>Whether the item is a link, junction or other reparse point. When that cannot be read the answer is yes: it is then not followed and not deleted.</summary>
    private static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex is not null;
        }
    }

    [GeneratedRegex(@"^frame-\d{5,}\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex FrameFileName();

    /// <summary>
    /// The compact review file the evaluator makes (<c>yyyy-MM-dd_HH-mm-ss-review.zip</c>, or its pieces <c>yyyy-MM-dd_HH-mm-ss-review-1-of-3.zip</c> when it
    /// was too large for one attachment), which holds small copies of the pictures and so goes with them.
    /// </summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-review(-\d{1,3}-of-\d{1,3})?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewFileName();
}
