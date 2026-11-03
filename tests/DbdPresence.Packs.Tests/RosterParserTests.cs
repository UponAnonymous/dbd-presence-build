// Covers: FR-ROS-03 (the signed roster: purpose roster, 30 days, the service's contract fixture), FR-ROS-04 (RosterParser: the service's rules
// repeated, append-only against the embedded vocabulary and the last good roster, withdraw only non-embedded), FR-ROS-05 (seed agreement: the
// service serves the 98 seed characters with the embedded ids), FR-PACK-07 (the merged import roster).
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbdPresence.Core.Game;
using DbdPresence.Packs.Roster;
using DbdPresence.Packs.Signing;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Packs.Tests;

public sealed class RosterParserTests
{
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(TestRoster.FixturePath("roster", "signed-roster.json")))!.AsObject();

    private static JsonObject Character(string code, string id, string name, bool withdrawn = false) => new()
    {
        ["code"] = code,
        ["id"] = id,
        ["role"] = code[0] == 'K' ? "killer" : "survivor",
        ["displayName"] = name,
        ["screenName"] = name,
        ["aliases"] = new JsonArray(),
        ["licensed"] = null,
        ["withdrawn"] = withdrawn,
    };

    private static string Payload(params JsonObject[] characters) =>
        new JsonObject { ["schemaVersion"] = 1, ["characters"] = new JsonArray([.. characters.Select(c => (JsonNode?)c)]) }.ToJsonString();

    private static RosterDocument FixtureRoster() => RosterParser.Parse(Fixture()["expectedPayload"]!.ToJsonString()).Roster!;

    [Fact]
    public void TheServicesSignedRoster_VerifiesWithTheContentKey_AndParses()
    {
        var fixture = Fixture();
        using var json = JsonDocument.Parse(File.ReadAllText(TestRoster.FixturePath("signing", "keys", "keys.json")));
        var keys = json.RootElement.GetProperty("keys").EnumerateArray().ToDictionary(
            k => k.GetProperty("kid").GetString()!,
            k => new TrustedKey(Base64Url.DecodeFromChars(k.GetProperty("spki").GetString()!), [.. k.GetProperty("purposes").EnumerateArray().Select(p => p.GetString()!)]),
            StringComparer.Ordinal);
        var now = DateTimeOffset.Parse(fixture["now"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var verifier = new SignedDocumentVerifier(new TrustedKeySet(keys), new InMemoryVersionStore(), new FakeTimeProvider(now));

        var result = verifier.Verify(System.Text.Encoding.UTF8.GetBytes(fixture["documentText"]!.GetValue<string>()), SignedPurposes.Roster, "stable");

        Assert.Equal(VerificationOutcome.Valid, result.Outcome);
        Assert.Equal(TimeSpan.FromDays(30), result.Envelope!.ExpiresAt - result.Envelope.IssuedAt);
        var parsed = RosterParser.Parse(result.Envelope.PayloadJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        Assert.Equal(100, parsed.Roster!.Characters.Count);
        Assert.Equal(VerificationOutcome.WrongPurpose, verifier.Verify(System.Text.Encoding.UTF8.GetBytes(fixture["documentText"]!.GetValue<string>()), SignedPurposes.PackIndex, "stable").Outcome);
    }

    [Fact]
    public void TheServiceSeed_AgreesWithTheEmbeddedVocabulary_AndPassesTheAppendOnlyRules()
    {
        var roster = FixtureRoster();
        var seedCodes = Fixture()["seedCodes"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(TestRoster.Embedded.Characters.Count, seedCodes.Count);
        foreach (var embedded in TestRoster.Embedded.Characters)
        {
            var entry = roster.Characters.Single(c => c.Code == embedded.Code);
            Assert.Equal((embedded.Id, embedded.Role, embedded.Name, false), (entry.Id, entry.Role, entry.DisplayName, entry.Withdrawn));
        }

        Assert.Empty(RosterParser.CheckAppendOnly(roster, TestRoster.Embedded, previous: null));
        var added = Assert.Single(roster.NewCharacters(TestRoster.Embedded));
        Assert.Equal(("K45", "the-lantern", Role.Killer), (added.Code, added.Id, added.Role));
    }

    [Fact]
    public void MergeInto_AddsTheNewCharactersForImportMatching()
    {
        var merged = FixtureRoster().MergeInto(TestRoster.Embedded);

        Assert.Equal(TestRoster.Embedded.Characters.Count + 1, merged.Characters.Count);
        Assert.Equal("The Lantern", merged.FindByCode("K45")!.Name);
        Assert.Null(merged.FindByCode("S55"));
        Assert.Equal(TestRoster.Embedded.FindByCode("K27")!.Name, merged.FindByCode("K27")!.Name);
    }

    [Theory]
    [InlineData("K45", "the-lantern", "The Lantern", true)]
    [InlineData("K4", "the-lantern", "The Lantern", false)]
    [InlineData("S45", "the-lantern", "The Lantern", false)]
    [InlineData("K45", "The-Lantern", "The Lantern", false)]
    [InlineData("K45", "generic", "The Lantern", false)]
    [InlineData("K45", "the-lantern", "", false)]
    [InlineData("K45", "the-lantern", " The Lantern", false)]
    [InlineData("K45", "the-lantern", "The  Lantern", false)]
    [InlineData("K45", "the-lantern", "The‮Lantern", false)]
    [InlineData("K45", "the-lantern", "The <b>Lantern</b>", false)]
    [InlineData("K45", "the-lantern", "The Onryō – Sadako", true)]
    [InlineData("K45", "the-lantern", "Æther", false)]
    public void Characters_AreCheckedLikeTheService(string code, string id, string name, bool valid)
    {
        var character = Character(code, id, name);
        if (code == "S45")
        {
            character["role"] = "killer";
        }

        Assert.Equal(valid, RosterParser.Parse(Payload(Character("K01", "the-trapper", "The Trapper"), character)).IsValid);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"characters":[]}""")]
    [InlineData("""{"characters":[]}""")]
    [InlineData("""{"schemaVersion":1,"characters":{}}""")]
    [InlineData("""{"schemaVersion":1,"characters":[1]}""")]
    [InlineData("""{"schemaVersion":1,"characters":[{"code":"K01","id":"a","role":"killer","displayName":"A b","screenName":"A b","aliases":[],"licensed":null}]}""")]
    [InlineData("""{"schemaVersion":1,"characters":[{"code":"K01","id":"a","role":"killer","displayName":"A b","screenName":"A b","aliases":[{"name":"A c"}],"licensed":null,"withdrawn":false}]}""")]
    [InlineData("""{"schemaVersion":1,"characters":[{"code":"K01","id":"a","role":"killer","displayName":"A b","screenName":"A b","aliases":[],"licensed":{"holder":""},"withdrawn":false}]}""")]
    [InlineData("""{"schemaVersion":1,"characters":[],"characters":[]}""")]
    [InlineData("not json")]
    public void MalformedPayloads_AreRefused(string payload)
    {
        Assert.False(RosterParser.Parse(payload).IsValid);
    }

    [Fact]
    public void DuplicateCodesAndIds_TooManyCharactersAndTooManyAliases_AreRefused()
    {
        Assert.False(RosterParser.Parse(Payload(Character("K01", "a", "A"), Character("K01", "b", "B"))).IsValid);
        Assert.False(RosterParser.Parse(Payload(Character("K01", "a", "A"), Character("K02", "a", "B"))).IsValid);
        var many = Enumerable.Range(0, RosterParser.MaxCharacters + 1).Select(i => Character($"K{i % 100:D2}", $"c{i}", $"N{i}")).ToArray();
        Assert.False(RosterParser.Parse(Payload(many)).IsValid);
        var aliased = Character("K01", "a", "A");
        aliased["aliases"] = new JsonArray([.. Enumerable.Range(0, RosterParser.MaxAliases + 1).Select(i => (JsonNode?)new JsonObject { ["name"] = $"Alias {i}", ["seenOnScreen"] = "test" })]);
        Assert.False(RosterParser.Parse(Payload(aliased)).IsValid);
        Assert.True(RosterParser.Parse(Payload(Character("K01", "a", "A"))).IsValid);
    }

    [Fact]
    public void AppendOnly_RefusesDroppedChangedAndWithdrawnEmbeddedCharacters()
    {
        var embedded = CharacterRoster.Create([new RosterCharacter("K01", "the-trapper", Role.Killer, "The Trapper", [])]);
        RosterDocument Doc(params JsonObject[] characters) => RosterParser.Parse(Payload(characters)).Roster!;

        Assert.Empty(RosterParser.CheckAppendOnly(Doc(Character("K01", "the-trapper", "The Trapper")), embedded, null));
        Assert.NotEmpty(RosterParser.CheckAppendOnly(Doc(Character("K02", "the-wraith", "The Wraith")), embedded, null));
        Assert.NotEmpty(RosterParser.CheckAppendOnly(Doc(Character("K01", "the-trapper-x", "The Trapper")), embedded, null));
        Assert.NotEmpty(RosterParser.CheckAppendOnly(Doc(Character("K01", "the-trapper", "The Trapper", withdrawn: true)), embedded, null));

        var previous = Doc(Character("K01", "the-trapper", "The Trapper"), Character("K45", "the-lantern", "The Lantern"));
        Assert.NotEmpty(RosterParser.CheckAppendOnly(Doc(Character("K01", "the-trapper", "The Trapper")), embedded, previous));
        Assert.NotEmpty(RosterParser.CheckAppendOnly(Doc(Character("K01", "the-trapper", "The Trapper"), Character("K45", "the-lantern-2", "The Lantern")), embedded, previous));
        Assert.Empty(RosterParser.CheckAppendOnly(Doc(Character("K01", "the-trapper", "The Trapper"), Character("K45", "the-lantern", "The Lantern", withdrawn: true)), embedded, previous));
    }

    [Fact]
    public void ATooLargePayload_IsRefusedBeforeParsing()
    {
        var huge = $$"""{"schemaVersion":1,"characters":[],"pad":"{{new string('x', RosterParser.MaxPayloadBytes)}}"}""";
        Assert.Contains("256 KiB", Assert.Single(RosterParser.Parse(huge).Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void RosterStatus_SaysCouldNotRefresh_AfterAFailureOrExpiry()
    {
        Assert.False(RosterSyncStatus.None.CouldNotRefresh);
        Assert.True(new RosterSyncStatus(null, RosterSyncOutcome.Offline, false, null).CouldNotRefresh);
        Assert.True(new RosterSyncStatus(null, RosterSyncOutcome.UpToDate, true, null).CouldNotRefresh);
        Assert.False(new RosterSyncStatus(null, RosterSyncOutcome.NotAllowed, false, null).CouldNotRefresh);
    }
}
