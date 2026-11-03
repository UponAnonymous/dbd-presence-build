namespace DbdPresence.Core.Detection;

/// <summary>
/// Per-detector publication and health configuration. The default is <see cref="DetectionTier.Disabled"/> with
/// <see cref="ThresholdProvenance.InitialGuess"/>: nothing reaches Discord until a held-out report moves a detector up
/// (DETECTION_VALIDATION §7).
/// </summary>
/// <param name="Tier">Publication tier.</param>
/// <param name="Provenance">Whether the thresholds were measured. Initial guesses never publish.</param>
/// <param name="BaselineUnreadableRate">Unreadable rate measured during validation; enables the 3× health rule.</param>
/// <param name="ValidatedGameVersion">The game version the tier was earned on; another version degrades the detector.</param>
/// <param name="ValidatedProfileId">The HUD profile the tier was earned on; another profile degrades the detector.</param>
/// <param name="PublishableClasses">
/// The classes of a detector that earned the per-class gate (at least 10 held-out trials with no false positive, DETECTION_VALIDATION §7), copied from the
/// evaluator report. Only the own-status and the two result detectors read it: they publish a class only when it is listed here, so a null or empty list
/// publishes none (fail closed). For own status the classes are "Hooked" and "Dying"; for results they are outcome ids (<see cref="ResultOutcomes"/>).
/// </param>
public sealed record DetectorSettings(
    DetectionTier Tier = DetectionTier.Disabled,
    ThresholdProvenance Provenance = ThresholdProvenance.InitialGuess,
    double? BaselineUnreadableRate = null,
    string? ValidatedGameVersion = null,
    string? ValidatedProfileId = null,
    IReadOnlyList<string>? PublishableClasses = null)
{
    /// <summary>Gets the settings every detector starts with: Disabled, initial-guess thresholds.</summary>
    public static DetectorSettings Default { get; } = new();

    /// <summary>
    /// Returns a reason when a tier above Disabled is configured without the evidence that must come with it
    /// (measured thresholds and a validated game version), otherwise null.
    /// </summary>
    public string? Problem() =>
        Tier == DetectionTier.Disabled ? null
        : Provenance != ThresholdProvenance.Measured ? "a tier above Disabled needs measured thresholds"
        : string.IsNullOrWhiteSpace(ValidatedGameVersion) ? "a tier above Disabled needs the validated game version"
        : BaselineUnreadableRate is < 0 or > 1 ? "baseline unreadable rate must be within [0, 1]"
        : null;
}

/// <summary>
/// Engine thresholds and time constants. Every value is an <b>initial guess</b> taken from ARCHITECTURE §4 until the
/// tuning split measures it (DETECTION_VALIDATION §6); they only matter while a detector is Disabled for Discord.
/// </summary>
public sealed record EngineOptions
{
    /// <summary>Gets the scene confirmation rule.</summary>
    public ConfirmationRule Scene { get; init; } = ConfirmationRule.DefaultScene;

