// Covers: FR-ROS-05 (code, id and names: the reviewed seed, 98 vocabulary codes, immutable ids), FR-ROS-04 / FR-DET-18 (Vocabulary.WithCharacters:
// append-only, collisions refused, detection unchanged for embedded entries). PLAN_V2_S5B §2.1, §2.4; sub-slice S5b-1.
using System.Text.Json;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Text;

public sealed class VocabularyCharacterTests
{
    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();

    private const string Template = """
        {"schemaVersion":1,"language":"en","retrievedOn":"2026-10-24",
         "source":{"name":"n","urls":["https://example.org"],"licence":"l","licenceUrl":"u","attribution":"a"},
         "counts":{"killers":1,"survivors":1,"realms":1,"maps":1},
         "killers":[{"id":"KID","code":"KCODE","name":"The K"}],"survivors":[{"id":"s","code":"SCODE","name":"SNAME"}],
         "realms":[{"id":"r","name":"R"}],"maps":[{"id":"r/m","name":"M","realm":"r"MAPEXTRA}],
         "modes":[{"id":"m","name":"Mode"}],
         "eventModes":[{"id":"e","name":"Event"}],"resultOutcomes":[{"id":"o","name":"Outcome"}]}
        """;

    private static string Doc(string killerId = "k", string killerCode = "\"K01\"", string survivorCode = "\"S01\"", string survivorName = "Sam", string mapExtra = "") =>
        Template.Replace("\"KCODE\"", killerCode, StringComparison.Ordinal)
            .Replace("\"SCODE\"", survivorCode, StringComparison.Ordinal)
            .Replace("KID", killerId, StringComparison.Ordinal)
            .Replace("SNAME", survivorName, StringComparison.Ordinal)
            .Replace("MAPEXTRA", mapExtra, StringComparison.Ordinal);

    private static IEnumerable<VocabularyEntry> Characters(Vocabulary vocabulary) =>
        vocabulary.Entries(VocabularyKind.Killer).Concat(vocabulary.Entries(VocabularyKind.Survivor));

    [Fact]
    public void EveryEmbeddedCharacter_HasAUniqueCodeOfItsRole()
    {
        var killers = s_vocabulary.Entries(VocabularyKind.Killer);
        var survivors = s_vocabulary.Entries(VocabularyKind.Survivor);

        Assert.All(killers, e => Assert.Matches("^K[0-9]{2}$", e.Code));
        Assert.All(survivors, e => Assert.Matches("^S[0-9]{2}$", e.Code));
        Assert.Equal(98, Characters(s_vocabulary).Select(e => e.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 44).Select(i => $"K{i:00}"), killers.Select(e => e.Code!).Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(1, 54).Select(i => $"S{i:00}"), survivors.Select(e => e.Code!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EmbeddedCodes_AgreeWithTheSharedSeed()
    {
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "roster", "seed.json")));
        Assert.Equal(1, seed.RootElement.GetProperty("schemaVersion").GetInt32());
        var rows = seed.RootElement.GetProperty("characters").EnumerateArray()
            .Select(r => (Code: r.GetProperty("code").GetString()!, Id: r.GetProperty("id").GetString()!, Role: r.GetProperty("role").GetString()!))
            .ToList();

        var expected = s_vocabulary.Entries(VocabularyKind.Killer).Select(e => (Code: e.Code!, e.Id, Role: "killer"))
            .Concat(s_vocabulary.Entries(VocabularyKind.Survivor).Select(e => (Code: e.Code!, e.Id, Role: "survivor")))
            .OrderBy(r => r.Code, StringComparer.Ordinal);
        Assert.Equal(98, rows.Count);
        Assert.Equal(expected, rows.OrderBy(r => r.Code, StringComparer.Ordinal));
    }

    [Theory]
    // The reviewed rows of docs/evidence/s5b-0-spike.md §4, including the three that only the seed can give.
    [InlineData("K05", "the-hag")]
    [InlineData("K06", "the-shape")]
    [InlineData("K25", "the-cenobite")]
    [InlineData("K27", "the-onryo")]
    [InlineData("K34", "the-good-guy")]
    [InlineData("K36", "the-lich")]
    [InlineData("K37", "the-dark-lord")]
    [InlineData("K39", "the-ghoul")]
    [InlineData("S06", "laurie-strode")]
    [InlineData("S08", "bill-overbeck")]
    [InlineData("S12", "david-tapp")]
    [InlineData("S17", "ash-williams")]
    [InlineData("S18", "steve-harrington")]
    [InlineData("S19", "nancy-wheeler")]
    [InlineData("S24", "elodie-rakoto")]
    [InlineData("S25", "lee-yun-jin")]
    [InlineData("S27", "leon-scott-kennedy")]
    [InlineData("S42", "aestri-yazar-baermar-uraz")]
    public void ReviewedCodes_MapToTheirIds(string code, string id) => Assert.Equal(id, s_vocabulary.FindByCode(code)?.Id);

    [Fact]
    public void FindByCode_ReturnsNullForAnUnknownCode()
    {
        Assert.Null(s_vocabulary.FindByCode("K99"));
        Assert.Null(s_vocabulary.FindByCode("k27"));
        Assert.Throws<ArgumentNullException>(() => s_vocabulary.FindByCode(null!));
    }

