using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Vision.Imaging;
using static DbdPresence.Vision.JsonFields;

namespace DbdPresence.Vision.Evaluation;

/// <summary>Decodes one stored frame into pixels. The Windows imaging adapter implements JPEG and PNG; <see cref="PnmDecoder"/> reads PGM and PPM.</summary>
public interface IFrameDecoder
{
    /// <summary>Decodes the file at <paramref name="path"/>.</summary>
    ImageBuffer Decode(string path);
}

/// <summary>Recording metadata of one session (<c>session.json</c>, "frame dataset format" in DETECTION_VALIDATION).</summary>
/// <param name="SessionId">Session id; equals the folder name.</param>
/// <param name="StartedAtUtc">When recording started.</param>
/// <param name="GameVersion">Game executable file version.</param>
/// <param name="Width">Client-area width in pixels.</param>
/// <param name="Height">Client-area height in pixels.</param>
/// <param name="WindowMode">Fullscreen, Borderless or Windowed.</param>
/// <param name="DpiScale">Windows display scale as a factor (1.0 = 100%).</param>
/// <param name="HudScale">User-entered HUD scale.</param>
/// <param name="MenuScale">User-entered menu scale.</param>
/// <param name="Language">Game language tag.</param>
/// <param name="ColourBlindMode">User-entered colour-blind mode.</param>
/// <param name="DeclaredMode">User-declared game mode.</param>
/// <param name="Split">"tuning" or "heldout".</param>
/// <param name="ClientAreaOnly">
/// True when the recorder cut every picture to the window's client area; false when it says it did not; null for recordings made before the
/// recorder wrote the flag (the evaluator then applies <see cref="DbdPresence.Vision.Imaging.ClientAreaNormaliser"/> to windowed sessions and says so in its report).
/// </param>
public sealed record SessionMetadata(
    string SessionId,
    DateTimeOffset StartedAtUtc,
    string GameVersion,
    int Width,
    int Height,
    string WindowMode,
    double DpiScale,
    double HudScale,
    double MenuScale,
    string Language,
    string ColourBlindMode,
    string DeclaredMode,
    string Split,
    bool? ClientAreaOnly = null);

/// <summary>One line of <c>frames.jsonl</c>.</summary>
/// <param name="FrameId">Frame id, sortable (zero-padded sequence).</param>
/// <param name="TimestampUtc">When the frame was sampled.</param>
/// <param name="File">Path of the image relative to the session folder.</param>
public sealed record FrameRecord(string FrameId, DateTimeOffset TimestampUtc, string File);

/// <summary>One line of <c>labels.jsonl</c>.</summary>
/// <param name="Type">"segment" (a scene segment) or "trial-fact" (a per-trial fact such as role or map).</param>
/// <param name="TrialId">The trial the label belongs to.</param>
/// <param name="Detector">The detector the label is ground truth for ("scene", "role", "map", ...).</param>
/// <param name="Label">The label value.</param>
/// <param name="FromFrame">First frame of the unit (inclusive).</param>
/// <param name="ToFrame">Last frame of the unit (inclusive).</param>
/// <param name="Legible">False when the truth was not visible on screen: the unit is then excluded from precision and recall.</param>
/// <param name="Labeller">Who labelled.</param>
/// <param name="LabelledAtUtc">When it was labelled.</param>
/// <param name="ReviewStatus">"labelled", "rechecked" or "disputed".</param>
/// <param name="Source">"owner" (made by the owner; also what a label with no source field means) or "reviewer" (made elsewhere and imported; it counts only once the owner's blind spot-check passed it).</param>
public sealed record LabelRecord(
    string Type,
    string TrialId,
    string Detector,
    string Label,
    string FromFrame,
    string ToFrame,
    bool Legible,
    string Labeller,
    DateTimeOffset LabelledAtUtc,
    string ReviewStatus,
    string Source = "owner");

/// <summary>One recording session loaded from disk.</summary>
public sealed class DatasetSession
{
    /// <summary>Initializes a new instance of the <see cref="DatasetSession"/> class.</summary>
    public DatasetSession(string directory, SessionMetadata metadata, IReadOnlyList<FrameRecord> frames, IReadOnlyList<LabelRecord> labels, string labelsSha256)
    {
        Directory = directory;
        Metadata = metadata;
        Frames = frames;
        Labels = labels;
        LabelsSha256 = labelsSha256;
    }

    /// <summary>Gets the session folder.</summary>
    public string Directory { get; }

    /// <summary>Gets the recording metadata.</summary>
    public SessionMetadata Metadata { get; }

    /// <summary>Gets the frames in time order.</summary>
    public IReadOnlyList<FrameRecord> Frames { get; }

    /// <summary>Gets the labels.</summary>
    public IReadOnlyList<LabelRecord> Labels { get; }

