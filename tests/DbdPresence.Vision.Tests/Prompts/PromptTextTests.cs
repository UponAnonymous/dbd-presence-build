// Covers: FR-DET-22 follow-up (power prompts). MECHANICS ONLY: strings, not screens.
using DbdPresence.Vision.Prompts;

namespace DbdPresence.Vision.Tests.Prompts;

public sealed class PromptTextTests
{
    [Theory]
    [InlineData("E  VILE DARKNESS", "VILE DARKNESS")]
    [InlineData("[RMB] CAST FLY", "CAST FLY")]
    [InlineData("<R2> CAST FLY", "CAST FLY")]
    [InlineData("RB CAST FLY", "CAST FLY")]
    [InlineData("CAST FLY (E)", "CAST FLY")]
    [InlineData("12 CAST FLY", "CAST FLY")]
    [InlineData("CAST FLY", "CAST FLY")]
    public void KeyGlyphs_AreRemoved_AndWordsKept(string line, string expected)
    {
        Assert.Equal(expected, PromptText.WithoutKeyGlyphs(line));
    }

    [Theory]
    [InlineData("vile  darkness!", "VILE DARKNESS")]
    [InlineData("[E] Cast Fly", "CAST FLY")]
    [InlineData("SPRINT", "SPRINT")]
    public void ACandidate_IsUpperCaseLettersAndSingleSpaces(string line, string expected)
    {
        Assert.Equal(expected, PromptText.Candidate(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OK")]
    [InlineData("FOUR")]
    [InlineData("ABC DEF")] // six letters but no word of four
    [InlineData("12 34 56")]
    [InlineData("ONE TWO THREE FOUR FIVE SIX")] // six words
    [InlineData("THIS PHRASE IS FAR TOO LONG TO BE A PROMPT")] // more than forty characters
    public void ALineThatIsNotPromptShaped_IsNotACandidate(string line)
    {
        Assert.Null(PromptText.Candidate(line));
    }

    [Fact]
    public void Lines_SplitsOnAnyNewline_AndDropsEmptyOnes()
    {
        Assert.Equal(["A B", "C"], PromptText.Lines("A B\r\n\n C \r").ToArray());
        Assert.Empty(PromptText.Lines(null));
        Assert.Empty(PromptText.Lines("  "));
    }
}
