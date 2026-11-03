namespace DbdPresence.Core.Detection;

/// <summary>
/// Where a detector ran: the HUD profile, resolution class, language and game version (DETECTION_VALIDATION §1.3).
/// Evidence is per context, so the engine compares it against what a detector was validated for.
/// </summary>
/// <param name="ProfileId">HUD profile id; empty when none.</param>
/// <param name="ResolutionClass">Resolution class label, for example "1920x1080".</param>
/// <param name="Locale">BCP-47 tag of the game language. Only English is supported (PLAN_V2 D17).</param>
/// <param name="GameVersion">Executable file version, when known.</param>
public sealed record ContextKey(string ProfileId, string ResolutionClass, string Locale, string? GameVersion);

/// <summary>
/// One detector reading (ARCHITECTURE §3). A detector emits observations and nothing else: it never confirms,
/// publishes or touches presence state (FR-DET-01). <see cref="Score"/> is a raw detector score, not a probability.
/// </summary>
/// <param name="Detector">The emitting detector.</param>
/// <param name="DetectorVersion">The detector's version; a change invalidates validation.</param>
/// <param name="ObservedAtUtc">When the frame was sampled.</param>
/// <param name="Kind">What the value is about.</param>
/// <param name="Value">Canonical value, or null when the detector could not read the frame ("unreadable").</param>
/// <param name="Score">Raw score in [0, 1].</param>
/// <param name="Evidence">A reference to the evidence (region id, metric values). Never pixels.</param>
/// <param name="Context">The detection context.</param>
public sealed record Observation(
    DetectorId Detector,
    int DetectorVersion,
    DateTimeOffset ObservedAtUtc,
    ObservationKind Kind,
    string? Value,
    double Score,
    string Evidence,
    ContextKey Context)
{
    /// <summary>Gets a value indicating whether the detector produced a value.</summary>
    public bool IsReadable => Value is not null;

    /// <summary>Returns the observation kind a detector may emit, or null for detectors the engine does not support yet.</summary>
    public static ObservationKind? KindOf(DetectorId detector) => detector switch
    {
        DetectorId.Scene => ObservationKind.Scene,
        DetectorId.Role => ObservationKind.Role,
        DetectorId.Map => ObservationKind.Map,
        DetectorId.Generators => ObservationKind.Generators,
        DetectorId.Character => ObservationKind.Character,
        DetectorId.QueueMode => ObservationKind.QueueMode,
        DetectorId.Killer => ObservationKind.Killer,
        DetectorId.KillerPrompt => ObservationKind.KillerPrompt,
        DetectorId.Status => ObservationKind.OwnStatus,
        DetectorId.SurvivorResult => ObservationKind.SurvivorResult,
        DetectorId.KillerResult => ObservationKind.KillerResult,
        DetectorId.HookCount => ObservationKind.HookCount,
        _ => null,
    };

    /// <summary>Returns the detector that emits an observation kind.</summary>
    public static DetectorId DetectorOf(ObservationKind kind) => kind switch
    {
        ObservationKind.Scene => DetectorId.Scene,
        ObservationKind.Role => DetectorId.Role,
        ObservationKind.Map => DetectorId.Map,
        ObservationKind.Generators => DetectorId.Generators,
        ObservationKind.Character => DetectorId.Character,
        ObservationKind.QueueMode => DetectorId.QueueMode,
        ObservationKind.Killer => DetectorId.Killer,
        ObservationKind.KillerPrompt => DetectorId.KillerPrompt,
        ObservationKind.OwnStatus => DetectorId.Status,
        ObservationKind.SurvivorResult => DetectorId.SurvivorResult,
        ObservationKind.KillerResult => DetectorId.KillerResult,
        ObservationKind.HookCount => DetectorId.HookCount,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>The values of a queue-mode observation other than an event-mode id.</summary>
public static class QueueModes
{
    /// <summary>The ordinary queue (an event queue carries the event-mode id instead).</summary>
    public const string Normal = "Normal";
}
