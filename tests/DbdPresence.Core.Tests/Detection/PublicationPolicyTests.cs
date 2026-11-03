// Covers: C1 (tier controls publication), FR-DET-01 (detectors never publish), FR-DET-10 (degraded fields are
// hidden), FR-DET-17 (experimental opt-in and beta marker), FR-PACK-06 / D16 (map only when Confirmed),
// NFR-ACC-01 mechanics (a Disabled or initial-guess detector never reaches Discord).
// Mechanics only: scripted observation sequences, not measurements.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Detection;

public sealed class PublicationPolicyTests
{
    private const string MapA = "coldwind-farm/rotten-fields";

    private sealed class FakeMaps : IMapNameResolver
    {
        public MapNames? Resolve(string mapId) => mapId == MapA ? new MapNames("Rotten Fields", "Coldwind Farm") : null;
    }

    /// <summary>Runs a survivor trial to the point where scene, role, map and generators are all confirmed in the engine.</summary>
    private static EngineHarness FullyConfirmedTrial(IReadOnlyDictionary<DetectorId, DetectorSettings>? settings = null, string map = MapA)
    {
        var h = new EngineHarness(settings: settings);
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Survivor", 3);
        h.EnterTrial();
        h.Frames(ObservationKind.Map, map, 3);
        h.Frames(ObservationKind.Generators, "3", 3);
        h.SceneFrame(Scene.InTrial);
        return h;
    }

    private static PublicationResult Compose(EngineHarness h, IReadOnlyDictionary<DetectorId, DetectorSettings> settings, bool optIn = false, IMapNameResolver? maps = null) =>
        PublicationPolicy.Compose(h.Snapshot, settings, new PublicationOptions(optIn), maps ?? new FakeMaps(), h.Now, h.Now.AddHours(-1));

    [Fact]
    public void EveryDetectorDefaultsToDisabled_AndNothingDetectedReachesDiscord()
    {
        var h = FullyConfirmedTrial();
        Assert.Equal(FieldState.Confirmed, h.Snapshot.Map.State); // the engine did confirm everything...

        var result = Compose(h, new Dictionary<DetectorId, DetectorSettings>());

        var inputs = result.Inputs; // ...and none of it is publishable
        Assert.Equal(PresenceState.Running, inputs.State);
        Assert.Equal(Role.None, inputs.DetectedRole);
        Assert.Null(inputs.MapName);
        Assert.Null(inputs.MapId);
        Assert.Null(inputs.RealmName);
        Assert.Null(inputs.GeneratorsRemaining);
        Assert.Null(inputs.MatchStartedUtc);
        Assert.Empty(result.BetaFields);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.Map && s.Reason.Contains("Disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatedDetectors_PublishSceneRoleMapAndGenerators()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = FullyConfirmedTrial(settings);

        var result = Compose(h, settings);

        Assert.Equal(PresenceState.InTrialSurvivor, result.Inputs.State);
        Assert.Equal(Role.Survivor, result.Inputs.DetectedRole);
        Assert.Equal("Rotten Fields", result.Inputs.MapName);
        Assert.Equal(MapA, result.Inputs.MapId);
        Assert.Equal("Coldwind Farm", result.Inputs.RealmName);
        Assert.Equal(3, result.Inputs.GeneratorsRemaining);
        Assert.Equal(h.Snapshot.Session!.StartedUtc, result.Inputs.MatchStartedUtc);
        Assert.False(result.Inputs.MatchStartApproximate);
        Assert.Empty(result.BetaFields);
        Assert.Empty(result.Suppressed);
    }

    [Fact]
    public void EachDetectorIsGatedOnItsOwnTier()
    {
        var settings = new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Scene] = EngineHarness.Measured(DetectionTier.Validated),
            [DetectorId.Role] = EngineHarness.Measured(DetectionTier.Validated),
        };
        var h = FullyConfirmedTrial(settings);

        var inputs = Compose(h, settings).Inputs;

