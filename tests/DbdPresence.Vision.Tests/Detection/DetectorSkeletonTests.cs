// Covers: C1/C2 detector skeletons (FR-DET-01 observations only, FR-DET-02 scene, FR-DET-03 role, FR-DET-13 regions requested are
// the profile's regions, FR-CAP-02 profile in normalised coordinates, PLAN_V2 D17 any resolution, English only).
// Mechanics only: the layouts are invented shapes and test glyphs, calibrated in the test; nothing here measures accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Detection;

public sealed class DetectorSkeletonTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static Observation Analyze(IDetector detector, ImageBuffer frame, int width = SyntheticScenes.CalibrationWidth, string locale = "en-US")
    {
        var context = new DetectionContext(SyntheticScenes.Context(width, locale), s_now);
        var crops = FrameCrops.FromFrame(frame, detector.Regions(context));
        return Assert.Single(detector.Analyze(crops, context).Observations);
    }

    [Fact]
    public void EveryDetector_DefaultsToTheDisabledTier()
    {
        Assert.Equal(DetectionTier.Disabled, new PhaseDetector().DefaultTier);
        Assert.Equal(DetectionTier.Disabled, new RoleDetector().DefaultTier);
        Assert.Equal(DetectionTier.Disabled, new PhaseDetector(SyntheticScenes.CalibrateScenes()).DefaultTier);
        Assert.Equal(DetectionTier.Disabled, new RoleDetector(SyntheticScenes.CalibrateRoles()).DefaultTier);
    }

    [Fact]
    public void AnUncalibratedDetector_NeverReadsAnything()
    {
        // No real templates ship with the app, so a fresh install has detection unavailable (ADR-0004).
        foreach (IDetector detector in new IDetector[] { new PhaseDetector(), new RoleDetector() })
        {
            var observation = Analyze(detector, SyntheticScenes.Render(SceneKind.Lobby, 640));

            Assert.Null(observation.Value);
            Assert.Equal(0, observation.Score);
            Assert.Contains("not calibrated", observation.Evidence, StringComparison.Ordinal);
            Assert.Empty(detector.Regions(new DetectionContext(SyntheticScenes.Context(), s_now)));
        }

        Assert.False(new PhaseDetector().IsCalibrated);
        Assert.True(new PhaseDetector(SyntheticScenes.CalibrateScenes()).IsCalibrated);
    }

    [Theory]
    [InlineData(SceneKind.Lobby)]
    [InlineData(SceneKind.InTrial)]
    [InlineData(SceneKind.Results)]
    public void PhaseDetector_ReadsTheCalibratedScene_AtEveryResolution(SceneKind scene)
    {
        var detector = new PhaseDetector(SyntheticScenes.CalibrateScenes());

        foreach (var width in new[] { 320, 640, 960 })
        {
            var observation = Analyze(detector, SyntheticScenes.Render(scene, width, brightness: 15, seed: width), width);

            Assert.Equal(scene.ToString(), observation.Value);
            Assert.Equal(ObservationKind.Scene, observation.Kind);
            Assert.Equal(DetectorId.Scene, observation.Detector);
            Assert.True(observation.Score > 0.5, $"{scene} at {width}: score {observation.Score}");
        }
    }

    [Fact]
    public void PhaseDetector_IsUnreadable_WhenNoCalibratedSceneIsShown()
    {
        var detector = new PhaseDetector(SyntheticScenes.CalibrateScenes());

        var observation = Analyze(detector, SyntheticScenes.Render(SceneKind.MainMenu, 640));

        Assert.Null(observation.Value);
        Assert.Contains("below score floor", observation.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseDetector_AbstainsWhenTwoScenesLookAlike()
    {
        // Calibrate "Lobby" and "Results" from the same layout: the detector cannot tell them apart, so it must not pick one.
        var sample = SyntheticScenes.Render(SceneKind.Lobby, 640, seed: 5);
        var anchor = TemplateFeature.FromSample(sample.Crop(SyntheticScenes.LobbyRegion), SyntheticScenes.Anchor);
        var profile = new AnchorProfile(
            "twins",
            [new RegionRequest("r", SyntheticScenes.LobbyRegion)],
            [new LabelAnchors("Lobby", [new FeatureBinding("r", anchor)]), new LabelAnchors("Results", [new FeatureBinding("r", anchor)])],
            0.5,
            0.2);

        var observation = Analyze(new PhaseDetector(profile), sample);

        Assert.Null(observation.Value);
        Assert.Contains("ambiguous", observation.Evidence, StringComparison.Ordinal);
        Assert.True(observation.Score > 0.9, "the raw score is still reported for diagnostics");
    }

    [Fact]
    public void ALabelNeedsEveryBinding_AndAMissingRegionScoresZero()
    {
        var sample = SyntheticScenes.Render(SceneKind.Lobby, 640, seed: 5);
        var present = TemplateFeature.FromSample(sample.Crop(SyntheticScenes.LobbyRegion), SyntheticScenes.Anchor);
        var absent = TemplateFeature.FromSample(SyntheticScenes.Render(SceneKind.Results, 640, seed: 6).Crop(SyntheticScenes.ResultsRegion), SyntheticScenes.Anchor);
        var profile = new AnchorProfile(
            "both",
            [new RegionRequest("lobby", SyntheticScenes.LobbyRegion), new RegionRequest("results", SyntheticScenes.ResultsRegion)],
            [new LabelAnchors("Lobby", [new FeatureBinding("lobby", present), new FeatureBinding("results", absent)])],
            0.5,
            0.0);
        var detector = new PhaseDetector(profile);

        // The lobby element alone is not enough: the results element is missing, so the label's score is that lowest binding.
        var observation = Analyze(detector, sample);
        Assert.Null(observation.Value);

        // A frame crop set without a declared region cannot support the label at all.
        var context = new DetectionContext(SyntheticScenes.Context(), s_now);
        var partial = new FrameCrops(640, 360, new Dictionary<string, ImageBuffer> { ["lobby"] = sample.Crop(SyntheticScenes.LobbyRegion) });
        var missing = Assert.Single(detector.Analyze(partial, context).Observations);
        Assert.Null(missing.Value);
        Assert.Equal(0, missing.Score);
    }

    [Fact]
    public void Detectors_ReportNothing_WhenTheGameIsNotInEnglish()
    {
        var detector = new PhaseDetector(SyntheticScenes.CalibrateScenes());

        var observation = Analyze(detector, SyntheticScenes.Render(SceneKind.Lobby, 640), locale: "de-DE");

        Assert.Null(observation.Value);
        Assert.Contains("unsupported game language", observation.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Role.Survivor)]
    [InlineData(Role.Killer)]
    public void RoleDetector_ReadsTheCalibratedRole_AtEveryResolution(Role role)
    {
        var detector = new RoleDetector(SyntheticScenes.CalibrateRoles());

        foreach (var width in new[] { 320, 640, 960 })
        {
            var observation = Analyze(detector, SyntheticScenes.RenderRole(role, width, brightness: -10, seed: width), width);

            Assert.Equal(role.ToString(), observation.Value);
            Assert.Equal(ObservationKind.Role, observation.Kind);
            Assert.Equal(DetectorId.Role, observation.Detector);
        }
    }

    [Fact]
    public void Observations_CarryTheContextTimeAndVersion_AndNoPixels()
    {
        var detector = new PhaseDetector(SyntheticScenes.CalibrateScenes());

        var observation = Analyze(detector, SyntheticScenes.Render(SceneKind.Lobby, 640));

        Assert.Equal(s_now, observation.ObservedAtUtc);
        Assert.Equal(PhaseDetector.CurrentVersion, observation.DetectorVersion);
        Assert.Equal(SyntheticScenes.Context().ProfileId, observation.Context.ProfileId);
        Assert.Matches(@"^[A-Za-z]+=\d\.\d{3}(;[A-Za-z]+=\d\.\d{3})*$", observation.Evidence);
    }

    [Fact]
    public void Analyze_IsPure_TheSameFrameGivesTheSameObservation()
    {
        var detector = new PhaseDetector(SyntheticScenes.CalibrateScenes());
        var frame = SyntheticScenes.Render(SceneKind.InTrial, 640, seed: 9);

        Assert.Equal(Analyze(detector, frame), Analyze(detector, frame));
    }

    [Fact]
    public void Profile_ValidationRejectsUnknownLabelsDuplicateRegionsAndBadBindings()
    {
        var feature = TemplateFeature.FromSample(SyntheticScenes.Render(SceneKind.Lobby, 640).Crop(SyntheticScenes.LobbyRegion), SyntheticScenes.Anchor);
        var region = new RegionRequest("r", SyntheticScenes.LobbyRegion);

        AnchorProfile Make(string label, string bindingRegion, params RegionRequest[] regions) =>
            new("p", regions, [new LabelAnchors(label, [new FeatureBinding(bindingRegion, feature)])], 0.5, 0.2);

        Assert.NotNull(new PhaseDetector(Make("Lobby", "r", region)));
        Assert.Throws<ArgumentException>(() => new PhaseDetector(Make("Unknown", "r", region))); // Unknown is never observed
        Assert.Throws<ArgumentException>(() => new PhaseDetector(Make("Elevator", "r", region)));
        Assert.Throws<ArgumentException>(() => new RoleDetector(Make("Lobby", "r", region)));
        Assert.Throws<ArgumentException>(() => new PhaseDetector(Make("Lobby", "missing", region)));
        Assert.Throws<ArgumentException>(() => new PhaseDetector(Make("Lobby", "r", region, region)));
        Assert.Throws<ArgumentException>(() => new PhaseDetector(new AnchorProfile("p", [region], [new LabelAnchors("Lobby", [])], 0.5, 0.2)));
        Assert.Throws<ArgumentException>(() => new PhaseDetector(new AnchorProfile("p", [region], [new LabelAnchors("Lobby", [new FeatureBinding("r", feature)]), new LabelAnchors("Lobby", [new FeatureBinding("r", feature)])], 0.5, 0.2)));
        Assert.Throws<ArgumentException>(() => new PhaseDetector(new AnchorProfile("p", [region], [], 1.5, 0.2)));
    }

    [Fact]
    public void ProfileRegions_AreTheOnlyRegionsRequested()
    {
        // FR-DET-13: the capture scheduler asks the detector for regions; it must never ask for more than the profile declares.
        var profile = SyntheticScenes.CalibrateScenes();
        var requested = new PhaseDetector(profile).Regions(new DetectionContext(SyntheticScenes.Context(), s_now));

        Assert.Equal(profile.Regions, requested);
        Assert.All(requested, r => Assert.True(r.Rect.Width * r.Rect.Height < 0.5, "each region is a part of the frame, never the whole window"));
        Assert.True(requested.Sum(r => r.Rect.Width * r.Rect.Height) < 0.6, "together the regions cover well under the whole frame");
    }

    [Fact]
    public void ThresholdProvenance_DefaultsToInitialGuess()
    {
        Assert.Equal(ThresholdProvenance.InitialGuess, SyntheticScenes.CalibrateScenes().Provenance);
        Assert.Equal(ThresholdProvenance.InitialGuess, AnchorProfile.Uncalibrated.Provenance);
    }

    [Fact]
    public void ColourHistogramAndHashFeatures_ScoreSimilarRegionsAboveDifferentOnes()
    {
        var lobby = SyntheticScenes.Render(SceneKind.Lobby, 640, seed: 1).Crop(SyntheticScenes.LobbyRegion);
        var lobbyAgain = SyntheticScenes.Render(SceneKind.Lobby, 960, seed: 2).Crop(SyntheticScenes.LobbyRegion);
        var results = SyntheticScenes.Render(SceneKind.Results, 640, seed: 3).Crop(SyntheticScenes.LobbyRegion);

        var histogram = HistogramFeature.FromSample(lobby);
        var hash = HashFeature.FromSample(lobby);

        Assert.True(histogram.Score(lobbyAgain) > histogram.Score(results));
        Assert.True(hash.Score(lobbyAgain) > hash.Score(results));
        Assert.Equal(1.0, hash.Score(lobby));

        var bright = new ColorFractionFeature(200, 200, 200, 20, expectedFraction: 0.2);
        Assert.True(bright.Score(lobby) > 0.9);
        Assert.Equal(0.0, bright.Score(results));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorFractionFeature(0, 0, 0, 10, 0.0));
    }

    [Fact]
    public void TemplateFeature_RejectsFlatTemplates()
    {
        Assert.Throws<ArgumentException>(() => new TemplateFeature(ImageBuffer.CreateGray(16, 16, 80), 64));
        Assert.Throws<ArgumentException>(() => new TemplateFeature(SyntheticScenes.Render(SceneKind.Lobby, 640), 64)); // wider than the canonical width
    }
}
