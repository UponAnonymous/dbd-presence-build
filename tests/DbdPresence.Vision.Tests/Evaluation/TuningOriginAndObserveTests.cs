// Covers: FR-DET-16 (a run on tuning data can never earn a tier) and FR-DET-24 (the adapter exposes one ordered pass over a session).
// MECHANICS ONLY: drawn text on flat colour read back by a fake recogniser; none of this is accuracy.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class TuningOriginAndObserveTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConfirmationRule s_rule = new(3, 4, TimeSpan.FromSeconds(1.5), 0.6, TimeSpan.FromSeconds(30));

    private static EvaluationUnit Unit(int number, string session, string truth, ImageBuffer image) =>
        new($"{session}-{number}-{truth}", session, truth, Enumerable.Range(0, 5).Select(i => new EvaluationFrame(s_t0.AddSeconds(i), image)).ToList());

    [Fact]
    public void ATuningRun_IsNeverApplicable_EvenWithEnoughCleanUnits_AndTheReportSaysSo()
    {
        var adapter = new HudEvaluationAdapter(HudProfile.Catalogue, HudHarness.Lists, new BitmapFontOcr(), HudHarness.Context, HudScenes.Owner);
        var units = Enumerable.Range(0, 40).Select(i => Unit(i, $"s{i % 12}", "MainMenu", HudScenes.MainMenu(1280))).ToList();

        var tuning = DetectionEvaluator.Run(units, adapter.ClassifierFor(ObservationKind.Scene), new EvaluationOptions("d", "scene", 1, "c", DataOrigin.Tuning, s_rule));
        var real = DetectionEvaluator.Run(units, new HudEvaluationAdapter(HudProfile.Catalogue, HudHarness.Lists, new BitmapFontOcr(), HudHarness.Context, HudScenes.Owner).ClassifierFor(ObservationKind.Scene), new EvaluationOptions("d", "scene", 1, "c", DataOrigin.Real, s_rule));

        Assert.False(tuning.Gates.Applicable);
        Assert.False(tuning.Gates.ExperimentalMeasuredGatesMet);
        Assert.False(tuning.Gates.ValidatedMeasuredGatesMet);
        Assert.Contains("TUNING SPLIT", EvaluationReportWriter.ToMarkdown(tuning), StringComparison.Ordinal);
        Assert.Contains("Tuning split", tuning.Gates.Notes[0], StringComparison.Ordinal);

        // The same clean units on a held-out run do meet the measured Experimental gates: the tuning label is what withholds them (mechanics of the gate, not accuracy).
        Assert.True(real.Gates.Applicable);
        Assert.True(real.Gates.ExperimentalMeasuredGatesMet);
        Assert.DoesNotContain("TUNING", EvaluationReportWriter.ToMarkdown(real), StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_GivesEveryDetectorsReadingForAPicture_AndTheSameAnswerAsTheClassifier()
    {
        var adapter = new HudEvaluationAdapter(HudProfile.Catalogue, HudHarness.Lists, new BitmapFontOcr(), HudHarness.Context, HudScenes.Owner);
        var frame = new EvaluationFrame(s_t0, HudScenes.MainMenu(1280));

        var observations = adapter.Observe(frame);
        var viaClassifier = adapter.ClassifierFor(ObservationKind.Scene)(frame);

        var scene = observations.Single(o => o.Kind == ObservationKind.Scene);
        Assert.Equal("MainMenu", scene.Value);
        Assert.Equal(new FramePrediction(scene.Value, scene.Score), viaClassifier);
        Assert.All(observations, o => Assert.Equal(frame.TimestampUtc, o.ObservedAtUtc));
        Assert.Throws<ArgumentNullException>(() => adapter.Observe(null!));
    }

    [Fact]
    public void VersionOf_ReportsTheDetectorVersion_AndNullWhereNoDetectorExists()
    {
        var adapter = new HudEvaluationAdapter(HudProfile.Catalogue, HudHarness.Lists, new BitmapFontOcr(), HudHarness.Context, HudScenes.Owner);

        Assert.Equal(1, adapter.VersionOf(ObservationKind.Scene));
        Assert.Equal(1, adapter.VersionOf(ObservationKind.Killer));
        Assert.Null(adapter.VersionOf(ObservationKind.Generators));
    }
}
