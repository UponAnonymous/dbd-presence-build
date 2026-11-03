// Covers: C5, FR-DET-16 (Wilson bounds and the n/errors table), DETECTION_VALIDATION §4 (Cohen's kappa) and §6.
// Reference values were computed independently with the textbook formula; they are not read from the implementation.
using DbdPresence.Vision.Evaluation;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class WilsonAndAgreementTests
{
    [Theory]
    [InlineData(0, 381)]
    [InlineData(1, 563)]
    [InlineData(2, 726)]
    [InlineData(3, 878)]
    public void MinimumTrials_ReproducesTheSampleSizeTableInTheValidationPlan(int errors, int expectedTrials)
    {
        Assert.Equal(expectedTrials, WilsonInterval.MinimumTrials(errors, 0.99));

        // And one fewer trial is not enough.
        Assert.True(WilsonInterval.TwoSided95(expectedTrials - errors, expectedTrials).Lower >= 0.99);
        Assert.True(WilsonInterval.TwoSided95(expectedTrials - 1 - errors, expectedTrials - 1).Lower < 0.99);
    }

    [Fact]
    public void ReferencePoints_FromTheValidationPlan()
    {
        // "30/30 correct -> lower bound 88.7%; 100/100 -> 96.3%".
        Assert.Equal(0.8865, WilsonInterval.TwoSided95(30, 30).Lower, precision: 4);
        Assert.Equal(0.9630, WilsonInterval.TwoSided95(100, 100).Lower, precision: 4);
        Assert.Equal(1.0, WilsonInterval.TwoSided95(30, 30).Upper, precision: 12);
    }

    [Theory]
    [InlineData(9, 13, 0.423693, 0.873193)]
    [InlineData(3, 4, 0.300642, 0.954413)]
    [InlineData(10, 10, 0.722467, 1.0)]
    [InlineData(0, 10, 0.0, 0.277533)]
    [InlineData(381, 381, 0.990018, 1.0)]
    public void Intervals_MatchIndependentlyComputedValues(int successes, int trials, double lower, double upper)
    {
        var interval = WilsonInterval.TwoSided95(successes, trials);

        Assert.Equal(lower, interval.Lower, precision: 6);
        Assert.Equal(upper, interval.Upper, precision: 6);
    }

    [Fact]
    public void LowerBoundJustBelowTheGate_FailsAt380()
    {
        Assert.True(WilsonInterval.TwoSided95(381, 381).Lower >= 0.99);
        Assert.True(WilsonInterval.TwoSided95(380, 380).Lower < 0.99);
    }

    [Fact]
    public void Interval_IsOrderedAndNarrowsWithMoreTrials()
    {
        var small = WilsonInterval.TwoSided95(8, 10);
        var large = WilsonInterval.TwoSided95(800, 1000);

        Assert.True(small.Lower < 0.8 && 0.8 < small.Upper);
        Assert.True(large.Upper - large.Lower < small.Upper - small.Lower);
    }

    [Fact]
    public void Wilson_RejectsImpossibleCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.TwoSided95(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.TwoSided95(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.TwoSided95(6, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.MinimumTrials(-1, 0.99));
        Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.MinimumTrials(0, 1.0));
    }

    [Fact]
    public void CohensKappa_MatchesHandComputedAgreement()
    {
        // a = A A B B, b = A B B B: observed agreement 3/4; chance agreement 0.5 * 0.25 + 0.5 * 0.75 = 0.5; kappa = (0.75 - 0.5) / 0.5 = 0.5.
        Assert.Equal(0.5, AgreementStatistics.CohensKappa(["A", "A", "B", "B"], ["A", "B", "B", "B"])!.Value, precision: 12);

        // Perfect agreement over two classes: kappa = 1.
        Assert.Equal(1.0, AgreementStatistics.CohensKappa(["A", "B", "A", "B"], ["A", "B", "A", "B"])!.Value, precision: 12);

        // Complete disagreement over two balanced classes: observed 0, chance 0.5, kappa = -1.
        Assert.Equal(-1.0, AgreementStatistics.CohensKappa(["A", "B", "A", "B"], ["B", "A", "B", "A"])!.Value, precision: 12);
    }

    [Fact]
    public void CohensKappa_IsUndefinedWhenEveryLabelIsTheSame_AndRejectsBadInput()
    {
        Assert.Null(AgreementStatistics.CohensKappa(["A", "A"], ["A", "A"]));
        Assert.Throws<ArgumentException>(() => AgreementStatistics.CohensKappa(["A"], ["A", "B"]));
        Assert.Throws<ArgumentException>(() => AgreementStatistics.CohensKappa([], []));
    }
}
