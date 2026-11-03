using DbdPresence.Core.Detection;

namespace DbdPresence.Vision.Detection;

/// <summary>A feature measured in one named region.</summary>
/// <param name="RegionId">Id of a region declared in the profile.</param>
/// <param name="Feature">The measurement.</param>
public sealed record FeatureBinding(string RegionId, IFeature Feature);

/// <summary>The evidence that identifies one label: every binding must hold (the label's score is the lowest binding score).</summary>
/// <param name="Label">The label the evidence belongs to.</param>
/// <param name="Bindings">The features that must all be present.</param>
public sealed record LabelAnchors(string Label, IReadOnlyList<FeatureBinding> Bindings);

/// <summary>
/// The data a calibration produces for one anchor detector: regions in normalised coordinates, the evidence per label and
/// the abstention thresholds. <see cref="Provenance"/> is <see cref="ThresholdProvenance.InitialGuess"/> until the thresholds are
/// measured on the tuning split; initial-guess profiles can never publish (DETECTION_VALIDATION §6).
/// </summary>
/// <param name="ProfileId">Profile id, copied into every observation's context by the capture layer.</param>
/// <param name="Regions">The regions the detector needs.</param>
/// <param name="Labels">The evidence per label.</param>
/// <param name="MinScore">The best label's score must reach this, otherwise the frame is unreadable.</param>
/// <param name="MinMargin">The best label must lead the second best by this much, otherwise the frame is unreadable.</param>
/// <param name="Provenance">Whether the thresholds were measured.</param>
public sealed record AnchorProfile(
    string ProfileId,
    IReadOnlyList<RegionRequest> Regions,
    IReadOnlyList<LabelAnchors> Labels,
    double MinScore,
    double MinMargin,
    ThresholdProvenance Provenance = ThresholdProvenance.InitialGuess)
{
    /// <summary>Gets a profile with no evidence at all: a detector built on it can only report "unreadable".</summary>
    public static AnchorProfile Uncalibrated { get; } = new(string.Empty, [], [], 1.0, 1.0);

    /// <summary>Gets a value indicating whether the profile has any evidence.</summary>
    public bool IsCalibrated => Labels.Count > 0;

    /// <summary>Throws when the profile is inconsistent or uses a label outside <paramref name="allowedLabels"/>.</summary>
    public void Validate(IReadOnlySet<string> allowedLabels)
    {
        ArgumentNullException.ThrowIfNull(allowedLabels);
        if (double.IsNaN(MinScore) || MinScore is < 0 or > 1 || double.IsNaN(MinMargin) || MinMargin is < 0 or > 1)
        {
            throw new ArgumentException("Thresholds must lie within [0, 1].");
        }

        var regionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in Regions)
        {
            if (!regionIds.Add(region.Id))
            {
                throw new ArgumentException($"Duplicate region id '{region.Id}'.");
            }
        }

        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in Labels)
        {
            if (!allowedLabels.Contains(label.Label) || !labels.Add(label.Label))
            {
                throw new ArgumentException($"Label '{label.Label}' is not allowed or is repeated.");
            }

            if (label.Bindings.Count == 0 || label.Bindings.Any(b => !regionIds.Contains(b.RegionId)))
            {
                throw new ArgumentException($"Label '{label.Label}' needs bindings to declared regions.");
            }
        }
    }
}

/// <summary>
/// A detector that classifies a frame by comparing region measurements with calibrated evidence per label. It reports the
/// best label with its raw score, or "unreadable" when the best score is below the floor, the lead over the runner-up is
/// below the margin, evidence regions are missing, or the game language is not English (PLAN_V2 D17). It emits observations only.
/// </summary>
public abstract class AnchorDetector : IDetector
{
    private readonly ObservationKind _kind;
    private readonly AnchorProfile _profile;

