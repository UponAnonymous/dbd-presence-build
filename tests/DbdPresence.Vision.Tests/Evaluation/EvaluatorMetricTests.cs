// Covers: C5 (evaluator produces a metrics report from a labelled set with hand-verified numbers), FR-DET-16 (per-class metrics,
// Wilson bounds, deterministic report), DETECTION_VALIDATION §6/§11 (Wilson table, confusion matrix, kappa).
// MECHANICS ONLY: every unit below is scripted. None of these numbers is detector accuracy, and none comes from game data.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class EvaluatorMetricTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The confirmation rule of the scene detector, without dwell so that one frame per second confirms on the K-th frame.</summary>
    private static readonly ConfirmationRule s_rule = new(3, 4, TimeSpan.Zero, 0.5, TimeSpan.FromMinutes(1));

    private static readonly string[] s_codes = ["-", "A", "B", "C"];

    /// <summary>A unit whose frame i carries the scripted reading <c>script[i]</c> ('-' = unreadable), one frame per second.</summary>
    private static EvaluationUnit Unit(string id, string session, string? truth, string script) =>
        new(id, session, truth, script.Select((c, i) => new EvaluationFrame(s_t0.AddSeconds(i), ImageBuffer.CreateGray(1, 1, (byte)Array.IndexOf(s_codes, c.ToString())))).ToList());

    private static FramePrediction Scripted(EvaluationFrame frame)
    {
        var code = frame.Image.GrayAt(0, 0);
        return code == 0 ? new FramePrediction(null, 0) : new FramePrediction(s_codes[code], 0.9);
    }

    private static EvaluationOptions Options(DataOrigin origin = DataOrigin.Synthetic) => new("scripted-18", "scripted", 1, "ctx", origin, s_rule);

    /// <summary>
    /// The 18-unit scripted set worked out by hand (the expected numbers in the tests were derived on paper from this table, not from the output):
    /// <code>
    /// unit truth readings   result
    /// u1   A    AAAA        confirmed A at frame 3 (t = 2 s): correct
    /// u2   A    AAAA        correct, 2 s
    /// u3   A    ABAA        window [A,B,A,A] holds 3 A and 1 B (1 &lt; 3/2): correct at t = 3 s
    /// u4   A    A----       one reading only: abstain
    /// u5   A    BBBA        confirms B at t = 2 s: error
    /// u6   A    AAABBB      confirms A (t = 2 s), then B: a wrong value was confirmed in the unit: error
    /// u7   B    BBB         correct, 2 s
    /// u8   B    BBBB        correct, 2 s
    /// u9   B    BBBBB       correct, 2 s
    /// u10  B    CCC         error (C)
    /// u11  B    -----       abstain
    /// u12  C    CCC         correct, 2 s
    /// u13  C    CCCC        correct, 2 s
    /// u14  C    CCC         correct, 2 s
    /// u15  C    AAA         error (A)
    /// u16  C    CBCBCB      alternating, never confirms: abstain
    /// u17  -    AAA         no legible truth, A is confirmed: counted separately, never as correct
    /// u18  -    ---         no legible truth, nothing confirmed
    /// </code>
    /// Sessions: u1-2 s1, u3-4 s2, u5-6 s3, u7-8 s4, u9-10 s5, u11-12 s6, u13-14 s7, u15-16 s8, u17-18 s9.
    /// </summary>
    private static List<EvaluationUnit> ScriptedSet() =>
    [
        Unit("u1", "s1", "A", "AAAA"),
        Unit("u2", "s1", "A", "AAAA"),
        Unit("u3", "s2", "A", "ABAA"),
        Unit("u4", "s2", "A", "A----"),
        Unit("u5", "s3", "A", "BBBA"),
        Unit("u6", "s3", "A", "AAABBB"),
        Unit("u7", "s4", "B", "BBB"),
        Unit("u8", "s4", "B", "BBBB"),
        Unit("u9", "s5", "B", "BBBBB"),
        Unit("u10", "s5", "B", "CCC"),
        Unit("u11", "s6", "B", "-----"),
        Unit("u12", "s6", "C", "CCC"),
        Unit("u13", "s7", "C", "CCCC"),
        Unit("u14", "s7", "C", "CCC"),
        Unit("u15", "s8", "C", "AAA"),
        Unit("u16", "s8", "C", "CBCBCB"),
        Unit("u17", "s9", null, "AAA"),
        Unit("u18", "s9", null, "---"),
    ];

    private static EvaluationReport Run(IEnumerable<EvaluationUnit>? units = null, DataOrigin origin = DataOrigin.Synthetic) =>
        DetectionEvaluator.Run(units ?? ScriptedSet(), Scripted, Options(origin));

    [Fact]
    public void Totals_MatchTheHandCountedSet()
    {
        var r = Run();

        Assert.Equal(18, r.Units);
        Assert.Equal(16, r.LegibleUnits);
        Assert.Equal(1, r.ConfirmedWithoutTruth);
        Assert.Equal(8, r.Sessions); // s9 holds only illegible units
        Assert.Equal(13, r.Confirmed);
        Assert.Equal(9, r.Correct);
        Assert.Equal(3, r.Abstained);
        Assert.Equal(new Proportion(9, 13), r.Precision);
        Assert.Equal(new Proportion(9, 16), r.Coverage);
        Assert.Equal(new Proportion(3, 16), r.AbstentionRate);
    }

    [Fact]
    public void Proportions_CarryHandCheckedWilsonIntervals()
    {
        // Intervals computed independently (Python, same formula) for 9/13, 9/16 and 3/16.
        var r = Run();

        AssertInterval(0.423693, 0.873193, r.Precision.Wilson!.Value);
        AssertInterval(0.331786, 0.769013, r.Coverage.Wilson!.Value);
        AssertInterval(0.065916, 0.430089, r.AbstentionRate.Wilson!.Value);
        Assert.Equal(9.0 / 13.0, r.Precision.Value!.Value, precision: 12);
        Assert.Equal(0.5625, r.Coverage.Value!.Value, precision: 12);
        Assert.Equal(0.1875, r.AbstentionRate.Value!.Value, precision: 12);
    }

    [Fact]
    public void PerClassMetrics_MatchTheHandCountedTable()
    {
        var r = Run();

        Assert.Equal(["A", "B", "C"], r.Classes.Select(c => c.Label));

        var a = r.Classes[0];
        Assert.Equal((6, 4, 3, 1, 1), (a.TruthUnits, a.ConfirmedAsClass, a.TruePositives, a.FalsePositives, a.Abstained));
        Assert.Equal(new Proportion(3, 4), a.Precision);
        Assert.Equal(new Proportion(3, 6), a.Recall);
        Assert.Equal(new Proportion(1, 10), a.FalsePositiveRate);

        var b = r.Classes[1];
        Assert.Equal((5, 5, 3, 2, 1), (b.TruthUnits, b.ConfirmedAsClass, b.TruePositives, b.FalsePositives, b.Abstained));
        Assert.Equal(new Proportion(3, 5), b.Precision);
        Assert.Equal(new Proportion(3, 5), b.Recall);
        Assert.Equal(new Proportion(2, 11), b.FalsePositiveRate);

        var c = r.Classes[2];
        Assert.Equal((5, 4, 3, 1, 1), (c.TruthUnits, c.ConfirmedAsClass, c.TruePositives, c.FalsePositives, c.Abstained));
        Assert.Equal(new Proportion(3, 4), c.Precision);
        Assert.Equal(new Proportion(3, 5), c.Recall);
        Assert.Equal(new Proportion(1, 11), c.FalsePositiveRate);

        AssertInterval(0.300642, 0.954413, a.Precision.Wilson!.Value);
        AssertInterval(0.187616, 0.812384, a.Recall.Wilson!.Value);
        AssertInterval(0.230724, 0.882379, b.Recall.Wilson!.Value);
        Assert.All(r.Classes, cls => Assert.False(cls.Publishable)); // the per-class rule needs >= 10 units of the class
    }

    [Fact]
    public void ConfusionMatrix_MatchesTheHandCountedTable()
    {
        var m = Run().Confusion;

        Assert.Equal(["A", "B", "C"], m.Classes);
        // columns: A, B, C, abstain
        Assert.Equal([3, 2, 0, 1], m.Counts[0]); // truth A: u1 u2 u3 right; u5 u6 confirmed B; u4 abstained
        Assert.Equal([0, 3, 1, 1], m.Counts[1]); // truth B: u7 u8 u9 right; u10 confirmed C; u11 abstained
        Assert.Equal([1, 0, 3, 1], m.Counts[2]); // truth C: u12 u13 u14 right; u15 confirmed A; u16 abstained
    }

    [Fact]
    public void TimeToDetect_IsMeasuredFromTheFirstFrameToTheCorrectConfirmation()
    {
        // Correct units took 2, 2, 3, 2, 2, 2, 2, 2, 2 seconds: median 2 (5th of 9), nearest-rank p95 = ceil(.95 * 9) = 9th = 3.
        var r = Run();

        Assert.Equal(2.0, r.MedianSecondsToDetect);
        Assert.Equal(3.0, r.P95SecondsToDetect);
    }

    [Fact]
    public void AWrongValueAnywhereInAUnit_MakesItAnError_EvenAfterTheRightValueWasConfirmed()
    {
        // u6 confirmed the right value first (AAA) and then flipped to B: "silence over error" counts it as an error.
        var r = DetectionEvaluator.Run([Unit("u6", "s", "A", "AAABBB")], Scripted, Options());

        Assert.Equal(1, r.Confirmed);
        Assert.Equal(0, r.Correct);
        Assert.Null(r.MedianSecondsToDetect);
        var b = r.Classes.Single(c => c.Label == "B");
        Assert.Equal((1, 1), (b.ConfirmedAsClass, b.FalsePositives));
    }

    [Fact]
    public void NoUnits_GiveUnknownRatherThanZeroOrOne()
    {
        var r = DetectionEvaluator.Run([], Scripted, Options());

        Assert.Equal(0, r.Units);
        Assert.Null(r.Precision.Value);
        Assert.Null(r.Precision.Wilson);
        Assert.Null(r.Coverage.Value);
        Assert.Null(r.MedianSecondsToDetect);
        Assert.Empty(r.Classes);
        Assert.Contains("UNKNOWN", EvaluationReportWriter.ToMarkdown(r), StringComparison.Ordinal);
    }

    [Fact]
    public void AllAbstentions_GiveUnknownPrecisionButFullAbstention()
    {
        var r = DetectionEvaluator.Run([Unit("x", "s", "A", "-----"), Unit("y", "s", "B", "--")], Scripted, Options());

        Assert.Null(r.Precision.Value);
        Assert.Equal(new Proportion(2, 2), r.AbstentionRate);
        Assert.Equal(0, r.Correct);
    }

    [Fact]
    public void PerClassRule_RequiresTenUnitsAndZeroFalsePositives()
    {
        EvaluationUnit[] right = [.. Enumerable.Range(0, 10).Select(i => Unit($"a{i}", $"s{i}", "A", "AAA"))];
        EvaluationUnit[] withError = [.. right, Unit("e", "s", "B", "AAA")];

        Assert.True(DetectionEvaluator.Run(right, Scripted, Options()).Classes.Single().Publishable);
        Assert.False(DetectionEvaluator.Run(right.Take(9), Scripted, Options()).Classes.Single().Publishable);
        Assert.False(DetectionEvaluator.Run(withError, Scripted, Options()).Classes.Single(c => c.Label == "A").Publishable);
    }

    [Fact]
    public void Evaluation_IsDeterministic()
    {
        var first = EvaluationReportWriter.ToMarkdown(Run());
        var second = EvaluationReportWriter.ToMarkdown(Run());

        Assert.Equal(first, second);
        Assert.DoesNotContain("2026", first, StringComparison.Ordinal); // no timestamps in a report
    }

    [Fact]
    public void DuplicateUnitIds_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => DetectionEvaluator.Run([Unit("u", "s", "A", "AAA"), Unit("u", "s", "A", "AAA")], Scripted, Options()));
    }

    [Fact]
    public void SyntheticReports_AreMarkedMechanicsOnly_AndCannotEarnATier()
    {
        var synthetic = Run();
        var markdown = EvaluationReportWriter.ToMarkdown(synthetic);

        Assert.False(synthetic.Gates.Applicable);
        Assert.False(synthetic.Gates.ExperimentalMeasuredGatesMet);
        Assert.False(synthetic.Gates.ValidatedMeasuredGatesMet);
        Assert.Contains("MECHANICS ONLY", markdown, StringComparison.Ordinal);
        Assert.Contains("not detector accuracy", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedReport_ContainsTheHandCheckedFigures()
    {
        var markdown = EvaluationReportWriter.ToMarkdown(Run());

        Assert.Contains("| Precision of published values | 69.2% (9/13; 95% Wilson 42.4% to 87.3%) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Coverage (recall) | 56.3% (9/16; 95% Wilson 33.2% to 76.9%) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Abstention rate | 18.8% (3/16; 95% Wilson 6.6% to 43.0%) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| A | 6 | 4 | 3 | 1 | 1 | 75.0% (3/4; 95% Wilson 30.1% to 95.4%) | 50.0% (3/6; 95% Wilson 18.8% to 81.2%) | 10.0% (1/10; 95% Wilson 1.8% to 40.4%) | no |", markdown, StringComparison.Ordinal);
        Assert.Contains("| A | 3 | 2 | 0 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Time to detect, p95 (nearest rank) | 3.0 s |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RealDataGates_FollowTheDocumentedThresholds()
    {
        // The gate arithmetic is exercised with scripted units flagged as Real. This is a test of the thresholds, not a result.
        EvaluationUnit[] Correct(int n, int sessions) =>
            [.. Enumerable.Range(0, n).Select(i => Unit($"u{i}", $"s{i % sessions}", "A", "AAA"))];

        var exp = DetectionEvaluator.Run(Correct(30, 5), Scripted, Options(DataOrigin.Real)).Gates;
        Assert.True(exp.Applicable);
        Assert.True(exp.ExperimentalMeasuredGatesMet);
        Assert.False(exp.ValidatedMeasuredGatesMet);

        Assert.False(DetectionEvaluator.Run(Correct(29, 5), Scripted, Options(DataOrigin.Real)).Gates.ExperimentalMeasuredGatesMet);
        Assert.False(DetectionEvaluator.Run(Correct(30, 4), Scripted, Options(DataOrigin.Real)).Gates.ExperimentalMeasuredGatesMet);
        EvaluationUnit[] oneError = [.. Correct(30, 5).Take(29), Unit("bad", "s1", "B", "AAA")];
        Assert.False(DetectionEvaluator.Run(oneError, Scripted, Options(DataOrigin.Real)).Gates.ExperimentalMeasuredGatesMet);

        // Coverage below 50%: 30 correct units plus 31 abstentions.
        EvaluationUnit[] sparse = [.. Correct(30, 5), .. Enumerable.Range(0, 31).Select(i => Unit($"q{i}", "s1", "A", "---"))];
        Assert.False(DetectionEvaluator.Run(sparse, Scripted, Options(DataOrigin.Real)).Gates.ExperimentalMeasuredGatesMet);

        // Validated: Wilson lower bound >= 99% needs 381 error-free units, >= 10 sessions.
        Assert.True(DetectionEvaluator.Run(Correct(381, 10), Scripted, Options(DataOrigin.Real)).Gates.ValidatedMeasuredGatesMet);
        Assert.False(DetectionEvaluator.Run(Correct(380, 10), Scripted, Options(DataOrigin.Real)).Gates.ValidatedMeasuredGatesMet);
        Assert.False(DetectionEvaluator.Run(Correct(381, 9), Scripted, Options(DataOrigin.Real)).Gates.ValidatedMeasuredGatesMet);
        Assert.NotEmpty(DetectionEvaluator.Run(Correct(381, 10), Scripted, Options(DataOrigin.Real)).Gates.NotAssessed);
    }

    private static void AssertInterval(double lower, double upper, Interval actual)
    {
        Assert.Equal(lower, actual.Lower, precision: 6);
        Assert.Equal(upper, actual.Upper, precision: 6);
    }
}
