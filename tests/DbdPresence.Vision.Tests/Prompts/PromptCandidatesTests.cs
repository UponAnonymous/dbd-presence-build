// Covers: security review M1 (the prompt name filter compares letters-only skeletons), FR-DET-23 (killer from power prompts), D31 and D35.
// MECHANICS ONLY: names and prompts are test values.
using DbdPresence.Vision.Prompts;
using DbdPresence.Vision.Tests.Support;

namespace DbdPresence.Vision.Tests.Prompts;

public sealed class PromptCandidatesTests
{
    private static IReadOnlyList<string> Recurring(string candidate, params string[] names)
    {
        var held = new PromptCandidates();
        var line = PromptText.Candidate(candidate) ?? throw new InvalidOperationException("not prompt shaped");
        held.Observe([line], names, HudHarness.Lists);
        held.Observe([], names, HudHarness.Lists);
        held.Observe([line], names, HudHarness.Lists);
        return held.Recurring();
    }

    [Theory]
    [InlineData("DeathSlinger77", "DEATHSLINGER IS LOOKING")]
    [InlineData("Shadow_Wolf", "SHADOW WOLF IS LOOKING")]
    [InlineData("Shadow_Wolf", "SHADOWWOLF IS LOOKING")]
    [InlineData("ShadowWolf", "SHADOW WOLF IS LOOKING")]
    [InlineData("xX_DeathSlinger_99_Xx", "DEATH SLINGER SEES YOU")]
    [InlineData("DeathSlinger77", "DEATHSLINGFR IS LOOKING")]
    [InlineData("Dea7hSlinger77", "DEATHSLINGER IS LOOKING")]
    public void ANameWithDigitsOrUnderscores_IsNeverKept(string name, string line)
    {
        Assert.Empty(Recurring(line, name));
    }

    [Fact]
    public void APromptWithNoNameInIt_IsStillKept_WhenNamesAreOnScreen()
    {
        Assert.Equal(["CAST FLY"], Recurring("CAST FLY", "DeathSlinger77", "Shadow_Wolf"));
    }

    [Fact]
    public void AShortNameNeedsAnExactMatch()
    {
        Assert.Empty(Recurring("AMY IS BACK NOW", "AMY"));
        Assert.Equal(["AMX IS BACK NOW"], Recurring("AMX IS BACK NOW", "AMY"));
    }
}
