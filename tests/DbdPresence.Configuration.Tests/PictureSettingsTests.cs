// Covers: FR-PACK-12 (up to 3 own pictures, character keys only, 2 MB file rule), FR-PACK-16 (settings v2 -> v3 migration: overrides, generic keys,
// idempotence, nothing queued for upload, old settings still load), ADR-0007 (field-by-field validation, round trip), D28.
using System.Text.Json;
using System.Text.Json.Nodes;
using DbdPresence.Core.Pictures;

namespace DbdPresence.Configuration.Tests;

public sealed class PictureSettingsTests
{
    private static readonly string s_a = new('a', 64);
    private static readonly string s_b = new('b', 64);
    private static readonly string s_c = new('c', 64);
    private static readonly string s_d = new('d', 64);
    private static readonly DateTimeOffset s_when = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static PictureItem File(string sha, long bytes = 1000, PictureOrigin origin = PictureOrigin.Pc) =>
        new(sha, null, bytes, sha + ".png", null, origin, s_when);

    private static string V2Document(string packsJson) =>
        """{"schemaVersion":2,"firstRunCompleted":true,"discord":{"selectedApplication":"dbd","useOwnApplication":false,"ownApplicationId":"","applicationName":"DBD Presence"},"packs":{""" + packsJson + "}}";

    [Fact]
    public void Defaults_HaveNoPicturesStandardSizeAndUnsetRules()
    {
        var settings = new AppSettings();

        Assert.Equal(3, AppSettings.CurrentSchemaVersion);
        Assert.Empty(settings.Pictures);
        Assert.Equal(512, settings.PictureSize);
        Assert.Equal(PictureRules.Unset, settings.PictureRules);
        Assert.Null(settings.PictureNotice);
    }

