using System.Globalization;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Prompts;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// Base of the text-anchored HUD detectors (<c>docs/evidence/hud-cue-catalogue.md</c>). A detector is a pure function of the frame's crops, the text
/// read from them and the context: it emits observations with raw scores and never confirms or publishes anything (FR-DET-01). All of them start
/// <see cref="DetectionTier.Disabled"/>; no validation report exists for any of them, so none reaches Discord (DETECTION_VALIDATION §7).
/// </summary>
public abstract class HudDetector : IDetector
{
    private readonly ObservationKind _kind;

    /// <summary>Initializes a new instance of the <see cref="HudDetector"/> class.</summary>
    protected HudDetector(HudProfile profile, HudVocabulary vocabulary, DetectorId id, ObservationKind kind, int version)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(vocabulary);
        Profile = profile;
        Analyzer = new HudAnalyzer(profile, vocabulary);
        Id = id;
        Version = version;
        _kind = kind;
    }

    /// <inheritdoc />
    public DetectorId Id { get; }

    /// <inheritdoc />
    public int Version { get; }

    /// <inheritdoc />
    public DetectionTier DefaultTier => DetectionTier.Disabled;

    /// <summary>Gets the HUD profile (regions and initial-guess thresholds).</summary>
    public HudProfile Profile { get; }

    /// <summary>Gets the shared reading logic.</summary>
    internal HudAnalyzer Analyzer { get; }

    /// <inheritdoc />
    public IReadOnlyList<RegionRequest> Regions(DetectionContext context) => Profile.Requests(r => !r.Detail || NeedsDetailRegion(r.Id));

    /// <inheritdoc />
    public DetectorResult Analyze(FrameCrops crops, DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(context);
        return Read(crops, context) is { } reading ? new DetectorResult([Observe(context, reading.Value, reading.Score, reading.Evidence)]) : new DetectorResult([]);
    }

    /// <summary>Reads one frame. Returns null when the detector has nothing to say about this screen (no observation at all).</summary>
    internal abstract HudReading? Read(FrameCrops crops, DetectionContext context);

    /// <summary>Returns whether the detector needs this detail region (the anchor regions are always needed).</summary>
    protected abstract bool NeedsDetailRegion(string regionId);

    private Observation Observe(DetectionContext context, string? value, double score, string evidence) =>
        new(Id, Version, context.NowUtc, _kind, value, Math.Clamp(score, 0.0, 1.0), evidence, context.Context);
}

/// <summary>What a HUD detector read: a value (or null for unreadable), its raw score and an evidence note without names.</summary>
/// <param name="Value">The canonical value, or null.</param>
/// <param name="Score">The raw score.</param>
/// <param name="Evidence">Region and metric summary.</param>
internal sealed record HudReading(string? Value, double Score, string Evidence);

/// <summary>
/// Scene from text anchors: main menu (<c>QUIT [ESC]</c>), mode list (header <c>PLAY</c> or <c>PLAY EVENT</c>), lobby (<c>PLAY</c> button),
/// searching (<c>CANCEL</c> and <c>EST.</c>), pre-match lobby (<c>READY</c>), match start card (map over realm), trial HUD and spectating (weak:
/// the player column), match result (<c>MATCH RESULT</c> and outcome), scoreboard, TAB match details and the settings menu. It always reports: a
/// scene, or unreadable with the reason.
/// </summary>
public sealed class ScreenStateDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="ScreenStateDetector"/> class.</summary>
    public ScreenStateDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.Scene, ObservationKind.Scene, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => false;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var screen = Analyzer.Classify(crops, context);
        return screen.IsReadable
            ? new HudReading(screen.Scene!.Value.ToString(), screen.Score, $"{screen.Kind};{screen.Evidence}")
            : new HudReading(null, screen.Score, screen.Problem ?? "unreadable");
    }
}

/// <summary>
/// Normal or event queue: the mode list header (<c>PLAY</c> for normal, <c>PLAY EVENT</c> with the event name on the line below) and the event name
/// printed above the <c>CANCEL</c> button while searching. A normal queue prints nothing there, and the absence of a label proves nothing, so
/// "Normal" comes only from the mode list header. It reports only on those two screens.
/// </summary>
public sealed class QueueModeDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="QueueModeDetector"/> class.</summary>
    public QueueModeDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.QueueMode, ObservationKind.QueueMode, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => false;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var queue = Analyzer.ReadQueue(crops, context);
        return queue.Applicable ? new HudReading(queue.Value, queue.Score, queue.Evidence) : null;
    }
}

