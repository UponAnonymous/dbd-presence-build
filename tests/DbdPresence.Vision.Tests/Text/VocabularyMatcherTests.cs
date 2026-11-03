// Covers: C2 (vocabulary matcher margin and ambiguity rules), FR-DET-04, DETECTION_VALIDATION §8 ("ambiguous map names resolve to
// the realm only, or to unknown"). Mechanics only: tiny invented vocabularies.
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Text;

public sealed class VocabularyMatcherTests
{
    private static VocabularyMatcher Matcher(params (string Id, string Name, string? Group)[] entries) =>
        new(entries.Select(e => new VocabularyEntry(e.Id, e.Name, e.Group)));

    [Fact]
    public void Levenshtein_MatchesHandComputedDistances()
    {
        Assert.Equal(0, TextNormalizer.Levenshtein("kitten", "kitten"));
        Assert.Equal(3, TextNormalizer.Levenshtein("kitten", "sitting"));
        Assert.Equal(4, TextNormalizer.Levenshtein("", "abcd"));
        Assert.Equal(4, TextNormalizer.Levenshtein("abcd", ""));
        Assert.Equal(1, TextNormalizer.Levenshtein("flaw", "flow"));
        Assert.Equal(0.5, TextNormalizer.NormalizedDistance("abcd", "ab"));
        Assert.Equal(0.0, TextNormalizer.NormalizedDistance(string.Empty, string.Empty));
    }

    [Theory]
    [InlineData("  The  Pale-Rose!! ", "the pale rose")]
    [InlineData("Léry’s Memorial", "lerys memorial")]
    [InlineData("Onryō", "onryo")]
    [InlineData("Troupe®", "troupe")]
    [InlineData("A|B!", "alb")]
    [InlineData("|ronworks Pa!e", "lronworks pale")]
    [InlineData("| Gas Heaven |", "gas heaven")]
    [InlineData("Hello !!", "hello")]
    [InlineData("Ørsted Łódź", "orsted lodz")]
    [InlineData("", "")]
    [InlineData("--- ...", "")]
    public void Normalize_StripsCaseDiacriticsQuotesAndPunctuation(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Theory]
    [InlineData("Coa1 T0wer", "coal tower")]
    [InlineData("Torrnent", "torment")]
    [InlineData("Torment", "torment")]
    [InlineData("vvolf", "wolf")]
    [InlineData("S1B5", "slbs")]
    [InlineData("2V8", "zvb")]
    [InlineData("Illinois", "llllnols")]
    public void Skeleton_FoldsOcrConfusableCharacters(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Skeleton(input));

    [Fact]
    public void Accepts_WhenTheBestIsCloseEnoughAndClearlyAheadOfTheRest()
    {
        var matcher = Matcher(("a", "Coal Tower", "r1"), ("b", "Groaning Storehouse", "r1"));

        var result = matcher.Match("Coal Towor");

        Assert.Equal(MatchOutcome.Accepted, result.Outcome);
        Assert.Equal("a", result.Entry!.Id);
        Assert.Equal("r1", result.Group);
        Assert.Equal(0.1, result.Distance!.Value, precision: 9);
        Assert.NotNull(result.RunnerUpDistance);
    }

    [Fact]
    public void Abstains_WhenTheBestIsBeyondTheDistanceThreshold()
    {
        var matcher = Matcher(("a", "Coal Tower", null), ("b", "Groaning Storehouse", null));

        var result = matcher.Match("Coal Mine");

        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        Assert.Null(result.Entry);
        Assert.True(result.Distance > 0.2);
    }

    [Fact]
    public void Abstains_WhenTheTopTwoAreEquallyClose_EvenIfBothAreWithinTheThreshold()
    {
        // The reading "Owe" is one substitution away from both "One" and "Own".
        var matcher = Matcher(("one", "Alpha Hall One", null), ("two", "Alpha Hall Own", null));

        var result = matcher.Match("Alpha Hall Owe");

        Assert.Equal(MatchOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Entry);
        Assert.Equal(result.Distance, result.RunnerUpDistance);
    }

    [Fact]
    public void Abstains_WhenTheTopTwoAreNearlyTied()
    {
        // 1 edit to "Alpha Hallx" versus 2 edits to "Alpha Hallxy": the gap is one edit and 0.07 normalised, below both margins.
        var matcher = Matcher(("a", "Alpha Hallx", null), ("b", "Alpha Hallxy", null));

        var result = matcher.Match("Alpha Halla");

        Assert.Equal(MatchOutcome.Ambiguous, result.Outcome);
    }

