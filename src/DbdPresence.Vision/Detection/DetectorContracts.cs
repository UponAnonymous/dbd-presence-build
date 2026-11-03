using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Prompts;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Detection;

/// <summary>A region a detector needs, in normalised coordinates. The capture side requests only these regions (FR-DET-13).</summary>
/// <param name="Id">Region id, unique within a profile.</param>
/// <param name="Rect">The region as fractions of the client area.</param>
public sealed record RegionRequest(string Id, NormalizedRect Rect);

/// <summary>What a detector knows about the moment it runs. Detectors are pure, so everything they may depend on comes in here.</summary>
/// <param name="Context">HUD profile, resolution class, language and game version.</param>
/// <param name="NowUtc">When the frame was sampled.</param>
/// <param name="PriorScene">
/// The scene the engine had confirmed before this frame (<see cref="Scene.Unknown"/> when none). It is an input, not a decision: the same
/// settings screen belongs to the trial when it was opened from one and to the menus otherwise, and nothing on the screen says which.
/// </param>
/// <param name="Prompts">
/// What the match's power prompts have shown so far (the pipeline keeps it and hands the current value in). It is an input, like the prior scene: a
/// detector never changes it. Null means nothing was seen.
/// </param>
/// <param name="OwnerName">
/// The owner's in-game player name, used only to compare with names read on screen so the owner's row can be found. It is held in memory and is
/// never written to evidence strings, logs, datasets or presence. A detector that is not given it cannot name the owner's row.
/// </param>
/// <param name="PriorRole">
/// The role the engine had confirmed before this frame (<see cref="GameRole.None"/> when none). It is an input, like the prior scene: the owner's own
/// status icon exists only in a survivor's HUD and the hook counter only in a killer's, so those detectors read only for their role.
/// </param>
public sealed record DetectionContext(ContextKey Context, DateTimeOffset NowUtc, Scene PriorScene = Scene.Unknown, string? OwnerName = null, PromptEvidence? Prompts = null, GameRole PriorRole = GameRole.None)
{
    /// <summary>Returns a description that never contains the owner's name.</summary>
    public override string ToString() =>
        $"DetectionContext {{ Context = {Context}, NowUtc = {NowUtc:O}, PriorScene = {PriorScene}, PriorRole = {PriorRole}, OwnerName = {(OwnerName is null ? "none" : "set")} }}";
}

/// <summary>The cropped regions of one frame, and the text read from some of them. No full frame is ever handed to a detector.</summary>
public sealed class FrameCrops
{
    private readonly Dictionary<string, ImageBuffer> _regions;
    private readonly Dictionary<string, string> _text;
    private readonly Dictionary<string, object> _memo = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="FrameCrops"/> class.</summary>
    /// <param name="frameWidth">Width of the frame the crops were taken from.</param>
    /// <param name="frameHeight">Height of the frame the crops were taken from.</param>
    /// <param name="regions">Crops by region id.</param>
    public FrameCrops(int frameWidth, int frameHeight, IReadOnlyDictionary<string, ImageBuffer> regions)
        : this(frameWidth, frameHeight, regions, new Dictionary<string, string>())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FrameCrops"/> class with the text read from some regions.</summary>
    /// <param name="frameWidth">Width of the frame the crops were taken from.</param>
    /// <param name="frameHeight">Height of the frame the crops were taken from.</param>
    /// <param name="regions">Pixel crops by region id.</param>
    /// <param name="text">
    /// Recognised text by region id. A region with no entry was not read (text recognition is expensive and runs only on regions that changed or
    /// are needed); an entry with an empty string was read and held no text.
    /// </param>
    public FrameCrops(int frameWidth, int frameHeight, IReadOnlyDictionary<string, ImageBuffer> regions, IReadOnlyDictionary<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(text);
        if (frameWidth < 1 || frameHeight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }

        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        _regions = new Dictionary<string, ImageBuffer>(regions, StringComparer.Ordinal);
        _text = new Dictionary<string, string>(text, StringComparer.Ordinal);
    }

    /// <summary>Gets the width of the source frame.</summary>
    public int FrameWidth { get; }

    /// <summary>Gets the height of the source frame.</summary>
    public int FrameHeight { get; }

    /// <summary>Gets the region ids present.</summary>
    public IReadOnlyCollection<string> RegionIds => _regions.Keys;

    /// <summary>Crops the requested regions out of a full frame (used by the evaluator and tests; the live capture crops at the source).</summary>
    public static FrameCrops FromFrame(ImageBuffer frame, IEnumerable<RegionRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(requests);
        var regions = requests.ToDictionary(r => r.Id, r => frame.Crop(r.Rect), StringComparer.Ordinal);
        return new FrameCrops(frame.Width, frame.Height, regions);
    }

    /// <summary>Returns a region's crop, or null when it was not captured.</summary>
    public ImageBuffer? Get(string regionId) => _regions.GetValueOrDefault(regionId);

    /// <summary>Returns the text read from a region, or null when the region was not read.</summary>
    public string? GetText(string regionId) => _text.GetValueOrDefault(regionId);

    /// <summary>Returns a copy of these crops that also carries recognised text. The pixel crops are shared and the per-frame memo starts empty.</summary>
    public FrameCrops WithText(IReadOnlyDictionary<string, string> text) => new(FrameWidth, FrameHeight, _regions, text);

    /// <summary>
    /// Computes a value derived from this frame once and reuses it for every detector that asks. The factory must be a pure function of this
    /// frame's crops and text, so the memo never changes an outcome, it only saves repeating work.
    /// </summary>
    public T Memo<T>(string key, Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_memo)
        {
            if (!_memo.TryGetValue(key, out var value))
            {
                value = factory();
                _memo[key] = value;
            }

            return (T)value;
        }
    }
}

/// <summary>The observations one detector produced for one frame.</summary>
/// <param name="Observations">Observations only: a detector never confirms or publishes anything.</param>
public sealed record DetectorResult(IReadOnlyList<Observation> Observations);

/// <summary>
/// A pure recognition step (ARCHITECTURE §3, ADR-0005). It declares which regions it needs and turns their pixels into
/// <see cref="Observation"/>s with raw scores. It has no I/O, no clock, no access to presence or engine state, and it
/// never reports a guess as a reading: when unsure it emits an unreadable observation.
/// </summary>
public interface IDetector
{
    /// <summary>Gets the detector id.</summary>
    DetectorId Id { get; }

    /// <summary>Gets the detector version; any change to logic or profile format bumps it and invalidates validation.</summary>
    int Version { get; }

    /// <summary>Gets the tier the detector starts in. Every detector starts <see cref="DetectionTier.Disabled"/>.</summary>
    DetectionTier DefaultTier { get; }

    /// <summary>Returns the regions this detector needs for the given context.</summary>
    IReadOnlyList<RegionRequest> Regions(DetectionContext context);

    /// <summary>Analyses the crops of one frame.</summary>
    DetectorResult Analyze(FrameCrops crops, DetectionContext context);
}
