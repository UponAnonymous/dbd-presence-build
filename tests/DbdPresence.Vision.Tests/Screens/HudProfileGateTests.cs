// Covers: FR-DET-01 and DETECTION_VALIDATION §7 / §14 (publication is tied to the profile in use: a tier above Disabled needs a Measured profile that is
// the validated one). Review U5 F4. MECHANICS ONLY: settings objects and scripted observations; no frame is read and nothing here is accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Vision.Tests.Screens;

public sealed class HudProfileGateTests
{
    private const string Version = "10.2.0";

    private static HudProfile MeasuredCopy(string id) =>
        new(id, ThresholdProvenance.Measured, HudProfile.Catalogue.ReferenceWidth, HudProfile.Catalogue.ReferenceHeight, HudProfile.Catalogue.AspectTolerance, HudProfile.Catalogue.Regions, HudProfile.Catalogue.Thresholds, HudProfile.Catalogue.Highlight);

    private static DetectorSettings Earned(DetectionTier tier, string? profileId) =>
        new(tier, ThresholdProvenance.Measured, 0.05, Version, profileId);

    private static Dictionary<DetectorId, DetectorSettings> All(DetectionTier tier, string? profileId) =>
        Enum.GetValues<DetectorId>().ToDictionary(id => id, _ => Earned(tier, profileId));

    [Theory]
    [InlineData(DetectionTier.Experimental)]
    [InlineData(DetectionTier.Validated)]
    public void TheCatalogueProfile_CanNeverBeAboveDisabled_EvenWithMeasuredSettingsThatNameIt(DetectionTier tier)
    {
        var settings = Earned(tier, HudProfile.CatalogueId);

        Assert.Null(settings.Problem()); // the settings alone look fine ...
        Assert.Contains("initial-guess", HudProfile.Catalogue.PublicationProblem(settings), StringComparison.Ordinal); // ... the profile in use vetoes them
    }

    [Fact]
    public void AMeasuredProfile_IsAcceptedOnlyForTheSettingsThatNameItsId()
    {
        var profile = MeasuredCopy("hud-measured-1");

        Assert.Null(profile.PublicationProblem(Earned(DetectionTier.Validated, "hud-measured-1")));
        Assert.Null(profile.PublicationProblem(Earned(DetectionTier.Experimental, "hud-measured-1")));
        Assert.Contains("another HUD profile", profile.PublicationProblem(Earned(DetectionTier.Validated, "hud-measured-2")), StringComparison.Ordinal);
        Assert.Contains("no validated HUD profile", profile.PublicationProblem(Earned(DetectionTier.Validated, null)), StringComparison.Ordinal);
        Assert.Contains("no validated HUD profile", profile.PublicationProblem(Earned(DetectionTier.Validated, "  ")), StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredProfile_StillNeedsTheSettingsToBeComplete()
    {
        var profile = MeasuredCopy("hud-measured-1");

        var noVersion = new DetectorSettings(DetectionTier.Validated, ThresholdProvenance.Measured, 0.05, null, "hud-measured-1");
        var initialGuess = new DetectorSettings(DetectionTier.Validated, ThresholdProvenance.InitialGuess, 0.05, Version, "hud-measured-1");

        Assert.NotNull(profile.PublicationProblem(noVersion));
        Assert.NotNull(profile.PublicationProblem(initialGuess));
    }

    [Fact]
    public void Disabled_IsAlwaysFine_WhateverTheProfile()
    {
        Assert.Null(HudProfile.Catalogue.PublicationProblem(DetectorSettings.Default));
        Assert.Null(HudProfile.Catalogue.PublicationProblem(new DetectorSettings(DetectionTier.Disabled, ThresholdProvenance.Measured, null, "x", "other")));
    }

    [Fact]
    public void ThePipelineGate_PutsEveryRefusedHudDetectorBackToDisabled_AndSaysWhy()
    {
        var h = new HudHarness();
        var requested = All(DetectionTier.Validated, HudProfile.CatalogueId);

        var gate = h.Pipeline.GateSettings(requested);

        foreach (var detector in h.Pipeline.Detectors)
        {
            Assert.Equal(DetectorSettings.Default, gate.Effective[detector.Id]);
            Assert.Contains(gate.Refused, line => line.StartsWith(detector.Id.ToString(), StringComparison.Ordinal));
        }

        Assert.Equal(h.Pipeline.Detectors.Count, gate.Refused.Count);
        Assert.DoesNotContain(HudScenes.Owner, string.Join(' ', gate.Refused), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePipelineGate_LetsAMeasuredValidatedProfileThrough_AndDoesNotTouchDetectorsItDoesNotOwn()
    {
        var profile = MeasuredCopy("hud-measured-1");
        var h = new HudHarness(profile: profile);
        var requested = All(DetectionTier.Validated, "hud-measured-1");
        requested[DetectorId.Generators] = new DetectorSettings(DetectionTier.Validated, ThresholdProvenance.Measured, null, Version, "someone-elses-profile");

        var gate = h.Pipeline.GateSettings(requested);

        Assert.Empty(gate.Refused);
        Assert.All(h.Pipeline.Detectors, d => Assert.Equal(DetectionTier.Validated, gate.Effective[d.Id].Tier));
        Assert.Equal(requested[DetectorId.Generators], gate.Effective[DetectorId.Generators]); // not a HUD detector: passed through unchanged

        var wrongId = h.Pipeline.GateSettings(All(DetectionTier.Validated, "hud-measured-2"));
        Assert.Equal(h.Pipeline.Detectors.Count, wrongId.Refused.Count);
        Assert.All(h.Pipeline.Detectors, d => Assert.Equal(DetectionTier.Disabled, wrongId.Effective[d.Id].Tier));
    }

    [Fact]
    public void WithTheCatalogueProfile_TheGatedEngineNeverPublishes_WhileTheUngatedOneWould()
    {
        var h = new HudHarness();
        var requested = All(DetectionTier.Validated, HudProfile.CatalogueId);
        var gated = h.Pipeline.GateSettings(requested).Effective;

        var ungatedResult = Publish(requested);
        var gatedResult = Publish(gated);

        Assert.Equal(PresenceState.InTrial, ungatedResult.Inputs.State); // what the gate prevents
        Assert.Equal(PresenceState.Running, gatedResult.Inputs.State);
        Assert.Equal(Role.None, gatedResult.Inputs.DetectedRole);
        Assert.Contains(gatedResult.Suppressed, s => s.Field == FieldKind.Scene);
    }

    private static PublicationResult Publish(IReadOnlyDictionary<DetectorId, DetectorSettings> settings)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var engine = new MatchStateEngine(time, settings: settings);
        engine.SetGameState(GameState.Running);
        engine.SetDeclaredMode(GameMode.Standard);
        for (var i = 0; i < 4; i++)
        {
            Submit(engine, time, ObservationKind.Scene, nameof(Scene.InTrial));
            Submit(engine, time, ObservationKind.Role, nameof(Role.Survivor));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        return PublicationPolicy.Compose(engine.Snapshot, settings, new PublicationOptions(), null, time.GetUtcNow(), null);
    }

    private static void Submit(MatchStateEngine engine, TimeProvider time, ObservationKind kind, string value) =>
        engine.Submit(new Observation(Observation.DetectorOf(kind), 1, time.GetUtcNow(), kind, value, 0.95, "mechanics", HudHarness.Context));
}
