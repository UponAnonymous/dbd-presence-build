using DbdPresence.Core.Game;
using GameRole = DbdPresence.Core.Game.Role;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Core.Detection;

/// <summary>
/// One field of the match state with its confirmation state (ARCHITECTURE §3). <see cref="LastValue"/> is for
/// in-app diagnostics; anything that leaves the app must use <see cref="ConfirmedValue"/>, which is null unless the
/// field is <see cref="FieldState.Confirmed"/>.
/// </summary>
/// <param name="State">Confirmation state.</param>
/// <param name="LastValue">The last confirmed (or declared) value, even if it is stale now.</param>
/// <param name="Source">Detected or user-declared.</param>
/// <param name="ConfirmedAtUtc">When the value was confirmed.</param>
/// <param name="ExpiresAtUtc">When a confirmed value goes stale without renewal, if it can.</param>
/// <param name="Reason">Why the field is not confirmed, for the "why is this hidden" UI.</param>
public sealed record ConfirmedField(
    FieldState State,
    string? LastValue,
    FieldSource Source,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Reason)
{
    /// <summary>Gets the value only when it is confirmed and fresh, otherwise null.</summary>
    public string? ConfirmedValue => State == FieldState.Confirmed ? LastValue : null;

    /// <summary>Gets a field with nothing confirmed.</summary>
    public static ConfirmedField Empty(string? reason = null) => new(FieldState.Unknown, null, FieldSource.Detected, null, null, reason);
}

/// <summary>An open trial (ARCHITECTURE §4.3).</summary>
/// <param name="Number">Increasing session number (unique per engine run).</param>
/// <param name="StartedUtc">Timestamp of the first sampled frame of the confirming window.</param>
/// <param name="Quality">Whether the start is exact or approximate.</param>
/// <param name="Mode">The declared game mode when the session started; custom trials are tagged by it.</param>
public sealed record MatchSession(long Number, DateTimeOffset StartedUtc, StartQuality Quality, GameMode Mode);

/// <summary>
/// What the last trial confirmed, for Results, Lobby and MainMenu templates. Cleared on confirmed Matchmaking or
/// Loading, on game exit or 15 minutes after the session ended, so it cannot reach a new trial.
/// </summary>
/// <param name="SessionNumber">The ended session.</param>
/// <param name="Role">Confirmed detected role at the end, if any.</param>
/// <param name="MapId">Confirmed map id at the end, if any.</param>
/// <param name="EndedUtc">When the session ended.</param>
/// <param name="OutcomeId">The confirmed outcome id of the result screen, attached within <see cref="EngineOptions.ResultAttach"/> of the end (ARCHITECTURE §4.3), or null.</param>
public sealed record LastSessionSummary(long SessionNumber, GameRole Role, string? MapId, DateTimeOffset EndedUtc, string? OutcomeId = null);

/// <summary>Health of one detector (FR-DET-10).</summary>
/// <param name="State">The state.</param>
/// <param name="Reason">Why the detector is degraded or stale.</param>
/// <param name="LastObservationUtc">When the detector last produced an accepted observation.</param>
/// <param name="UnreadableRate">Unreadable share of the health window, or null with too few samples.</param>
/// <param name="Samples">Observations in the health window.</param>
public sealed record DetectorHealth(HealthState State, string? Reason, DateTimeOffset? LastObservationUtc, double? UnreadableRate, int Samples);

/// <summary>Counters for the diagnostics page and anomaly review. They never reach Discord.</summary>
/// <param name="Accepted">Observations accepted.</param>
/// <param name="Rejected">Observations rejected or ignored.</param>
/// <param name="IllegalTransitions">Scene changes applied via Unknown after doubled evidence.</param>
/// <param name="MonotonicViolations">Generator increases applied after doubled evidence.</param>
/// <param name="UndetectedBoundaries">Map changes inside a session, treated as a missed match boundary.</param>
public sealed record EngineDiagnostics(long Accepted, long Rejected, long IllegalTransitions, long MonotonicViolations, long UndetectedBoundaries);

/// <summary>The immutable engine output (ARCHITECTURE §3). Produced only by <see cref="MatchStateEngine"/>.</summary>
public sealed record MatchSnapshot
{
    /// <summary>Gets the change counter; it increases whenever visible state changes.</summary>
    public required long Sequence { get; init; }

    /// <summary>Gets the process-level state.</summary>
    public required GameState Game { get; init; }

    /// <summary>Gets a value indicating whether capture is paused.</summary>
    public required bool CapturePaused { get; init; }

    /// <summary>Gets the confirmed scene (value = the scene name).</summary>
    public required ConfirmedField Scene { get; init; }

