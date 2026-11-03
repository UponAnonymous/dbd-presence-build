// Covers: C2 (vocabulary data: on-screen aliases, event modes, result outcomes), FR-DET-04 (closed vocabulary, margin rule, ambiguous -> unknown).
// Mechanics only: the OCR noise is invented, so these tests say nothing about how well Windows OCR reads the game (UNKNOWN).
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Text;

public sealed class VocabularyAliasTests
{
    private const string LeonId = "leon-scott-kennedy";
    private const string HauntedId = "event-haunted-by-daylight";

    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();

    // A minimal valid document; EXTRA is spliced into the survivor entry and EVENTS replaces the event-mode list.
    private const string Template = """
        {"schemaVersion":1,"language":"en","retrievedOn":"2026-10-24",
         "source":{"name":"n","urls":["https://example.org"],"licence":"l","licenceUrl":"u","attribution":"a"},
         "counts":{"killers":1,"survivors":2,"realms":1,"maps":1},
         "killers":[{"id":"k","code":"K01","name":"The K"}],
         "survivors":[{"id":"s","code":"S01","name":"Sam Smith"EXTRA},{"id":"t","code":"S02","name":"Tina Turner"}],
         "realms":[{"id":"r","name":"R"}],"maps":[{"id":"r/m","name":"M","realm":"r"}],
         "modes":[{"id":"m","name":"Mode"}],
         "eventModes":EVENTS,"resultOutcomes":[{"id":"o","name":"Outcome"}]}
        """;

    private static string Doc(string extra = "", string events = "[{\"id\":\"e\",\"name\":\"Event\"}]") =>
        Template.Replace("EXTRA", extra, StringComparison.Ordinal).Replace("EVENTS", events, StringComparison.Ordinal);

    private static VocabularyMatch Survivor(string text) => s_vocabulary.MatcherFor(VocabularyKind.Survivor).Match(text);

    [Fact]
    public void LeonAlias_IsInTheData_WithItsProvenance()
    {
        var leon = Assert.Single(s_vocabulary.Entries(VocabularyKind.Survivor), e => e.Id == LeonId);

        Assert.Equal("Leon Scott Kennedy", leon.Name);
        var alias = Assert.Single(leon.Aliases);
        Assert.Equal("Leon S. Kennedy", alias.Name);
        Assert.Equal("2026-10-24, owner screenshot, pre-match lobby and scoreboard", alias.SeenOnScreen);
    }