    [Fact]
    public void Accepts_WhenTheSecondIsFarEnoughAwayInEdits_EvenIfTheNormalisedGapIsSmall()
    {
        // Two long names that differ by two letters: an exact reading is 2 edits ahead, which is enough.
        var matcher = Matcher(("e", "Raccoon City Police Station East Wing", "rc"), ("w", "Raccoon City Police Station West Wing", "rc"));

        var exact = matcher.Match("Raccoon City Police Station East Wing");

        Assert.Equal(MatchOutcome.Accepted, exact.Outcome);
        Assert.Equal("e", exact.Entry!.Id);
        Assert.True(exact.RunnerUpDistance < 0.10);
    }

    [Fact]
    public void ResolvesToTheGroup_WhenTheCandidatesAreAmbiguousButShareARealm()
    {
        var matcher = Matcher(("e", "Raccoon City Police Station East Wing", "rc"), ("w", "Raccoon City Police Station West Wing", "rc"), ("x", "Dead Sands", "fb"));

        var result = matcher.Match("Raccoon City Police Station Eest Wing");

        Assert.Equal(MatchOutcome.GroupOnly, result.Outcome);
        Assert.Equal("rc", result.Group);
        Assert.Null(result.Entry);
    }

    [Fact]
    public void StaysAmbiguous_WhenTheCandidatesBelongToDifferentGroups()
    {
        var matcher = Matcher(("a", "Alpha Hall One", "g1"), ("b", "Alpha Hall Own", "g2"));

        var result = matcher.Match("Alpha Hall Owe");

        Assert.Equal(MatchOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.Group);
    }

    [Fact]
    public void StaysAmbiguous_WhenNoGroupIsKnown()
    {
        var matcher = Matcher(("a", "Alpha Hall One", null), ("b", "Alpha Hall Own", null));

        Assert.Equal(MatchOutcome.Ambiguous, matcher.Match("Alpha Hall Owe").Outcome);
    }

    [Fact]
    public void TheLeadingThe_IsOptionalInTheReading_OnlyForLongEnoughNames()
    {
        var matcher = Matcher(("pr", "The Pale Rose", null), ("g", "The Game", null), ("o", "Other Place", null));

        Assert.Equal("pr", matcher.Match("Pale Rose").Entry!.Id);
        Assert.Equal(MatchOutcome.NoMatch, matcher.Match("Game").Outcome); // 'Game' is too short to count as a safe alias
    }

    [Fact]
    public void ShortAndEmptyTexts_AreNeverMatched()
    {
        var matcher = Matcher(("a", "Coal Tower", null));

        Assert.Equal(MatchOutcome.Empty, matcher.Match(null).Outcome);
        Assert.Equal(MatchOutcome.Empty, matcher.Match("  ... ").Outcome);
        Assert.Equal(MatchOutcome.TooShort, matcher.Match("Coa").Outcome);
    }

    [Fact]
    public void AVocabularyWithAShortName_CanMatchItExactly()
    {
        var matcher = Matcher(("m", "2v8", null), ("c", "Custom Game", null));

        Assert.Equal("m", matcher.Match("2V8").Entry!.Id);
        Assert.Equal(MatchOutcome.NoMatch, matcher.Match("3v8").Outcome);
    }

    [Fact]
    public void ASingleEntryVocabulary_StillAppliesTheDistanceThreshold()
    {
        var matcher = Matcher(("a", "Coal Tower", null));

        Assert.Equal(MatchOutcome.Accepted, matcher.Match("Coal Towor").Outcome);
        Assert.Equal(MatchOutcome.NoMatch, matcher.Match("Something else entirely").Outcome);
    }

    [Fact]
    public void Constructor_RejectsEmptyVocabulariesAndUnusableNames()
    {
        Assert.Throws<ArgumentException>(() => new VocabularyMatcher([]));
        Assert.Throws<ArgumentException>(() => new VocabularyMatcher([new VocabularyEntry("x", "...")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VocabularyMatcher([new VocabularyEntry("x", "Name")], new VocabularyMatcherOptions(MaxDistance: 1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VocabularyMatcher([new VocabularyEntry("x", "Name")], new VocabularyMatcherOptions(MinEditMargin: 0)));
    }

    [Fact]
    public void StricterThresholds_TurnAnAcceptedReadingIntoAnAbstention()
    {
        var entries = new[] { new VocabularyEntry("a", "Coal Tower"), new VocabularyEntry("b", "Groaning Storehouse") };
        var strict = new VocabularyMatcher(entries, new VocabularyMatcherOptions(MaxDistance: 0.05));

        Assert.Equal(MatchOutcome.NoMatch, strict.Match("Coal Towor").Outcome);
        Assert.Equal(MatchOutcome.Accepted, strict.Match("Coal Tower").Outcome);
    }
}
