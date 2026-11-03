// Covers: C1 (confirmation, TTL), FR-DET-01 (engine alone confirms), FR-DET-05 (generators monotonic guard),
// FR-DET-06 (stale-value expiry with a fake clock), FR-DET-12 (mode gating), NFR-ACC-01 mechanics (never publish a guess).
// Mechanics only: scripted observation sequences, not measurements.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Detection;

public sealed class MatchStateEngineConfirmationTests
{
    [Fact]
    public void Scene_NeedsKAgreeingFrames_AndStartsUnknown()
    {
        var h = new EngineHarness();
        Assert.Equal(FieldState.Unknown, h.Snapshot.Scene.State);

        h.ConfirmScene(Scene.MainMenu, frames: 2);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Scene.State);
        Assert.Equal(GamePhase.Unknown, h.Snapshot.Phase);

        h.ConfirmScene(Scene.MainMenu, frames: 1);
        Assert.Equal(FieldState.Confirmed, h.Snapshot.Scene.State);
        Assert.Equal("MainMenu", h.Snapshot.Scene.ConfirmedValue);
        Assert.Equal(GamePhase.Menu, h.Snapshot.Phase);
    }

    [Fact]
    public void LowScore_BlocksConfirmation_UntilEveryAgreeingFrameIsAboveTheFloor()
    {
        var h = new EngineHarness();
        h.SceneFrame(Scene.Lobby, score: 0.9);
        h.SceneFrame(Scene.Lobby, score: 0.4); // below the 0.6 floor
        h.SceneFrame(Scene.Lobby, score: 0.9);
        h.SceneFrame(Scene.Lobby, score: 0.9);
        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene);

        // The weak frame ages out of the 4-frame window; then three strong frames confirm.
        h.SceneFrame(Scene.Lobby, score: 0.9);
        h.SceneFrame(Scene.Lobby, score: 0.9);
        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void Dwell_RequiresTheAgreeingFramesToSpanTheMinimumTime()
    {
        var h = new EngineHarness();
        for (var i = 0; i < 3; i++)
        {
            h.Observe(ObservationKind.Scene, "Lobby", advance: false);
            h.Advance(0.1); // frames only 100 ms apart
        }

        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene);

        h.Advance(2);
        h.Observe(ObservationKind.Scene, "Lobby");
        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void KOfN_AllowsOneStrayFrame_ButNotAnAlternatingStream()
    {
        var h = new EngineHarness();
        foreach (var scene in new[] { Scene.Lobby, Scene.Lobby, Scene.MainMenu, Scene.Lobby })
        {
            h.SceneFrame(scene);
        }

        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene); // 3 of the last 4, competitor 1 < K/2

        var alternating = new EngineHarness();
        for (var i = 0; i < 20; i++)
        {
            alternating.SceneFrame(i % 2 == 0 ? Scene.Lobby : Scene.MainMenu);
        }

        Assert.Equal(Scene.Unknown, alternating.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void SingleFrameSpike_NeverChangesAConfirmedScene()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);

        h.SceneFrame(Scene.MainMenu);
        h.SceneFrame(Scene.Lobby);
        h.SceneFrame(Scene.Lobby);

        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void Hysteresis_ChangingAConfirmedValueNeedsKNewAgreeingFrames()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);

        h.ConfirmScene(Scene.MainMenu, frames: 2);
        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);

        h.ConfirmScene(Scene.MainMenu, frames: 1);
        Assert.Equal(Scene.MainMenu, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void UnreadableFrames_NeitherConfirmNorRefute()
    {
        var h = new EngineHarness();
        h.SceneFrame(Scene.Lobby);
        h.SceneFrame(Scene.Lobby);
        h.Observe(ObservationKind.Scene, null);
        h.SceneFrame(Scene.Lobby);
        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);

        for (var i = 0; i < 5; i++)
        {
            h.Observe(ObservationKind.Scene, null);
        }

        Assert.Equal(Scene.Lobby, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void SceneLoss_MenuScenesFallToUnknownAfterTenSeconds_TrialScenesAfterThirty()
    {
        var menu = new EngineHarness();
        menu.ConfirmScene(Scene.MainMenu); // last supporting frame is 1 s before the clock
        menu.Advance(9);
        menu.Engine.Tick();
        Assert.Equal(Scene.MainMenu, menu.Snapshot.ConfirmedScene); // exactly 10 s since support: not yet lost

        menu.Advance(1.5);
        menu.Engine.Tick();
        Assert.Equal(Scene.Unknown, menu.Snapshot.ConfirmedScene);
        Assert.Equal(GamePhase.Unknown, menu.Snapshot.Phase);

        var trial = new EngineHarness();
        trial.EnterTrial();
        trial.Advance(20);
        trial.Engine.Tick();
        Assert.Equal(Scene.InTrial, trial.Snapshot.ConfirmedScene);

        trial.Advance(11);
        trial.Engine.Tick();
        Assert.Equal(Scene.Unknown, trial.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void SupportingFrames_KeepTheSceneAlive_BelowTheKOfNThreshold()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.MainMenu);

        for (var i = 0; i < 30; i++)
        {
            h.SceneFrame(Scene.MainMenu);
            h.Engine.Tick();
        }

        Assert.Equal(Scene.MainMenu, h.Snapshot.ConfirmedScene);
    }

    [Fact]
    public void Role_IsConfirmedOnlyInRoleBearingScenes()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.MainMenu);
        Assert.Equal(SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.Role, "Killer"));

        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Killer", 3);

        Assert.Equal(FieldState.Confirmed, h.Snapshot.Role.State);
        Assert.Equal(Role.Killer, h.Snapshot.DetectedRole);
        Assert.Equal(FieldSource.Detected, h.Snapshot.Role.Source);
    }

    [Fact]
    public void Role_ChangesOnlyAfterKNewFrames_AndExpiresOnMainMenu()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Survivor", 3);
        h.Frames(ObservationKind.Role, "Killer", 2);
        h.SceneFrame(Scene.Lobby);
        Assert.Equal(Role.Survivor, h.Snapshot.DetectedRole);

        h.Frames(ObservationKind.Role, "Killer", 3);
        Assert.Equal(Role.Killer, h.Snapshot.DetectedRole);

        h.ConfirmScene(Scene.MainMenu);
        Assert.Equal(Role.None, h.Snapshot.DetectedRole);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Role.State);
    }

    [Fact]
    public void DeclaredRoleAndCharacter_AreUserDeclaredFields_NotDetectedOnes()
    {
        var h = new EngineHarness();
        h.Engine.DeclareRole(Role.Killer);
        h.Engine.DeclareCharacter("  The Trapper ");

        Assert.Equal(FieldSource.UserDeclared, h.Snapshot.Role.Source);
        Assert.Equal("Killer", h.Snapshot.Role.ConfirmedValue);
        Assert.Equal(Role.None, h.Snapshot.DetectedRole); // detection and declaration stay separate
        Assert.Equal("The Trapper", h.Snapshot.Character.ConfirmedValue);

        h.Engine.DeclareCharacter(null);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Character.State);
    }

    [Fact]
    public void Role_ValueMustBeSurvivorOrKiller()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Role, "Spectator"));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Role, "1"));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Scene, "7"));
    }

    [Theory]
    [InlineData(GameMode.Standard, true)]
    [InlineData(GameMode.Custom, true)]
    [InlineData(GameMode.Unknown, false)]
    [InlineData(GameMode.TwoVersusEight, false)]
    [InlineData(GameMode.Other, false)]
    public void Generators_RunOnlyInStandardOrCustomMode(GameMode mode, bool enabled)
    {
        var h = new EngineHarness(mode: mode);
        h.EnterTrial();
        var result = h.Observe(ObservationKind.Generators, "4");
        h.Frames(ObservationKind.Generators, "4", 3);

        if (enabled)
        {
            Assert.Equal(SubmitResult.Accepted, result);
            Assert.Equal(4, h.Snapshot.ConfirmedGenerators);
        }
        else
        {
            Assert.Equal(SubmitResult.RejectedModeGate, result);
            Assert.Equal(FieldState.Disabled, h.Snapshot.Generators.State);
            Assert.Null(h.Snapshot.ConfirmedGenerators);
        }
    }

    [Fact]
    public void Generators_AreRejectedOutsideAnOpenTrial_AndOutOfRange()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        Assert.Equal(SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.Generators, "3"));

        h.ConfirmScene(Scene.Loading);
        h.ConfirmScene(Scene.InTrial);
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Generators, "6"));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Generators, "-1"));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Generators, "three"));
        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.Generators, "5"));
    }

    [Fact]
    public void Generators_MayOnlyDecrease_AnIncreaseNeedsDoubledEvidence()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Generators, "3", 3);
        Assert.Equal(3, h.Snapshot.ConfirmedGenerators);

        h.Frames(ObservationKind.Generators, "4", 5); // 2K - 1: not enough
        Assert.Equal(3, h.Snapshot.ConfirmedGenerators);
        Assert.Equal(0, h.Snapshot.Diagnostics.MonotonicViolations);

        h.Frames(ObservationKind.Generators, "4", 1); // 2K
        Assert.Equal(4, h.Snapshot.ConfirmedGenerators);
        Assert.Equal(1, h.Snapshot.Diagnostics.MonotonicViolations);

        h.Frames(ObservationKind.Generators, "2", 3); // a decrease needs only K
        Assert.Equal(2, h.Snapshot.ConfirmedGenerators);
        Assert.Equal(1, h.Snapshot.Diagnostics.MonotonicViolations);
    }

    [Fact]
    public void Generators_GoStaleAfterTheTtl_AndNeedReconfirmation()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Generators, "3", 3);
        Assert.Equal(3, h.Snapshot.ConfirmedGenerators);

        for (var i = 0; i < 28; i++)
        {
            h.SceneFrame(Scene.InTrial); // keep the scene alive; generators get no renewal
            h.Engine.Tick();
        }

        Assert.Equal(FieldState.Confirmed, h.Snapshot.Generators.State);

        for (var i = 0; i < 3; i++)
        {
            h.SceneFrame(Scene.InTrial);
        }

        h.Engine.Tick();
        Assert.Equal(FieldState.Stale, h.Snapshot.Generators.State);
        Assert.Null(h.Snapshot.ConfirmedGenerators);
        Assert.Equal("3", h.Snapshot.Generators.LastValue); // still visible to diagnostics, never to presence

        h.Frames(ObservationKind.Generators, "3", 3);
        Assert.Equal(3, h.Snapshot.ConfirmedGenerators);
    }

    [Fact]
    public void Generators_RenewingFramesKeepTheValueFresh()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Generators, "3", 3);

        for (var i = 0; i < 60; i++)
        {
            h.Observe(ObservationKind.Generators, "3");
            h.SceneFrame(Scene.InTrial);
            h.Engine.Tick();
        }

        Assert.Equal(3, h.Snapshot.ConfirmedGenerators);
    }

    [Fact]
    public void SceneLoss_MarksSessionFieldsStale_AndRecoveryRequiresReconfirmation()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Map, "coldwind-farm/rotten-fields", 3);
        Assert.Equal("coldwind-farm/rotten-fields", h.Snapshot.Map.ConfirmedValue);

        h.Advance(31);
        h.Engine.Tick();
        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene);
        Assert.Equal(FieldState.Stale, h.Snapshot.Map.State);
        Assert.Null(h.Snapshot.Map.ConfirmedValue);
        Assert.NotNull(h.Snapshot.Session); // the session survives a lost scene

        h.ConfirmScene(Scene.InTrial);
        Assert.Equal(FieldState.Stale, h.Snapshot.Map.State); // not shown again until re-confirmed
        h.Frames(ObservationKind.Map, "coldwind-farm/rotten-fields", 3);
        Assert.Equal("coldwind-farm/rotten-fields", h.Snapshot.Map.ConfirmedValue);
    }

    [Fact]
    public void Map_NeedsAnOpenTrialAndAnObservationNotOlderThanTheSession()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Loading);
        Assert.Equal(SubmitResult.RejectedSceneGate, h.Observe(ObservationKind.Map, "ormond/mount-ormond-resort"));

        h.ConfirmScene(Scene.InTrial);
        var started = h.Snapshot.Session!.StartedUtc;
        Assert.Equal(SubmitResult.RejectedNoSession, h.Observe(ObservationKind.Map, "ormond/mount-ormond-resort", at: started - TimeSpan.FromSeconds(0.5)));
        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.Map, "ormond/mount-ormond-resort"));
    }

    [Fact]
    public void Map_IdsOutsideTheKnownVocabularyAreRejected_WhenAValidatorIsConfigured()
    {
        var h = new EngineHarness(new EngineOptions { IsKnownMapId = id => id == "ormond/ormond-lake-mine" });
        h.EnterTrial();

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Map, "ormond/made-up"));
        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.Map, "ormond/ormond-lake-mine"));
    }

    [Fact]
    public void Pause_MarksFieldsStaleAtOnce_HoldsTheScene_AndKeepsTheSession()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Survivor", 3);
        h.ConfirmScene(Scene.Loading);
        h.ConfirmScene(Scene.InTrial);
        h.Frames(ObservationKind.Map, "ormond/ormond-lake-mine", 3);
        var session = h.Snapshot.Session!.Number;

        h.Engine.SetCapturePaused(true);
        Assert.Equal(FieldState.Stale, h.Snapshot.Map.State);
        Assert.Equal(FieldState.Stale, h.Snapshot.Role.State);
        Assert.Equal(Scene.InTrial, h.Snapshot.ConfirmedScene); // held
        Assert.True(h.Snapshot.CapturePaused);
        Assert.Equal(SubmitResult.IgnoredPaused, h.Observe(ObservationKind.Scene, "InTrial"));

        h.Advance(58);
        h.Engine.Tick();
        Assert.Equal(Scene.InTrial, h.Snapshot.ConfirmedScene);

        h.Advance(2);
        h.Engine.Tick();
        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene); // T_hold = 60 s
        Assert.Equal(session, h.Snapshot.Session!.Number);
    }

    [Fact]
    public void Resume_GivesTheHeldSceneAGracePeriod_ButFieldsStayHiddenUntilReconfirmed()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Map, "ormond/ormond-lake-mine", 3);

        h.Engine.SetCapturePaused(true);
        h.Advance(30);
        h.Engine.SetCapturePaused(false);
        h.Engine.Tick();

        Assert.Equal(Scene.InTrial, h.Snapshot.ConfirmedScene);
        Assert.Equal(FieldState.Stale, h.Snapshot.Map.State);

        h.Frames(ObservationKind.Map, "ormond/ormond-lake-mine", 3);
        Assert.Equal(FieldState.Confirmed, h.Snapshot.Map.State);
    }

    [Fact]
    public void Observations_AreIgnoredWhileTheGameIsNotRunning()
    {
        var h = new EngineHarness(running: false);
        Assert.Equal(SubmitResult.IgnoredNotRunning, h.SceneFrame(Scene.MainMenu));
        Assert.Equal(GamePhase.NotRunning, h.Snapshot.Phase);

        h.Engine.SetGameState(GameState.Launching);
        Assert.Equal(GamePhase.Launching, h.Snapshot.Phase);
        Assert.Equal(SubmitResult.IgnoredNotRunning, h.SceneFrame(Scene.MainMenu));

        h.Engine.SetGameState(GameState.Running);
        h.ConfirmScene(Scene.MainMenu);
        Assert.Equal(GamePhase.Menu, h.Snapshot.Phase);
    }

    [Fact]
    public void Observations_AreRejected_WhenMalformedLateDuplicatedOrFromTheFuture()
    {
        var h = new EngineHarness();
        var now = h.Now;

        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Scene, "Lobby", score: double.NaN, advance: false));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Scene, "Lobby", score: 1.5, advance: false));
        Assert.Equal(SubmitResult.RejectedInvalid, h.Observe(ObservationKind.Scene, "Lobby", score: -0.1, advance: false));
        Assert.Equal(SubmitResult.RejectedTooOld, h.Observe(ObservationKind.Scene, "Lobby", at: now - TimeSpan.FromSeconds(11), advance: false));
        Assert.Equal(SubmitResult.RejectedFromFuture, h.Observe(ObservationKind.Scene, "Lobby", at: now + TimeSpan.FromSeconds(3), advance: false));

        Assert.Equal(SubmitResult.Accepted, h.Observe(ObservationKind.Scene, "Lobby", advance: false));
        Assert.Equal(SubmitResult.RejectedNotNewer, h.Observe(ObservationKind.Scene, "Lobby", advance: false)); // duplicate timestamp
        Assert.Equal(SubmitResult.RejectedNotNewer, h.Observe(ObservationKind.Scene, "Lobby", at: now - TimeSpan.FromSeconds(1), advance: false)); // late
    }

    [Fact]
    public void Observations_FromDetectorsWithoutEngineSupport_OrWithAMismatchedKind_AreRejected()
    {
        var h = new EngineHarness();
        var stageD = new Observation((DetectorId)999, 1, h.Now, ObservationKind.Scene, "Lobby", 0.9, "x", EngineHarness.Context);
        var wrongKind = new Observation(DetectorId.Role, 1, h.Now, ObservationKind.Map, "a/b", 0.9, "x", EngineHarness.Context);

        Assert.Equal(SubmitResult.RejectedInvalid, h.Engine.Submit(stageD));
        Assert.Equal(SubmitResult.RejectedKindMismatch, h.Engine.Submit(wrongKind));
        Assert.Throws<ArgumentNullException>(() => h.Engine.Submit(null!));
    }

    [Fact]
    public void SnapshotChanged_FiresOnVisibleChangesOnly_WithIncreasingSequence()
    {
        var h = new EngineHarness();
        var seen = new List<MatchSnapshot>();
        h.Engine.SnapshotChanged += (_, s) => seen.Add(s);

        h.ConfirmScene(Scene.MainMenu);
        var count = seen.Count;
        Assert.True(count >= 1);
        Assert.Equal(Scene.MainMenu, seen[^1].ConfirmedScene);

        // A late observation is rejected and a tick with nothing to do changes nothing visible.
        Assert.Equal(SubmitResult.RejectedNotNewer, h.Observe(ObservationKind.Scene, "Lobby", at: h.Now - TimeSpan.FromSeconds(5)));
        h.Engine.Tick();
        Assert.Equal(count, seen.Count);

        h.ConfirmScene(Scene.Lobby);
        Assert.True(seen.Count > count);
        Assert.All(seen.Zip(seen.Skip(1)), pair => Assert.True(pair.Second.Sequence > pair.First.Sequence));
        Assert.Equal(Scene.Lobby, seen[^1].ConfirmedScene);
    }

    [Fact]
    public void Constructor_RejectsInvalidOptionsAndTiersWithoutEvidence()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchStateEngine(time, new EngineOptions { Scene = new ConfirmationRule(5, 4, TimeSpan.Zero, 0.5, TimeSpan.FromSeconds(5)) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchStateEngine(time, new EngineOptions { MaxGenerators = 0 }));
        Assert.Throws<ArgumentException>(() => new MatchStateEngine(time, null, new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Map] = new(DetectionTier.Validated), // initial-guess thresholds cannot carry a tier
        }));
        Assert.Throws<ArgumentException>(() => new MatchStateEngine(time, null, new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Map] = new(DetectionTier.Experimental, ThresholdProvenance.Measured), // no validated game version
        }));
        Assert.Null(EngineHarness.Measured(DetectionTier.Experimental).Problem());
        Assert.Null(DetectorSettings.Default.Problem());
        Assert.Equal(DetectionTier.Disabled, DetectorSettings.Default.Tier);
        Assert.Equal(ThresholdProvenance.InitialGuess, DetectorSettings.Default.Provenance);
    }
}