    [Fact]
    public void RoundTrip_KeepsPicturesUseSizeRulesAndNotice()
    {
        var settings = new AppSettings
        {
            PictureSize = 1024,
            PictureRules = PictureRules.Accepted,
            PictureNotice = new PictureMigrationNotice { GenericOverrides = ["role:killer"], OverLimit = 2 },
            Pictures = new Dictionary<string, CharacterPictures>
            {
                ["character:the-onryo"] = new([File(s_a, 900_000), PictureSlots.LinkItem("https://example.com/a.png", s_when)!], PictureUse.Personal(s_a)),
                ["character:the-trapper"] = new([], PictureUse.Owner("hollow-ink", "hollow-trapper")),
            },
        };

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        var (validated, issues) = SettingsValidator.Validate(back);

        Assert.Empty(issues);
        Assert.Equal(1024, validated.PictureSize);
        Assert.Equal(PictureRules.Accepted, validated.PictureRules);
        Assert.Equal(["role:killer"], validated.PictureNotice!.GenericOverrides);
        Assert.Equal(2, validated.PictureNotice.OverLimit);
        var onryo = validated.Pictures["character:the-onryo"];
        Assert.Equal(2, onryo.Items.Count);
        Assert.Equal(s_a, onryo.Items[0].File![..64]);
        Assert.Equal(900_000, onryo.Items[0].Bytes);
        Assert.Equal("https://example.com/a.png", onryo.Items[1].Link);
        Assert.Equal(PictureUse.Personal(s_a), onryo.Use);
        Assert.Equal(PictureUse.Owner("hollow-ink", "hollow-trapper"), validated.Pictures["character:the-trapper"].Use);
        Assert.Contains("\"pictureSize\": 1024", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_V2Overrides_BecomeInUsePicturesAndKeepTheOverridesField()
    {
        var document = JsonNode.Parse(V2Document($$"""
            "charactersPack":"mine","overrides":{"character:the-onryo":"{{s_a}}.png","character:the-trapper":"{{s_b}}.png","role:killer":"{{s_c}}.png","character:generic-survivor":"{{s_d}}.png"}
            """))!.AsObject();

        var (outcome, applied) = new SettingsMigrator().Migrate(document);

        Assert.Equal(MigrationOutcome.Current, outcome);
        Assert.Equal([2], applied);
        var settings = JsonSerializer.Deserialize(document.ToJsonString(), SettingsJsonContext.Default.AppSettings)!;
        var (validated, issues) = SettingsValidator.Validate(settings);
        Assert.DoesNotContain(issues, i => i.Path.StartsWith("picture", StringComparison.Ordinal));
        Assert.Equal(3, validated.SchemaVersion);
        Assert.Equal(2, validated.Pictures.Count);
        var onryo = validated.Pictures["character:the-onryo"];
        Assert.Equal(s_a + ".png", onryo.Items.Single().File);
        Assert.Equal(PictureOrigin.Pc, onryo.Items.Single().Origin);
        Assert.Equal(PictureUse.Personal(s_a), onryo.Use);
        Assert.Equal(512, validated.PictureSize);
        Assert.Equal(PictureRules.Unset, validated.PictureRules);
        Assert.Equal(4, validated.Packs.Overrides.Count);
        Assert.Equal("mine", validated.Packs.CharactersPack);
        Assert.Equal(["character:generic-survivor", "role:killer"], validated.PictureNotice!.GenericOverrides.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Migrate_QueuesNothingForUpload_RulesStayUnset()
    {
        var document = JsonNode.Parse(V2Document($$"""
            "overrides":{"character:the-onryo":"{{s_a}}.png"}
            """))!.AsObject();

        new SettingsMigrator().Migrate(document);

        Assert.Equal("Unset", (string?)document["pictureRules"]);
    }

    [Fact]
    public void Migrate_IsIdempotent_ASecondRunChangesNothing()
    {
        var step = new SettingsV2ToV3Migration();
        var document = JsonNode.Parse(V2Document($$"""
            "overrides":{"character:the-onryo":"{{s_a}}.png","role:killer":"{{s_c}}.png"}
            """))!.AsObject();

        step.Migrate(document);
        var once = document.ToJsonString();
        step.Migrate(document);

        Assert.Equal(once, document.ToJsonString());
    }

    [Fact]
    public void Migrate_NeverOverwritesACharacterThatAlreadyHasPictures()
    {
        // Review F10, mutant M17: a pictures entry that exists is not replaced by the override of the same character.
        var step = new SettingsV2ToV3Migration();
        var document = JsonNode.Parse(V2Document($$"""
            "overrides":{"character:the-onryo":"{{s_a}}.png"}
            """))!.AsObject();
        document["pictures"] = new JsonObject
        {
            ["character:the-onryo"] = new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject
                {
                    ["sha256"] = s_b,
                    ["sourceSha256"] = null,
                    ["bytes"] = 500,
                    ["file"] = s_b + ".png",
                    ["link"] = null,
                    ["origin"] = "Pc",
                    ["addedAt"] = s_when.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                }),
                ["use"] = new JsonObject { ["kind"] = "Personal", ["sha256"] = s_b, ["pack"] = null, ["entry"] = null },
            },
        };

        step.Migrate(document);

        var items = document["pictures"]!["character:the-onryo"]!["items"]!.AsArray();
        Assert.Equal(s_b, (string?)items.Single()!["sha256"]);
        Assert.Equal(s_b, (string?)document["pictures"]!["character:the-onryo"]!["use"]!["sha256"]);
    }

    [Fact]
    public void Validate_ReportsPictureKeysBeyondTheLimit()
    {
        var pictures = new Dictionary<string, CharacterPictures>();
        for (var i = 0; i < SettingsValidator.MaxPictureKeys + 5; i++)
        {
            pictures[$"character:c{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] = new([], PictureUse.Owner("hollow-ink", "hollow-trapper"));
        }

        var (result, issues) = SettingsValidator.Validate(new AppSettings { Pictures = pictures });

        Assert.Equal(SettingsValidator.MaxPictureKeys, result.Pictures.Count);
        Assert.Contains(issues, i => i.Path == "pictures" && i.Message.Contains("first", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RemovesALinkWhoseHashIsNotTheHashOfItsText()
    {
        var good = PictureSlots.LinkItem("https://example.com/a.png", s_when)!;
        var forged = good with { Sha256 = s_a };

        Assert.True(PictureSlots.IsValid(good));
        Assert.False(PictureSlots.IsValid(forged));
        var (result, issues) = SettingsValidator.Validate(new AppSettings
        {
            Pictures = new Dictionary<string, CharacterPictures> { ["character:the-onryo"] = new([forged], null) },
        });

        Assert.Empty(result.Pictures);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Migrate_NoOverrides_GivesEmptyPicturesAndNoNotice()
    {
        var document = JsonNode.Parse("""{"schemaVersion":2,"firstRunCompleted":true}""")!.AsObject();

        new SettingsMigrator().Migrate(document);

        var settings = JsonSerializer.Deserialize(document.ToJsonString(), SettingsJsonContext.Default.AppSettings)!;
        Assert.Empty(settings.Pictures);
        Assert.Null(settings.PictureNotice);
        Assert.True(settings.FirstRunCompleted);
    }

    [Fact]
    public void Migrate_IgnoresMalformedOverrideFileNames()
    {
        var document = JsonNode.Parse(V2Document("""
            "overrides":{"character:the-onryo":"not-a-file.png"}
            """))!.AsObject();

        new SettingsMigrator().Migrate(document);

        Assert.Empty(document["pictures"]!.AsObject());
    }

    [Fact]
    public void Store_LoadsAnOldV2File_AndSavesItAsV3()
    {
        var fs = new InMemoryFileSystem();
        var paths = new SettingsPaths("/cfg", "/data");
        fs.Files[paths.SettingsFile] = V2Document($$"""
            "overrides":{"character:the-onryo":"{{s_a}}.png"}
            """);
        var store = new SettingsStore(fs, paths);

        var loaded = store.Load();
        store.Save(loaded.Settings);

        Assert.Equal(SettingsSource.File, loaded.Source);
        Assert.Equal(3, loaded.Settings.SchemaVersion);
        Assert.Single(loaded.Settings.Pictures);
        Assert.Contains("\"schemaVersion\": 3", fs.Files[paths.SettingsFile], StringComparison.Ordinal);
        Assert.True(loaded.Settings.FirstRunCompleted);
    }

    [Fact]
    public void Validate_KeepsAtMostThreePictures_PerCharacter()
    {
        var settings = new AppSettings
        {
            Pictures = new Dictionary<string, CharacterPictures>
            {
                ["character:the-onryo"] = new([File(s_a), File(s_b), File(s_c), File(s_d)], PictureUse.Personal(s_d)),
            },
        };

        var (result, issues) = SettingsValidator.Validate(settings);

        var onryo = result.Pictures["character:the-onryo"];
        Assert.Equal([s_a, s_b, s_c], onryo.Items.Select(i => i.Sha256));
        Assert.Null(onryo.Use);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Validate_RefusesFilesOf2MbOrMore_AndNonCharacterKeys()
    {
        var settings = new AppSettings
        {
            Pictures = new Dictionary<string, CharacterPictures>
            {
                ["character:the-onryo"] = new([File(s_a, PictureLimits.MaxFileBytes), File(s_b, PictureLimits.MaxFileBytes - 1)], null),
                ["role:killer"] = new([File(s_c)], null),
                ["character:generic-killer"] = new([File(s_d)], null),
            },
        };

        var (result, issues) = SettingsValidator.Validate(settings);

        Assert.Equal([s_b], result.Pictures["character:the-onryo"].Items.Select(i => i.Sha256));
        Assert.Single(result.Pictures);
        Assert.True(issues.Count >= 3);
    }

    [Fact]
    public void Validate_RejectsBadLinks_BadSizeAndDanglingUse()
    {
        var bad = new PictureItem(s_a, null, 0, null, "http://example.com/a.png", PictureOrigin.Link, s_when);
        var settings = new AppSettings
        {
            PictureSize = 300,
            PictureRules = (PictureRules)9,
            Pictures = new Dictionary<string, CharacterPictures>
            {
                ["character:the-onryo"] = new([bad], PictureUse.Personal(s_a)),
                ["character:the-trapper"] = new([File(s_b)], PictureUse.Personal(s_c)),
            },
        };

        var (result, issues) = SettingsValidator.Validate(settings);

        Assert.Equal(512, result.PictureSize);
        Assert.Equal(PictureRules.Unset, result.PictureRules);
        Assert.False(result.Pictures.ContainsKey("character:the-onryo"));
        Assert.Null(result.Pictures["character:the-trapper"].Use);
        Assert.Single(result.Pictures["character:the-trapper"].Items);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Validate_KeepsMigratedSizeZero_AsNotReadYet()
    {
        var settings = new AppSettings { Pictures = new Dictionary<string, CharacterPictures> { ["character:the-onryo"] = new([File(s_a, 0)], null) } };

        var (result, issues) = SettingsValidator.Validate(settings);

        Assert.Empty(issues);
        Assert.Equal(0, result.Pictures["character:the-onryo"].Items.Single().Bytes);
    }
}