    /// <summary>Initializes a new instance of the <see cref="AnchorDetector"/> class.</summary>
    protected AnchorDetector(DetectorId id, int version, ObservationKind kind, IReadOnlySet<string> allowedLabels, AnchorProfile? profile)
    {
        _profile = profile ?? AnchorProfile.Uncalibrated;
        _profile.Validate(allowedLabels);
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

    /// <summary>Gets a value indicating whether the detector has calibrated evidence.</summary>
    public bool IsCalibrated => _profile.IsCalibrated;

    /// <inheritdoc />
    public IReadOnlyList<RegionRequest> Regions(DetectionContext context) => _profile.Regions;

    /// <inheritdoc />
    public DetectorResult Analyze(FrameCrops crops, DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Context.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return One(context, null, 0, "unsupported game language: detection unavailable");
        }

        if (!_profile.IsCalibrated)
        {
            return One(context, null, 0, "not calibrated: no profile");
        }

        var scored = _profile.Labels
            .Select(l => (l.Label, Score: ScoreLabel(l, crops)))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();
        var evidence = string.Join(';', scored.Select(s => FormattableString.Invariant($"{s.Label}={s.Score:0.000}")));
        var best = scored[0];
        var second = scored.Count > 1 ? scored[1].Score : 0.0;

        if (best.Score < _profile.MinScore)
        {
            return One(context, null, best.Score, "below score floor; " + evidence);
        }

        if (best.Score - second < _profile.MinMargin)
        {
            return One(context, null, best.Score, "ambiguous; " + evidence);
        }

        return One(context, best.Label, best.Score, evidence);
    }

    private static double ScoreLabel(LabelAnchors anchors, FrameCrops crops)
    {
        var lowest = 1.0;
        foreach (var binding in anchors.Bindings)
        {
            var crop = crops.Get(binding.RegionId);
            if (crop is null)
            {
                return 0.0; // Evidence that cannot be seen does not count.
            }

            lowest = Math.Min(lowest, Math.Clamp(binding.Feature.Score(crop), 0.0, 1.0));
        }

        return lowest;
    }

    private DetectorResult One(DetectionContext context, string? value, double score, string evidence) =>
        new([new Observation(Id, Version, context.NowUtc, _kind, value, score, evidence, context.Context)]);
}

/// <summary>
/// Scene detector skeleton (lobby, in-trial, results and the other scenes of ARCHITECTURE §4.2). It has no built-in evidence:
/// it works only on a profile calibrated from the user's own captures, and without one it reports "unreadable".
/// Starts in the Disabled tier.
/// </summary>
public sealed class PhaseDetector : AnchorDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    private static readonly IReadOnlySet<string> s_labels = Enum.GetValues<Scene>().Where(s => s != Scene.Unknown).Select(s => s.ToString()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="PhaseDetector"/> class.</summary>
    /// <param name="profile">A calibrated profile, or null for an uncalibrated detector.</param>
    public PhaseDetector(AnchorProfile? profile = null)
        : base(DetectorId.Scene, CurrentVersion, ObservationKind.Scene, s_labels, profile)
    {
    }
}

/// <summary>
/// Role detector skeleton (Survivor or Killer from lobby layout or HUD). Like the scene detector it has no built-in evidence and
/// reports "unreadable" until calibrated. Starts in the Disabled tier.
/// </summary>
public sealed class RoleDetector : AnchorDetector
{
    /// <summary>The detector version; bump on any change to logic or profile format.</summary>
    public const int CurrentVersion = 1;

    private static readonly IReadOnlySet<string> s_labels = new HashSet<string>(StringComparer.Ordinal) { "Survivor", "Killer" };

    /// <summary>Initializes a new instance of the <see cref="RoleDetector"/> class.</summary>
    /// <param name="profile">A calibrated profile, or null for an uncalibrated detector.</param>
    public RoleDetector(AnchorProfile? profile = null)
        : base(DetectorId.Role, CurrentVersion, ObservationKind.Role, s_labels, profile)
    {
    }
}