    /// <summary>Gets the role confirmation rule.</summary>
    public ConfirmationRule Role { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>Gets the map confirmation rule.</summary>
    public ConfirmationRule Map { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>Gets the generators confirmation rule.</summary>
    public ConfirmationRule Generators { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>Gets the owner-character confirmation rule.</summary>
    public ConfirmationRule Character { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>Gets the queue-mode confirmation rule.</summary>
    public ConfirmationRule QueueMode { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>Gets the killer-name confirmation rule.</summary>
    public ConfirmationRule Killer { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>Gets the confirmation rule of the killer inferred from power prompts. The pipeline has already required agreement between prompts; this rule then needs the same agreed value on several frames.</summary>
    public ConfirmationRule KillerPrompt { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>
    /// Gets the own-status confirmation rule (FR-AUTO-03): K=3, N=4, dwell 1.5 s, like the scene (an initial guess). Changing a confirmed status
    /// needs the same K new agreeing readings (hysteresis), so alternating frames never flap it.
    /// </summary>
    public ConfirmationRule OwnStatus { get; init; } = ConfirmationRule.DefaultScene;

    /// <summary>
    /// Gets the outcome confirmation rule (FR-AUTO-04): at least 3 agreeing readings spanning at least 2 s (an initial guess). The outcome is irreversible
    /// and public, so it needs a longer span than a field.
    /// </summary>
    public ConfirmationRule ResultOutcome { get; init; } = new(3, 4, TimeSpan.FromSeconds(2), 0.6, TimeSpan.FromSeconds(10));

    /// <summary>Gets the confirmation rule of the killer HUD's hook counter (FR-AUTO-07).</summary>
    public ConfirmationRule HookCount { get; init; } = ConfirmationRule.DefaultField;

    /// <summary>
    /// Gets how long a confirmed own status stays fresh without a renewing reading (5 s, an initial guess): an unread row (TAB open, the row hidden) goes
    /// stale and the small picture returns to the role icon.
    /// </summary>
    public TimeSpan OwnStatusTtl { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long a confirmed hook count stays fresh without a renewing reading (30 s, like the generators).</summary>
    public TimeSpan HookCountTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long after a session ended an outcome may still attach to it (T_result, 20 s; ARCHITECTURE §4.3).</summary>
    public TimeSpan ResultAttach { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the highest hook count accepted (two digits; an initial guess).</summary>
    public int MaxHookCount { get; init; } = 99;

    /// <summary>
    /// Gets the lowest score every supporting observation needs for the trial (<see cref="Scene.InTrial"/> or <see cref="Scene.Spectating"/>) to be
    /// confirmed while the confirmed scene is a menu-type scene (main menu, mode list, lobby, searching, results). The weak trial HUD cues
    /// (a free-text player column) score below it, so a lobby whose anchors went unread can never turn into a trial on them alone: only a strong cue
    /// (the match start card) crosses menu to trial. From Unknown, Loading and the in-match menu the ordinary scene floor applies (0.80, initial guess).
    /// </summary>
    public double TrialEntryFromMenuMinScore { get; init; } = 0.80;

    /// <summary>Gets how long without scene evidence before a menu-type scene becomes Unknown (T_lost, 10 s).</summary>
    public TimeSpan SceneLostMenu { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets how long without evidence before InTrial or Spectating becomes Unknown (30 s: the HUD can be hidden).</summary>
    public TimeSpan SceneLostTrial { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long the scene is held while capture is paused before it becomes Unknown (T_hold, 60 s).</summary>
    public TimeSpan SceneHold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets how long Unknown or paused may last before an open session ends (T_session_lost, 10 min).</summary>
    public TimeSpan SessionLost { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets how long a confirmed generators value stays fresh without a renewing observation (30 s).</summary>
    public TimeSpan GeneratorsTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long the last-session summary lives after the session ended (15 min).</summary>
    public TimeSpan SummaryTtl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets the longest gap between a confirmed Loading scene and the trial for the start to count as exact (60 s).</summary>
    public TimeSpan LoadingToTrial { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the oldest observation the engine accepts, relative to its clock.</summary>
    public TimeSpan MaxObservationAge { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets how far an observation timestamp may be ahead of the clock.</summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the highest generators count accepted (Standard 1v4: 5, ARCHITECTURE FR-DET-05).</summary>
    public int MaxGenerators { get; init; } = 5;

    /// <summary>Gets the health window (5 min).</summary>
    public TimeSpan HealthWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the minimum observations in the health window before the unreadable-rate rule applies.</summary>
    public int HealthMinSamples { get; init; } = 20;

    /// <summary>Gets the factor over the validation baseline at which a detector is Degraded (3×).</summary>
    public double DegradedFactor { get; init; } = 3.0;

    /// <summary>Gets the lowest unreadable rate that can degrade a detector, so a near-zero baseline does not trip on one miss.</summary>
    public double MinDegradedUnreadableRate { get; init; } = 0.10;

    /// <summary>Gets how long without any observation (while capture runs) marks a detector Stale.</summary>
    public TimeSpan HealthSilence { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets an optional check that a map id belongs to the closed vocabulary; null accepts any non-empty id.</summary>
    public Func<string, bool>? IsKnownMapId { get; init; }

    /// <summary>Gets an optional check that a character id (survivor or killer) belongs to the closed vocabulary; null accepts any non-empty id.</summary>
    public Func<string, bool>? IsKnownCharacterId { get; init; }

    /// <summary>Gets an optional check that a killer id belongs to the closed vocabulary; null accepts any non-empty id.</summary>
    public Func<string, bool>? IsKnownKillerId { get; init; }

    /// <summary>Gets an optional check that an event-mode id belongs to the closed vocabulary; null accepts any non-empty id. "Normal" is always accepted.</summary>
    public Func<string, bool>? IsKnownEventModeId { get; init; }

    /// <summary>Throws when an option is not usable.</summary>
    public void Validate()
    {
        Scene.Validate();
        Role.Validate();
        Map.Validate();
        Generators.Validate();
        Character.Validate();
        QueueMode.Validate();
        Killer.Validate();
        KillerPrompt.Validate();
        OwnStatus.Validate();
        ResultOutcome.Validate();
        HookCount.Validate();
        if (MaxHookCount is < 1 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHookCount));
        }

        if (double.IsNaN(TrialEntryFromMenuMinScore) || TrialEntryFromMenuMinScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TrialEntryFromMenuMinScore), "Must be within [0, 1].");
        }

        if (MaxGenerators is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxGenerators));
        }

        if (HealthMinSamples < 1 || DegradedFactor <= 1.0 || MinDegradedUnreadableRate is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HealthMinSamples), "Invalid health thresholds.");
        }

        TimeSpan[] spans = [SceneLostMenu, SceneLostTrial, SceneHold, SessionLost, GeneratorsTtl, SummaryTtl, OwnStatusTtl, HookCountTtl, ResultAttach, LoadingToTrial, MaxObservationAge, MaxClockSkew, HealthWindow, HealthSilence];
        if (spans.Any(s => s < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(SceneLostMenu), "Time constants must not be negative.");
        }
    }
}