    /// <summary>Gets the SHA-256 (lower-case hex) of the exact bytes of <c>labels.jsonl</c>.</summary>
    public string LabelsSha256 { get; }
}

/// <summary>Reads the frame dataset format described in DETECTION_VALIDATION and turns labels into evaluation units.</summary>
public static class DatasetLoader
{
    /// <summary>The only schema version this loader understands.</summary>
    public const int SchemaVersion = 1;

    private static readonly HashSet<string> s_windowModes = new(StringComparer.Ordinal) { "Fullscreen", "Borderless", "Windowed" };
    private static readonly HashSet<string> s_splits = new(StringComparer.Ordinal) { "tuning", "heldout" };
    private static readonly HashSet<string> s_types = new(StringComparer.Ordinal) { "segment", "trial-fact" };
    private static readonly HashSet<string> s_reviews = new(StringComparer.Ordinal) { "labelled", "rechecked", "disputed" };

    /// <summary>Loads the sessions under <c>{datasetDirectory}/sessions</c>, ordered by session id.</summary>
    /// <exception cref="FormatException">A file does not follow the format.</exception>
    public static IReadOnlyList<DatasetSession> LoadDataset(string datasetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        var sessions = Path.Combine(datasetDirectory, "sessions");
        if (!System.IO.Directory.Exists(sessions))
        {
            throw new DirectoryNotFoundException($"No 'sessions' folder in '{datasetDirectory}'.");
        }

        return [.. System.IO.Directory.GetDirectories(sessions).Order(StringComparer.Ordinal).Select(LoadSession)];
    }

    /// <summary>Loads one session folder (<c>session.json</c>, <c>frames.jsonl</c>, <c>labels.jsonl</c>).</summary>
    public static DatasetSession LoadSession(string sessionDirectory)
    {
        var metadata = ParseMetadata(File.ReadAllText(Path.Combine(sessionDirectory, "session.json")));
        if (!string.Equals(metadata.SessionId, Path.GetFileName(sessionDirectory), StringComparison.Ordinal))
        {
            throw new FormatException($"session.json sessionId '{metadata.SessionId}' does not match the folder name.");
        }

        var frames = ParseLines(File.ReadAllText(Path.Combine(sessionDirectory, "frames.jsonl")), "frames.jsonl", ParseFrame);
        if (frames.Select(f => f.FrameId).Distinct(StringComparer.Ordinal).Count() != frames.Count || frames.Zip(frames.Skip(1)).Any(p => p.Second.TimestampUtc <= p.First.TimestampUtc))
        {
            throw new FormatException("frames.jsonl must list unique frame ids with strictly increasing timestamps.");
        }

        var labelBytes = File.ReadAllBytes(Path.Combine(sessionDirectory, "labels.jsonl"));
        var labels = ParseLines(Encoding.UTF8.GetString(labelBytes), "labels.jsonl", ParseLabel);
        var frameIds = frames.Select(f => f.FrameId).ToHashSet(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (!frameIds.Contains(label.FromFrame) || !frameIds.Contains(label.ToFrame) || IndexOf(frames, label.FromFrame) > IndexOf(frames, label.ToFrame))
            {
                throw new FormatException($"Label '{label.Detector}/{label.Label}' in trial '{label.TrialId}' refers to frames that are missing or out of order.");
            }
        }

        return new DatasetSession(sessionDirectory, metadata, frames, labels, Convert.ToHexStringLower(SHA256.HashData(labelBytes)));
    }

