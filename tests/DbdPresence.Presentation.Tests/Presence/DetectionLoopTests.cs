// Covers: FR-DET-25 (the live loop: capture, change-gated reader, observations, engine, presence coordinator), FR-DET-13 (only the profile's regions are
// requested), FR-CAP-08 and SAFETY_AND_PRIVACY section 2.3 (runs only when the user turned detection on and the signed configuration allows it),
// review U5 F4 (a tier above Disabled needs a Measured profile that is the validated one) and F9 (re-entrancy guard), NFR-PRIV-01.
// MECHANICS ONLY: every screen is drawn text on flat colour and the recogniser is an exact fake. Nothing here is accuracy; how the Windows capture,
// Windows OCR and a running game behave through this loop is PENDING-ENV.
using DbdPresence.Configuration;
using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class DetectionLoopTests
{
    private static HudProfile MeasuredProfile(string id) =>
        new(id, ThresholdProvenance.Measured, HudProfile.Catalogue.ReferenceWidth, HudProfile.Catalogue.ReferenceHeight, HudProfile.Catalogue.AspectTolerance, HudProfile.Catalogue.Regions, HudProfile.Catalogue.Thresholds, HudProfile.Catalogue.Highlight);

    private static Dictionary<DetectorId, DetectorSettings> Requested(DetectionTier tier, string? profileId) =>
        new Dictionary<DetectorId, DetectorSettings>
        {
            [DetectorId.Scene] = new(tier, ThresholdProvenance.Measured, 0.05, "10.2.0", profileId),
        };

    // ---- It runs only when it may --------------------------------------------------------------------------------------

    [Fact]
    public async Task ByDefault_NothingIsCaptured_AndTheStatusSaysSo()
    {
        using var h = new LoopHarness(detectionOn: false);
        h.StartGame();

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.Off, h.Loop.Status.Run);
        Assert.Equal(DetectionText.Off, h.Loop.Status.Detail);
        Assert.Empty(h.Captures);
        Assert.Null(h.Loop.Snapshot);
        Assert.Equal(0, h.Loop.Status.OcrCalls);
        Assert.Equal(DetectionTier.Disabled, DetectorTier(h.Loop, DetectorId.Scene));
    }

    [Fact]
    public async Task EnabledWithoutTheRiskNotice_IsStillOff()
    {
        using var h = new LoopHarness(settings: new AppSettings { Detection = new DetectionSettings { Enabled = true, RiskAcknowledged = false } });
        h.StartGame();

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.Off, h.Loop.Status.Run);
        Assert.Empty(h.Captures);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WhenTheSignedConfigurationForcesItOffOrIsMissing_NothingIsCaptured(bool captureForcedOff, bool detectionForcedOff)
    {
        using var h = new LoopHarness();
        h.StartGame();
        h.Remote.Set(FakeRemote.Allowing with { CaptureForcedOff = captureForcedOff, DetectionForcedOff = detectionForcedOff });

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.SwitchedOffRemotely, h.Loop.Status.Run);
        Assert.Empty(h.Captures);
        Assert.Null(h.Loop.Snapshot);
    }

    [Fact]
    public async Task WithTheSafeDefaultConfiguration_ThatIsMissingOrExpired_NothingIsCaptured()
    {
        using var h = new LoopHarness();
        h.StartGame();
        h.Remote.Set(RemoteConfigSnapshot.SafeDefaults);

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.SwitchedOffRemotely, h.Loop.Status.Run);
        Assert.Empty(h.Captures);
    }

    [Fact]
    public async Task WhileTheGameIsNotRunning_TheLoopWaits_AndCreatesNoCaptureSource()
    {
        using var h = new LoopHarness();

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.WaitingForGame, h.Loop.Status.Run);
        Assert.Equal(DetectionText.WaitingForGame, h.Loop.Status.Detail);
        Assert.Empty(h.Captures);
        Assert.Null(h.Loop.Snapshot);
    }

    [Fact]
    public async Task TurningTheSettingOffWhileReading_StopsCaptureAndForgetsEverything()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CyclesAsync(3);
        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
        var capture = h.Current!;

        h.Settings.Update(s => s with { Detection = s.Detection with { Enabled = false } });
        await h.CycleAsync();

        Assert.True(capture.Disposed);
        Assert.Equal(DetectionRunState.Off, h.Loop.Status.Run);
        Assert.Null(h.Loop.Snapshot);
        Assert.Equal(GameState.NotRunning, h.Loop.Engine.Snapshot.Game);
        Assert.Equal(SceneKind.Unknown, h.Loop.Engine.Snapshot.ConfirmedScene);
    }

    [Fact]
    public async Task WhenTheGameCloses_CaptureIsReleased_AndAnotherProcessGetsItsOwnSource()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CyclesAsync(2);
        var first = h.Current!;

        h.Game.Set(GameLifecycle.Initial);
        await h.CycleAsync();
        Assert.True(first.Disposed);
        Assert.Equal(DetectionRunState.WaitingForGame, h.Loop.Status.Run);

        h.Game.Set(new GameLifecycle { State = GameState.Running, Process = new GameProcessInfo(9999, "DeadByDaylight-Win64-Shipping.exe", h.Time.GetUtcNow()), StartedUtc = h.Time.GetUtcNow() });
        await h.CycleAsync();

        Assert.Equal(2, h.Captures.Count);
        Assert.Equal(9999, h.Current!.ProcessId);
    }

    // ---- The chain: capture, reader, observations, engine --------------------------------------------------------------

    [Fact]
    public async Task FramesFlowThroughTheChain_UntilTheEngineConfirmsTheScene()
    {
        using var h = new LoopHarness();
        h.StartGame();

        await h.CycleAsync();
        Assert.NotEqual("MainMenu", h.Loop.Engine.Snapshot.Scene.ConfirmedValue); // one reading never confirms
        await h.CyclesAsync(2);

        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
        Assert.Equal("MainMenu", h.Loop.Engine.Snapshot.Scene.ConfirmedValue);
        Assert.Equal(SceneKind.MainMenu, h.Loop.Status.Match!.ConfirmedScene);
        Assert.True(h.Loop.Status.OcrCalls > 0);
        Assert.Equal(0, h.Loop.Status.SkippedCycles);
    }

    [Fact]
    public async Task OnlyTheProfilesRegionsAreRequested_AndEachCycleCapturesOnce()
    {
        using var h = new LoopHarness();
        h.StartGame();

        await h.CyclesAsync(2);

        Assert.Equal(2, h.Current!.Captures);
        Assert.Single(h.Captures);
    }

    [Fact]
    public async Task AnUnchangedScreen_IsRecognisedOncePerRegion_NotOnEveryCycle()
    {
        using var h = new LoopHarness();
        h.StartGame();

        await h.CycleAsync();
        var afterFirst = h.Loop.Status.OcrCalls;
        await h.CyclesAsync(3);

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, h.Loop.Status.OcrCalls);
    }

    [Fact]
    public async Task AMinimisedWindow_PausesTheEngine_AndAnUnreadableOneIsJustNotRead()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CyclesAsync(3);
        Assert.False(h.Loop.Engine.Snapshot.CapturePaused);

        h.Current!.Status = CaptureStatus.Minimized;
        await h.CycleAsync();
        Assert.Equal(DetectionRunState.WindowUnavailable, h.Loop.Status.Run);
        Assert.Contains("cannot be read right now", h.Loop.Status.Detail, StringComparison.Ordinal);
        Assert.True(h.Loop.Engine.Snapshot.CapturePaused);

        h.Current.Status = CaptureStatus.BlackFrames;
        await h.CycleAsync();
        Assert.Equal(DetectionRunState.WindowUnavailable, h.Loop.Status.Run);
        Assert.False(h.Loop.Engine.Snapshot.CapturePaused);

        h.Current.Status = CaptureStatus.Ready;
        await h.CycleAsync();
        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
    }

    [Fact]
    public async Task AFailureInsideACycle_IsCountedReportedAndSurvived_NeverSwallowed()
    {
        using var h = new LoopHarness();
        h.StartGame();
        await h.CyclesAsync(1);
        var reported = new List<Exception>();
        h.Loop.CycleFailed += (_, e) => reported.Add(e);
        h.Current!.ThrowOnCapture = new InvalidOperationException("capture broke");

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.Problem, h.Loop.Status.Run);
        Assert.Equal(DetectionText.Problem, h.Loop.Status.Detail);
        Assert.Equal(1, h.Loop.FailureCount);
        Assert.Equal("capture broke", Assert.Single(reported).Message);
        Assert.DoesNotContain("capture broke", h.Loop.Status.Detail, StringComparison.Ordinal);

        h.Current.ThrowOnCapture = null;
        await h.CycleAsync();
        await h.CycleAsync();
        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
    }

    [Theory]
    [InlineData(TextRecognitionStatus.EnglishLanguageMissing)]
    [InlineData(TextRecognitionStatus.Unavailable)]
    [InlineData(TextRecognitionStatus.ImageTooLarge)]
    public async Task WhenTextRecognitionCannotRun_TheStatusSaysSo_InsteadOfClaimingTheWindowIsBeingRead(TextRecognitionStatus status)
    {
        var ocr = new BitmapFontOcr { ForceStatus = status };
        using var h = new LoopHarness(recognizer: ocr);
        h.StartGame();

        await h.CycleAsync();

        Assert.Equal(DetectionRunState.TextRecognitionUnavailable, h.Loop.Status.Run);
        Assert.NotEqual(DetectionText.Reading, h.Loop.Status.Detail);

        // A missing language pack is explained in the recogniser's own sentence; anything else gets the app's generic one.
        Assert.Equal(status == TextRecognitionStatus.EnglishLanguageMissing ? "forced by the test" : DetectionText.TextRecognitionBroken, h.Loop.Status.Detail);

        ocr.ForceStatus = null;
        h.Show(HudScenes.Lobby(1280)); // a different screen, so the change gate recognises again
        await h.CycleAsync();
        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
    }

    // ---- Re-entrancy guard (review U5 F9) -------------------------------------------------------------------------------

    private sealed class BlockingOcr : ITextRecognizer
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public int Concurrent { get; private set; }

        public int MaxConcurrent { get; private set; }

        public void Release() => _release.TrySetResult();

        public async Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
        {
            Concurrent++;
            MaxConcurrent = Math.Max(MaxConcurrent, Concurrent);
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            Concurrent--;
            return new TextRecognitionResult(TextRecognitionStatus.Recognized, BitmapFontOcr.Read(image), "blocked then released");
        }
    }

    [Fact]
    public async Task ACycleThatFindsThePreviousOneRunning_IsSkippedAndCounted_NeverRunsAtTheSameTime()
    {
        var ocr = new BlockingOcr();
        using var h = new LoopHarness(recognizer: ocr);
        h.StartGame();

        var first = h.Loop.RunCycleAsync(TestContext.Current.CancellationToken);
        await ocr.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = await h.Loop.RunCycleAsync(TestContext.Current.CancellationToken);
        var third = await h.Loop.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DetectionCadence.IdleInterval, second);
        Assert.Equal(DetectionCadence.IdleInterval, third);
        ocr.Release();
        await first;
        Assert.Equal(2, h.Loop.Status.SkippedCycles);
        Assert.Equal(1, ocr.MaxConcurrent);
        Assert.Equal(1, h.Current!.Captures);

        // The guard releases: the next cycle runs normally.
        await h.CycleAsync();
        Assert.Equal(2, h.Current.Captures);
    }

    [Fact]
    public async Task TheTimer_RunsCyclesOnItsOwn_OneAtATime()
    {
        using var h = new LoopHarness();
        h.StartGame();
        h.Loop.Start();
        h.Loop.Start(); // idempotent

        for (var i = 0; i < 6; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => h.Loop.Status.Cycles > i);
        }

        Assert.Equal(DetectionRunState.Reading, h.Loop.Status.Run);
        Assert.Single(h.Captures);
        Assert.Equal(0, h.Loop.Status.SkippedCycles);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition was not reached in time.");
    }

    // ---- Cadence and busy-time budget are profile values -------------------------------------------------------------------

    [Fact]
    public void TheCadenceComesFromTheProfile_TheUserCanOnlyAskForSlower_AndLowImpactStretchesIt()
    {
        var sampling = new HudSampling(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), LowImpactFactor: 2, BusyBudget: 0.2);
        var asksForFast = new DetectionSettings { MenuScanIntervalMs = 1000, TrialScanIntervalMs = 500 };

        Assert.Equal(TimeSpan.FromSeconds(2), DetectionCadence.NextDelay(sampling, asksForFast, SceneKind.MainMenu, TimeSpan.Zero)); // the profile's 2 s beats the user's 1 s
        Assert.Equal(TimeSpan.FromSeconds(1), DetectionCadence.NextDelay(sampling, asksForFast, SceneKind.InTrial, TimeSpan.Zero)); // the profile's 1 s beats the user's 0.5 s
        Assert.Equal(TimeSpan.FromSeconds(1), DetectionCadence.NextDelay(sampling, asksForFast, SceneKind.Spectating, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(1), DetectionCadence.NextDelay(sampling, asksForFast, SceneKind.InMatchMenu, TimeSpan.Zero));

        var oneSecondProfile = new HudSampling(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
        var asksForSlower = new DetectionSettings { MenuScanIntervalMs = 2000, TrialScanIntervalMs = 1000 };
        Assert.Equal(TimeSpan.FromSeconds(2), DetectionCadence.NextDelay(oneSecondProfile, asksForSlower, SceneKind.Lobby, TimeSpan.Zero)); // slower is honoured

        Assert.Equal(TimeSpan.FromSeconds(4), DetectionCadence.NextDelay(sampling, asksForFast with { LowImpact = true }, SceneKind.Results, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(2), DetectionCadence.NextDelay(sampling, asksForFast with { LowImpact = true }, SceneKind.InTrial, TimeSpan.Zero));
    }

    [Fact]
    public void ASlowCycle_MakesTheLoopWaitSoThatBusyTimeStaysWithinTheProfilesBudget()
    {
        var sampling = new HudSampling(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), BusyBudget: 0.2);
        var settings = new DetectionSettings();

        // busy / (busy + delay) <= 0.2  <=>  delay >= 4 x busy.
        Assert.Equal(TimeSpan.FromSeconds(1), DetectionCadence.NextDelay(sampling, settings, SceneKind.Lobby, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.FromSeconds(2), DetectionCadence.NextDelay(sampling, settings, SceneKind.Lobby, TimeSpan.FromMilliseconds(500)));
        Assert.Equal(TimeSpan.FromSeconds(8), DetectionCadence.NextDelay(sampling, settings, SceneKind.Lobby, TimeSpan.FromSeconds(2)));
    }

    private sealed class SlowOcr(FakeTimeProvider time, TimeSpan perCall) : ITextRecognizer
    {
        public Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
        {
            time.Advance(perCall);
            return Task.FromResult(new TextRecognitionResult(TextRecognitionStatus.Recognized, BitmapFontOcr.Read(image), "slow fake"));
        }
    }

    [Fact]
    public async Task TheLoop_WaitsLongerAfterASlowCycle_BecauseTheBudgetIsAProfileValue()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero));
        using var slow = new LoopHarness(recognizer: new SlowOcr(clock, TimeSpan.FromMilliseconds(300)), time: clock);
        slow.StartGame();

        var delay = await slow.CycleAsync();

        // The first cycle recognises many regions at 300 ms each: far over 15% of a second, so the wait is stretched above the plain 1 s interval.
        Assert.True(slow.Loop.Status.OcrCalls > 1);
        Assert.True(delay > TimeSpan.FromSeconds(1), $"the delay was {delay}");
        var busy = TimeSpan.FromMilliseconds(300 * slow.Loop.Status.OcrCalls);
        Assert.Equal(busy * ((1.0 / HudSampling.InitialGuess.BusyBudget) - 1.0), delay);
    }

    // ---- Tiers above Disabled need a Measured profile that is the validated one (review U5 F4) ----------------------------

    private static DetectionTier DetectorTier(DetectionLoop loop, DetectorId id) => loop.DetectorSettings.GetValueOrDefault(id, DetectorSettings.Default).Tier;

    [Fact]
    public async Task WithTheCatalogueProfile_EveryRequestedTierAboveDisabled_IsRefused_AndTheStatusSaysWhy()
    {
        using var h = new LoopHarness(requested: Requested(DetectionTier.Validated, HudProfile.CatalogueId));

        await h.CycleAsync();

        Assert.Equal(DetectionTier.Disabled, DetectorTier(h.Loop, DetectorId.Scene));
        var refused = Assert.Single(h.Loop.Status.RefusedDetectors);
        Assert.Contains("initial-guess", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredProfileThatIsNotTheValidatedOne_IsRefused_ButTheValidatedOneKeepsItsTier()
    {
        using var other = new LoopHarness(profile: MeasuredProfile("measured-a"), requested: Requested(DetectionTier.Validated, "measured-b"));
        using var same = new LoopHarness(profile: MeasuredProfile("measured-a"), requested: Requested(DetectionTier.Validated, "measured-a"));

        Assert.Equal(DetectionTier.Disabled, DetectorTier(other.Loop, DetectorId.Scene));
        Assert.Equal(DetectionTier.Validated, DetectorTier(same.Loop, DetectorId.Scene));
    }

    [Fact]
    public async Task EvenWhenTheConfigurationAsksForValidated_TheCatalogueProfileKeepsEverythingOutOfDiscord()
    {
        using var asked = new LoopHarness(requested: Requested(DetectionTier.Validated, HudProfile.CatalogueId));
        using var baseline = new LoopHarness(detectionOn: false);
        asked.StartGame();
        baseline.StartGame();
        var (coordinator, sink) = asked.Coordinator();
        var (baselineCoordinator, baselineSink) = baseline.Coordinator();
        using var c1 = coordinator;
        using var c2 = baselineCoordinator;

        await asked.CyclesAsync(4);
        await baseline.CyclesAsync(4);

        Assert.Equal("MainMenu", asked.Loop.Engine.Snapshot.Scene.ConfirmedValue); // the engine confirms it for the app's own screen ...
        Assert.Equal(PresenceState.Running, coordinator.Snapshot.Composition.TemplateState); // ... and none of it reaches the activity
        Assert.Equal(baselineSink.Last, sink.Last);
        Assert.NotNull(sink.Last);
    }

    [Fact]
    public async Task OnlyAMeasuredValidatedProfile_LetsAConfirmedSceneReachThePresenceCoordinator()
    {
        // Test-only: this shows the plumbing publishes when, and only when, the gate lets a tier through. No such profile exists in the product.
        using var h = new LoopHarness(profile: MeasuredProfile("measured-a"), requested: Requested(DetectionTier.Validated, "measured-a"), gameVersion: "10.2.0");
        h.StartGame();
        var (coordinator, sink) = h.Coordinator();
        using var c = coordinator;
        var before = sink.Last;

        await h.CyclesAsync(4);

        Assert.Equal(PresenceState.MainMenu, coordinator.Snapshot.Composition.TemplateState);
        Assert.NotEqual(before, sink.Last);
    }

    [Fact]
    public async Task WithoutAReadableGameVersion_AValidatedTierCountsAsDegraded_SoNothingReachesThePresenceCoordinator()
    {
        // The game version reader is not built (PENDING-ENV), so the app passes no version: a tier validated on a version cannot publish.
        using var h = new LoopHarness(profile: MeasuredProfile("measured-a"), requested: Requested(DetectionTier.Validated, "measured-a"), gameVersion: null);
        h.StartGame();
        var (coordinator, _) = h.Coordinator();
        using var c = coordinator;

        await h.CyclesAsync(4);

        Assert.Null(h.Loop.Engine.Snapshot.Scene.ConfirmedValue); // the engine hides a degraded detector's field too
        Assert.Equal(HealthState.Degraded, h.Loop.Engine.Snapshot.Health[DetectorId.Scene].State);
        Assert.Equal<PresenceState?>(PresenceState.Running, coordinator.Snapshot.Composition.TemplateState);
    }

    // ---- The coordinator's use of the feed fails closed -----------------------------------------------------------------

    private sealed class FakeFeed : IDetectionFeed
    {
        public event EventHandler? Changed;

        public MatchSnapshot? Snapshot { get; set; }

        public IReadOnlyDictionary<DetectorId, DetectorSettings> DetectorSettings { get; set; } = new Dictionary<DetectorId, DetectorSettings>();

        public PublicationOptions Options { get; set; } = new();

        public IMapNameResolver? Maps => HudHarness.Vocabulary;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private static readonly ContextKey s_context = new("p", "1920x1080", "en-US", "1");

    /// <summary>A real engine that confirmed the main menu from three scripted readings, with the given per-detector settings.</summary>
    private static MatchSnapshot ConfirmedMainMenu(FakeTimeProvider time, IReadOnlyDictionary<DetectorId, DetectorSettings> settings)
    {
        var engine = new MatchStateEngine(time, settings: settings);
        engine.SetGameState(GameState.Running);
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            engine.Submit(new Observation(DetectorId.Scene, 1, time.GetUtcNow(), ObservationKind.Scene, "MainMenu", 0.9, "test", s_context));
        }

        Assert.Equal(SceneKind.MainMenu, engine.Snapshot.ConfirmedScene);
        return engine.Snapshot;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExperimentalFieldWouldNeedABetaMarkerTheComposerDoesNotHaveYet_SoTheCoordinatorAddsNothing(bool sessionOptIn)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero));
        var settings = new FakeSettingsService();
        var game = new FakeLifecycleSource();
        game.SetRunning(time.GetUtcNow());
        var experimental = new Dictionary<DetectorId, DetectorSettings> { [DetectorId.Scene] = new(DetectionTier.Experimental, ThresholdProvenance.Measured, 0.05, "1", "p") };
        var feed = new FakeFeed { Snapshot = ConfirmedMainMenu(time, experimental), DetectorSettings = experimental, Options = new PublicationOptions(sessionOptIn) };
        var sink = new FakeSink();
        var baselineSink = new FakeSink();
        using var baseline = new PresenceCoordinator(settings, new BundledApplicationCatalog(), game, baselineSink, time);
        using var coordinator = new PresenceCoordinator(settings, new BundledApplicationCatalog(), game, sink, time, detection: feed);
        baseline.Start();
        coordinator.Start();

        // With the opt-in the policy would publish the scene with a beta marker; the composer cannot carry one, so nothing is added (fail closed).
        Assert.Equal(baselineSink.Last, sink.Last);
        Assert.Equal<PresenceState?>(PresenceState.Running, coordinator.Snapshot.Composition.TemplateState);
    }

    [Fact]
    public void AValidatedField_ThroughTheFeed_ReachesTheActivity_ButADisabledOneDoesNot()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero));
        var settings = new FakeSettingsService();
        var game = new FakeLifecycleSource();
        game.SetRunning(time.GetUtcNow());
        var validated = new Dictionary<DetectorId, DetectorSettings> { [DetectorId.Scene] = new(DetectionTier.Validated, ThresholdProvenance.Measured, 0.05, "1", "p") };
        var disabled = new Dictionary<DetectorId, DetectorSettings>();
        var snapshot = ConfirmedMainMenu(time, validated);
        var onSink = new FakeSink();
        var offSink = new FakeSink();
        using var on = new PresenceCoordinator(settings, new BundledApplicationCatalog(), game, onSink, time, detection: new FakeFeed { Snapshot = snapshot, DetectorSettings = validated });
        using var off = new PresenceCoordinator(settings, new BundledApplicationCatalog(), game, offSink, time, detection: new FakeFeed { Snapshot = snapshot, DetectorSettings = disabled });
        on.Start();
        off.Start();

        Assert.Equal<PresenceState?>(PresenceState.MainMenu, on.Snapshot.Composition.TemplateState);
        Assert.Equal<PresenceState?>(PresenceState.Running, off.Snapshot.Composition.TemplateState);
        Assert.NotEqual(onSink.Last, offSink.Last);
    }

    [Fact]
    public void ADetectionFeed_ThatIsUnsubscribedOnDispose_NoLongerTriggersRecomputes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.Zero));
        var settings = new FakeSettingsService();
        var game = new FakeLifecycleSource();
        var sink = new FakeSink();
        var feed = new FakeFeed();
        var coordinator = new PresenceCoordinator(settings, new BundledApplicationCatalog(), game, sink, time, detection: feed);
        coordinator.Start();
        var changes = 0;
        coordinator.SnapshotChanged += (_, _) => changes++;

        feed.Raise();
        Assert.Equal(1, changes);
        coordinator.Dispose();
        feed.Raise();
        Assert.Equal(1, changes);
    }

    // ---- Disposal ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_StopsTheTimer_ReleasesCapture_AndLaterCyclesAreRefused()
    {
        var h = new LoopHarness();
        h.StartGame();
        h.Loop.Start();
        await h.CyclesAsync(2);
        var capture = h.Current!;

        h.Dispose();

        Assert.True(capture.Disposed);
        Assert.Throws<ObjectDisposedException>(h.Loop.Start);
        h.Dispose(); // idempotent
    }

    [Fact]
    public void TheProfilesSamplingValues_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithSampling(new HudSampling(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500))));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithSampling(new HudSampling(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), BusyBudget: 0.9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HudProfile.Catalogue.WithSampling(new HudSampling(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), LowImpactFactor: 0)));
        Assert.Equal(HudSampling.InitialGuess, HudProfile.Catalogue.Sampling);
    }
}
