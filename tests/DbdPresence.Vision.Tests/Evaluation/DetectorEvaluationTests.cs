// Covers: C5 (evaluator runs the real detector pipeline on a synthetic labelled set; numbers verified by construction),
// C2 (resolution-independent detection, PLAN_V2 D17), FR-DET-16.
// MECHANICS ONLY: the frames are invented shapes and test glyphs rendered by the test. The report below is not accuracy for any game.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class DetectorEvaluationTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly ConfirmationRule s_rule = new(3, 4, TimeSpan.FromSeconds(1.5), 0.5, TimeSpan.FromSeconds(30));

    private static EvaluationUnit SceneUnit(string id, string session, SceneKind? truth, SceneKind shown, int width, int frames = 4, int brightness = 0)
    {
        var list = Enumerable.Range(0, frames)
            .Select(i => new EvaluationFrame(s_t0.AddSeconds(i), SyntheticScenes.Render(shown, width, brightness + (i % 2 * 6), seed: (width * 31) + i)))
            .ToList();
        return new EvaluationUnit(id, session, truth?.ToString(), list);
    }

    /// <summary>
    /// The synthetic labelled set, built so the expected counts follow from its construction (not from running the detector):
    /// <list type="bullet">
    /// <item>9 units: Lobby, InTrial and Results each rendered at 320, 640 and 960 px wide, truth equal to what is shown: 9 correct.</item>
    /// <item>1 unit: truth InTrial, frames show a screen no template knows (the main menu): the detector must abstain.</item>
    /// <item>1 unit: truth Lobby but the frames show the Results layout (a deliberate labelling mismatch): one confirmed error.</item>
    /// </list>
    /// </summary>
    private static List<EvaluationUnit> SyntheticSet()
    {
        var units = new List<EvaluationUnit>();
        var n = 0;
        foreach (var scene in new[] { SceneKind.Lobby, SceneKind.InTrial, SceneKind.Results })
        {
            foreach (var width in new[] { 320, 640, 960 })
            {
                units.Add(SceneUnit($"u{n}", $"s{n % 3}", scene, scene, width, brightness: 10 * (n % 3)));
                n++;
            }
        }

        units.Add(SceneUnit("blank", "s0", SceneKind.InTrial, SceneKind.MainMenu, 640));
        units.Add(SceneUnit("mismatch", "s1", SceneKind.Lobby, SceneKind.Results, 640));
        return units;
    }

    // The evaluation renders and matches dozens of frames; it is deterministic, so one run is shared by the tests that inspect it.
    private static readonly Lazy<EvaluationReport> s_report = new(() => Evaluate());

    private static EvaluationReport Evaluate()
    {
        var detector = new PhaseDetector(SyntheticScenes.CalibrateScenes());
        var classifier = DetectorClassifier.Create(detector, ObservationKind.Scene, SyntheticScenes.Context());
        return DetectionEvaluator.Run(
            SyntheticSet(),
            classifier,
            new EvaluationOptions("synthetic-scenes", "scene (PhaseDetector)", PhaseDetector.CurrentVersion, "synthetic profile, 320/640/960 px, English", DataOrigin.Synthetic, s_rule));
    }

    [Fact]
    public void RealDetectorPipeline_ProducesTheCountsThatFollowFromTheSetConstruction()
    {
        var r = s_report.Value;

        Assert.Equal(11, r.Units);
        Assert.Equal(11, r.LegibleUnits);
        Assert.Equal(10, r.Confirmed); // everything except the blank-menu unit
        Assert.Equal(9, r.Correct);
        Assert.Equal(1, r.Abstained);
        Assert.Equal(new Proportion(9, 10), r.Precision);
        Assert.Equal(new Proportion(9, 11), r.Coverage);
        Assert.Equal(3, r.Sessions);
    }

    [Fact]
    public void RealDetectorPipeline_ConfusionMatrixIsAsConstructed()
    {
        var m = s_report.Value.Confusion;

        Assert.Equal(["InTrial", "Lobby", "Results"], m.Classes);
        // columns: InTrial, Lobby, Results, abstain
        Assert.Equal([3, 0, 0, 1], m.Counts[0]); // 3 InTrial right + the blank-menu unit abstained
        Assert.Equal([0, 3, 1, 0], m.Counts[1]); // 3 Lobby right + the mismatching unit confirmed as Results
        Assert.Equal([0, 0, 3, 0], m.Counts[2]);
    }

    [Fact]
    public void RealDetectorPipeline_PerClassTable()
    {
        var r = s_report.Value;

        var lobby = r.Classes.Single(c => c.Label == "Lobby");
        Assert.Equal(new Proportion(3, 3), lobby.Precision);
        Assert.Equal(new Proportion(3, 4), lobby.Recall);

        var results = r.Classes.Single(c => c.Label == "Results");
        Assert.Equal(new Proportion(3, 4), results.Precision); // the mismatching unit was confirmed as Results
        Assert.Equal(1, results.FalsePositives);
        Assert.Equal(new Proportion(1, 8), results.FalsePositiveRate); // 8 legible units are not Results

        var trial = r.Classes.Single(c => c.Label == "InTrial");
        Assert.Equal(new Proportion(3, 3), trial.Precision);
        Assert.Equal(1, trial.Abstained);
    }

    [Fact]
    public void RealDetectorPipeline_TimeToDetectIsTheConfirmationLatency()
    {
        // One frame per second, K = 3 with 1.5 s dwell: the third frame (t = 2 s) confirms every correct unit.
        var r = s_report.Value;

        Assert.Equal(2.0, r.MedianSecondsToDetect);
        Assert.Equal(2.0, r.P95SecondsToDetect);
    }

    [Fact]
    public void ReportIsMarkedMechanicsOnly_AndNoGateIsClaimed()
    {
        var r = s_report.Value;
        var markdown = EvaluationReportWriter.ToMarkdown(r);

        Assert.StartsWith("# Detection evaluation: scene (PhaseDetector) v1", markdown, StringComparison.Ordinal);
        Assert.Contains("MECHANICS ONLY", markdown, StringComparison.Ordinal);
        Assert.False(r.Gates.Applicable);
        Assert.Equal(markdown, EvaluationReportWriter.ToMarkdown(Evaluate())); // a second full run gives the identical report (byte for byte)
    }

    [Fact]
    public void AnUncalibratedDetector_AbstainsOnEveryUnit()
    {
        var detector = new PhaseDetector();
        var classifier = DetectorClassifier.Create(detector, ObservationKind.Scene, SyntheticScenes.Context());

        var r = DetectionEvaluator.Run(SyntheticSet(), classifier, new EvaluationOptions("synthetic-scenes", "scene", 1, "none", DataOrigin.Synthetic, s_rule));

        Assert.Equal(0, r.Confirmed);
        Assert.Equal(11, r.Abstained);
        Assert.Null(r.Precision.Value); // UNKNOWN: nothing was published
    }

    [Fact]
    public void RoleDetector_CanBeEvaluatedTheSameWay()
    {
        var detector = new RoleDetector(SyntheticScenes.CalibrateRoles());
        var classifier = DetectorClassifier.Create(detector, ObservationKind.Role, SyntheticScenes.Context());
        var units = new List<EvaluationUnit>();
        var n = 0;
        foreach (var role in new[] { DbdPresence.Core.Game.Role.Survivor, DbdPresence.Core.Game.Role.Killer })
        {
            foreach (var width in new[] { 320, 640, 960 })
            {
                var frames = Enumerable.Range(0, 4).Select(i => new EvaluationFrame(s_t0.AddSeconds(i), SyntheticScenes.RenderRole(role, width, seed: width + i))).ToList();
                units.Add(new EvaluationUnit($"r{n++}", "s", role.ToString(), frames));
            }
        }

        var r = DetectionEvaluator.Run(units, classifier, new EvaluationOptions("synthetic-roles", "role", 1, "synthetic", DataOrigin.Synthetic, s_rule));

        Assert.Equal(new Proportion(6, 6), r.Precision);
        Assert.Equal(new Proportion(6, 6), r.Coverage);
    }

    [Fact]
    public void ADetectorAdapter_IgnoresObservationsOfOtherKinds()
    {
        var detector = new RoleDetector(SyntheticScenes.CalibrateRoles());
        var wrongKind = DetectorClassifier.Create(detector, ObservationKind.Scene, SyntheticScenes.Context());
        var frame = new EvaluationFrame(s_t0, SyntheticScenes.RenderRole(DbdPresence.Core.Game.Role.Killer, 640));

        Assert.Null(wrongKind(frame).Label);
    }
}
