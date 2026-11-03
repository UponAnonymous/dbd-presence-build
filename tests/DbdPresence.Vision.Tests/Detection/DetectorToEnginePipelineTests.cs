// Covers: FR-DET-01 (detectors emit observations only; the engine alone confirms; tiers decide what is published), C1/C2 integration.
// MECHANICS ONLY: synthetic frames through the real detector, engine and publication policy on a fake clock. Not accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Detection;

public sealed class DetectorToEnginePipelineTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly PhaseDetector _detector = new(SyntheticScenes.CalibrateScenes());

    private MatchStateEngine NewEngine(IReadOnlyDictionary<DetectorId, DetectorSettings>? settings = null)
    {
        var engine = new MatchStateEngine(_time, null, settings);
        engine.SetGameState(GameState.Running);
        engine.SetDeclaredMode(GameMode.Standard);
        return engine;
    }

    private void Feed(MatchStateEngine engine, SceneKind scene, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            var context = new DetectionContext(SyntheticScenes.Context(), _time.GetUtcNow());
            var crops = FrameCrops.FromFrame(SyntheticScenes.Render(scene, 640, seed: i + 1), _detector.Regions(context));
            foreach (var observation in _detector.Analyze(crops, context).Observations)
            {
                engine.Submit(observation);
            }

            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void FramesIntoDetectorIntoEngine_ConfirmTheScene_ButADisabledDetectorNeverPublishes()
    {
        var engine = NewEngine();

        Feed(engine, SceneKind.InTrial, 3);

        Assert.Equal(SceneKind.InTrial, engine.Snapshot.ConfirmedScene); // confirmed inside the engine, for in-app diagnostics
        Assert.Equal(DetectorId.Scene, _detector.Id);
        var result = PublicationPolicy.Compose(engine.Snapshot, new Dictionary<DetectorId, DetectorSettings>(), new PublicationOptions(), null, _time.GetUtcNow(), null);
        Assert.Equal(PresenceState.Running, result.Inputs.State); // nothing reaches Discord
        Assert.Null(result.Inputs.MatchStartedUtc);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.Scene);
    }

    [Fact]
    public void AValidatedSceneDetector_PublishesTheConfirmedScene_AndOnlyAfterTheEngineConfirmsIt()
    {
        var settings = ValidatedSceneSettings();
        var engine = NewEngine(settings);

        Feed(engine, SceneKind.Results, 2);
        Assert.Equal(PresenceState.Running, Publish(engine, settings)); // two frames are not a confirmation

        Feed(engine, SceneKind.Results, 1);
        Assert.Equal(PresenceState.Results, Publish(engine, settings));
    }

    [Fact]
    public void AnUnreadableFrame_NeverChangesWhatIsPublished()
    {
        var settings = ValidatedSceneSettings();
        var engine = NewEngine(settings);
        Feed(engine, SceneKind.InTrial, 3);

        Feed(engine, SceneKind.MainMenu, 4); // no template knows this screen: the detector abstains on every frame

        Assert.Equal(SceneKind.InTrial, engine.Snapshot.ConfirmedScene);
        Assert.Equal(PresenceState.InTrial, Publish(engine, settings));
    }

    [Fact]
    public void AnIllegalJump_NeedsDoubledEvidence_AndIsCounted()
    {
        var engine = NewEngine();
        Feed(engine, SceneKind.InTrial, 3);
        Assert.NotNull(engine.Snapshot.Session);

        Feed(engine, SceneKind.Lobby, 3); // InTrial -> Lobby skips Results and the menu: not in the transition graph
        Assert.Equal(SceneKind.InTrial, engine.Snapshot.ConfirmedScene);

        Feed(engine, SceneKind.Lobby, 3);
        Assert.Equal(SceneKind.Lobby, engine.Snapshot.ConfirmedScene);
        Assert.Equal(1, engine.Snapshot.Diagnostics.IllegalTransitions);
        Assert.Null(engine.Snapshot.Session);
    }

    private static Dictionary<DetectorId, DetectorSettings> ValidatedSceneSettings() => new()
    {
        [DetectorId.Scene] = new(DetectionTier.Validated, ThresholdProvenance.Measured, null, "synthetic", null),
    };

    private PresenceState Publish(MatchStateEngine engine, IReadOnlyDictionary<DetectorId, DetectorSettings> settings) =>
        PublicationPolicy.Compose(engine.Snapshot, settings, new PublicationOptions(), null, _time.GetUtcNow(), null).Inputs.State;
}
