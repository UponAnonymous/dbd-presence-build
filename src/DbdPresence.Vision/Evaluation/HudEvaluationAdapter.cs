using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Vision.Evaluation;

/// <summary>A clock the caller sets, so an offline run reads the same times on every run (the change gate uses time).</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>Initializes a new instance of the <see cref="ManualTimeProvider"/> class.</summary>
    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    /// <summary>Sets the time returned by <see cref="GetUtcNow"/>.</summary>
    public void Set(DateTimeOffset now) => _now = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Runs recorded frames through the same code the live capture feeds (crops cut with the capture seam's rounding, the change-gated text reader,
/// the six HUD detectors) and hands the evaluator one observation kind at a time. Text recognition runs once per frame however many kinds are
/// evaluated. The evaluator is an offline tool, so the asynchronous recogniser is awaited synchronously here; nothing in the shipped loop does that.
/// </summary>
public sealed class HudEvaluationAdapter
{
    private readonly HudDetectionPipeline _pipeline;
    private readonly ManualTimeProvider _clock;
    private readonly ContextKey _context;
    private EvaluationFrame? _lastFrame;
    private Scene _lastPrior;
    private GameRole _lastRole;
    private HudFrame? _lastResult;

    /// <summary>Initializes a new instance of the <see cref="HudEvaluationAdapter"/> class.</summary>
    /// <param name="profile">The HUD profile.</param>
    /// <param name="vocabulary">The closed lists.</param>
    /// <param name="ocr">The recogniser (Windows OCR in the evaluator tool, a fake in tests).</param>
    /// <param name="context">The context the units come from.</param>
    /// <param name="ownerName">The owner's player name for this run, given on the command line and never stored; null if unknown.</param>
    /// <param name="gate">Change-gate options.</param>
    public HudEvaluationAdapter(HudProfile profile, HudVocabulary vocabulary, ITextRecognizer ocr, ContextKey context, string? ownerName, ChangeGateOptions? gate = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _context = context;
        _clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        _pipeline = new HudDetectionPipeline(profile, vocabulary, ocr, _clock, gate, ownerName);
    }

    /// <summary>Gets how many text recognitions ran so far.</summary>
    public long OcrCalls => _pipeline.OcrCalls;

    /// <summary>
    /// Creates the evaluator classifier for one observation kind.
    /// </summary>
    /// <param name="kind">The kind to report.</param>
    /// <param name="priorScene">
    /// The scene to pass as "prior" (the <c>PriorScene</c> of <see cref="DbdPresence.Vision.Detection.DetectionContext"/>). Only the settings screen depends on it: label a unit of the
    /// in-trial settings menu with <see cref="Scene.InTrial"/> and the main-menu settings with <see cref="Scene.MainMenu"/>.
    /// </param>
    /// <param name="priorRole">The role to pass as "prior" (the own-row status detector reads for a survivor and the hook counter for a killer); taken from the owner's role label of the unit.</param>
    public FrameClassifier ClassifierFor(ObservationKind kind, Scene priorScene = Scene.Unknown, GameRole priorRole = GameRole.None) => frame =>
    {
        var result = Process(frame, priorScene, priorRole);
        var observation = result.Observations.FirstOrDefault(o => o.Kind == kind);
        return observation is null ? new FramePrediction(null, 0) : new FramePrediction(observation.Value, observation.Score);
    };

    /// <summary>
    /// Runs one frame through the pipeline and returns every detector's observations for it. Frames must be given in time order, as the live loop
    /// would see them; the tool uses this to make one ordered pass over a session and look the observations up per frame afterwards.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="priorScene">The scene to pass as "prior" (see <see cref="ClassifierFor"/>).</param>
    /// <param name="priorRole">The role to pass as "prior" (see <see cref="ClassifierFor"/>).</param>
    public IReadOnlyList<Observation> Observe(EvaluationFrame frame, Scene priorScene = Scene.Unknown, GameRole priorRole = GameRole.None)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Process(frame, priorScene, priorRole).Observations;
    }

    /// <summary>Gets the version of the detector that emits <paramref name="kind"/>, or null when this pipeline has none (for example generators).</summary>
    public int? VersionOf(ObservationKind kind) =>
        _pipeline.Detectors.FirstOrDefault(d => d.Id == Observation.DetectorOf(kind))?.Version;

    private HudFrame Process(EvaluationFrame frame, Scene prior, GameRole role)
    {
        if (ReferenceEquals(frame, _lastFrame) && prior == _lastPrior && role == _lastRole && _lastResult is not null)
        {
            return _lastResult;
        }

        _clock.Set(frame.TimestampUtc);
        var full = frame.Image.ToPixelBuffer();
        var regions = new Dictionary<string, PixelBuffer>(StringComparer.Ordinal);
        foreach (var request in _pipeline.Regions)
        {
            var region = new NormalizedRegion(request.Rect.X, request.Rect.Y, request.Rect.Width, request.Rect.Height);
            regions[request.Id] = full.Crop(region.ToPixels(full.Width, full.Height));
        }

        // Offline tool: the recogniser's task is awaited here on purpose (see the class remarks).
        var result = Task.Run(() => _pipeline.ProcessAsync(regions, full.Width, full.Height, _context, prior, CancellationToken.None, role)).GetAwaiter().GetResult();
        _lastFrame = frame;
        _lastPrior = prior;
        _lastRole = role;
        _lastResult = result;
        return result;
    }
}
