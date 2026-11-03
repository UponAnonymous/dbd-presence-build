// Covers: FR-DET-22 follow-up (killer identity without opening TAB), D31 and D35 (no website is read), DETECTION_VALIDATION section 7 (nothing here is validated).
// MECHANICS ONLY: the texts are the prompts the owner's screenshots show (hud-cue-catalogue.md), fed as strings. Whether the game prints them exactly
// like this, and whether OCR reads them, is UNKNOWN until the evaluator has run on the owner's recordings.
using DbdPresence.Vision.Prompts;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Prompts;

public sealed class PowerPromptMapTests
{
    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();
    private static readonly HudVocabulary s_lists = HudVocabulary.Create(s_vocabulary);

    private static string Json(params string[] prompts) =>
        "{\"schemaVersion\":1,\"language\":\"en\",\"version\":1,\"dated\":\"2026-10-25\",\"provenance\":\"test\",\"prompts\":[" + string.Join(',', prompts) + "]}";

    private static string Prompt(string id, string text, string killer = "the-lich", string seen = "test") =>
        $"{{\"id\":\"{id}\",\"text\":\"{text}\",\"killer\":\"{killer}\",\"seenOnScreen\":\"{seen}\"}}";

    private static PowerPromptMap Parse(params string[] prompts) => PowerPromptMap.Parse(Json(prompts), s_lists.IsKillerId);

