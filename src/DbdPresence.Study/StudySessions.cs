using System.Globalization;
using System.Text.Json;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;

namespace DbdPresence.Study;

/// <summary>The files this tool adds to a study session folder. The study tool's 30-day clean-up knows these names and deletes them with the pictures.</summary>
internal static class SessionFiles
{
    public const string Session = "session.json";
    public const string Frames = "frames.jsonl";
    public const string Labels = "labels.jsonl";
    public const string Plan = "review-plan.json";
    public const string Answers = "review-answers.json";
    public const string ReviewPage = "review.html";
    public const string RecheckPlan = "recheck-plan.json";
    public const string RecheckPage = "recheck.html";
    public const string RecheckResult = "recheck.json";
}

/// <summary>How a study session was prepared for labelling.</summary>
/// <param name="SessionId">The session id (the folder name).</param>
/// <param name="Split">"tuning" or "heldout".</param>
/// <param name="Frames">How many frames were listed.</param>
/// <param name="Note">Anything the owner should know (for example how the window mode was read).</param>
internal sealed record PreparedSession(string SessionId, string Split, int Frames, string Note);

/// <summary>What the owner may override when a study session is prepared. Every field has a plain default.</summary>
internal sealed record PrepareOptions
{
    /// <summary>Gets an explicit split, or null to use the default rule.</summary>
    public string? Split { get; init; }

    /// <summary>Gets an explicit window mode, or null to read it from the recording.</summary>
    public string? WindowMode { get; init; }

    /// <summary>Gets the game version, or "unknown".</summary>
    public string GameVersion { get; init; } = "unknown";

    /// <summary>Gets the HUD scale as a factor.</summary>
    public double HudScale { get; init; } = 1.0;

    /// <summary>Gets the menu scale as a factor.</summary>
    public double MenuScale { get; init; } = 1.0;

    /// <summary>Gets the game language tag.</summary>
    public string Language { get; init; } = "en";
}

/// <summary>Finds study sessions and turns a recording folder into a dataset session in place (no pictures are copied or moved).</summary>
internal static class StudySessions
{
    /// <summary>How many sessions are kept for tuning before later ones default to held-out (they supply the calibration the detectors need first).</summary>
    public const int TuningSessionsFirst = 2;

