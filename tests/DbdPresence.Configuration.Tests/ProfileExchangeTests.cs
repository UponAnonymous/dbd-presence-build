// Covers: FR-PROF-02 (export/import validated + migrated, malformed input rejected without partial apply, diff preview), S-09.
using System.Text.Json.Nodes;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration.Tests;

public sealed class ProfileExchangeTests
{
    [Fact]
    public void ExportThenImport_RoundTrips()
    {
        var original = BuiltInProfiles.KillerMain with { Visibility = new VisibilitySettings { ShowMap = false } };

        var result = ProfileExchange.Import(ProfileExchange.Export(original));

        Assert.Null(result.Error);
        Assert.Empty(result.Issues);
        Assert.Empty(ProfileExchange.Diff(original, result.Profile!));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("[]", "expected a JSON object")]
    [InlineData("{\"kind\":\"other\",\"schemaVersion\":1,\"profile\":{}}", "missing kind")]
    [InlineData("{\"kind\":\"dbd-presence-profile\",\"profile\":{}}", "schema version")]
    [InlineData("{\"kind\":\"dbd-presence-profile\",\"schemaVersion\":7,\"profile\":{}}", "newer version")]
    [InlineData("{\"kind\":\"dbd-presence-profile\",\"schemaVersion\":1}", "missing")]
    [InlineData("{\"kind\":\"dbd-presence-profile\",\"schemaVersion\":1,\"profile\":{\"visibility\":\"yes\"}}", "")]
    [InlineData("{oops", "not valid JSON")]
    public void Import_Malformed_IsRejectedWithReason(string json, string fragment)
    {
        var result = ProfileExchange.Import(json);

        Assert.Null(result.Profile);
        Assert.Contains(fragment, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_Oversized_IsRejected()
    {
        Assert.Contains("1 MB", ProfileExchange.Import(new string(' ', ProfileExchange.MaxImportLength + 1)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_InvalidValues_AreCorrectedWithIssues()
    {
        var json = ProfileExchange.Export(BuiltInProfiles.Default with { Name = "", Artwork = new ArtworkSettings { AppLogo = "C:\\evil.png" } });

        var result = ProfileExchange.Import(json);

        Assert.NotNull(result.Profile);
        Assert.Equal("app_logo", result.Profile.Artwork.AppLogo);
        Assert.Equal("Profile", result.Profile.Name);
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public void Import_FuzzedInputs_NeverThrowAndNeverReturnPartialResults()
    {
        var valid = ProfileExchange.Export(BuiltInProfiles.Streamer);
        var random = new Random(99);
        for (var i = 0; i < 1500; i++)
        {
            var chars = valid.ToCharArray();
            var mode = i % 3;
            if (mode == 0)
            {
                chars = chars[..random.Next(chars.Length)];
            }
            else
            {
                for (var k = 0; k < 1 + random.Next(6); k++)
                {
                    chars[random.Next(chars.Length)] = "{}[]\":,0a \\"[random.Next(11)];
                }
            }

            var result = ProfileExchange.Import(new string(chars));

            Assert.True((result.Profile is null) != (result.Error is null), "exactly one of profile or error");
        }
    }

    [Fact]
    public void Diff_ListsChangedPaths()
    {
        var current = BuiltInProfiles.Default;
        var incoming = current with
        {
            Visibility = current.Visibility with { ShowMap = false, ShowMode = false },
        };

        var diff = ProfileExchange.Diff(current, incoming);

        Assert.Equal(["visibility.showMap", "visibility.showMode"], diff);
    }

    [Fact]
    public void Export_IsVersionedAndKinded()
    {
        var document = JsonNode.Parse(ProfileExchange.Export(BuiltInProfiles.Default))!.AsObject();

        Assert.Equal("dbd-presence-profile", (string)document["kind"]!);
        Assert.Equal(AppSettings.CurrentSchemaVersion, (int)document["schemaVersion"]!);
    }

    [Fact]
    public void Import_OfAnExportFromBeforeV3_TurnsTheNewSwitchesOn_AndDropsDeclaredDetails()
    {
        // Older exports lack showMode, showScene and showResult (the reader would read them as false) and carry declared details (D30).
        var document = JsonNode.Parse(ProfileExchange.Export(BuiltInProfiles.Default))!.AsObject();
        document["schemaVersion"] = 2;
        var profile = document["profile"]!.AsObject();
        var visibility = profile["visibility"]!.AsObject();
        visibility.Remove("showMode");
        visibility.Remove("showScene");
        visibility.Remove("showResult");
        visibility["showMap"] = false;
        profile["manual"] = JsonNode.Parse("""{ "role": "Killer", "character": "The Trapper", "mode": "Custom", "roleSource": "Manual" }""");

        var result = ProfileExchange.Import(document.ToJsonString());

        Assert.Null(result.Error);
        var imported = result.Profile!.Visibility;
        Assert.False(imported.ShowMap);
        Assert.True(imported.ShowMode);
        Assert.True(imported.ShowScene);
        Assert.True(imported.ShowResult);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Import_AcceptsEarlierAndCurrentSchemaVersions(int version)
    {
        var document = JsonNode.Parse(ProfileExchange.Export(BuiltInProfiles.KillerMain))!.AsObject();
        document["schemaVersion"] = version;

        var result = ProfileExchange.Import(document.ToJsonString());

        Assert.Null(result.Error);
        Assert.Equal(BuiltInProfiles.KillerMain.Name, result.Profile!.Name);
    }
}
