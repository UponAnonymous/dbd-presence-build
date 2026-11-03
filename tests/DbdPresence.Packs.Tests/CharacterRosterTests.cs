// Covers: FR-ROS-05 (code, id and role: the shipped roster agrees with the reviewed seed), FR-PACK-07 (import proposals by code, then by name;
// the T_UI_ prefix and _Portrait suffix; code and name disagreeing proposes nothing). PLAN_V2_S5B §2.1, §2.7; sub-slice S5b-1.
using System.Text.Json;
using DbdPresence.Core.Game;
using DbdPresence.Packs.Import;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Packs.Tests;

public sealed class CharacterRosterTests
{
    private static readonly CharacterRoster s_roster = TestRoster.Embedded;

    public static TheoryData<string, string> ImportNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRoster.FixturePath("roster", "import-names.json")));
        var data = new TheoryData<string, string>();
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            data.Add(file.GetProperty("stem").GetString()!, file.GetProperty("id").GetString()!);
        }

        return data;
    }

    public static TheoryData<string, string> SeedRows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRoster.FixturePath("roster", "seed.json")));
        var data = new TheoryData<string, string>();
        foreach (var row in document.RootElement.GetProperty("characters").EnumerateArray())
        {
            data.Add(row.GetProperty("code").GetString()!, row.GetProperty("id").GetString()!);
        }

        return data;
    }

    [Fact]
    public void ShippedRoster_AgreesWithTheSeed_RowForRow()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRoster.FixturePath("roster", "seed.json")));
        var seed = document.RootElement.GetProperty("characters").EnumerateArray()
            .Select(r => (Code: r.GetProperty("code").GetString()!, Id: r.GetProperty("id").GetString()!, Role: r.GetProperty("role").GetString()!))
            .OrderBy(r => r.Code, StringComparer.Ordinal);

        var shipped = s_roster.Characters
            .Select(c => (c.Code, c.Id, Role: c.Role == Role.Killer ? "killer" : "survivor"))
            .OrderBy(r => r.Code, StringComparer.Ordinal);

        Assert.Equal(98, s_roster.Characters.Count);
        Assert.Equal(seed, shipped);
    }

    [Theory]
    [MemberData(nameof(ImportNames))]
    public void OwnerPackNames_MatchByCode_InEveryForm(string stem, string id)
    {
        foreach (var file in new[] { stem + ".png", stem + "_Portrait.png", "T_UI_" + stem + "_Portrait.png", "t_ui_" + stem + "_portrait.PNG", stem + ".webp" })
        {
            var proposal = MappingProposer.Propose(file, s_roster);

            Assert.NotNull(proposal);
            Assert.Equal(ProposalKind.Character, proposal.Kind);
            Assert.Equal(id, proposal.Slug);
            Assert.Equal(ProposalBasis.Code, proposal.Basis);
            Assert.True(proposal.NeedsUserConfirmation);
        }
    }

    [Theory]
    [InlineData("K27.png")]
    [InlineData("K27_Portrait.png")]
    [InlineData("T_UI_K27_Portrait.png")]
    [InlineData("t_ui_k27_portrait.PNG")]
    public void ACodeAlone_NamesItsCharacter(string file)
    {
        var proposal = MappingProposer.Propose(file, s_roster);

        Assert.Equal("character:the-onryo", proposal?.Key);
        Assert.Equal(ProposalBasis.Code, proposal!.Basis);
    }

    [Theory]
    [InlineData("K00.png")] // a code no character has
    [InlineData("X27.png")] // not a code
    public void ACodeAloneThatNamesNoOne_ProposesNothing(string file) => Assert.Null(MappingProposer.Propose(file, s_roster));

    [Theory]
    [MemberData(nameof(SeedRows))]
    public void EverySeedCode_GivesItsCharacter(string code, string id) =>
        Assert.Equal($"character:{id}", MappingProposer.Propose($"T_UI_{code}_Anyone_Portrait.png", s_roster)?.Key);

    [Theory]
    [InlineData("TheOnryo.png", "the-onryo")]
    [InlineData("The Onryō.png", "the-onryo")]
    [InlineData("T_UI_TheTrapper_Portrait.png", "the-trapper")]
    [InlineData("Trapper.png", "the-trapper")]
    [InlineData("LeonSKennedy.png", "leon-scott-kennedy")] // the alias seen on screen
    [InlineData("Ghostface.png", "the-ghost-face")] // a leading "the" may be left out
    [InlineData("the-dark-lord.png", "the-dark-lord")] // the id itself
    [InlineData("K99_TheTrapper_Portrait.png", "the-trapper")] // an unknown code: the rest is matched by name
    public void NamesWithoutAKnownCode_MatchByName(string file, string id)
    {
        var proposal = MappingProposer.Propose(file, s_roster);

        Assert.NotNull(proposal);
        Assert.Equal(id, proposal.Slug);
        Assert.Equal(ProposalBasis.Name, proposal.Basis);
    }

    [Theory]
    [InlineData("K27_TheTrapper_Portrait.png")] // the code says The Onryō, the name says The Trapper
    [InlineData("S01_MegThomas.png")]
    [InlineData("K01_DwightFairfield.png")]
    public void CodeAndNameThatDisagree_ProposeNothing(string file) => Assert.Null(MappingProposer.Propose(file, s_roster));

    [Theory]
    [InlineData("K99_Nobody.png")] // unknown code, unknown name
    [InlineData("the-trapper-and-the-wraith.png")] // two characters
    [InlineData("T_UI_.png")]
    [InlineData("_Portrait.png")]
    public void UnusableNames_ProposeNothing(string file) => Assert.Null(MappingProposer.Propose(file, s_roster));

    [Fact]
    public void AnEmptyRoster_StillProposesRolesAndMaps_ButNoCharacter()
    {
        Assert.Null(MappingProposer.Propose("K27_TheOnryo_Portrait.png", CharacterRoster.Empty));
        Assert.Equal("role:killer", MappingProposer.Propose("killer.png", CharacterRoster.Empty)?.Key);
        Assert.Equal("map:hawkins", MappingProposer.Propose("Hawkins.webp", CharacterRoster.Empty)?.Key);
    }

    [Fact]
    public void Propose_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => MappingProposer.Propose(null!, s_roster));
        Assert.Throws<ArgumentNullException>(() => MappingProposer.Propose("a.png", null!));
    }

    [Fact]
    public void FindByCode_FindsShippedCharacters()
    {
        Assert.Equal("the-good-guy", s_roster.FindByCode("K34")?.Id);
        Assert.Equal(Role.Survivor, s_roster.FindByCode("S25")?.Role);
        Assert.Null(s_roster.FindByCode("K99"));
    }

    public static TheoryData<RosterCharacter[]> InvalidRosters => new()
    {
        { [new RosterCharacter("K1", "a", Role.Killer, "A", [])] },
        { [new RosterCharacter("k01", "a", Role.Killer, "A", [])] },
        { [new RosterCharacter("K01", "a", Role.Survivor, "A", [])] },
        { [new RosterCharacter("S01", "a", Role.Killer, "A", [])] },
        { [new RosterCharacter("K01", "a", Role.None, "A", [])] },
        { [new RosterCharacter("K01", "The A", Role.Killer, "A", [])] },
        { [new RosterCharacter("K01", "a", Role.Killer, " ", [])] },
        { [new RosterCharacter("K01", "a", Role.Killer, "A", [""])] },
        { [new RosterCharacter("K01", "a", Role.Killer, "A", []), new RosterCharacter("K01", "b", Role.Killer, "B", [])] },
        { [new RosterCharacter("K01", "a", Role.Killer, "A", []), new RosterCharacter("S01", "a", Role.Survivor, "B", [])] },
    };

    [Theory]
    [MemberData(nameof(InvalidRosters))]
    public void Create_RejectsInvalidCharacters(RosterCharacter[] characters) =>
        Assert.Throws<FormatException>(() => CharacterRoster.Create(characters));

    [Fact]
    public void Create_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => CharacterRoster.Create(null!));
        Assert.Throws<ArgumentNullException>(() => CharacterRoster.Create([null!]));
    }
}
