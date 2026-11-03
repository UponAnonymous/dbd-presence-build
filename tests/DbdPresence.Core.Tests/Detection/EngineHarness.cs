using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Core.Tests.Detection;

/// <summary>Drives a <see cref="MatchStateEngine"/> with scripted observations on a fake clock (1 s between frames).</summary>
internal sealed class EngineHarness
{
    public static readonly ContextKey Context = new("profile-1", "1920x1080", "en-US", "10.2.0");

    public EngineHarness(EngineOptions? options = null, IReadOnlyDictionary<DetectorId, DetectorSettings>? settings = null, bool running = true, GameMode mode = GameMode.Standard)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Engine = new MatchStateEngine(Time, options, settings);
        if (running)
        {
            Engine.SetGameState(GameState.Running);
        }

        Engine.SetDeclaredMode(mode);
    }

    public FakeTimeProvider Time { get; }

    public MatchStateEngine Engine { get; }

    public MatchSnapshot Snapshot => Engine.Snapshot;

    public DateTimeOffset Now => Time.GetUtcNow();

    public void Advance(TimeSpan span) => Time.Advance(span);

    public void Advance(double seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));

    /// <summary>Submits one observation stamped "now" (or <paramref name="at"/>) and then advances the clock one second.</summary>
    public SubmitResult Observe(ObservationKind kind, string? value, double score = 0.9, DateTimeOffset? at = null, ContextKey? context = null, bool advance = true)
    {
        var observation = new Observation(Observation.DetectorOf(kind), 1, at ?? Now, kind, value, score, "test", context ?? Context);
        var result = Engine.Submit(observation);
        if (advance)
        {
            Advance(1);
        }

        return result;
    }

    public SubmitResult SceneFrame(Scene scene, double score = 0.9) => Observe(ObservationKind.Scene, scene.ToString(), score);

    /// <summary>Confirms a scene with three agreeing frames (K=3, one second apart, dwell 2 s).</summary>
    public void ConfirmScene(Scene scene, int frames = 3)
    {
        for (var i = 0; i < frames; i++)
        {
            SceneFrame(scene);
        }
    }

    public void Frames(ObservationKind kind, string? value, int count, double score = 0.9)
    {
        for (var i = 0; i < count; i++)
        {
            Observe(kind, value, score);
        }
    }

    /// <summary>Reaches an open trial with Exact start: Loading, then InTrial.</summary>
    public void EnterTrial()
    {
        ConfirmScene(Scene.Loading);
        ConfirmScene(Scene.InTrial);
    }

    public static IReadOnlyDictionary<DetectorId, DetectorSettings> AllAt(DetectionTier tier, string version = "10.2.0") =>
        new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Scene] = Measured(tier, version),
            [DetectorId.Role] = Measured(tier, version),
            [DetectorId.Map] = Measured(tier, version),
            [DetectorId.Generators] = Measured(tier, version),
            [DetectorId.Character] = Measured(tier, version),
            [DetectorId.QueueMode] = Measured(tier, version),
            [DetectorId.Killer] = Measured(tier, version),
            [DetectorId.KillerPrompt] = Measured(tier, version),
        };

    public static DetectorSettings Measured(DetectionTier tier, string version = "10.2.0", double? baseline = null) =>
        new(tier, ThresholdProvenance.Measured, baseline, version, null);
}