    [Fact]
    public void TheCommittedList_HoldsOnlyPromptsSeenOnTheOwnersScreen_AllForTheLich()
    {
        var map = s_lists.Prompts;

        Assert.Equal(1, map.Version);
        Assert.Equal(3, map.Entries.Count);
        Assert.All(map.Entries, e =>
        {
            Assert.Equal("the-lich", e.KillerId);
            Assert.Equal(PromptSource.Seeded, e.Source);
            Assert.Contains("2026-10-24", e.SeenOnScreen, StringComparison.Ordinal);
        });
        Assert.Equal(["CAST FLIGHT OF THE DAMNED", "CAST FLY", "VILE DARKNESS"], map.Entries.Select(e => e.Text).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("VILE DARKNESS", "the-lich/vile-darkness")]
    [InlineData("CAST FLY", "the-lich/cast-fly")]
    [InlineData("CAST FLIGHT OF THE DAMNED", "the-lich/cast-flight-of-the-damned")]
    [InlineData("[RMB] CAST FLY", "the-lich/cast-fly")]
    [InlineData("E  VILE DARKNESS", "the-lich/vile-darkness")]
    [InlineData("vile darkness", "the-lich/vile-darkness")]
    [InlineData("VILE DARKNE55", "the-lich/vile-darkness")]
    [InlineData("noise\nCAST FLY\n", "the-lich/cast-fly")]
    public void AMappedPrompt_IsRecognised_WithGlyphsCaseAndOcrNoise(string text, string expected)
    {
        var match = s_lists.Prompts.Match(text);

        Assert.True(match.Accepted);
        Assert.Equal(expected, match.PromptId);
        Assert.Equal("the-lich", match.KillerId);
        Assert.InRange(match.Score, 0.7, 1.0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PICK UP SURVIVOR")]
    [InlineData("CAST")]
    [InlineData("DARKNESS FALLS")]
    [InlineData("THE LICH")]
    public void AnythingElse_IsNotRecognised(string text)
    {
        Assert.False(s_lists.Prompts.Match(text).Accepted);
    }

    [Fact]
    public void ThePromptsOfOnePower_AreNotMixedUp()
    {
        // CAST FLY is a prefix-like neighbour of CAST FLIGHT OF THE DAMNED: each reading must land on its own prompt or on none.
        Assert.Equal("the-lich/cast-fly", s_lists.Prompts.Match("CAST FLY").PromptId);
        Assert.Equal("the-lich/cast-flight-of-the-damned", s_lists.Prompts.Match("CAST FLIGHT OF THE DAMNED").PromptId);
    }

    // ---- The file's own rules ------------------------------------------------------------------------------------------

    [Fact]
    public void APromptThatWasNotSeenOnScreen_CannotBeListed()
    {
        Assert.Throws<FormatException>(() => Parse(Prompt("a", "SOME POWER", seen: string.Empty)));
    }

    [Fact]
    public void AnUnknownKiller_IsRefused()
    {
        Assert.Throws<FormatException>(() => Parse(Prompt("a", "SOME POWER", killer: "not-a-killer")));
    }

    [Fact]
    public void ATextThatTwoKillersWouldPrint_IsRefused_BecauseItNamesNobody()
    {
        Assert.Throws<FormatException>(() => Parse(Prompt("a", "SOME POWER"), Prompt("b", "Some  power!", killer: "the-nightmare")));
    }

    [Fact]
    public void ARepeatedId_AnEmptyList_AndAWrongSchema_AreRefused()
    {
        Assert.Throws<FormatException>(() => Parse(Prompt("a", "ONE POWER"), Prompt("a", "OTHER POWER")));
        Assert.Throws<FormatException>(() => Parse());
        Assert.Throws<FormatException>(() => PowerPromptMap.Parse(Json(Prompt("a", "ONE POWER")).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal), s_lists.IsKillerId));
        Assert.Throws<FormatException>(() => PowerPromptMap.Parse(Json(Prompt("a", "ONE POWER")).Replace("\"language\":\"en\"", "\"language\":\"de\"", StringComparison.Ordinal), s_lists.IsKillerId));
    }

    [Fact]
    public void TheListMustSayWhereItCameFrom()
    {
        var json = Json(Prompt("a", "ONE POWER")).Replace("\"provenance\":\"test\"", "\"provenance\":\"\"", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => PowerPromptMap.Parse(json, s_lists.IsKillerId));
    }

    // ---- Learned prompts -----------------------------------------------------------------------------------------------

    [Fact]
    public void LearnedPrompts_AreAddedAfterTheSeededOnes_AndRecognised()
    {
        var map = s_lists.Prompts.WithLearned([new LearnedPrompt("SCREAMING WAIL", "the-nightmare", 3)]);

        var match = map.Match("SCREAMING WAIL");

        Assert.True(match.Accepted);
        Assert.Equal("the-nightmare", match.KillerId);
        Assert.StartsWith("learned/", match.PromptId, StringComparison.Ordinal);
        Assert.Equal(4, map.Entries.Count);
        Assert.Equal(3, map.Entries.Count(e => e.Source == PromptSource.Seeded));
        Assert.Contains("3 matches", map.Entries.Single(e => e.Source == PromptSource.Learned).SeenOnScreen, StringComparison.Ordinal);
    }

    [Fact]
    public void ALearnedPrompt_NeverReplacesASeededOne_OrPointsATextAtTwoKillers()
    {
        var map = s_lists.Prompts.WithLearned(
        [
            new LearnedPrompt("VILE DARKNESS", "the-nightmare", 5),
            new LearnedPrompt("SHARED TEXT", "the-nightmare", 2),
            new LearnedPrompt("Shared  text", "the-lich", 2),
        ]);

        Assert.Equal("the-lich", map.Match("VILE DARKNESS").KillerId);
        Assert.Single(map.Entries, e => e.Source == PromptSource.Learned);
    }

    [Fact]
    public void WithLearned_LeavesTheOriginalMapAlone()
    {
        var original = s_lists.Prompts;

        _ = original.WithLearned([new LearnedPrompt("SCREAMING WAIL", "the-nightmare", 3)]);

        Assert.Equal(3, original.Entries.Count);
        Assert.False(original.Match("SCREAMING WAIL").Accepted);
    }

    [Fact]
    public void AVocabularyCopyWithAMap_RefusesAKillerItDoesNotKnow()
    {
        var other = PowerPromptMap.Parse(Json(Prompt("a", "ONE POWER")), _ => true);
        var bad = other.WithLearned([new LearnedPrompt("OTHER POWER", "nobody", 2)]);

        Assert.Throws<ArgumentException>(() => s_lists.WithPromptMap(bad));
        Assert.Equal("the-lich", s_lists.WithPromptMap(s_lists.Prompts.WithLearned([new LearnedPrompt("SCREAMING WAIL", "the-nightmare", 3)])).Prompts.Match("VILE DARKNESS").KillerId);
    }
}
