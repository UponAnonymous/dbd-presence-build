// Covers: FR-PACK-06 (per-category pack choice, per-image override), ADR-0007 (field-by-field validation), D16 (no pack chosen by default).
using System.Text.Json;

namespace DbdPresence.Configuration.Tests;

public sealed class PackSettingsTests
{
    private static readonly string s_sha = new('a', 64);

    [Fact]
    public void Defaults_ChooseNoPackForEveryCategoryAndHaveNoOverrides()
    {
        var packs = new AppSettings().Packs;

        Assert.Equal(string.Empty, packs.KillerPack);
        Assert.Equal(string.Empty, packs.SurvivorPack);
        Assert.Equal(string.Empty, packs.CharactersPack);
        Assert.Equal(string.Empty, packs.MapsPack);
        Assert.Empty(packs.Overrides);
    }

    [Fact]
    public void Validate_KeepsValidChoicesAndOverrides()
    {
        var settings = new AppSettings
        {
            Packs = new PackSettings
            {
                KillerPack = "my-import",
                Overrides = new Dictionary<string, string> { ["role:killer"] = s_sha + ".png" },
            },
        };

        var (result, issues) = SettingsValidator.Validate(settings);

        Assert.Empty(issues);
        Assert.Equal("my-import", result.Packs.KillerPack);
        Assert.Equal(s_sha + ".png", result.Packs.Overrides["role:killer"]);
    }

    [Theory]
    [InlineData("Not A Slug")]
    [InlineData("../escape")]
    public void Validate_ResetsInvalidPackIdToNoPack(string id)
    {
        var (result, issues) = SettingsValidator.Validate(new AppSettings { Packs = new PackSettings { MapsPack = id } });

        Assert.Equal(string.Empty, result.Packs.MapsPack);
        Assert.Contains(issues, i => i.Path == "packs.mapsPack");
    }

    [Fact]
    public void Validate_AcceptsTheEmptyIdMeaningNoPack()
    {
        var (result, issues) = SettingsValidator.Validate(new AppSettings { Packs = new PackSettings { MapsPack = string.Empty } });

        Assert.Empty(issues);
        Assert.Equal(string.Empty, result.Packs.MapsPack);
    }

    [Theory]
    [InlineData("role:killer", "../../secret.png")]
    [InlineData("role:killer", "C:\\x.png")]
    [InlineData("nonsense", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png")]
    [InlineData("role:Killer", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png")]
    public void Validate_RemovesOverridesThatCouldEscapeTheOverridesFolder(string key, string file)
    {
        var settings = new AppSettings { Packs = new PackSettings { Overrides = new Dictionary<string, string> { [key] = file } } };

        var (result, issues) = SettingsValidator.Validate(settings);

        Assert.Empty(result.Packs.Overrides);
        Assert.Contains(issues, i => i.Path == "packs.overrides");
    }

    [Fact]
    public void Json_RoundTripsAndAcceptsDocumentsWithoutThePacksMember()
    {
        var withoutPacks = JsonSerializer.Deserialize<AppSettings>("{\"schemaVersion\":2}", SettingsJsonContext.Default.AppSettings);

        Assert.NotNull(withoutPacks);
        var (validated, _) = SettingsValidator.Validate(withoutPacks);
        Assert.Equal(string.Empty, validated.Packs.KillerPack);

        var json = JsonSerializer.Serialize(new AppSettings { Packs = new PackSettings { SurvivorPack = "x1" } }, SettingsJsonContext.Default.AppSettings);
        Assert.Equal("x1", JsonSerializer.Deserialize<AppSettings>(json, SettingsJsonContext.Default.AppSettings)!.Packs.SurvivorPack);
    }
}
