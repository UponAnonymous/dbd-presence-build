// Covers: C2 (vocabulary data: source, date, integrity), FR-DET-04 (closed vocabulary), FR-PACK-06 (map ids resolve to names).
// The counts below are those the source wiki itself stated on the retrieval date; they are data checks, not accuracy.
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Text;

public sealed class VocabularyTests
{
    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();

    [Fact]
    public void EmbeddedVocabulary_CarriesItsSourceLicenceAndRetrievalDate()
    {
        Assert.Equal(new DateOnly(2026, 10, 24), s_vocabulary.RetrievedOn);
        Assert.Contains("wiki.gg", s_vocabulary.Source.Urls[0], StringComparison.Ordinal);
        Assert.All(s_vocabulary.Source.Urls, url => Assert.StartsWith("https://", url, StringComparison.Ordinal));
        Assert.Equal("CC BY-NC-SA 3.0", s_vocabulary.Source.Licence);
        Assert.StartsWith("https://creativecommons.org/", s_vocabulary.Source.LicenceUrl, StringComparison.Ordinal);
        Assert.Contains("Dead by Daylight Wiki", s_vocabulary.Source.Attribution, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedVocabulary_HasTheRosterSizesTheSourceStated()
    {
        Assert.Equal(44, s_vocabulary.Entries(VocabularyKind.Killer).Count);
        Assert.Equal(54, s_vocabulary.Entries(VocabularyKind.Survivor).Count);
        Assert.Equal(21, s_vocabulary.Entries(VocabularyKind.Realm).Count);
        Assert.Equal(46, s_vocabulary.Entries(VocabularyKind.Map).Count);
        Assert.Contains(s_vocabulary.Entries(VocabularyKind.Killer), e => e.Name == "The Trapper");
        Assert.Contains(s_vocabulary.Entries(VocabularyKind.Survivor), e => e.Name == "Dwight Fairfield");
        Assert.Contains(s_vocabulary.Entries(VocabularyKind.Map), e => e.Name == "Léry's Memorial Institute" || e.Name == "Treatment Theatre");
    }

    [Theory]
    [InlineData(VocabularyKind.Killer)]
    [InlineData(VocabularyKind.Survivor)]
    [InlineData(VocabularyKind.Realm)]
    [InlineData(VocabularyKind.Map)]
    [InlineData(VocabularyKind.Mode)]
    [InlineData(VocabularyKind.EventMode)]
    [InlineData(VocabularyKind.ResultOutcome)]
    public void Entries_HaveUniqueIdsNamesAndOcrSkeletons(VocabularyKind kind)
    {
        // Folding OCR-confusable characters must never make two different names identical, or the fold would hide a real difference.
        var entries = s_vocabulary.Entries(kind);

        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(entries.Count, entries.Select(e => TextNormalizer.Normalize(e.Name)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(entries.Count, entries.Select(e => TextNormalizer.Skeleton(e.Name)).Distinct(StringComparer.Ordinal).Count());

        // Names and aliases together: no spelling of one entry may fold onto a spelling of another.
        var spellings = entries.SelectMany(e => e.Aliases.Select(a => a.Name).Prepend(e.Name)).ToList();
        Assert.Equal(spellings.Count, spellings.Select(TextNormalizer.Skeleton).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(VocabularyKind.Killer)]
    [InlineData(VocabularyKind.Survivor)]
    [InlineData(VocabularyKind.Realm)]
    [InlineData(VocabularyKind.Map)]
    [InlineData(VocabularyKind.Mode)]
    [InlineData(VocabularyKind.EventMode)]
    [InlineData(VocabularyKind.ResultOutcome)]
    public void EveryEntry_MatchesItself_WithTheDefaultThresholds(VocabularyKind kind)
    {
        var matcher = s_vocabulary.MatcherFor(kind);

        foreach (var entry in s_vocabulary.Entries(kind))
        {
            foreach (var spelling in entry.Aliases.Select(a => a.Name).Prepend(entry.Name))
            {
                var match = matcher.Match(spelling);
                Assert.True(match.IsAccepted, $"{kind} '{spelling}' was {match.Outcome} (runner-up distance {match.RunnerUpDistance})");
                Assert.Equal(entry.Id, match.Entry!.Id);
                Assert.Equal(0.0, match.Distance);
            }
        }
    }

    [Fact]
    public void MapIds_ResolveToTheirNamesAndRealms_AndUnknownIdsDoNot()
    {
        var names = s_vocabulary.Resolve("coldwind-farm/rotten-fields");

        Assert.Equal(new DbdPresence.Core.Detection.MapNames("Rotten Fields", "Coldwind Farm"), names);
        Assert.Null(s_vocabulary.Resolve("coldwind-farm/not-a-map"));
        Assert.Null(s_vocabulary.Resolve(string.Empty));
        Assert.All(s_vocabulary.Entries(VocabularyKind.Map), map => Assert.NotNull(s_vocabulary.Resolve(map.Id)));
        Assert.Equal(["Coldwind Farm"], s_vocabulary.Entries(VocabularyKind.Realm).Where(r => r.Id == "coldwind-farm").Select(r => r.Name));
    }

    [Fact]
    public void MapsAreGroupedByAnExistingRealm()
    {
        var realms = s_vocabulary.Entries(VocabularyKind.Realm).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(s_vocabulary.Entries(VocabularyKind.Map), m => Assert.Contains(m.Group!, realms));
        Assert.All(s_vocabulary.Entries(VocabularyKind.Map), m => Assert.StartsWith(m.Group + "/", m.Id, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"language\":\"en\"}")]
    [InlineData("{\"schemaVersion\":1,\"language\":\"de\"}")]
    public void Parse_RejectsMalformedDocuments(string json) =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(json));

    [Fact]
    public void Parse_RejectsInconsistentData()
    {
        const string Template = """
        {"schemaVersion":1,"language":"en","retrievedOn":"2026-10-24",
         "source":{"name":"n","urls":["https://example.org"],"licence":"l","licenceUrl":"u","attribution":"a"},
         "counts":{"killers":1,"survivors":1,"realms":1,"maps":1},
         "killers":[{"id":"k","code":"K01","name":"The K"}],"survivors":[{"id":"s","code":"S01","name":"S"}],
         "realms":[{"id":"r","name":"R"}],"maps":[{"id":"r/m","name":"M","realm":"MAPREALM"}],
         "modes":[{"id":"m","name":"Mode"}],
         "eventModes":[{"id":"e","name":"Event"}],"resultOutcomes":[{"id":"o","name":"Outcome"}]}
        """;

        Assert.NotNull(Vocabulary.Parse(Template.Replace("MAPREALM", "r", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Template.Replace("MAPREALM", "missing", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Template.Replace("MAPREALM", "r", StringComparison.Ordinal).Replace("\"killers\":1", "\"killers\":2", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Template.Replace("MAPREALM", "r", StringComparison.Ordinal).Replace("2026-10-24", "yesterday", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Template.Replace("MAPREALM", "r", StringComparison.Ordinal).Replace("[\"https://example.org\"]", "[]", StringComparison.Ordinal)));
    }

    [Fact]
    public void MatcherFor_CachesTheDefaultMatcher_AndHonoursCustomThresholds()
    {
        Assert.Same(s_vocabulary.MatcherFor(VocabularyKind.Map), s_vocabulary.MatcherFor(VocabularyKind.Map));
        Assert.NotSame(s_vocabulary.MatcherFor(VocabularyKind.Map), s_vocabulary.MatcherFor(VocabularyKind.Map, new VocabularyMatcherOptions(0.1, 0.2)));
    }
}
