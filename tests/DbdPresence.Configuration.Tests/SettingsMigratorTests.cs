// Covers: ADR-0007 migrations (ordered pure steps, newer/invalid detection).
using System.Text.Json.Nodes;

namespace DbdPresence.Configuration.Tests;

public sealed class SettingsMigratorTests
{
    private sealed class RenameField(int from, string oldName, string newName) : ISettingsMigration
    {
        public int FromVersion => from;

        public void Migrate(JsonObject document)
        {
            if (document.Remove(oldName, out var value))
            {
                document[newName] = value;
            }
        }
    }

    [Fact]
    public void Migrate_AppliesStepsInOrder()
    {
        var migrator = new SettingsMigrator([new RenameField(2, "b", "c"), new RenameField(1, "a", "b")]);
        var document = new JsonObject { ["schemaVersion"] = 1, ["a"] = 42 };

        var (outcome, applied) = migrator.Migrate(document, currentVersion: 3);

        Assert.Equal(MigrationOutcome.Current, outcome);
        Assert.Equal([1, 2], applied);
        Assert.Equal(42, (int)document["c"]!);
        Assert.Equal(3, (int)document["schemaVersion"]!);
    }

    [Fact]
    public void Migrate_MissingStep_IsInvalid()
    {
        var (outcome, _) = new SettingsMigrator().Migrate(new JsonObject { ["schemaVersion"] = 3 }, currentVersion: 4);

        Assert.Equal(MigrationOutcome.Invalid, outcome);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":4}", MigrationOutcome.NewerThanSupported)]
    [InlineData("{\"schemaVersion\":3}", MigrationOutcome.Current)]
    [InlineData("{\"schemaVersion\":2}", MigrationOutcome.Current)]
    [InlineData("{\"schemaVersion\":0}", MigrationOutcome.Invalid)]
    [InlineData("{\"schemaVersion\":\"1\"}", MigrationOutcome.Invalid)]
    [InlineData("{}", MigrationOutcome.Invalid)]
    [InlineData("{\"schemaVersion\":1}", MigrationOutcome.Current)]
    public void Migrate_ClassifiesVersions(string json, MigrationOutcome expected)
    {
        Assert.Equal(expected, new SettingsMigrator().Migrate(JsonNode.Parse(json)!.AsObject()).Outcome);
    }

    [Fact]
    public void ProductionList_MigratesV1ApplicationIdToOwnApplication()
    {
        var document = JsonNode.Parse("{\"schemaVersion\":1,\"discord\":{\"applicationId\":\" 123456789012345678 \",\"applicationName\":\"Mine\"}}")!.AsObject();

        var (outcome, applied) = new SettingsMigrator().Migrate(document);

        Assert.Equal(MigrationOutcome.Current, outcome);
        Assert.Equal([1, 2], applied);
        var discord = document["discord"]!.AsObject();
        Assert.Equal(3, (int)document["schemaVersion"]!);
        Assert.Equal("123456789012345678", (string)discord["ownApplicationId"]!);
        Assert.True((bool)discord["useOwnApplication"]!);
        Assert.False(discord.ContainsKey("applicationId"));
        Assert.Equal("Mine", (string)discord["applicationName"]!);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"discord\":{\"applicationId\":\"\"}}")]
    [InlineData("{\"schemaVersion\":1,\"discord\":{}}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void ProductionList_EmptyOrMissingApplicationId_DoesNotEnableOwnApplication(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();

        var (outcome, _) = new SettingsMigrator().Migrate(document);

        Assert.Equal(MigrationOutcome.Current, outcome);
        Assert.NotEqual(true, (bool?)document["discord"]?["useOwnApplication"]);
        Assert.NotEqual("1", (string?)document["discord"]?["ownApplicationId"]);
        Assert.Equal(3, (int)document["schemaVersion"]!);
    }

    [Fact]
    public void V2ToV3_WritesTheNewSwitchesOn_KeepsTheUsersOwn_AndDropsDeclaredDetails()
    {
        var document = JsonNode.Parse(
            """
            { "schemaVersion": 2,
              "profiles": [
                { "id": "default", "visibility": { "showMap": false, "showRole": false },
                  "manual": { "role": "Killer", "character": "The Trapper", "mode": "Custom", "roleSource": "Manual" } },
                { "id": "second", "visibility": { "showMode": false } },
                { "id": "third" } ],
              "lifecycle": { "closeToTray": true } }
            """)!.AsObject();

        var (outcome, applied) = new SettingsMigrator().Migrate(document);

        Assert.Equal(MigrationOutcome.Current, outcome);
        Assert.Equal([2], applied);
        var profiles = document["profiles"]!.AsArray();
        var first = profiles[0]!["visibility"]!.AsObject();
        Assert.False((bool)first["showMap"]!);
        Assert.False((bool)first["showRole"]!);
        Assert.True((bool)first["showMode"]!);
        Assert.True((bool)first["showScene"]!);
        Assert.True((bool)first["showResult"]!);
        Assert.False(profiles[0]!.AsObject().ContainsKey("manual"));
        Assert.False((bool)profiles[1]!["visibility"]!["showMode"]!); // a switch the user already turned off stays off
        Assert.False(profiles[2]!.AsObject().ContainsKey("visibility"));
        Assert.False((bool)document["lifecycle"]!["screenReadingTipDismissed"]!);
    }
}
