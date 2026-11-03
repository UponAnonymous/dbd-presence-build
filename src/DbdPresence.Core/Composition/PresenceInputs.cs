using DbdPresence.Core.Game;

namespace DbdPresence.Core.Composition;

/// <summary>What the owner's own HUD row shows that has a status icon (FR-AUTO-01). <see cref="None"/> means the ordinary portrait or nothing confirmed.</summary>
public enum OwnStatus
{
    /// <summary>Nothing to show: the portrait, or nothing confirmed.</summary>
    None,

    /// <summary>The owner is on a hook.</summary>
    Hooked,

    /// <summary>The owner is down (dying).</summary>
    Dying,
}

/// <summary>
/// Everything the composer may publish, already reduced to <em>confirmed</em> values: a null field means
/// "not confirmed" and is never published. Built from the engine snapshot; nothing is declared by hand (D30).
/// </summary>
public sealed record PresenceInputs
{
    /// <summary>Gets the presence state whose template chain is used.</summary>
    public PresenceState State { get; init; } = PresenceState.NotRunning;

    /// <summary>Gets the current time (for {duration}).</summary>
    public DateTimeOffset Now { get; init; }

    /// <summary>Gets when the game session started.</summary>
    public DateTimeOffset? GameStartedUtc { get; init; }

    /// <summary>Gets when the current trial started (confirmed in-trial only).</summary>
    public DateTimeOffset? MatchStartedUtc { get; init; }

    /// <summary>Gets a value indicating whether the match start is approximate (joined late).</summary>
    public bool MatchStartApproximate { get; init; }

    /// <summary>Gets the confirmed detected role, if any.</summary>
    public Role DetectedRole { get; init; } = Role.None;

    /// <summary>Gets the confirmed display name of the player's own character, or null when it is not confirmed. Never declared by hand (D30).</summary>
    public string? CharacterName { get; init; }

    /// <summary>Gets the confirmed game mode in words ("1v4", "2v8", "Custom game", an event's name), or null when it is not confirmed. Never declared by hand (D30).</summary>
    public string? ModeName { get; init; }

    /// <summary>Gets the confirmed map display name.</summary>
    public string? MapName { get; init; }

    /// <summary>Gets the confirmed map id (for artwork mapping).</summary>
    public string? MapId { get; init; }

    /// <summary>Gets the confirmed realm name.</summary>
    public string? RealmName { get; init; }

    /// <summary>Gets the confirmed generators remaining.</summary>
    public int? GeneratorsRemaining { get; init; }

    /// <summary>Gets the confirmed result text.</summary>
    public string? Result { get; init; }

    /// <summary>Gets the confirmed hook counter of the killer's own HUD, or null when it is not confirmed (FR-AUTO-07). Killer only.</summary>
    public int? HookCount { get; init; }

    /// <summary>Gets the confirmed state of the owner's own row. Survivor only; <see cref="OwnStatus.None"/> when it is not confirmed (FR-AUTO-01).</summary>
    public OwnStatus OwnStatus { get; init; }

    /// <summary>Gets the confirmed outcome id of the result screen (<c>ResultOutcomes</c>), or null when it is not confirmed (FR-AUTO-01). <see cref="Result"/> stays the display text of <c>{result}</c>.</summary>
    public string? OutcomeId { get; init; }

    /// <summary>Gets the confirmed count of survivors shown dead.</summary>
    public int? StatusDead { get; init; }

    /// <summary>Gets the confirmed count of survivors shown hooked.</summary>
    public int? StatusHooked { get; init; }
}

/// <summary>Live privacy toggles (FR-VIS-01..03).</summary>
/// <param name="Paused">Quick pause is on.</param>
/// <param name="StreamerMode">Streamer mode is on.</param>
/// <param name="HideFullActivity">Hide the whole activity.</param>
/// <param name="Suspended">A signed revocation was received for the account (D8): nothing is shown at all.</param>
public sealed record PrivacyState(bool Paused = false, bool StreamerMode = false, bool HideFullActivity = false, bool Suspended = false);
