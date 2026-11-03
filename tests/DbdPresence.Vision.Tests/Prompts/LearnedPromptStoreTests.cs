// Covers: the learning path for power prompts (the owner's app keeps what it learns on this PC), D31 and D35; FR-DET-23; QA review F4 and F5
// (trust needs negative evidence; only prompt-shaped text is kept); security review L2 and L3 (malformed files are FormatException; shape guard on input).
// MECHANICS ONLY: texts and killer ids are test values.
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Prompts;

namespace DbdPresence.Vision.Tests.Prompts;

public sealed class LearnedPromptStoreTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static void Match(LearnedPromptStore store, string killer, params string[] prompts) =>
        store.PromptsAssociated(new PromptAssociation(killer, prompts, s_now));

    [Fact]
    public void APromptSeenInOneMatch_IsNotYetTrusted()
    {
        var store = new LearnedPromptStore();

        Match(store, "the-lich", "SCREAMING WAIL");

        Assert.Empty(store.Trusted());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void APromptTiedToOneKillerInTwoMatches_AndAbsentFromAnotherKillersMatch_IsTrusted()
    {
        var store = new LearnedPromptStore();

        Match(store, "the-lich", "SCREAMING WAIL");
        Match(store, "the-lich", "SCREAMING WAIL", "OTHER SPELL");
        Match(store, "the-nightmare", "DREAM SNARE");

        var trusted = Assert.Single(store.Trusted());
        Assert.Equal(new LearnedPrompt("SCREAMING WAIL", "the-lich", 2), trusted);
    }

    [Fact]
    public void TwoMatchesOfOneKiller_WithNoMatchOfAnotherKiller_AreNotEnough_ForGenericText()
    {
        var store = new LearnedPromptStore();

        Match(store, "the-lich", "PICK UP SURVIVOR");
        Match(store, "the-lich", "PICK UP SURVIVOR");

        Assert.Empty(store.Trusted());
    }

    [Fact]
    public void ATextThatEveryKillersMatchesShow_NeverBecomesTrusted_EvenForTheFirstKiller()
    {
        var store = new LearnedPromptStore();

        Match(store, "the-lich", "PICK UP SURVIVOR");
        Match(store, "the-lich", "PICK UP SURVIVOR");
        Match(store, "the-nightmare", "PICK UP SURVIVOR");

        Assert.Empty(store.Trusted());
    }

    [Fact]
    public void ALaterMatchOfAnotherKillerWithoutTheText_TurnsEvidenceIntoTrust()
    {
        var store = new LearnedPromptStore();
        Match(store, "the-lich", "SCREAMING WAIL");
        Match(store, "the-lich", "SCREAMING WAIL");
        Assert.Empty(store.Trusted());

        Match(store, "the-nightmare");

        Assert.Single(store.Trusted());
    }

    [Theory]
    [InlineData("12 34")]
    [InlineData("OK")]
    [InlineData("ONE TWO THREE FOUR FIVE SIX SEVEN")]
    [InlineData("A VERY LONG SENTENCE OF MORE THAN FORTY CHARACTERS")]
    public void TextThatIsNotPromptShaped_IsNeverKept_AndNeverWritten(string text)
    {
        var store = new LearnedPromptStore();

        Match(store, "the-lich", text);
        Match(store, "the-lich", text);

        Assert.Equal(0, store.Count);
        Assert.DoesNotContain(text, store.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneMatch_AddsAtMostAHandfulOfTexts()
    {
        var store = new LearnedPromptStore();
        var texts = Enumerable.Range(0, 40).Select(i => "SPELL " + new string((char)('A' + (i % 8)), 5) + new string((char)('A' + (i / 8)), 2)).ToArray();

        Match(store, "the-lich", texts);

        Assert.Equal(LearnedPromptStore.MaxPromptsPerMatch, store.Count);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":[{\"text\":\"SCREAMING WAIL\",\"killers\":5}]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":\"x\"}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"matchesByKiller\":{},\"prompts\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"matchesByKiller\":[{\"killer\":\"nobody\",\"matches\":1}],\"prompts\":[]}")]
    public void AFileWithTheWrongShape_IsAFormatException_NotAnInvalidOperation(string json)
    {
        Assert.Throws<FormatException>(() => LearnedPromptStore.FromJson(json, id => id is "the-lich"));
    }

    [Fact]
    public void AGenericPromptTiedToTwoKillers_IsNeverTrusted_HoweverOftenItIsSeen()
    {
        var store = new LearnedPromptStore();
        for (var i = 0; i < 5; i++)
        {
            Match(store, "the-lich", "PICK UP SURVIVOR");
            Match(store, "the-nightmare", "PICK UP SURVIVOR");
        }

        Assert.Empty(store.Trusted());
    }

    [Fact]
    public void ThePromptsOfOneMatch_CountOncePerMatch()
    {
        var store = new LearnedPromptStore();

        Match(store, "the-lich", "SCREAMING WAIL", "SCREAMING WAIL", "Screaming  wail");

        Assert.Empty(store.Trusted());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void TheStore_IsBounded()
    {
        var store = new LearnedPromptStore();
        for (var i = 0; i < LearnedPromptStore.MaxPrompts + 50; i++)
        {
            Match(store, "the-lich", "SPELL " + new string((char)('A' + (i % 26)), 3) + new string((char)('A' + (i / 26 % 26)), 3) + new string((char)('A' + (i / 676)), 3));
        }

        Assert.Equal(LearnedPromptStore.MaxPrompts, store.Count);
    }

    [Fact]
    public void AnUnrecognisedKiller_IsCounted_WithoutKeepingAnyText()
    {
        var store = new LearnedPromptStore();

        store.KillerNotRecognised(new UnmatchedKillerPrompts(["SOME SPELL"], s_now));

        Assert.Equal(1, store.UnrecognisedKillerMatches);
        Assert.Equal(0, store.Count);
        Assert.DoesNotContain("SOME SPELL", store.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStore_SurvivesAJsonRoundTrip()
    {
        var store = new LearnedPromptStore();
        Match(store, "the-lich", "SCREAMING WAIL");
        Match(store, "the-lich", "SCREAMING WAIL");
        Match(store, "the-nightmare", "PICK UP SURVIVOR");
        Assert.Single(store.Trusted());
        store.KillerNotRecognised(new UnmatchedKillerPrompts([], s_now));

        var again = LearnedPromptStore.FromJson(store.ToJson(), id => id is "the-lich" or "the-nightmare");

        Assert.Equal(store.Trusted(), again.Trusted());
        Assert.Equal(2, again.Count);
        Assert.Equal(1, again.UnrecognisedKillerMatches);
        Assert.Equal(store.ToJson(), again.ToJson());
    }

    [Fact]
    public void ThePersistedJson_HoldsTrustedPromptsOnly_AndStillRoundTripsToTheSameTrust()
    {
        var store = new LearnedPromptStore();
        Match(store, "the-lich", "SCREAMING WAIL", "SEEN ONCE");
        Match(store, "the-lich", "SCREAMING WAIL");
        Match(store, "the-nightmare", "DREAM SNARE");

        var persisted = store.ToPersistedJson();

        Assert.Contains("SCREAMING WAIL", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("SEEN ONCE", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("DREAM SNARE", persisted, StringComparison.Ordinal);
        Assert.Contains("SEEN ONCE", store.ToJson(), StringComparison.Ordinal);
        var again = LearnedPromptStore.FromJson(persisted, id => id is "the-lich" or "the-nightmare");
        Assert.Equal(store.Trusted(), again.Trusted());
    }

    [Fact]
    public void ThePersistedJson_LeavesOutATextTiedToMoreThanOneKiller_EvenWhenEachTieHasTwoMatches()
    {
        var store = new LearnedPromptStore();
        Match(store, "the-lich", "SCREAMING WAIL", "ON SCREEN EVERYWHERE");
        Match(store, "the-lich", "SCREAMING WAIL", "ON SCREEN EVERYWHERE");
        Match(store, "the-nightmare", "DREAM SNARE", "ON SCREEN EVERYWHERE");
        Match(store, "the-nightmare", "DREAM SNARE", "ON SCREEN EVERYWHERE");

        var persisted = store.ToPersistedJson();

        Assert.Contains("SCREAMING WAIL", persisted, StringComparison.Ordinal);
        Assert.Contains("DREAM SNARE", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("ON SCREEN EVERYWHERE", persisted, StringComparison.Ordinal);
        Assert.Contains("ON SCREEN EVERYWHERE", store.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePersistedJson_LeavesOutATextUntilItWasAbsentFromAMatchAsAnotherKiller()
    {
        var store = new LearnedPromptStore();
        Match(store, "the-lich", "SCREAMING WAIL");
        Match(store, "the-lich", "SCREAMING WAIL");

        // Two matches as the same killer prove nothing: the text may be on screen in every match.
        Assert.Empty(store.Trusted());
        Assert.DoesNotContain("SCREAMING WAIL", store.ToPersistedJson(), StringComparison.Ordinal);

        Match(store, "the-nightmare");

        Assert.Single(store.Trusted());
        Assert.Contains("SCREAMING WAIL", store.ToPersistedJson(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"unrecognisedKillerMatches\":0,\"prompts\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":[{\"text\":\"SCREAMING WAIL\",\"killers\":[{\"killer\":\"nobody\",\"matches\":2}]}]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":[{\"text\":\"SCREAMING WAIL\",\"killers\":[{\"killer\":\"the-lich\",\"matches\":0}]}]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":[{\"text\":\"SCREAMING WAIL\",\"killers\":[]}]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":[{\"text\":\"12\",\"killers\":[{\"killer\":\"the-lich\",\"matches\":2}]}]}")]
    [InlineData("{\"schemaVersion\":1,\"unrecognisedKillerMatches\":0,\"prompts\":[{\"text\":\"SCREAMING WAIL\",\"killers\":[{\"killer\":\"the-lich\",\"matches\":2}]},{\"text\":\"screaming wail\",\"killers\":[{\"killer\":\"the-lich\",\"matches\":2}]}]}")]
    [InlineData("not json")]
    public void AFileThatIsWrong_IsRefused(string json)
    {
        Assert.Throws<FormatException>(() => LearnedPromptStore.FromJson(json, id => id is "the-lich"));
    }

    [Fact]
    public void AHugeFile_IsRefused()
    {
        Assert.Throws<FormatException>(() => LearnedPromptStore.FromJson(new string(' ', 300 * 1024), _ => true));
    }

    [Fact]
    public void TrustedPrompts_FeedTheMap_AndThenNameTheirKiller()
    {
        var store = new LearnedPromptStore();
        Match(store, "the-nightmare", "SCREAMING WAIL");
        Match(store, "the-nightmare", "SCREAMING WAIL");
        Match(store, "the-lich");
        var lists = DbdPresence.Vision.Screens.HudVocabulary.Create(DbdPresence.Vision.Text.Vocabulary.LoadEmbedded());

        var map = lists.Prompts.WithLearned(store.Trusted());

        Assert.Equal("the-nightmare", map.Match("SCREAMING WAIL").KillerId);
    }
}