/// <summary>
/// Map from the match start card (the map name over the realm name, which must be the realm of that map in the vocabulary) and from the
/// <c>REALM - MAP</c> line of TAB match details (same rule). A map name whose realm text disagrees, or that is ambiguous, is unreadable. It
/// reports only on those two screens.
/// </summary>
public sealed class MapTextDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="MapTextDetector"/> class.</summary>
    public MapTextDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.Map, ObservationKind.Map, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => regionId == HudRegionIds.TabRealmMap;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var map = Analyzer.ReadMap(crops, context);
        return map.Applicable ? new HudReading(map.MapId, map.Score, map.Evidence) : null;
    }
}

/// <summary>
/// Survivor or killer from the owner's own row: in TAB match details and on the scoreboard the row whose player name equals the owner's name
/// <b>and</b> that is highlighted (gold: survivor; red: killer); in the survivor pre-match lobby the pair of character name over the owner's
/// player name; in the killer pre-match lobby the owner's name next to the red killer icon. Both cues must agree or the frame is unreadable. It
/// reports only on those screens.
/// </summary>
public sealed class RoleCueDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="RoleCueDetector"/> class.</summary>
    public RoleCueDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.Role, ObservationKind.Role, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => HudRegionSets.Identity.Contains(regionId);

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var identity = Analyzer.ReadIdentity(crops, context);
        return identity.Applicable ? new HudReading(identity.Role, identity.RoleScore, $"{identity.Source}: {identity.RoleProblem ?? "owner row found"}") : null;
    }
}

/// <summary>
/// The owner's own character: the character name in the owner's row (TAB match details, scoreboard; the survivor pre-match lobby's pairs). The row
/// is found by two cues that must agree: its player name equals the owner's name, and it is highlighted. The name must be in the closed list and
/// its kind must fit the row colour (a survivor in the gold row, a killer in the red row). Anonymous mode, where a player name can equal a
/// character name, never lets the name alone select a row. Reports only on those screens.
/// </summary>
public sealed class CharacterDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="CharacterDetector"/> class.</summary>
    public CharacterDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.Character, ObservationKind.Character, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => HudRegionSets.Identity.Contains(regionId);

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var identity = Analyzer.ReadIdentity(crops, context);
        return identity.Applicable ? new HudReading(identity.CharacterId, identity.CharacterScore, $"{identity.Source}: {identity.CharacterProblem ?? "owner row found"}") : null;
    }
}

/// <summary>
/// The killer's name, only where the game prints it: the last row of TAB match details and of the scoreboard, matched against the closed list of
/// killers. It is never inferred; a row without a killer name (the game did not print one in some TAB captures) is unreadable. Reports only on
/// those two screens.
/// </summary>
public sealed class KillerNameDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="KillerNameDetector"/> class.</summary>
    public KillerNameDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.Killer, ObservationKind.Killer, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => HudRegionSets.Rows.Contains(regionId);

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var identity = Analyzer.ReadIdentity(crops, context);
        return identity.Applicable && identity.Source != "lobby" ? new HudReading(identity.KillerId, identity.KillerScore, $"{identity.Source}: {identity.KillerProblem ?? "killer row read"}") : null;
    }
}

/// <summary>
/// The killer the owner is playing, from the power prompts at the bottom centre of the killer HUD (for example <c>VILE DARKNESS</c>, <c>CAST FLY</c>),
/// so the owner does not have to open TAB. It reads one frame and holds no state: the match's earlier prompts come in through
/// <see cref="DetectionContext.Prompts"/>. One mapped prompt is never enough. The value is a killer id only when two distinct mapped prompts of one
/// killer have been seen, or one mapped prompt together with another cue that named the same killer in this match; prompts of two killers, or a prompt
/// that contradicts the other cue, give an unreadable observation. The map holds only prompts seen on the owner's screen (<see cref="PowerPromptMap"/>).
/// It reports only while the trial HUD is up, scores at most <see cref="HudThresholds.PromptAgreementCap"/>, and stays Disabled for Discord.
/// </summary>
public sealed class KillerPromptDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="KillerPromptDetector"/> class.</summary>
    public KillerPromptDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.KillerPrompt, ObservationKind.KillerPrompt, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => regionId == HudRegionIds.PowerPrompt;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        var reading = Analyzer.ReadPrompt(crops, context);
        if (!reading.Applicable)
        {
            return null;
        }

        var evidence = (context.Prompts ?? PromptEvidence.Empty).With(reading.Match);
        var agreement = evidence.Evaluate();
        if (agreement.Agrees)
        {
            return new HudReading(agreement.KillerId, Math.Min(agreement.Score, Profile.Thresholds.PromptAgreementCap), agreement.Reason);
        }

        return reading.Match.Accepted || evidence.DistinctPrompts > 0 ? new HudReading(null, 0, agreement.Reason) : null;
    }
}

