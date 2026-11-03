// Covers: FR-PACK-01 (pack index format and strict validation), FR-PACK-04 (licensed flag with named holder).
using System.Text.Json.Nodes;
using DbdPresence.Packs.Index;

namespace DbdPresence.Packs.Tests;

public sealed class PackIndexParserTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static JsonObject ValidEntry(string id = "e1", string key = "role:killer", string kind = "role") => new()
    {
        ["id"] = id,
        ["pack"] = "p1",
        ["kind"] = kind,
        ["key"] = key,
        ["sha256"] = Sha,
        ["size"] = 1000,
        ["width"] = 1024,
        ["height"] = 512,
        ["format"] = "png",
        ["licensed"] = null,
    };

    private static JsonObject ValidPack(string id = "p1") => new()
    {
        ["id"] = id,
        ["name"] = "Pack",
        ["version"] = 2,
        ["author"] = "Someone",
        ["licenceNote"] = "Note",
    };

    private static JsonObject Doc(JsonObject? pack = null, params JsonObject[] entries) => new()
    {
        ["packs"] = new JsonArray(pack ?? ValidPack()),
        ["entries"] = new JsonArray([.. entries.Select(e => (JsonNode?)e)]),
    };

    private static PackIndexParseResult Parse(JsonObject doc) => PackIndexParser.Parse(doc.ToJsonString());

    private static void AssertRejected(JsonObject doc, string expectedFragment)
    {
        var result = Parse(doc);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidIndex_ParsesAllFields()
    {
        var licensed = ValidEntry("e2", "character:ghostface", "character");
        licensed["licensed"] = new JsonObject { ["holder"] = "Holder Inc" };
        var pack = ValidPack();
        pack["credit"] = "Creator X";

        var result = Parse(Doc(pack, ValidEntry(), licensed));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var index = result.Index!;
        Assert.Equal(new PackInfo("p1", "Pack", 2, "Someone", "Note", "Creator X"), index.Packs.Single());
        var first = index.Entries[0];
        Assert.Equal((PackEntryKind.Role, "role:killer", 1000L, 1024, 512, PackImageFormat.Png), (first.Kind, first.Key, first.Size, first.Width, first.Height, first.Format));
        Assert.Null(first.Licensed);
        Assert.Equal($"{Sha}.png", first.CacheFileName);
        Assert.Equal("Holder Inc", index.Entries[1].Licensed!.Holder);
    }

    [Fact]
    public void BehaviourArtFlag_IsOptionalAndDefaultsToFalse()
    {
        static PackInfo PackWith(params (string Name, object? Value)[] members)
        {
            var pack = ValidPack();
            foreach (var (name, value) in members)
            {
                pack[name] = value is null ? null : System.Text.Json.Nodes.JsonValue.Create((bool)value);
            }

            return Parse(Doc(pack, ValidEntry())).Index!.Packs.Single();
        }

        Assert.False(PackWith().BehaviourArt);
        Assert.False(PackWith(("behaviourArt", null)).BehaviourArt);
        Assert.True(PackWith(("behaviourArt", true)) is { BehaviourArt: true, Fallback: false });
        Assert.True(PackWith(("behaviourArt", true), ("fallback", true)) is { BehaviourArt: true, Fallback: true });
        Assert.False(PackWith(("behaviourArt", false)).BehaviourArt);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("[true]")]
    public void BehaviourArtFlag_OfTheWrongTypeRefusesTheIndex(string json)
    {
        var pack = ValidPack();
        pack["behaviourArt"] = System.Text.Json.Nodes.JsonNode.Parse(json);

        AssertRejected(Doc(pack, ValidEntry()), "behaviourArt");
    }

    [Fact]
    public void FallbackFlag_IsOptionalAndDefaultsToFalse()
    {
        var absent = Parse(Doc(ValidPack(), ValidEntry()));
        var explicitNull = ValidPack();
        explicitNull["fallback"] = null;

        Assert.False(absent.Index!.Packs.Single().Fallback);
        Assert.False(Parse(Doc(explicitNull, ValidEntry())).Index!.Packs.Single().Fallback);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FallbackFlag_IsReadFromThePack(bool value)
    {
        var pack = ValidPack();
        pack["fallback"] = value;

        var result = Parse(Doc(pack, ValidEntry()));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(value, result.Index!.Packs.Single().Fallback);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData(1)]
    public void FallbackFlag_ThatIsNotABoolean_IsRejected(object value)
    {
        var pack = ValidPack();
        pack["fallback"] = JsonValue.Create(value);

        AssertRejected(Doc(pack, ValidEntry()), "fallback");
    }

    [Fact]
    public void UnknownMembers_AreIgnored()
    {
        var entry = ValidEntry();
        entry["futureField"] = 1;
        var doc = Doc(null, entry);
        doc["schemaNote"] = "x";

        Assert.True(Parse(doc).IsValid);
    }

    [Fact]
    public void EmptyIndex_IsValid()
    {
        Assert.True(Parse(Doc()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void BadHash_IsRejected(string sha)
    {
        var entry = ValidEntry();
        entry["sha256"] = sha;

        AssertRejected(Doc(null, entry), "sha256");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(2_097_153)]
    public void BadSize_IsRejected(long size)
    {
        var entry = ValidEntry();
        entry["size"] = size;

        AssertRejected(Doc(null, entry), "size");
    }

    [Theory]
    [InlineData("width", 0)]
    [InlineData("width", 1025)]
    [InlineData("height", 0)]
    [InlineData("height", 4096)]
    [InlineData("height", -1)]
    public void BadDimensions_AreRejected(string member, int value)
    {
        var entry = ValidEntry();
        entry[member] = value;

        AssertRejected(Doc(null, entry), "width and height");
    }

    [Fact]
    public void MaximumDimensionsAndSize_AreAccepted()
    {
        var entry = ValidEntry();
        entry["width"] = 1024;
        entry["height"] = 1024;
        entry["size"] = PackIndexParser.MaxFileBytes;

        Assert.True(Parse(Doc(null, entry)).IsValid);
    }

    [Fact]
    public void PackTotalAtTheCap_IsAccepted_AndOneByteOverIsRejected()
    {
        // Entries carry different sizes; the cap applies to the sum per pack (ADR-0013; 32 MB since S5b-3).
        JsonObject[] Entries(long total)
        {
            var list = new List<JsonObject>();
            var left = total;
            for (var i = 0; left > 0; i++)
            {
                var entry = ValidEntry($"e{i}", $"role:r{i}", "role");
                entry["size"] = Math.Min(left, PackIndexParser.MaxFileBytes);
                left -= PackIndexParser.MaxFileBytes;
                list.Add(entry);
            }

            return [.. list];
        }

        Assert.Equal(32L * 1024 * 1024, PackIndexParser.MaxPackBytes);
        Assert.True(Parse(Doc(null, Entries(21L * 1024 * 1024))).IsValid);
        Assert.True(Parse(Doc(null, Entries(PackIndexParser.MaxPackBytes))).IsValid);
        AssertRejected(Doc(null, Entries(PackIndexParser.MaxPackBytes + 1)), "larger than");
        AssertRejected(Doc(null, Entries(33L * 1024 * 1024)), "larger than 32 MB");
    }

    [Fact]
    public void DefaultFlag_IsOptional_AndOnlyTheFirstDefaultPackKeepsIt()
    {
        // FR-PACK-11 (S5b-3): one owner pack per channel is the default pictures for characters.
        var first = ValidPack("p1");
        first["default"] = true;
        var second = ValidPack("p2");
        second["default"] = true;
        var third = ValidPack("p3");
        var doc = new JsonObject { ["packs"] = new JsonArray(first, second, third), ["entries"] = new JsonArray() };

        var result = Parse(doc);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal([true, false, false], result.Index!.Packs.Select(p => p.Default));
        Assert.False(Parse(Doc(null)).Index!.Packs[0].Default);
        var bad = ValidPack();
        bad["default"] = "yes";
        AssertRejected(Doc(bad), "default");
    }

    [Fact]
    public void SourceSha256_IsOptional_AndMustBeLowerHexWhenPresent()
    {
        var withSource = ValidEntry();
        withSource["sourceSha256"] = new string('a', 64);
        var withNull = ValidEntry("e2", "role:survivor");
        withNull["sourceSha256"] = null;

        var result = Parse(Doc(null, withSource, withNull, ValidEntry("e3", "map:generic", "map")));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal([new string('a', 64), null, null], result.Index!.Entries.Select(e => e.SourceSha256));
        var upper = ValidEntry();
        upper["sourceSha256"] = new string('A', 64);
        AssertRejected(Doc(null, upper), "sourceSha256");
        var number = ValidEntry();
        number["sourceSha256"] = 5;
        AssertRejected(Doc(null, number), "sourceSha256");
    }

    [Fact]
    public void UnknownMembers_OnPacksAndEntries_AreIgnored()
    {
        var pack = ValidPack();
        pack["futureFlag"] = true;
        var entry = ValidEntry();
        entry["futureMember"] = new JsonObject { ["x"] = 1 };

        Assert.True(Parse(Doc(pack, entry)).IsValid);
    }

    [Theory]
    [InlineData("role:Killer")]
    [InlineData("role:")]
    [InlineData("role")]
    [InlineData(":killer")]
    [InlineData("role:kil ler")]
    [InlineData("role:kil_ler")]
    [InlineData("role:killer/../x")]
    [InlineData("role:kïller")]
    public void BadKeys_AreRejected(string key)
    {
        AssertRejected(Doc(null, ValidEntry(key: key)), "key");
    }

    [Fact]
    public void KeyKindMustMatchKind()
    {
        AssertRejected(Doc(null, ValidEntry(key: "map:hawkins", kind: "role")), "does not match kind");
    }

    [Fact]
    public void UnknownKindAndFormat_AreRejected()
    {
        AssertRejected(Doc(null, ValidEntry(kind: "perk")), "unknown kind");

        var entry = ValidEntry();
        entry["format"] = "webp";
        AssertRejected(Doc(null, entry), "format");
    }

    [Fact]
    public void DuplicateEntryIds_AreRejected()
    {
        AssertRejected(Doc(null, ValidEntry("same", "role:killer"), ValidEntry("same", "role:survivor")), "duplicate id");
    }

    [Fact]
    public void DuplicateKeysInOnePack_AreRejected()
    {
        AssertRejected(Doc(null, ValidEntry("a", "role:killer"), ValidEntry("b", "role:killer")), "duplicate key");
    }

    [Fact]
    public void SameKeyInDifferentPacks_IsAllowed()
    {
        var other = ValidEntry("b", "role:killer");
        other["pack"] = "p2";
        var doc = Doc(null, ValidEntry("a", "role:killer"), other);
        ((JsonArray)doc["packs"]!).Add(ValidPack("p2"));

        Assert.True(Parse(doc).IsValid);
    }

    [Fact]
    public void DuplicatePackIds_AreRejected()
    {
        var doc = Doc();
        ((JsonArray)doc["packs"]!).Add(ValidPack());

        AssertRejected(doc, "duplicate pack id");
    }

    [Fact]
    public void EntryOfUnknownPack_IsRejected()
    {
        var entry = ValidEntry();
        entry["pack"] = "ghost";

        AssertRejected(Doc(null, entry), "unknown pack");
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("")]
    public void BadIds_AreRejected(string id)
    {
        AssertRejected(Doc(null, ValidEntry(id: id)), "id");
        AssertRejected(Doc(ValidPack(id)), "id");
    }

    [Fact]
    public void OverlongSlug_IsRejected()
    {
        AssertRejected(Doc(null, ValidEntry(id: new string('a', 65))), "id");
    }

    [Fact]
    public void LicensedMustBePresentAndWellFormed()
    {
        var missing = ValidEntry();
        missing.Remove("licensed");
        AssertRejected(Doc(null, missing), "licensed");

        var noHolder = ValidEntry();
        noHolder["licensed"] = new JsonObject();
        AssertRejected(Doc(null, noHolder), "licensed");

        var blankHolder = ValidEntry();
        blankHolder["licensed"] = new JsonObject { ["holder"] = "  " };
        AssertRejected(Doc(null, blankHolder), "holder");

        var wrongType = ValidEntry();
        wrongType["licensed"] = true;
        AssertRejected(Doc(null, wrongType), "licensed");
    }

    [Theory]
    [InlineData("version", 0)]
    [InlineData("version", -1)]
    public void PackVersionMustBePositive(string member, int value)
    {
        var pack = ValidPack();
        pack[member] = value;

        AssertRejected(Doc(pack), "version");
    }

    [Fact]
    public void PackTextFieldsAreRequiredAndBounded()
    {
        var noAuthor = ValidPack();
        noAuthor.Remove("author");
        AssertRejected(Doc(noAuthor), "author");

        var longNote = ValidPack();
        longNote["licenceNote"] = new string('x', 201);
        AssertRejected(Doc(longNote), "licenceNote");

        var badCredit = ValidPack();
        badCredit["credit"] = 5;
        AssertRejected(Doc(badCredit), "credit");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"packs":{},"entries":[]}""")]
    [InlineData("""{"packs":[]}""")]
    [InlineData("""{"packs":[1],"entries":[]}""")]
    [InlineData("""{"packs":[],"entries":["x"]}""")]
    public void WrongShapes_AreRejectedWithoutThrowing(string json)
    {
        var result = PackIndexParser.Parse(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ErrorList_IsCapped()
    {
        var entries = Enumerable.Range(0, 100).Select(i => ValidEntry($"BAD{i}")).ToArray();

        var result = Parse(Doc(null, entries));

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count <= 20);
    }

    [Fact]
    public void Validate_AppliesTheSameRulesToIndexesBuiltInCode()
    {
        var ok = PackData.Index([PackData.Pack("classic")], PackData.Entry("c1", "classic", "role:killer", "k"));
        var bad = PackData.Index([PackData.Pack("classic")], PackData.Entry("c1", "classic", "role:killer", "k"), PackData.Entry("c2", "classic", "role:killer", "k2"));

        Assert.Empty(PackIndexParser.Validate(ok));
        Assert.NotEmpty(PackIndexParser.Validate(bad));
    }

    [Fact]
    public void PackKey_RoundTripsAndRejectsBadSlugs()
    {
        Assert.Equal("character:the-trapper", PackKey.Make(PackEntryKind.Character, "the-trapper"));
        Assert.Equal("map:generic", PackKey.Generic(PackEntryKind.Map));
        Assert.Throws<ArgumentException>(() => PackKey.Make(PackEntryKind.Map, "Bad Slug"));
        Assert.True(PackKey.TryParse("app:mark", out var kind, out var slug));
        Assert.Equal((PackEntryKind.App, "mark"), (kind, slug));
        Assert.False(PackKey.TryParse(null, out _, out _));
    }
}
