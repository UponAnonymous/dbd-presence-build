using System.Text.Json;
using System.Text.Json.Serialization;
using DbdPresence.Core.Capture;

namespace DbdPresence.StudyCapture;

/// <summary>One saved frame.</summary>
internal sealed record FrameRecord(int Index, string File, DateTimeOffset CapturedUtc, int Width, int Height, long Bytes, string Reason);

/// <summary>A change of capture state (for example black frames or a minimised window), with its time.</summary>
internal sealed record StatusRecord(DateTimeOffset AtUtc, string Status, string Detail);

/// <summary>
/// The session's <c>metadata.json</c>: only technical facts about the machine and the capture. It holds no account name, no
/// player names and no file paths.
/// </summary>
internal sealed class SessionMetadata
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Gets the marker that identifies this file as written by the study tool; the 30-day clean-up deletes only folders that carry it.</summary>
    public string Tool { get; init; } = SessionRetention.ToolMarker;

    public string ToolVersion { get; init; } = string.Empty;

    public string OsVersion { get; init; } = string.Empty;

    public int OsBuild { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Gets the moment this recording deletes itself: 30 days after it started (PLAN_V2 D25).</summary>
    public DateTimeOffset ExpiresUtc => SessionRetention.ExpiryOf(StartedUtc);

    public DateTimeOffset? EndedUtc { get; set; }

    public string StopReason { get; set; } = "running";

    public int? FirstFrameWidth { get; set; }

    public int? FirstFrameHeight { get; set; }

    public uint? Dpi { get; set; }

    public WindowDisplayMode WindowMode { get; set; } = WindowDisplayMode.Unknown;

    public string WindowModeNote { get; init; } =
        "FullscreenOrBorderless means the window has no frame and covers its monitor exactly; borderless and exclusive fullscreen cannot be told apart this way.";

    /// <summary>
    /// Gets or sets whether every saved picture was cut to the window's client area (no title bar, no border). Recordings made before this field
    /// existed do not have it; the evaluator then treats the pictures as of unknown origin and applies its documented normalisation heuristic.
    /// </summary>
    public bool? ClientAreaOnly { get; set; }

    public List<string> DistinctResolutions { get; } = [];

    public List<FrameRecord> Frames { get; } = [];

    public List<StatusRecord> StatusChanges { get; } = [];

    public long TotalBytes { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, s_options);
}