/// <summary>
/// The state icon in the owner's own row of the trial HUD (FR-AUTO-03): the portrait, or the hooked, dying or dead icon that replaces it. The icon cell is
/// compared with pictures the owner's own captures taught this PC (zero-mean normalised correlation; <see cref="OwnStatusTemplates"/>), and a class is
/// read only when it scores at least <see cref="HudThresholds.StatusMinScore"/> and leads the next class by <see cref="HudThresholds.StatusMinMargin"/>.
/// It reads only while the trial HUD is up, the confirmed role is Survivor and the first row carries the owner's name, so anonymous mode (no known name)
/// stays silent; and only when every class has a picture. Anything else gives no observation. <c>Dead</c> is read only to keep it from scoring as
/// <c>Hooked</c>. Stays Disabled for Discord until a validation report exists.
/// </summary>
public sealed class OwnStatusDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    private readonly OwnStatusTemplates _templates;

    /// <summary>Initializes a new instance of the <see cref="OwnStatusDetector"/> class.</summary>
    /// <param name="profile">The HUD profile.</param>
    /// <param name="vocabulary">The closed lists.</param>
    /// <param name="templates">The pictures learned on this PC; null means none.</param>
    public OwnStatusDetector(HudProfile profile, HudVocabulary vocabulary, OwnStatusTemplates? templates = null)
        : base(profile, vocabulary, DetectorId.Status, ObservationKind.OwnStatus, CurrentVersion)
    {
        _templates = templates ?? OwnStatusTemplates.Empty;
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => false;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        if (context.PriorRole != GameRole.Survivor || !_templates.IsComplete)
        {
            return null;
        }

        var screen = Analyzer.Classify(crops, context);
        if (!screen.IsReadable || screen.Kind != ScreenKind.TrialHud || !Analyzer.OwnerInFirstHudRow(crops, context.OwnerName))
        {
            return null;
        }

        if (crops.Get(HudRegionIds.HudIcon(0)) is not { } icon)
        {
            return null;
        }

        var ranked = _templates.Score(icon).OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
        var best = ranked[0];
        var margin = best.Value - (ranked.Count > 1 ? ranked[1].Value : -1.0);
        var t = Profile.Thresholds;
        var evidence = string.Create(CultureInfo.InvariantCulture, $"hud.icon.0: best {best.Key} {best.Value:0.00}, margin {margin:0.00}");
        return best.Value >= t.StatusMinScore && margin >= t.StatusMinMargin
            ? new HudReading(best.Key, Math.Clamp(best.Value, 0.0, 1.0), evidence)
            : new HudReading(null, Math.Clamp(best.Value, 0.0, 1.0), evidence + " (no class is clear)");
    }
}

/// <summary>
/// The hook counter of the killer's own trial HUD (FR-AUTO-07), read as a whole number of one or two digits. It reads only while the trial HUD is up and
/// the confirmed role is Killer; text that is not just digits is unreadable. Its position in the HUD is an initial guess, UNKNOWN until calibrated on the
/// owner's captures, and so is which of the two counters below the survivor list is the hook counter, which is why it stays Disabled for Discord until
/// a validation report exists. It scores at most <see cref="HudThresholds.WeakCueScore"/>: a bare number in a fixed place is a weak cue.
/// </summary>
public sealed class HookCountDetector : HudDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="HookCountDetector"/> class.</summary>
    public HookCountDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.HookCount, ObservationKind.HookCount, CurrentVersion)
    {
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => regionId == HudRegionIds.HookCounter;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        if (context.PriorRole != GameRole.Killer || crops.GetText(HudRegionIds.HookCounter) is not { } text)
        {
            return null;
        }

        var screen = Analyzer.Classify(crops, context);
        if (!screen.IsReadable || screen.Kind != ScreenKind.TrialHud)
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length is >= 1 and <= 2 && trimmed.All(char.IsAsciiDigit)
            ? new HudReading(trimmed, Profile.Thresholds.WeakCueScore, "hud.hooks: a number")
            : new HudReading(null, 0, "hud.hooks: not a number");
    }
}

