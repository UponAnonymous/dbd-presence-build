using System.Text.Json;

namespace DbdPresence.Presentation.Detection;

/// <summary>Tells how many matches the owner has recorded and confirmed for the detection study, or null when the app cannot know.</summary>
public interface IStudyProgress
{
    /// <summary>Gets how many matches are recorded, or null when unknown (then the progress line is left out).</summary>
    int? MatchesRecorded { get; }
}

/// <summary>
/// Counts matches from the label files that the study tools keep inside the study recording folders (<c>sessions</c> of the study capture tool, with a
/// <c>labels.jsonl</c> that the labelling tool wrote when the owner confirmed a recording). It reads those small text files only: it never opens a
/// picture, and it sends nothing anywhere. A match counts when the owner confirmed it in the labelling tool, because that is the only moment a match is
/// known; recordings that were not labelled yet count as nothing.
/// </summary>
public sealed class StudyFolderProgress(string studyFolder, TimeProvider time) : IStudyProgress
{
    private static readonly TimeSpan s_cache = TimeSpan.FromSeconds(30);
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private int? _value;

    /// <inheritdoc />
    public int? MatchesRecorded
    {
        get
        {
            var now = time.GetUtcNow();
            if (now - _readAt >= s_cache)
            {
                _value = Count(studyFolder);
                _readAt = now;
            }

            return _value;
        }
    }

    /// <summary>
    /// Counts the matches in a study folder: the distinct trials among the per-trial facts of every <c>labels.jsonl</c> directly inside a recording
    /// folder. Returns null when the folder does not exist or holds no labelled recording (so nothing is claimed).
    /// </summary>
    public static int? Count(string studyFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyFolder);
        if (!Directory.Exists(studyFolder))
        {
            return null;
        }

        var total = 0;
        var labelled = false;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(studyFolder))
            {
                var path = Path.Combine(folder, "labels.jsonl");
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    continue;
                }

                labelled = true;
                total += TrialsIn(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // the folder cannot be read now, or a label file is damaged: the app says nothing rather than a wrong number
        }

        return labelled ? total : null;
    }

    /// <summary>A label imported from a reviewer counts only after the owner's blind spot-check passed it (DETECTION_VALIDATION §16.1).</summary>
    private static bool IsUncheckedReviewerLabel(JsonElement label) =>
        label.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == "reviewer"
        && !(label.TryGetProperty("reviewStatus", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "rechecked");

    private static int TrialsIn(string path)
    {
        var trials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
        {
            using var document = JsonDocument.Parse(line);
            var e = document.RootElement;
            if (e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "trial-fact"
                && e.TryGetProperty("trialId", out var trial) && trial.ValueKind == JsonValueKind.String
                && !IsUncheckedReviewerLabel(e))
            {
                trials.Add(trial.GetString()!);
            }
        }

        return trials.Count;
    }
}