    /// <summary>Lists the study session folders (named like a session, carrying the study tool's marker), oldest first.</summary>
    public static IReadOnlyList<string> RecordingFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.GetDirectories(root)
            .Where(d => DateTime.TryParseExact(Path.GetFileName(d), SessionRetention.NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && IsStudyFolder(d))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Lists the folders that are prepared for labelling (they hold a <c>session.json</c>), oldest first.</summary>
    public static IReadOnlyList<string> PreparedFolders(string root) => [.. RecordingFolders(root).Where(d => File.Exists(Path.Combine(d, SessionFiles.Session)))];

    /// <summary>Loads every prepared session.</summary>
    public static IReadOnlyList<DatasetSession> LoadPrepared(string root) => [.. PreparedFolders(root).Select(DatasetLoader.LoadSession)];

    /// <summary>Prepares one recording folder. Returns null when it already is prepared or holds no pictures.</summary>
    /// <exception cref="FormatException">The recording's metadata cannot be used (the message says why).</exception>
    public static PreparedSession? Prepare(string folder, PrepareOptions options, int preparedSoFar)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (File.Exists(Path.Combine(folder, SessionFiles.Session)))
        {
            return null;
        }

        using var document = JsonText.ReadDocument(Path.Combine(folder, "metadata.json"));
        var root = document.RootElement;
        if (!root.TryGetProperty("Frames", out var frames) || frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() == 0)
        {
            return null;
        }

        var resolutions = root.TryGetProperty("DistinctResolutions", out var distinct) && distinct.ValueKind == JsonValueKind.Array ? distinct.GetArrayLength() : 1;
        if (resolutions > 1)
        {
            throw new FormatException("The window changed size during this recording. A session must have one picture size (one context), so it cannot be used. Record again without resizing the window.");
        }

        bool? clientArea = root.TryGetProperty("ClientAreaOnly", out var clientAreaValue) && (clientAreaValue.ValueKind is JsonValueKind.True or JsonValueKind.False) ? clientAreaValue.GetBoolean() : null;
        var width = IntOf(root, "FirstFrameWidth");
        var height = IntOf(root, "FirstFrameHeight");
        var sessionId = Path.GetFileName(folder);
        var startedUtc = StartOf(root);
        var (windowMode, note) = WindowModeOf(root, options.WindowMode);
        var dpi = root.TryGetProperty("Dpi", out var dpiValue) && dpiValue.ValueKind == JsonValueKind.Number && dpiValue.TryGetDouble(out var d) && d > 0 ? d / 96.0 : 1.0;
        var split = options.Split ?? (preparedSoFar < TuningSessionsFirst ? "tuning" : "heldout");

        var frameLines = new List<string>();
        var last = DateTimeOffset.MinValue;
        foreach (var frame in frames.EnumerateArray())
        {
            var file = frame.TryGetProperty("File", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : string.Empty;
            if (file.Length == 0 || file != Path.GetFileName(file) || !File.Exists(Path.Combine(folder, file)))
            {
                continue; // a frame that was listed but is gone (deleted by hand) is simply not part of the session
            }

            var captured = TimeOf(frame, "CapturedUtc");
            if (captured <= last)
            {
                throw new FormatException("The pictures are not in time order in metadata.json.");
            }

            last = captured;
            var id = (frameLines.Count + 1).ToString("D6", CultureInfo.InvariantCulture);
            frameLines.Add(JsonText.Compact(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", DatasetLoader.SchemaVersion);
                w.WriteString("frameId", id);
                w.WriteString("timestampUtc", JsonText.Time(captured));
                w.WriteString("file", file);
                w.WriteEndObject();
            }));
        }

        if (frameLines.Count == 0)
        {
            return null;
        }

        var session = JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", DatasetLoader.SchemaVersion);
            w.WriteString("sessionId", sessionId);
            w.WriteString("startedAtUtc", JsonText.Time(startedUtc));
            w.WriteString("gameVersion", options.GameVersion);
            w.WriteStartObject("resolution");
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteEndObject();
            w.WriteString("windowMode", windowMode);
            if (clientArea is { } cut)
            {
                w.WriteBoolean("clientArea", cut);
            }

            w.WriteNumber("dpiScale", dpi);
            w.WriteNumber("hudScale", options.HudScale);
            w.WriteNumber("menuScale", options.MenuScale);
            w.WriteString("language", options.Language);
            w.WriteString("colourBlindMode", "none");
            w.WriteString("declaredMode", "Unknown");
            w.WriteString("split", split);
            w.WriteEndObject();
        });
        _ = DatasetLoader.ParseMetadata(session); // the file we write must be one the evaluator accepts

        JsonText.WriteLines(Path.Combine(folder, SessionFiles.Frames), frameLines);
        JsonText.WriteLines(Path.Combine(folder, SessionFiles.Labels), []);
        JsonText.WriteLines(Path.Combine(folder, SessionFiles.Session), [session]);
        return new PreparedSession(sessionId, split, frameLines.Count, note);
    }

    private static bool IsStudyFolder(string folder)
    {
        var file = Path.Combine(folder, "metadata.json");
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            using var document = JsonText.ReadDocument(file);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Tool", out var tool)
                && tool.ValueKind == JsonValueKind.String
                && tool.GetString() == SessionRetention.ToolMarker;
        }
        catch (FormatException)
        {
            return false; // not ours, or unreadable: never touched
        }
    }

    private static (string Mode, string Note) WindowModeOf(JsonElement root, string? chosen)
    {
        if (chosen is not null)
        {
            return (chosen, "window mode as given");
        }

        var recorded = root.TryGetProperty("WindowMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
        return recorded switch
        {
            "Windowed" => ("Windowed", "window mode read from the recording"),
            "FullscreenOrBorderless" => ("Borderless", "the recording cannot tell borderless from exclusive fullscreen; it is filed as Borderless (pass --window-mode Fullscreen if that is what you used)"),
            _ => throw new FormatException("The recording does not say whether the game was windowed or fullscreen. Run again with --window-mode Windowed, Borderless or Fullscreen."),
        };
    }

    private static DateTimeOffset StartOf(JsonElement root) => TimeOf(root, "StartedUtc");

    private static DateTimeOffset TimeOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : throw new FormatException($"metadata.json has no readable {name}.");

    private static int IntOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 1
            ? n
            : throw new FormatException($"metadata.json has no {name}; the recording holds no usable picture.");
}