/// <summary>
/// The base of the two result detectors (FR-AUTO-04): it reads the outcome under <c>MATCH RESULT</c> against the closed list and reports it only when the
/// outcome belongs to the role the detector is for. A label without a listed outcome reports nothing. The outcome is irreversible and public, so the
/// engine confirms it only after three readings over two seconds, and these detectors never publish below the Validated tier (FR-AUTO-05).
/// </summary>
public abstract class ResultOutcomeDetector : HudDetector
{
    private readonly Func<string, bool> _belongs;
    private readonly GameRole _otherRole;

    /// <summary>Initializes a new instance of the <see cref="ResultOutcomeDetector"/> class.</summary>
    /// <param name="profile">The HUD profile.</param>
    /// <param name="vocabulary">The closed lists.</param>
    /// <param name="id">The detector id.</param>
    /// <param name="kind">The observation kind.</param>
    /// <param name="version">The detector version.</param>
    /// <param name="belongs">Whether an outcome id is one this detector reports.</param>
    /// <param name="otherRole">The role whose results screen this detector must not read.</param>
    protected ResultOutcomeDetector(HudProfile profile, HudVocabulary vocabulary, DetectorId id, ObservationKind kind, int version, Func<string, bool> belongs, GameRole otherRole)
        : base(profile, vocabulary, id, kind, version)
    {
        _belongs = belongs;
        _otherRole = otherRole;
    }

    /// <inheritdoc />
    protected override bool NeedsDetailRegion(string regionId) => false;

    internal override HudReading? Read(FrameCrops crops, DetectionContext context)
    {
        if (context.PriorRole == _otherRole)
        {
            return null;
        }

        var outcome = Analyzer.ReadOutcomeEntry(crops, context);
        return outcome is { Applicable: true, OutcomeId: { } id } && _belongs(id) ? new HudReading(id, outcome.Score, outcome.Evidence) : null;
    }
}

/// <summary>The survivor's outcome (<see cref="ResultOutcomes.Escaped"/>, <see cref="ResultOutcomes.Sacrificed"/>, <see cref="ResultOutcomes.Dead"/>) from the result screen (FR-AUTO-04).</summary>
public sealed class SurvivorResultDetector : ResultOutcomeDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="SurvivorResultDetector"/> class.</summary>
    public SurvivorResultDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.SurvivorResult, ObservationKind.SurvivorResult, CurrentVersion, ResultOutcomes.IsSurvivorOutcome, GameRole.Killer)
    {
    }
}

/// <summary>The killer's outcome (the four killer results) from the result screen (FR-AUTO-04).</summary>
public sealed class KillerResultDetector : ResultOutcomeDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Initializes a new instance of the <see cref="KillerResultDetector"/> class.</summary>
    public KillerResultDetector(HudProfile profile, HudVocabulary vocabulary)
        : base(profile, vocabulary, DetectorId.KillerResult, ObservationKind.KillerResult, CurrentVersion, ResultOutcomes.IsKillerOutcome, GameRole.Survivor)
    {
    }
}

/// <summary>The detail regions that belong together.</summary>
internal static class HudRegionSets
{
    /// <summary>The rows and bands of TAB match details and the scoreboard.</summary>
    public static HashSet<string> Rows { get; } = BuildRows();

    /// <summary>Everything the owner's row needs: the rows, the lobby columns and the killer list.</summary>
    public static HashSet<string> Identity { get; } = BuildIdentity();

    private static HashSet<string> BuildRows()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < HudProfile.PlayerRows; i++)
        {
            ids.Add(HudRegionIds.TabRow(i));
            ids.Add(HudRegionIds.TabBand(i));
            ids.Add(HudRegionIds.ScoreRow(i));
            ids.Add(HudRegionIds.ScoreBand(i));
        }

        return ids;
    }

    private static HashSet<string> BuildIdentity()
    {
        var ids = BuildRows();
        ids.Add(HudRegionIds.KillerList);
        for (var i = 0; i < HudProfile.LobbyColumns; i++)
        {
            ids.Add(HudRegionIds.LobbyPair(i));
        }

        return ids;
    }
}