    [Theory]
    [InlineData("K27", true)]
    [InlineData("S01", true)]
    [InlineData("k27", false)]
    [InlineData("K1", false)]
    [InlineData("K001", false)]
    [InlineData("X27", false)]
    [InlineData("K2a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCharacterCode_AcceptsALetterAndTwoDigits(string? code, bool expected) => Assert.Equal(expected, Vocabulary.IsCharacterCode(code));

    [Fact]
    public void Parse_AcceptsTheMinimalDocument() => Assert.Equal("K01", Vocabulary.Parse(Doc()).FindByCode("K01")?.Code);

    [Theory]
    [InlineData("null", "\"S01\"")] // a killer without a code
    [InlineData("\"S02\"", "\"S01\"")] // a killer with a survivor code
    [InlineData("\"K01\"", "\"K02\"")] // a survivor with a killer code
    [InlineData("\"K1\"", "\"S01\"")]
    [InlineData("\"k01\"", "\"S01\"")]
    [InlineData("\"K001\"", "\"S01\"")]
    [InlineData("1", "\"S01\"")]
    public void Parse_RejectsAMissingMalformedOrWrongRoleCode(string killerCode, string survivorCode) =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Doc(killerCode: killerCode, survivorCode: survivorCode)));

    [Fact]
    public void Parse_RejectsACodeOnAnotherKind() =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Doc(mapExtra: ",\"code\":\"K02\"")));

    [Theory]
    [InlineData("s")] // the survivor's id: both would be character:s
    [InlineData("The-K")] // not a pack slug
    public void Parse_RejectsCharacterIdsThatCannotBePackKeys(string killerId) =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Doc(killerId: killerId)));

    [Fact]
    public void Parse_RejectsAKillerAndASurvivorWhoseNamesFoldTogether() =>
        Assert.Throws<FormatException>(() => Vocabulary.Parse(Doc(survivorName: "THE K")));

    [Fact]
    public void WithCharacters_AddsNewCharactersToACopy_AndLeavesTheOriginalAlone()
    {
        var killer = new VocabularyEntry("the-newcomer", "The Newcomer", Code: "K45");
        var survivor = new VocabularyEntry("robin-quill", "Robin Quill", Code: "S55");

        var merged = s_vocabulary.WithCharacters([killer], [survivor]);

        Assert.Equal(44, s_vocabulary.Entries(VocabularyKind.Killer).Count);
        Assert.Null(s_vocabulary.FindByCode("K45"));
        Assert.Equal(45, merged.Entries(VocabularyKind.Killer).Count);
        Assert.Equal(55, merged.Entries(VocabularyKind.Survivor).Count);
        Assert.Equal("the-newcomer", merged.FindByCode("K45")?.Id);
        Assert.Equal("robin-quill", merged.FindByCode("S55")?.Id);

        // Detection: the new name is matched only from read text, and the embedded entries match exactly as before.
        Assert.Equal("the-newcomer", merged.MatcherFor(VocabularyKind.Killer).Match("THE NEWCOMER").Entry?.Id);
        Assert.False(s_vocabulary.MatcherFor(VocabularyKind.Killer).Match("THE NEWCOMER").IsAccepted);
        Assert.Equal("the-trapper", merged.MatcherFor(VocabularyKind.Killer).Match("THE TRAPPER").Entry?.Id);
        Assert.Equal(s_vocabulary.Entries(VocabularyKind.Map), merged.Entries(VocabularyKind.Map));
        Assert.Equal(s_vocabulary.RetrievedOn, merged.RetrievedOn);
    }

    [Fact]
    public void WithCharacters_AcceptsEmptyLists() =>
        Assert.Equal(98, Characters(s_vocabulary.WithCharacters([], [])).Count());

    public static TheoryData<VocabularyEntry[], VocabularyEntry[]> Collisions => new()
    {
        { [new VocabularyEntry("the-x", "The X", Code: "K27")], [] }, // a known code
        { [new VocabularyEntry("the-onryo", "The Other", Code: "K45")], [] }, // a known id
        { [], [new VocabularyEntry("the-trapper", "Someone", Code: "S55")] }, // a killer's id for a survivor
        { [new VocabularyEntry("the-x", "THE TRAPPER", Code: "K45")], [] }, // a known name after casing
        { [new VocabularyEntry("the-x", "The 0nry\u014D", Code: "K45")], [] }, // a known name after OCR folding and diacritics
        { [new VocabularyEntry("the-x", "Dwight Fairfield", Code: "K45")], [] }, // a survivor's name
        { [new VocabularyEntry("the-x", "The X", Aliases: [new VocabularyAlias("Leon S. Kennedy", "x")], Code: "K45")], [] }, // a known alias
        { [new VocabularyEntry("the-x", "The X", Aliases: [new VocabularyAlias("X", string.Empty)], Code: "K45")], [] }, // no provenance
        { [new VocabularyEntry("the-x", "The X")], [] }, // no code
        { [new VocabularyEntry("the-x", "The X", Code: "S55")], [] }, // wrong role
        { [], [new VocabularyEntry("x", "Ex", Code: "K45")] }, // wrong role
        { [new VocabularyEntry("The X", "The X", Code: "K45")], [] }, // not a slug
        { [new VocabularyEntry("the-x", "The X", Code: "K45"), new VocabularyEntry("the-y", "The Y", Code: "K45")], [] }, // the same new code twice
        { [new VocabularyEntry("the-x", "The X", Code: "K45")], [new VocabularyEntry("the-x", "Sam X", Code: "S55")] }, // the same new id twice
    };

    [Theory]
    [MemberData(nameof(Collisions))]
    public void WithCharacters_RefusesCollisionsAndInvalidEntries(VocabularyEntry[] killers, VocabularyEntry[] survivors) =>
        Assert.Throws<FormatException>(() => s_vocabulary.WithCharacters(killers, survivors));

    [Fact]
    public void WithCharacters_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => s_vocabulary.WithCharacters(null!, []));
        Assert.Throws<ArgumentNullException>(() => s_vocabulary.WithCharacters([], null!));
        Assert.Throws<ArgumentNullException>(() => s_vocabulary.WithCharacters([null!], []));
    }
}