    [Fact]
    public void EveryAliasInTheData_CarriesOwnerScreenshotProvenance()
    {
        foreach (var kind in Enum.GetValues<VocabularyKind>())
        {
            foreach (var alias in s_vocabulary.Entries(kind).SelectMany(e => e.Aliases))
            {
                Assert.False(string.IsNullOrWhiteSpace(alias.SeenOnScreen), $"{kind} alias '{alias.Name}' has no provenance");
                Assert.Contains("owner screenshot", alias.SeenOnScreen, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("LEON S. KENNEDY")]
    [InlineData("Leon S. Kennedy")]
    [InlineData("LE0N S. KENNEDY")]
    [InlineData("LEON S KENNEDY")]
    [InlineData("LEON 5. KENNEDY")]
    [InlineData("Leon Scott Kennedy")]
    [InlineData("LEON SCOTT KENNEDY")]
    public void LeonSpellings_AndOcrNoisyVariants_MatchTheSameId(string ocr)
    {
        var match = Survivor(ocr);

        Assert.True(match.IsAccepted, $"'{ocr}' was {match.Outcome} (distance {match.Distance}, runner-up {match.RunnerUpDistance})");
        Assert.Equal(LeonId, match.Entry!.Id);
    }

    [Theory]
    [InlineData("LEON")]
    [InlineData("S. KENNEDY")]
    [InlineData("KENNEDY")]
    [InlineData("LEON S.")]
    [InlineData("SCOTT KENNEDY")]
    public void PartOfLeonsName_DoesNotMatchOnItsOwn(string ocr)
    {
        var match = Survivor(ocr);

        Assert.False(match.IsAccepted, $"'{ocr}' was accepted as {match.Entry?.Name}");
        Assert.Null(match.Entry);
    }

    [Fact]
    public void AliasesOnlyApplyToTheirOwnKind()
    {
        Assert.False(s_vocabulary.MatcherFor(VocabularyKind.Killer).Match("LEON S. KENNEDY").IsAccepted);
        Assert.False(s_vocabulary.MatcherFor(VocabularyKind.Map).Match("LEON S. KENNEDY").IsAccepted);
    }

    [Fact]
    public void AnAlias_IsAnotherSpellingOfTheSameEntry_NotARivalOfIt()
    {
        var matcher = new VocabularyMatcher(
        [
            new VocabularyEntry("a", "Alexander Gregory", Aliases: [new VocabularyAlias("Alex G", "test")]),
            new VocabularyEntry("b", "Brenda Hall"),
        ]);

        Assert.Equal("a", matcher.Match("ALEX G").Entry?.Id);
        Assert.Equal("a", matcher.Match("Alexander Gregory").Entry?.Id);
        Assert.Equal("b", matcher.Match("Brenda Hall").Entry?.Id);
        Assert.Equal(MatchOutcome.NoMatch, matcher.Match("Alex").Outcome);
    }

    [Fact]
    public void WhenTwoEntriesShareASpelling_TheMatcherAbstainsInsteadOfGuessing()
    {
        // The vocabulary loader rejects this data (see below); the matcher itself must still not pick one.
        var matcher = new VocabularyMatcher(
        [
            new VocabularyEntry("a", "Alexander Gregory", Aliases: [new VocabularyAlias("Sam Jones", "test")]),
            new VocabularyEntry("b", "Samuel Jones", Aliases: [new VocabularyAlias("Sam Jones", "test")]),
        ]);

        var match = matcher.Match("SAM JONES");

        Assert.False(match.IsAccepted);
        Assert.Equal(MatchOutcome.Ambiguous, match.Outcome);
    }

    [Fact]
    public void Parse_AcceptsAnAliasWithProvenance()
    {
        var vocabulary = Vocabulary.Parse(Doc(",\"aliases\":[{\"name\":\"Sam S.\",\"seenOnScreen\":\"2026-10-24, test\"}]"));

        Assert.Equal("Sam S.", Assert.Single(vocabulary.Entries(VocabularyKind.Survivor)[0].Aliases).Name);
        Assert.Equal("s", vocabulary.MatcherFor(VocabularyKind.Survivor).Match("SAM S.").Entry?.Id);
    }

    [Theory]
    [InlineData(",\"aliases\":[{\"name\":\"Sam S.\"}]")]
    [InlineData(",\"aliases\":[{\"name\":\"Sam S.\",\"seenOnScreen\":\"\"}]")]
    [InlineData(",\"aliases\":[{\"name\":\"Sam S.\",\"seenOnScreen\":\"  \"}]")]
    [InlineData(",\"aliases\":{\"name\":\"Sam S.\"}")]
    [InlineData(",\"aliases\":[{\"seenOnScreen\":\"x\"}]")]
    [InlineData(",\"aliases\":[{\"name\":\"...\",\"seenOnScreen\":\"x\"}]")]
    public void Parse_RejectsAnAliasWithoutProvenanceOrWithoutUsableText(string extra) =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Doc(extra)));

    [Theory]
    [InlineData(",\"aliases\":[{\"name\":\"Tina Turner\",\"seenOnScreen\":\"x\"}]")] // another entry's name
    [InlineData(",\"aliases\":[{\"name\":\"TINA TURNER\",\"seenOnScreen\":\"x\"}]")] // same after casing
    [InlineData(",\"aliases\":[{\"name\":\"T1na Turner\",\"seenOnScreen\":\"x\"}]")] // same after OCR folding
    [InlineData(",\"aliases\":[{\"name\":\"Sam Smith\",\"seenOnScreen\":\"x\"}]")] // its own name
    [InlineData(",\"aliases\":[{\"name\":\"Sam S\",\"seenOnScreen\":\"x\"},{\"name\":\"SAM S.\",\"seenOnScreen\":\"x\"}]")] // twice
    public void Parse_RejectsNamesAndAliasesThatCollide(string extra) =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Doc(extra)));

    [Fact]
    public void Parse_RejectsCollidingAliasesOfDifferentEntries()
    {
        var json = Doc(",\"aliases\":[{\"name\":\"Ace\",\"seenOnScreen\":\"x\"}]")
            .Replace("\"name\":\"Tina Turner\"", "\"name\":\"Tina Turner\",\"aliases\":[{\"name\":\"ACE\",\"seenOnScreen\":\"x\"}]", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => Vocabulary.Parse(json));
    }

    [Fact]
    public void EventModes_ContainHauntedByDaylight_SeenOnScreen_AndMatchItsOcrVariants()
    {
        var haunted = Assert.Single(s_vocabulary.Entries(VocabularyKind.EventMode), e => e.Id == HauntedId);
        var matcher = s_vocabulary.MatcherFor(VocabularyKind.EventMode);

        Assert.Equal("Haunted by Daylight", haunted.Name);
        Assert.StartsWith("2026-10-24, owner screenshot", haunted.SeenOnScreen, StringComparison.Ordinal);
        foreach (var ocr in new[] { "HAUNTED BY DAYLIGHT", "Haunted by Daylight", "HAUNTED BY DAYL1GHT", "HAUNTED 8Y DAYLIGHT" })
        {
            Assert.Equal(HauntedId, matcher.Match(ocr).Entry?.Id);
        }

        Assert.False(matcher.Match("PLAY EVENT").IsAccepted);
        Assert.False(matcher.Match("HAUNTED").IsAccepted);
        Assert.False(matcher.Match("Lights Out").IsAccepted);
    }

    [Fact]
    public void WithEventModes_AddsNamesToACopy_AndLeavesTheOriginalAlone()
    {
        var before = s_vocabulary.Entries(VocabularyKind.EventMode).Count;
        var extra = new VocabularyEntry("event-summer-splash", "Summer Splash");

        var merged = s_vocabulary.WithEventModes([extra]);

        Assert.Equal(before, s_vocabulary.Entries(VocabularyKind.EventMode).Count);
        Assert.Equal(before + 1, merged.Entries(VocabularyKind.EventMode).Count);
        Assert.Equal(extra.Id, merged.MatcherFor(VocabularyKind.EventMode).Match("SUMMER SPLASH").Entry?.Id);
        Assert.Equal(HauntedId, merged.MatcherFor(VocabularyKind.EventMode).Match("HAUNTED BY DAYLIGHT").Entry?.Id);
        Assert.False(s_vocabulary.MatcherFor(VocabularyKind.EventMode).Match("SUMMER SPLASH").IsAccepted);
        Assert.Equal(s_vocabulary.RetrievedOn, merged.RetrievedOn);
        Assert.Equal(s_vocabulary.Entries(VocabularyKind.Killer), merged.Entries(VocabularyKind.Killer));
    }

    [Fact]
    public void WithEventModes_RejectsReusedIdsAndCollidingNames()
    {
        Assert.Throws<FormatException>(() => s_vocabulary.WithEventModes([new VocabularyEntry(HauntedId, "Something Else")]));
        Assert.Throws<FormatException>(() => s_vocabulary.WithEventModes([new VocabularyEntry("event-x", "HAUNTED BY DAYL1GHT")]));
        Assert.Throws<FormatException>(() => s_vocabulary.WithEventModes([new VocabularyEntry("event-x", "Winter", Aliases: [new VocabularyAlias("Haunted by Daylight", "x")])]));
        Assert.Throws<FormatException>(() => s_vocabulary.WithEventModes([new VocabularyEntry("event-x", "Winter", Aliases: [new VocabularyAlias("Snow", string.Empty)])]));
        Assert.Throws<FormatException>(() => s_vocabulary.WithEventModes([new VocabularyEntry("event-a", "Winter"), new VocabularyEntry("event-b", "WINTER")]));
        Assert.Throws<ArgumentNullException>(() => s_vocabulary.WithEventModes(null!));
    }

    [Fact]
    public void ResultOutcomes_OnlyEscapedIsSeenOnScreen_AndTheRestCiteTheWiki()
    {
        var outcomes = s_vocabulary.Entries(VocabularyKind.ResultOutcome);

        var escaped = Assert.Single(outcomes, e => e.Id == "outcome-escaped");
        Assert.Equal("Escaped", escaped.Name);
        Assert.StartsWith("2026-10-24, owner screenshot", escaped.SeenOnScreen, StringComparison.Ordinal);
        Assert.All(outcomes.Where(e => e.Id != "outcome-escaped"), e => Assert.Null(e.SeenOnScreen));
        Assert.Contains("https://deadbydaylight.wiki.gg/wiki/Tally_Screen", s_vocabulary.Source.Urls);
        Assert.Equal(
            ["Brutal Killer", "Dead", "Escaped", "Merciless Killer", "Ruthless Killer", "Sacrificed", "The Entity Hungers..."],
            outcomes.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ESCAPED", "outcome-escaped")]
    [InlineData("E5CAPED", "outcome-escaped")]
    [InlineData("SACRIFICED", "outcome-sacrificed")]
    [InlineData("MERC1LESS KILLER", "outcome-merciless-killer")]
    [InlineData("ENTITY HUNGERS", "outcome-entity-hungers")]
    public void ResultOutcomes_MatchTheirOcrVariants(string ocr, string expectedId) =>
        Assert.Equal(expectedId, s_vocabulary.MatcherFor(VocabularyKind.ResultOutcome).Match(ocr).Entry?.Id);

    [Theory]
    [InlineData("MATCH RESULT")]
    [InlineData("KILLER")]
    [InlineData("ESCAP")]
    [InlineData("Victory")]
    public void ResultOutcomes_AbstainOnOtherText(string ocr) =>
        Assert.False(s_vocabulary.MatcherFor(VocabularyKind.ResultOutcome).Match(ocr).IsAccepted);
}
