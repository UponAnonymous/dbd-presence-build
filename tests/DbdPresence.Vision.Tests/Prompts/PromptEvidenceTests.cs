// Covers: the owner's rule for killer identity without TAB: a prompt-based identity needs agreement (two distinct mapped prompts, or a prompt and another
// cue) before the engine may confirm. MECHANICS ONLY: ids and scores are invented values; nothing here says the game prints these prompts or that OCR reads them.
using DbdPresence.Vision.Prompts;

namespace DbdPresence.Vision.Tests.Prompts;

public sealed class PromptEvidenceTests
{
    private static PromptMatch Match(string killer, string prompt, double score = 0.95) => new(true, prompt, killer, score);

    [Fact]
    public void NothingSeen_IsNoAgreement()
    {
        var agreement = PromptEvidence.Empty.Evaluate();

        Assert.False(agreement.Agrees);
        Assert.Equal(0, agreement.DistinctPrompts);
    }

    [Fact]
    public void OnePrompt_IsNeverEnough_HoweverOftenItIsSeen()
    {
        var evidence = PromptEvidence.Empty;
        for (var i = 0; i < 20; i++)
        {
            evidence = evidence.With(Match("lich", "p1"));
        }

        var agreement = evidence.Evaluate();

        Assert.False(agreement.Agrees);
        Assert.Equal(1, agreement.DistinctPrompts);
        Assert.Contains("1 of 2", agreement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDistinctPromptsOfOneKiller_Agree_AtTheWeakerScore()
    {
        var agreement = PromptEvidence.Empty.With(Match("lich", "p1", 0.99)).With(Match("lich", "p2", 0.80)).Evaluate();

        Assert.True(agreement.Agrees);
        Assert.Equal("lich", agreement.KillerId);
        Assert.Equal(0.80, agreement.Score, 6);
        Assert.Equal(2, agreement.DistinctPrompts);
    }

    [Fact]
    public void ThreePrompts_KeepTheWeakestOfTheBestTwo()
    {
        var agreement = PromptEvidence.Empty.With(Match("lich", "p1", 0.99)).With(Match("lich", "p2", 0.70)).With(Match("lich", "p3", 0.90)).Evaluate();

        Assert.Equal(0.90, agreement.Score, 6);
    }

    [Fact]
    public void PromptsOfTwoKillers_GiveNoAgreement_EvenWhenOneHasTwo()
    {
        var agreement = PromptEvidence.Empty.With(Match("lich", "p1")).With(Match("lich", "p2")).With(Match("nightmare", "p9")).Evaluate();

        Assert.False(agreement.Agrees);
        Assert.Contains("more than one killer", agreement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnePromptWithTheSameKillerFromAnotherCue_Agrees()
    {
        var agreement = PromptEvidence.Empty.With(Match("lich", "p1", 0.9)).WithCorroboration("lich").Evaluate();

        Assert.True(agreement.Agrees);
        Assert.Equal("lich", agreement.KillerId);
        Assert.Equal(0.9, agreement.Score, 6);
    }

    [Fact]
    public void TheOtherCueOrderDoesNotMatter()
    {
        var a = PromptEvidence.Empty.WithCorroboration("lich").With(Match("lich", "p1")).Evaluate();
        var b = PromptEvidence.Empty.With(Match("lich", "p1")).WithCorroboration("lich").Evaluate();

        Assert.True(a.Agrees);
        Assert.True(b.Agrees);
    }

    [Fact]
    public void APromptThatContradictsTheOtherCue_GivesNoAgreement_EvenWithTwoPrompts()
    {
        var agreement = PromptEvidence.Empty.With(Match("lich", "p1")).With(Match("lich", "p2")).WithCorroboration("nightmare").Evaluate();

        Assert.False(agreement.Agrees);
        Assert.Contains("contradicts", agreement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherCueAlone_IsNotAPromptReading()
    {
        var agreement = PromptEvidence.Empty.WithCorroboration("lich").Evaluate();

        Assert.False(agreement.Agrees);
    }

    [Fact]
    public void AMatchThatIdentifiedNothing_ChangesNothing()
    {
        var evidence = PromptEvidence.Empty.With(Match("lich", "p1"));

        Assert.Same(evidence, evidence.With(PromptMatch.None));
    }

    [Fact]
    public void Evidence_IsImmutable()
    {
        var one = PromptEvidence.Empty.With(Match("lich", "p1"));
        var two = one.With(Match("lich", "p2"));

        Assert.False(one.Evaluate().Agrees);
        Assert.True(two.Evaluate().Agrees);
        Assert.Equal(0, PromptEvidence.Empty.DistinctPrompts);
    }

    [Fact]
    public void TheEvidenceReason_NeverContainsPromptText()
    {
        var agreement = PromptEvidence.Empty.With(Match("lich", "VILE DARKNESS")).With(Match("lich", "CAST FLY")).Evaluate();

        Assert.DoesNotContain("DARKNESS", agreement.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lich", agreement.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