    /// <summary>
    /// Builds one evaluation unit per label of the given detector. Frames are decoded lazily, one at a time, so a large session
    /// is never held in memory. An illegible label yields a unit with no truth.
    /// </summary>
    public static IEnumerable<EvaluationUnit> BuildUnits(DatasetSession session, string detector, IFrameDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decoder);
        var index = 0;
        foreach (var label in session.Labels.Where(l => string.Equals(l.Detector, detector, StringComparison.Ordinal)))
        {
            var from = IndexOf(session.Frames, label.FromFrame);
            var to = IndexOf(session.Frames, label.ToFrame);
            var range = session.Frames.Skip(from).Take(to - from + 1);
            yield return new EvaluationUnit(
                $"{session.Metadata.SessionId}/{label.TrialId}/{label.Detector}/{index++}",
                session.Metadata.SessionId,
                label.Legible ? label.Label : null,
                Decode(session, range, decoder));
        }
    }

    /// <summary>
    /// The held-out manifest hash (DETECTION_VALIDATION §5): SHA-256 over the sorted lines <c>sessionId:labelsSha256</c>. It is committed before the
    /// first evaluation of a frozen detector version; <see cref="VerifyManifest"/> refuses a set that changed afterwards.
    /// </summary>
    public static string ManifestHash(IEnumerable<DatasetSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var lines = sessions.Select(s => $"{s.Metadata.SessionId}:{s.LabelsSha256}").Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    /// <summary>Checks that the sessions are all held-out and match the committed manifest hash.</summary>
    /// <exception cref="InvalidOperationException">A session is not held-out, or the hash differs.</exception>
    public static void VerifyManifest(IReadOnlyList<DatasetSession> sessions, string committedHash)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(committedHash);
        var notHeldOut = sessions.Where(s => !string.Equals(s.Metadata.Split, "heldout", StringComparison.Ordinal)).Select(s => s.Metadata.SessionId).ToList();
        if (notHeldOut.Count > 0)
        {
            throw new InvalidOperationException($"Only held-out sessions may be evaluated for a gate; tuning sessions found: {string.Join(", ", notHeldOut)}.");
        }

        var actual = ManifestHash(sessions);
        if (!string.Equals(actual, committedHash.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The held-out set does not match the committed manifest hash; it was changed after the manifest was committed.");
        }
    }

    /// <summary>Parses <c>session.json</c>.</summary>
    public static SessionMetadata ParseMetadata(string json)
    {
        using var document = Parse(json, "session.json");
        var root = document.RootElement;
        CheckSchema(root);
        var resolution = Prop(root, "resolution");
        var metadata = new SessionMetadata(
            Str(root, "sessionId"),
            Time(root, "startedAtUtc"),
            Str(root, "gameVersion"),
            Int(resolution, "width"),
            Int(resolution, "height"),
            Str(root, "windowMode"),
            Double(root, "dpiScale"),
            Double(root, "hudScale"),
            Double(root, "menuScale"),
            Str(root, "language"),
            Str(root, "colourBlindMode"),
            Str(root, "declaredMode"),
            Str(root, "split"),
            root.TryGetProperty("clientArea", out var clientArea) && (clientArea.ValueKind is JsonValueKind.True or JsonValueKind.False) ? clientArea.GetBoolean() : null);
        if (metadata.SessionId.Length == 0 || metadata.Width < 1 || metadata.Height < 1 || metadata.DpiScale <= 0 || metadata.HudScale <= 0 || metadata.MenuScale <= 0
            || !s_windowModes.Contains(metadata.WindowMode) || !s_splits.Contains(metadata.Split))
        {
            throw new FormatException("session.json has an empty id, a non-positive size or scale, an unknown windowMode or an unknown split.");
        }

        return metadata;
    }

    private static IEnumerable<EvaluationFrame> Decode(DatasetSession session, IEnumerable<FrameRecord> range, IFrameDecoder decoder)
    {
        foreach (var frame in range)
        {
            yield return new EvaluationFrame(frame.TimestampUtc, decoder.Decode(Path.Combine(session.Directory, frame.File)));
        }
    }

    private static int IndexOf(IReadOnlyList<FrameRecord> frames, string id)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (string.Equals(frames[i].FrameId, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static List<T> ParseLines<T>(string text, string file, Func<JsonElement, T> parse)
    {
        var result = new List<T>();
        var number = 0;
        foreach (var line in text.Split('\n'))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = Parse(line, $"{file} line {number}");
            CheckSchema(document.RootElement);
            result.Add(parse(document.RootElement));
        }

        return result;
    }

    private static void CheckSchema(JsonElement root)
    {
        if (Int(root, "schemaVersion") != SchemaVersion)
        {
            throw new FormatException($"Unsupported schemaVersion; expected {SchemaVersion}.");
        }
    }

    private static FrameRecord ParseFrame(JsonElement e)
    {
        var file = Str(e, "file");
        if (Path.IsPathRooted(file) || file.Contains("..", StringComparison.Ordinal))
        {
            throw new FormatException($"Frame file '{file}' must be a relative path inside the session folder.");
        }

        return new FrameRecord(Str(e, "frameId"), Time(e, "timestampUtc"), file);
    }

    private static LabelRecord ParseLabel(JsonElement e)
    {
        var label = new LabelRecord(
            Str(e, "type"),
            Str(e, "trialId"),
            Str(e, "detector"),
            Str(e, "label"),
            Str(e, "fromFrame"),
            Str(e, "toFrame"),
            Bool(e, "legible"),
            Str(e, "labeller"),
            Time(e, "labelledAtUtc"),
            Str(e, "reviewStatus"),
            e.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString()! : "owner");
        if (label.Source is not ("owner" or "reviewer"))
        {
            throw new FormatException("A label's source must be owner or reviewer.");
        }

        if (!s_types.Contains(label.Type) || !s_reviews.Contains(label.ReviewStatus) || label.TrialId.Length == 0 || label.Detector.Length == 0 || label.Label.Length == 0)
        {
            throw new FormatException("A label has an unknown type or reviewStatus, or an empty trialId, detector or label.");
        }

        return label;
    }
}
