// Covers: C1 (sessions, health), FR-DET-07 (a new trial resets all match fields), FR-DET-08 (start quality, alt-tab
// never resets the timer), FR-DET-10 (detector health), NFR-ACC-03 (no value from a previous match in the next one),
// replay scenario S-05 (back-to-back trials) and the adversarial streams of DETECTION_VALIDATION §11.
// Mechanics only: scripted observation sequences, not measurements.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;

namespace DbdPresence.Core.Tests.Detection;

public sealed class MatchStateEngineSessionTests
{
    private const string MapA = "coldwind-farm/rotten-fields";
    private const string MapB = "ormond/ormond-lake-mine";

    [Fact]
    public void LoadingThenTrial_OpensAnExactSession_StartingAtTheFirstConfirmingFrame()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Loading);
        var firstTrialFrame = h.Now;
        h.ConfirmScene(Scene.InTrial);

        var session = h.Snapshot.Session;
        Assert.NotNull(session);
        Assert.Equal(StartQuality.Exact, session.Quality);
        Assert.Equal(firstTrialFrame, session.StartedUtc);
        Assert.Equal(GameMode.Standard, session.Mode);
    }

    [Fact]
    public void TrialWithoutAPrecedingLoading_IsJoinedLate()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(StartQuality.JoinedLate, h.Snapshot.Session!.Quality);
    }

    [Fact]
    public void LoadingMoreThanSixtySecondsBeforeTheTrial_IsJoinedLate()
    {
        var h = new EngineHarness(new EngineOptions { SceneLostMenu = TimeSpan.FromMinutes(10) });
        h.ConfirmScene(Scene.Loading);
        h.Advance(90);
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(StartQuality.JoinedLate, h.Snapshot.Session!.Quality);
    }

    [Fact]
    public void RecoveryAfterUnknown_IsJoinedLate_ButKeepsTheOpenSession()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        var number = h.Snapshot.Session!.Number;
        var started = h.Snapshot.Session.StartedUtc;

        h.Advance(40);
        h.Engine.Tick();
        Assert.Equal(Scene.Unknown, h.Snapshot.ConfirmedScene);
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(number, h.Snapshot.Session!.Number);
        Assert.Equal(started, h.Snapshot.Session.StartedUtc);
    }

    [Fact]
    public void Results_EndSessionAndResetMatchFields_ButKeepASummary()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Killer", 3);
        h.EnterTrial();
        h.Frames(ObservationKind.Map, MapA, 3);
        h.Frames(ObservationKind.Generators, "2", 3);

        h.ConfirmScene(Scene.Results);

        var s = h.Snapshot;
        Assert.Null(s.Session);
        Assert.Equal(FieldState.Unknown, s.Map.State);
        Assert.Equal(FieldState.Unknown, s.Generators.State);
        Assert.Equal(Role.Killer, s.DetectedRole); // role is lobby-scoped, not session-scoped
        Assert.Equal(new LastSessionSummary(1, Role.Killer, MapA, s.LastSession!.EndedUtc), s.LastSession);
    }

    [Fact]
    public void LastSessionSummary_IsClearedOnMatchmaking_OnLoading_AndAfterFifteenMinutes()
    {
        LastSessionSummary? SummaryAfter(Action<EngineHarness> act)
        {
            var h = new EngineHarness(new EngineOptions { SceneLostMenu = TimeSpan.FromHours(1) });
            h.EnterTrial();
            h.Frames(ObservationKind.Map, MapA, 3);
            h.ConfirmScene(Scene.Results);
            Assert.NotNull(h.Snapshot.LastSession);
            act(h);
            return h.Snapshot.LastSession;
        }

        Assert.NotNull(SummaryAfter(h => h.ConfirmScene(Scene.Lobby))); // Lobby and MainMenu may still use {map}
        Assert.Null(SummaryAfter(h =>
        {
            h.ConfirmScene(Scene.Lobby);
            h.ConfirmScene(Scene.Matchmaking);
        }));
        Assert.Null(SummaryAfter(h =>
        {
            h.ConfirmScene(Scene.Lobby);
            h.ConfirmScene(Scene.Loading);
        }));
        Assert.Null(SummaryAfter(h =>
        {
            h.Advance(TimeSpan.FromMinutes(15));
            h.Engine.Tick();
        }));
    }

    [Fact]
    public void BackToBackTrials_NeverCarryAValueFromTheFirstIntoTheSecond()
    {
        // Scenario S-05 / NFR-ACC-03.
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Survivor", 3);
        h.EnterTrial();
        h.Frames(ObservationKind.Map, MapA, 3);
        h.Frames(ObservationKind.Generators, "1", 3);
        var first = h.Snapshot.Session!.Number;

        h.ConfirmScene(Scene.Results);
        h.ConfirmScene(Scene.Lobby);
        h.ConfirmScene(Scene.Loading);
        Assert.Null(h.Snapshot.LastSession);
        h.ConfirmScene(Scene.InTrial);

        var second = h.Snapshot.Session!;
        Assert.NotEqual(first, second.Number);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Map.State);
        Assert.Null(h.Snapshot.Map.LastValue);
        Assert.Equal(FieldState.Unknown, h.Snapshot.Generators.State);
        Assert.Null(h.Snapshot.Generators.LastValue);

        // A straggler from the first trial (older than the second session) cannot attach.
        Assert.Equal(SubmitResult.RejectedNoSession, h.Observe(ObservationKind.Map, MapA, at: second.StartedUtc - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DifferentMapInsideOneSession_IsAnUndetectedBoundary_AndStartsAFreshJoinedLateSession()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        h.Frames(ObservationKind.Map, MapA, 3);
        h.Frames(ObservationKind.Generators, "2", 3);
        var first = h.Snapshot.Session!.Number;

        h.SceneFrame(Scene.InTrial);
        h.Frames(ObservationKind.Map, MapB, 3);

        var s = h.Snapshot;
        Assert.Equal(MapB, s.Map.ConfirmedValue);
        Assert.NotEqual(first, s.Session!.Number);
        Assert.Equal(StartQuality.JoinedLate, s.Session.Quality);
        Assert.Equal(FieldState.Unknown, s.Generators.State); // everything else is reset
        Assert.Equal(1, s.Diagnostics.UndetectedBoundaries);
        Assert.Equal(MapA, s.LastSession!.MapId);
    }

    [Fact]
    public void AltTab_DoesNotResetTheSessionTimer()
    {
        var h = new EngineHarness();
        h.EnterTrial();
        var started = h.Snapshot.Session!.StartedUtc;

        h.Engine.SetCapturePaused(true);
        h.Advance(45);
        h.Engine.SetCapturePaused(false);
        h.ConfirmScene(Scene.InTrial);

        Assert.Equal(started, h.Snapshot.Session!.StartedUtc);
        Assert.Equal(1, h.Snapshot.Session.Number);
    }

    [Fact]
    public void SessionEnds_AfterTenMinutesOfPause_AndAfterTenMinutesOfUnknown()
    {
        var paused = new EngineHarness();
        paused.EnterTrial();
        paused.Engine.SetCapturePaused(true);
        paused.Advance(TimeSpan.FromMinutes(9));
        paused.Engine.Tick();
        Assert.NotNull(paused.Snapshot.Session);
        paused.Advance(TimeSpan.FromMinutes(1.5));
        paused.Engine.Tick();
        Assert.Null(paused.Snapshot.Session);

        var unknown = new EngineHarness();
        unknown.EnterTrial();
        unknown.Advance(40); // scene lost after 30 s of silence
        unknown.Engine.Tick();
        Assert.Equal(Scene.Unknown, unknown.Snapshot.ConfirmedScene);
        unknown.Advance(TimeSpan.FromMinutes(9));
        unknown.Engine.Tick();
        Assert.NotNull(unknown.Snapshot.Session);
        unknown.Advance(TimeSpan.FromMinutes(2));
        unknown.Engine.Tick();
        Assert.Null(unknown.Snapshot.Session);
    }

    [Fact]
    public void GameExit_ClearsEverythingDetected_ButKeepsUserDeclarations()
    {
        var h = new EngineHarness();
        h.Engine.DeclareCharacter("Dwight Fairfield");
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Survivor", 3);
        h.EnterTrial();
        h.Frames(ObservationKind.Map, MapA, 3);
        h.ConfirmScene(Scene.Results);

        h.Engine.SetGameState(GameState.NotRunning);

        var s = h.Snapshot;
        Assert.Equal(GamePhase.NotRunning, s.Phase);
        Assert.Null(s.Session);
        Assert.Null(s.LastSession);
        Assert.Equal(FieldState.Unknown, s.Scene.State);
        Assert.Equal(FieldState.Unknown, s.Role.State);
        Assert.Equal(FieldState.Unknown, s.Map.State);
        Assert.Equal("Dwight Fairfield", s.Character.ConfirmedValue);
        Assert.Equal("Standard", s.Mode.ConfirmedValue);
    }

    [Fact]
    public void LateStragglers_AndSingleFrameSpikes_DoNotCreateOrChangeAnything()
    {
        // Adversarial stream: late duplicate, spike, alternating values and an illegal jump in one run.
        var h = new EngineHarness();
        h.ConfirmScene(Scene.MainMenu);

        h.SceneFrame(Scene.InTrial); // single-frame spike toward a trial
        h.SceneFrame(Scene.MainMenu);
        h.SceneFrame(Scene.InTrial);
        h.SceneFrame(Scene.MainMenu);
        Assert.Equal(Scene.MainMenu, h.Snapshot.ConfirmedScene);
        Assert.Null(h.Snapshot.Session);

        Assert.Equal(SubmitResult.RejectedNotNewer, h.Observe(ObservationKind.Scene, "InTrial", at: h.Now - TimeSpan.FromSeconds(3)));
        Assert.Equal(0, h.Snapshot.Diagnostics.IllegalTransitions);
    }

    [Fact]
    public void Health_StartsWithNoData_AndIsHealthyWhileObservationsArrive()
    {
        var h = new EngineHarness();
        Assert.All(h.Snapshot.Health.Values, health => Assert.Equal(HealthState.NoData, health.State));

        h.ConfirmScene(Scene.MainMenu);

        Assert.Equal(HealthState.Healthy, h.Snapshot.Health[DetectorId.Scene].State);
        Assert.Equal(HealthState.NoData, h.Snapshot.Health[DetectorId.Map].State);
    }

    [Fact]
    public void Health_DegradesWhenTheUnreadableRateExceedsThreeTimesTheBaseline_AndHidesTheField()
    {
        var settings = new Dictionary<DetectorId, DetectorSettings> { [DetectorId.Scene] = EngineHarness.Measured(DetectionTier.Experimental, baseline: 0.10) };
        var h = new EngineHarness(new EngineOptions { SceneLostMenu = TimeSpan.FromMinutes(5) }, settings);
        h.ConfirmScene(Scene.MainMenu);
        Assert.Equal(FieldState.Confirmed, h.Snapshot.Scene.State);

        // 20 samples are needed before the rate rule applies; 3 x 10% = 30% is the limit.
        for (var i = 0; i < 17; i++)
        {
            h.SceneFrame(Scene.MainMenu);
        }

        Assert.Equal(HealthState.Healthy, h.Snapshot.Health[DetectorId.Scene].State); // 20 samples, 0% unreadable
        for (var i = 0; i < 12; i++)
        {
            h.Observe(ObservationKind.Scene, null); // 12 unreadable of 32 = 37.5% > 30%
        }

        var health = h.Snapshot.Health[DetectorId.Scene];
        Assert.Equal(HealthState.Degraded, health.State);
        Assert.Contains("baseline", health.Reason);
        Assert.Equal(FieldState.Degraded, h.Snapshot.Scene.State);
        Assert.Null(h.Snapshot.Scene.ConfirmedValue);
    }

    [Fact]
    public void Health_WithoutABaseline_DoesNotApplyTheRateRule()
    {
        var h = new EngineHarness();
        for (var i = 0; i < 40; i++)
        {
            h.Observe(ObservationKind.Scene, null);
        }

        Assert.Equal(HealthState.Healthy, h.Snapshot.Health[DetectorId.Scene].State);
    }

    [Theory]
    [InlineData("de-DE", "10.2.0", "unsupported game language")]
    [InlineData("en-US", "10.3.0", "game version")]
    public void Health_ContextMismatch_DegradesTheDetector(string locale, string version, string reasonPart)
    {
        var settings = new Dictionary<DetectorId, DetectorSettings> { [DetectorId.Scene] = EngineHarness.Measured(DetectionTier.Validated, "10.2.0") };
        var h = new EngineHarness(settings: settings);
        var context = new ContextKey("profile-1", "1920x1080", locale, version);

        h.Observe(ObservationKind.Scene, "MainMenu", context: context);

        var health = h.Snapshot.Health[DetectorId.Scene];
        Assert.Equal(HealthState.Degraded, health.State);
        Assert.Contains(reasonPart, health.Reason);
    }

    [Fact]
    public void Health_DegradesOnAHudProfileMismatch_AndRecoversWhenTheContextMatchesAgain()
    {
        var settings = new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Scene] = new(DetectionTier.Validated, ThresholdProvenance.Measured, null, "10.2.0", "profile-1"),
        };
        var h = new EngineHarness(settings: settings);

        h.Observe(ObservationKind.Scene, "MainMenu", context: new ContextKey("other", "1920x1080", "en-US", "10.2.0"));
        Assert.Equal(HealthState.Degraded, h.Snapshot.Health[DetectorId.Scene].State);

        h.Observe(ObservationKind.Scene, "MainMenu");
        Assert.Equal(HealthState.Healthy, h.Snapshot.Health[DetectorId.Scene].State);
    }

    [Fact]
    public void Health_IsStaleWhenCaptureRunsButNothingArrives_NotWhilePaused()
    {
        var h = new EngineHarness();
        h.ConfirmScene(Scene.MainMenu);

        h.Advance(31);
        Assert.Equal(HealthState.Stale, h.Snapshot.Health[DetectorId.Scene].State);

        h.Engine.SetCapturePaused(true);
        Assert.Equal(HealthState.Healthy, h.Snapshot.Health[DetectorId.Scene].State);
    }

    [Fact]
    public void DisabledDetectors_StillConfirmInsideTheEngine_ForInAppDiagnostics()
    {
        // The engine confirms regardless of tier; whether anything is published is PublicationPolicy's decision.
        var h = new EngineHarness();
        h.ConfirmScene(Scene.Lobby);
        h.Frames(ObservationKind.Role, "Killer", 3);

        Assert.Equal(FieldState.Confirmed, h.Snapshot.Role.State);
        Assert.Equal(DetectionTier.Disabled, DetectorSettings.Default.Tier);
    }
}
