// Covers: FR-ROS-05 and FR-PACK-07 (the roster imports match against is the embedded vocabulary: same codes, ids, names and aliases), and
// FR-ROS-04 (a vocabulary extended with WithCharacters gives a roster with the new character). PLAN_V2_S5B §2.1, §2.4; sub-slice S5b-1.
using DbdPresence.Core.Game;
using DbdPresence.Packs.Import;
using DbdPresence.Presentation.Presence;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Tests.Presence;

public sealed class VocabularyRosterTests
{
    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();

    [Fact]
    public void From_CarriesEveryKillerAndSurvivor_WithCodeIdRoleNameAndAliases()
    {
        var roster = VocabularyRoster.From(s_vocabulary);

        Assert.Equal(98, roster.Characters.Count);
        Assert.Equal(44, roster.Characters.Count(c => c.Role == Role.Killer));
        Assert.All(roster.Characters, c => Assert.Equal(s_vocabulary.FindByCode(c.Code)?.Id, c.Id));
        var leon = roster.FindByCode("S27");
        Assert.NotNull(leon);
        Assert.Equal("Leon Scott Kennedy", leon.Name);
        Assert.Contains("Leon S. Kennedy", leon.OtherNames);
        Assert.Equal("The Good Guy", roster.FindByCode("K34")?.Name);
    }

    [Fact]
    public void From_IncludesCharactersAddedWithWithCharacters()
    {
        var merged = s_vocabulary.WithCharacters([new VocabularyEntry("the-newcomer", "The Newcomer", Code: "K45")], []);

        var roster = VocabularyRoster.From(merged);

        Assert.Equal("the-newcomer", roster.FindByCode("K45")?.Id);
        Assert.Equal("character:the-newcomer", MappingProposer.Propose("K45_TheNewcomer_Portrait.png", roster)?.Key);
        Assert.Null(VocabularyRoster.From(s_vocabulary).FindByCode("K45"));
    }

    [Fact]
    public void From_RejectsNull() => Assert.Throws<ArgumentNullException>(() => VocabularyRoster.From(null!));
}