        Assert.Equal(PresenceState.InTrialSurvivor, inputs.State);
        Assert.Null(inputs.MapName); // map detector is Disabled
        Assert.Null(inputs.GeneratorsRemaining); // generators detector is Disabled
    }

    [Fact]
    public void InitialGuessThresholds_NeverPublish_EvenAtAHighTier()
    {
        var settings = new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Map] = new(DetectionTier.Validated, ThresholdProvenance.InitialGuess, null, "10.2.0", null),
        };
        var h = FullyConfirmedTrial();

        var result = Compose(h, settings);

        Assert.Null(result.Inputs.MapName);
        Assert.Contains(result.Suppressed, s => s.Reason.Contains("initial guesses", StringComparison.Ordinal));
    }

    [Fact]
    public void Experimental_PublishesOnlyWithTheSessionOptIn_AndMarksTheFieldsBeta()
    {
        var settings = new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Scene] = EngineHarness.Measured(DetectionTier.Validated),
            [DetectorId.Map] = EngineHarness.Measured(DetectionTier.Experimental),
        };
        var h = FullyConfirmedTrial(settings);

        var off = Compose(h, settings, optIn: false);
        Assert.Null(off.Inputs.MapName);
        Assert.Empty(off.BetaFields);

        var on = Compose(h, settings, optIn: true);
        Assert.Equal("Rotten Fields", on.Inputs.MapName);
        Assert.Equal([FieldKind.Map], on.BetaFields);
    }

    [Fact]
    public void Map_IsPublishedOnlyWhenConfirmed_AndOnlyWhenItResolvesInTheVocabulary()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);

        // Not confirmed yet (only two agreeing frames).
        var early = new EngineHarness(settings: settings);
        early.EnterTrial();
        early.Frames(ObservationKind.Map, MapA, 2);
        Assert.Null(Compose(early, settings).Inputs.MapName);

        // Confirmed but unknown to the vocabulary: held back with a reason.
        var unknown = FullyConfirmedTrial(settings, map: "ormond/not-in-the-list");
        var result = Compose(unknown, settings);
        Assert.Null(result.Inputs.MapName);
        Assert.Null(result.Inputs.MapId);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.Map && s.Reason.Contains("vocabulary", StringComparison.Ordinal));

        // No resolver at all: maps are never published.
        var noResolver = FullyConfirmedTrial(settings);
        var none = PublicationPolicy.Compose(noResolver.Snapshot, settings, new PublicationOptions(), null, noResolver.Now, null);
        Assert.Null(none.Inputs.MapName);
    }

    [Fact]
    public void StaleOrDegradedFields_AreNeverPublished_AndTheReasonIsReported()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = FullyConfirmedTrial(settings);
        h.Engine.SetCapturePaused(true);

        var result = Compose(h, settings);

        Assert.Null(result.Inputs.MapName);
        Assert.Null(result.Inputs.GeneratorsRemaining);
        Assert.Contains(result.Suppressed, s => s.Field == FieldKind.Map && s.Reason == "capture paused");

        var degraded = new Dictionary<DetectorId, DetectorSettings>(settings)
        {
            [DetectorId.Map] = EngineHarness.Measured(DetectionTier.Validated, version: "10.9.9"), // validated on another game version
        };
        var h2 = FullyConfirmedTrial(degraded);
        var result2 = Compose(h2, degraded);
        Assert.Null(result2.Inputs.MapName);
        Assert.Equal(FieldState.Degraded, h2.Snapshot.Map.State);
        Assert.Equal(3, result2.Inputs.GeneratorsRemaining); // other detectors are unaffected
    }

    [Fact]
    public void PostMatch_ScenesMayUseTheLastSessionMap_ButNewTrialsNever()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = FullyConfirmedTrial(settings);
        h.ConfirmScene(Scene.Results);

        Assert.Equal("Rotten Fields", Compose(h, settings).Inputs.MapName);
        Assert.Null(Compose(h, settings).Inputs.MatchStartedUtc);

        h.ConfirmScene(Scene.Lobby);
        Assert.Equal("Rotten Fields", Compose(h, settings).Inputs.MapName);

        h.ConfirmScene(Scene.Loading);
        h.ConfirmScene(Scene.InTrial);
        Assert.Null(Compose(h, settings).Inputs.MapName);
    }

    [Fact]
    public void GeneratorsAndMap_AreHiddenWhenTheModeIsNotSupported_OrWhileSpectating()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var twoVersusEight = new EngineHarness(settings: settings, mode: GameMode.TwoVersusEight);
        twoVersusEight.EnterTrial();
        twoVersusEight.Frames(ObservationKind.Generators, "3", 3);
        Assert.Null(Compose(twoVersusEight, settings).Inputs.GeneratorsRemaining);

        var h = FullyConfirmedTrial(settings);
        h.ConfirmScene(Scene.Spectating);
        var inputs = Compose(h, settings).Inputs;
        Assert.Null(inputs.GeneratorsRemaining);
        Assert.Equal(PresenceState.InTrialSurvivor, inputs.State);
    }

    [Fact]
    public void ApproximateStart_IsFlagged_WhenTheTrialWasJoinedLate()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);
        h.ConfirmScene(Scene.InTrial);

        var inputs = Compose(h, settings).Inputs;

        Assert.True(inputs.MatchStartApproximate);
        Assert.NotNull(inputs.MatchStartedUtc);
    }

    [Fact]
    public void DeclaredValues_AreNotForwardedAsDetectedOnes()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);
        h.Engine.DeclareRole(Role.Killer);
        h.ConfirmScene(Scene.Lobby);

        var inputs = Compose(h, settings).Inputs;

        Assert.Equal(PresenceState.Lobby, inputs.State);
        Assert.Equal(Role.None, inputs.DetectedRole); // manual role is applied by the composer from the user's settings
    }

    [Theory]
    [InlineData(GameState.NotRunning, PresenceState.NotRunning)]
    [InlineData(GameState.Launching, PresenceState.Launching)]
    [InlineData(GameState.Running, PresenceState.Running)]
    public void ProcessState_DecidesTheBaseState(GameState state, PresenceState expected)
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings, running: false);
        h.Engine.SetGameState(state);

        Assert.Equal(expected, Compose(h, settings).Inputs.State);
    }

    [Theory]
    [InlineData(Scene.MainMenu, PresenceState.MainMenu)]
    [InlineData(Scene.Matchmaking, PresenceState.Matchmaking)]
    [InlineData(Scene.Lobby, PresenceState.Lobby)]
    [InlineData(Scene.Loading, PresenceState.Loading)]
    [InlineData(Scene.InTrial, PresenceState.InTrial)]
    [InlineData(Scene.Spectating, PresenceState.InTrial)]
    [InlineData(Scene.Results, PresenceState.Results)]
    [InlineData(Scene.Other, PresenceState.Running)]
    public void ConfirmedScene_MapsToThePresenceState_WhenTheRoleIsUnknown(Scene scene, PresenceState expected)
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);
        h.ConfirmScene(scene);

        Assert.Equal(expected, Compose(h, settings).Inputs.State);
    }

    [Fact]
    public void ConfirmedRole_SelectsTheRoleSpecificLobbyState()
    {
        var settings = EngineHarness.AllAt(DetectionTier.Validated);
        var h = new EngineHarness(settings: settings);
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Killer", 3);

        Assert.Equal(PresenceState.LobbyKiller, Compose(h, settings).Inputs.State);
    }
}
