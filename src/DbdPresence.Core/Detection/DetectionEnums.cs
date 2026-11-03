namespace DbdPresence.Core.Detection;

/// <summary>Every detector that may emit observations (DETECTION_VALIDATION §2). Stage D detectors have an id but no engine support yet.</summary>
public enum DetectorId
{
    /// <summary>Scene classification (phase of the game).</summary>
    Scene,

    /// <summary>Survivor or killer.</summary>
    Role,

    /// <summary>Map name read from on-screen text.</summary>
    Map,

    /// <summary>Generators remaining.</summary>
    Generators,

    /// <summary>The killer's match outcome, read from the result screen (FR-AUTO-04). Never publishes below Validated (FR-AUTO-05).</summary>
    KillerResult,

    /// <summary>The survivor's match outcome, read from the result screen (FR-AUTO-04). Never publishes below Validated (FR-AUTO-05).</summary>
    SurvivorResult,

    /// <summary>The state icon in the owner's own HUD row: hooked or dying (FR-AUTO-03). Never publishes below Validated (FR-AUTO-05).</summary>
    Status,

    /// <summary>The owner's own character, read from the owner's row (lobby pair, TAB match details, scoreboard).</summary>
    Character,

    /// <summary>The queue mode: normal or a limited-time event, read from the mode list and the searching screen.</summary>
    QueueMode,

    /// <summary>The killer's name, only where the game prints it (TAB match details, scoreboard).</summary>
    Killer,

    /// <summary>
    /// The killer the owner is playing, read from the power prompts at the bottom centre of the trial HUD (for example the prompts of The Lich). The
    /// detector reads one prompt at a time; the value is a killer id only after the pipeline's agreement rule held (two distinct mapped prompts, or one
    /// prompt and another cue), otherwise the observation is unreadable. In-app only: no presence token uses it.
    /// </summary>
    KillerPrompt,

    /// <summary>
    /// The hook counter of the killer's own HUD (a whole number), read only while the owner plays the killer. Disabled for Discord until a validation report exists
    /// (FR-AUTO-07).
    /// </summary>
    HookCount,
}

/// <summary>What an observation is about. Each kind belongs to exactly one detector.</summary>
public enum ObservationKind
{
    /// <summary>The current <see cref="Scene"/>.</summary>
    Scene,

    /// <summary>"Survivor" or "Killer".</summary>
    Role,

    /// <summary>A canonical map id.</summary>
    Map,

    /// <summary>An integer 0..max as text.</summary>
    Generators,

    /// <summary>A canonical character id (a survivor or killer) of the owner's own row.</summary>
    Character,

    /// <summary>"Normal" or the canonical id of a limited-time event mode.</summary>
    QueueMode,

    /// <summary>A canonical killer id, read from the killer's row.</summary>
    Killer,

    /// <summary>A canonical killer id inferred from power prompts after the agreement rule held; unreadable before.</summary>
    KillerPrompt,

    /// <summary>The state icon in the owner's own HUD row (<see cref="OwnStatuses"/>): portrait, hooked, dying or dead (FR-AUTO-03).</summary>
    OwnStatus,

    /// <summary>A canonical survivor outcome id from the match result screen (<see cref="ResultOutcomes"/>), FR-AUTO-04.</summary>
    SurvivorResult,

    /// <summary>A canonical killer outcome id from the match result screen (<see cref="ResultOutcomes"/>), FR-AUTO-04.</summary>
    KillerResult,

    /// <summary>The killer HUD's hook counter as a whole number (FR-AUTO-07).</summary>
    HookCount,
}

/// <summary>Scene states from ARCHITECTURE §4.2. <see cref="Unknown"/> means "no confirmed scene" and is never observed directly.</summary>
public enum Scene
{
    /// <summary>No confirmed scene (initial value; evidence lost).</summary>
    Unknown,

    /// <summary>Main menu.</summary>
    MainMenu,

    /// <summary>Matchmaking or searching.</summary>
    Matchmaking,

    /// <summary>Lobby.</summary>
    Lobby,

    /// <summary>Loading into a trial.</summary>
    Loading,

    /// <summary>In a trial (in-trial HUD visible).</summary>
    InTrial,

    /// <summary>Spectating after own death or escape.</summary>
    Spectating,

    /// <summary>Results (tally) screen.</summary>
    Results,

    /// <summary>Store, bloodweb, settings and everything else.</summary>
    Other,

    /// <summary>
    /// A menu laid over a running trial: TAB match details, or the ESC settings menu opened from a trial. The trial goes on underneath, so
    /// it keeps the session (ARCHITECTURE §4.2). Added after the catalogue of the owner's screenshots showed both screens.
    /// </summary>
    InMatchMenu,
}

/// <summary>Coarse game phase derived from the process state and the confirmed scene.</summary>
public enum GamePhase
{
    /// <summary>No game process.</summary>
    NotRunning,

    /// <summary>Only the launcher runs.</summary>
    Launching,

    /// <summary>The game runs but no scene is confirmed.</summary>
    Unknown,

    /// <summary>Menus: main menu, matchmaking and other screens.</summary>
    Menu,

    /// <summary>Lobby.</summary>
    Lobby,

    /// <summary>Loading screen.</summary>
    Loading,