    /// <summary>Gets the role field.</summary>
    public required ConfirmedField Role { get; init; }

    /// <summary>Gets the character field: the owner's character read from the owner's row, or the user-declared one, which wins.</summary>
    public required ConfirmedField Character { get; init; }

    /// <summary>Gets the map field (value = canonical map id).</summary>
    public required ConfirmedField Map { get; init; }

    /// <summary>Gets the mode field (user-declared only).</summary>
    public required ConfirmedField Mode { get; init; }

    /// <summary>Gets the generators field (value = the integer as text).</summary>
    public required ConfirmedField Generators { get; init; }

    /// <summary>Gets the queue-mode field (value = "Normal" or an event-mode id). In-app only.</summary>
    public required ConfirmedField QueueMode { get; init; }

    /// <summary>Gets the killer field (value = a killer id), only where the game prints the killer's name. In-app only.</summary>
    public required ConfirmedField Killer { get; init; }

    /// <summary>
    /// Gets the killer the owner is playing, inferred from power prompts after the agreement rule held (value = a killer id). In-app only: no presence
    /// token uses it. Empty until a validated-for-the-app detector reads it; every detector is Disabled for Discord.
    /// </summary>
    public ConfirmedField KillerFromPrompt { get; init; } = ConfirmedField.Empty("no power prompt agreement");

    /// <summary>
    /// Gets the state icon of the owner's own HUD row (value = an <see cref="OwnStatuses"/> value), read only in a trial while the owner plays a
    /// survivor. Session-scoped and short-lived (<see cref="EngineOptions.OwnStatusTtl"/>). Disabled for Discord until a validation report exists.
    /// </summary>
    public ConfirmedField OwnStatus { get; init; } = ConfirmedField.Empty("no own status confirmed");

    /// <summary>Gets the survivor outcome of the result screen (value = a survivor outcome id), attached to the session that just ended. Lasts until the scene changes.</summary>
    public ConfirmedField SurvivorResult { get; init; } = ConfirmedField.Empty("no result confirmed");

    /// <summary>Gets the killer outcome of the result screen (value = a killer outcome id), attached to the session that just ended. Lasts until the scene changes.</summary>
    public ConfirmedField KillerResult { get; init; } = ConfirmedField.Empty("no result confirmed");

    /// <summary>Gets the killer HUD's hook counter (value = the integer as text), read only while the owner plays the killer.</summary>
    public ConfirmedField HookCount { get; init; } = ConfirmedField.Empty("no hook count confirmed");

    /// <summary>Gets the open session, if any.</summary>
    public MatchSession? Session { get; init; }

    /// <summary>Gets the summary of the last ended session, if still valid.</summary>
    public LastSessionSummary? LastSession { get; init; }

    /// <summary>Gets the health of every supported detector.</summary>
    public required IReadOnlyDictionary<DetectorId, DetectorHealth> Health { get; init; }

    /// <summary>Gets the counters.</summary>
    public required EngineDiagnostics Diagnostics { get; init; }

    /// <summary>Gets the confirmed scene, or <see cref="SceneKind.Unknown"/>.</summary>
    public SceneKind ConfirmedScene =>
        Scene.ConfirmedValue is { } v && Enum.TryParse<SceneKind>(v, out var scene) ? scene : SceneKind.Unknown;

    /// <summary>Gets the coarse phase.</summary>
    public GamePhase Phase => Game switch
    {
        GameState.NotRunning => GamePhase.NotRunning,
        GameState.Launching => GamePhase.Launching,
        _ => ConfirmedScene switch
        {
            SceneKind.Unknown => GamePhase.Unknown,
            SceneKind.MainMenu or SceneKind.Matchmaking or SceneKind.Other => GamePhase.Menu,
            SceneKind.Lobby => GamePhase.Lobby,
            SceneKind.Loading => GamePhase.Loading,
            SceneKind.InTrial or SceneKind.Spectating or SceneKind.InMatchMenu => GamePhase.InMatch,
            SceneKind.Results => GamePhase.Results,
            _ => GamePhase.Unknown,
        },
    };

    /// <summary>Gets the confirmed detected role, or <see cref="GameRole.None"/>.</summary>
    public GameRole DetectedRole =>
        Role is { Source: FieldSource.Detected, ConfirmedValue: { } v } && Enum.TryParse<GameRole>(v, out var role) ? role : GameRole.None;

    /// <summary>Gets the confirmed hook count, or null.</summary>
    public int? ConfirmedHookCount =>
        int.TryParse(HookCount.ConfirmedValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Gets the confirmed generators count, or null.</summary>
    public int? ConfirmedGenerators =>
        int.TryParse(Generators.ConfirmedValue, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
