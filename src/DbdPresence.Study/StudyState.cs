using System.Text;
using System.Text.Json;

namespace DbdPresence.Study;

/// <summary>One held-out report that was written completely.</summary>
/// <param name="ManifestHash">The committed manifest hash the report was made for.</param>
/// <param name="RecordedUtc">When the report was confirmed complete.</param>
/// <param name="Sessions">The held-out recordings it covers (ids only).</param>
internal sealed record HeldOutReportRecord(string ManifestHash, string RecordedUtc, IReadOnlyList<string> Sessions);

/// <summary>
/// The two facts that decide when a practice recording's pictures may go (FR-DET-24, DETECTION_VALIDATION §21): a held-out report was written, and the
/// owner's status icons were taught. They are kept in one small file in the study folder next to the recordings (<c>study-state.json</c>): ids, a hash and
/// times only, no names and no pictures. The recorder's 30-day clean-up never touches it (it deletes recording folders only). If the file cannot be read,
/// nothing is deleted on its account and the owner is told.
/// </summary>
internal sealed record StudyState(IReadOnlyList<HeldOutReportRecord> HeldOutReports, string? StatusTaughtUtc)
{
    /// <summary>The file name inside the study folder.</summary>
    public const string FileName = "study-state.json";

    /// <summary>Gets the state of a study folder that has no file yet.</summary>
    public static StudyState Empty { get; } = new([], null);

    /// <summary>Gets whether at least one held-out report was written completely.</summary>
    public bool HasHeldOutReport => HeldOutReports.Count > 0;

    /// <summary>Reads the file, or returns <see cref="Empty"/> when there is none.</summary>
    /// <exception cref="FormatException">The file exists but cannot be used.</exception>
    public static StudyState Load(string root)
    {
        var path = Path.Combine(root, FileName);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Empty;
        }

        if (StudyFiles.IsLink(info))
        {
            throw new FormatException($"{FileName} is a link, so it was not followed.");
        }

        using var document = JsonText.ReadDocument(path, 1024 * 1024);
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("heldOutReports", out var reports) || reports.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"{FileName} is not a file this tool wrote.");
        }

        var list = new List<HeldOutReportRecord>();
        foreach (var report in reports.EnumerateArray())
        {
            if (report.ValueKind != JsonValueKind.Object
                || !report.TryGetProperty("manifestHash", out var hash) || hash.ValueKind != JsonValueKind.String
                || !report.TryGetProperty("recordedUtc", out var at) || at.ValueKind != JsonValueKind.String
                || !report.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException($"{FileName} is not a file this tool wrote.");
            }

            list.Add(new HeldOutReportRecord(hash.GetString()!, at.GetString()!, [.. sessions.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)]));
        }

        var taught = json.TryGetProperty("statusTaughtUtc", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return new StudyState(list, taught);
    }

    /// <summary>Gets a copy that also holds a held-out report. A report for a manifest that is already listed is not added twice.</summary>
    public StudyState WithHeldOutReport(string manifestHash, DateTimeOffset now, IEnumerable<string> sessions) =>
        HeldOutReports.Any(r => string.Equals(r.ManifestHash, manifestHash, StringComparison.Ordinal))
            ? this
            : this with { HeldOutReports = [.. HeldOutReports, new HeldOutReportRecord(manifestHash, JsonText.Time(now), [.. sessions.Order(StringComparer.Ordinal)])] };

    /// <summary>Gets a copy that records that the status icons were taught.</summary>
    public StudyState WithStatusTaught(DateTimeOffset now) => this with { StatusTaughtUtc = StatusTaughtUtc ?? JsonText.Time(now) };

    /// <summary>Writes the file in one step (a temporary file, then a move), so a crash leaves the old file or the new one.</summary>
    public void Save(string root)
    {
        var text = JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteStartArray("heldOutReports");
            foreach (var report in HeldOutReports)
            {
                w.WriteStartObject();
                w.WriteString("manifestHash", report.ManifestHash);
                w.WriteString("recordedUtc", report.RecordedUtc);
                w.WriteStartArray("sessions");
                foreach (var id in report.Sessions)
                {
                    w.WriteStringValue(id);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (StatusTaughtUtc is { } taught)
            {
                w.WriteString("statusTaughtUtc", taught);
            }

            w.WriteEndObject();
        });
        var path = Path.Combine(root, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