    /// <summary>In a trial, including spectating and the menus laid over a trial.</summary>
    InMatch,

    /// <summary>Results screen.</summary>
    Results,
}

/// <summary>The fields the engine tracks.</summary>
public enum FieldKind
{
    /// <summary>The scene.</summary>
    Scene,

    /// <summary>Survivor or killer.</summary>
    Role,

    /// <summary>The owner's character; read from the owner's row, or user-declared (a declared value wins).</summary>
    Character,

    /// <summary>Map; session-scoped.</summary>
    Map,

    /// <summary>Game mode; user-declared only (FR-DET-12).</summary>
    Mode,

    /// <summary>Generators remaining; session-scoped, monotonic.</summary>
    Generators,

    /// <summary>Normal or event queue; in-app only (no presence token uses it).</summary>
    QueueMode,

    /// <summary>The killer's name where the game prints it; in-app only.</summary>
    Killer,

    /// <summary>The killer the owner plays, inferred from power prompts; in-app only.</summary>
    KillerPrompt,

    /// <summary>The state icon of the owner's own row in a trial; session-scoped, short-lived.</summary>
    OwnStatus,

    /// <summary>The survivor outcome of the match result screen; lasts until the scene changes.</summary>
    SurvivorResult,

    /// <summary>The killer outcome of the match result screen; lasts until the scene changes.</summary>
    KillerResult,

    /// <summary>The killer HUD's hook counter; session-scoped.</summary>
    HookCount,
}

/// <summary>Confirmation state of a field (ARCHITECTURE §3).</summary>
public enum FieldState
{
    /// <summary>Nothing confirmed.</summary>
    Unknown,

    /// <summary>Confirmed by repeated agreeing observations (or declared by the user) and still fresh.</summary>
    Confirmed,

    /// <summary>Was confirmed, but the value expired, the scene was lost or capture paused. Hidden from presence.</summary>
    Stale,

    /// <summary>Not applicable now (for example generators in an unsupported mode).</summary>
    Disabled,

    /// <summary>The producing detector is unhealthy (stop-ship rule, FR-DET-10). Hidden from presence.</summary>
    Degraded,
}

/// <summary>Where a field value comes from.</summary>
public enum FieldSource
{
    /// <summary>Confirmed by the engine from detector observations.</summary>
    Detected,

    /// <summary>Declared by the user.</summary>
    UserDeclared,
}

/// <summary>Publication tier of one detector (DETECTION_VALIDATION §7). Every detector starts Disabled.</summary>
public enum DetectionTier
{
    /// <summary>In-app only. Never sent to Discord.</summary>
    Disabled,

    /// <summary>Held-out gate for Experimental passed. Published only with the per-session opt-in and a beta marker.</summary>
    Experimental,

    /// <summary>Held-out gate for Validated passed.</summary>
    Validated,
}

/// <summary>Where a threshold came from. Initial guesses can never publish (DETECTION_VALIDATION §6).</summary>
public enum ThresholdProvenance
{
    /// <summary>A default chosen without data.</summary>
    InitialGuess,

    /// <summary>Chosen on the tuning split and recorded with its evaluator report.</summary>
    Measured,
}

/// <summary>Runtime health of one detector.</summary>
public enum HealthState
{
    /// <summary>No observation has been received yet.</summary>
    NoData,

    /// <summary>Observations arrive and nothing is wrong.</summary>
    Healthy,

    /// <summary>No observation arrived recently although capture is active.</summary>
    Stale,

    /// <summary>High unreadable rate or a context mismatch (FR-DET-10). The detector's fields are hidden from presence.</summary>
    Degraded,
}

/// <summary>How precisely a session start is known (FR-DET-08).</summary>
public enum StartQuality
{
    /// <summary>A confirmed Loading scene preceded the trial within 60 s.</summary>
    Exact,

    /// <summary>The trial was joined late: app start mid-match, detection enabled mid-match or recovery after Unknown.</summary>
    JoinedLate,
}

/// <summary>What the engine did with one submitted observation.</summary>
public enum SubmitResult
{
    /// <summary>Counted toward confirmation (or toward health when unreadable).</summary>
    Accepted,

    /// <summary>The game is not running.</summary>
    IgnoredNotRunning,

    /// <summary>Capture is paused.</summary>
    IgnoredPaused,

    /// <summary>Malformed: score out of range, unparsable value or an unsupported kind.</summary>
    RejectedInvalid,

    /// <summary>The observation kind does not belong to the emitting detector.</summary>
    RejectedKindMismatch,

    /// <summary>Older than the maximum observation age.</summary>
    RejectedTooOld,

    /// <summary>Timestamp ahead of the clock by more than the skew allowance.</summary>
    RejectedFromFuture,

    /// <summary>Not newer than the last accepted observation of the same kind (late or duplicate).</summary>
    RejectedNotNewer,

    /// <summary>The confirmed scene does not allow this field.</summary>
    RejectedSceneGate,

    /// <summary>No open session, or the observation predates the session.</summary>
    RejectedNoSession,

    /// <summary>The declared game mode does not support this field (FR-DET-12).</summary>
    RejectedModeGate,

    /// <summary>The field belongs to the other role: the own-row status and the outcomes of a survivor, the hook counter of a killer.</summary>
    RejectedRoleGate,
}
