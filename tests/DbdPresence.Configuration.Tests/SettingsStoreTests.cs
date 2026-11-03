// Covers: ADR-0007 (atomic write, backups, recovery), S-08 (corrupt settings → backup restored + notice),
// FR-PROF-03 (backups and restore), newer-schema protection, FR-LAB-03 (the Study switch is an additive member: older files read it as on, no version bump).
using System.Text.Json.Nodes;
using DbdPresence.Core.Pictures;

namespace DbdPresence.Configuration.Tests;

public sealed class SettingsStoreTests
{
    private static readonly SettingsPaths s_paths = new("/cfg", "/data");

    private readonly InMemoryFileSystem _fs = new();

    private SettingsStore NewStore() => new(_fs, s_paths);

    private static string Customised() =>
        System.Text.Json.JsonSerializer.Serialize(new AppSettings { FirstRunCompleted = true, Discord = new DiscordSettings { UseOwnApplication = true, OwnApplicationId = "123456789012345678" } }, SettingsJsonContext.Default.AppSettings);

    [Fact]
    public void Load_NoFile_ReturnsDefaultsWithoutNotice()
    {
        var result = NewStore().Load();

        Assert.Equal(SettingsSource.Defaults, result.Source);
        Assert.Null(result.Notice);
        Assert.False(result.IsReadOnly);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllValues()
    {
        var settings = new AppSettings
        {
            FirstRunCompleted = true,
            Discord = new DiscordSettings { DisplayName = "My Fog", SelectedApplication = "dbd", UseOwnApplication = true, OwnApplicationId = "123456789012345678", ApplicationName = "My App" },
            Profiles = BuiltInProfiles.All,
            ActiveProfileId = "killer-main",
            Appearance = new AppearanceSettings { Theme = ThemePreference.Light, FontScalePercent = 120 },
            Lifecycle = new LifecycleSettings { Window = new WindowPlacement(120.5, 80, 1200, 760, Maximized: false) },
        };

        NewStore().Save(settings);
        var loaded = NewStore().Load();

        Assert.Equal(SettingsSource.File, loaded.Source);
        Assert.Equal(new WindowPlacement(120.5, 80, 1200, 760, false), loaded.Settings.Lifecycle.Window);
        Assert.Equal("My Fog", loaded.Settings.Discord.DisplayName);
        Assert.True(loaded.Settings.Discord.UseOwnApplication);
        Assert.Equal("123456789012345678", loaded.Settings.Discord.OwnApplicationId);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(SettingsValidator.Validate(settings).Settings, SettingsJsonContext.Default.AppSettings),
            System.Text.Json.JsonSerializer.Serialize(loaded.Settings, SettingsJsonContext.Default.AppSettings));
    }

    [Fact]
    public void SavedJson_UsesReadableEnumNamesAndCamelCase()
    {
        NewStore().Save(new AppSettings());

        var json = _fs.Files[s_paths.SettingsFile];
        Assert.Contains("\"schemaVersion\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"System\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Running\": {", json, StringComparison.Ordinal);
        Assert.DoesNotContain(s_paths.SettingsFile + ".tmp", _fs.Files.Keys);
    }

    [Fact]
    public void CorruptFile_RestoresNewestValidBackup_KeepsCorruptCopy()
    {
        _fs.Files[s_paths.SettingsFile] = "{ not json";
        _fs.Files[s_paths.SettingsFile + ".bak1"] = "also broken";
        _fs.Files[s_paths.SettingsFile + ".bak2"] = Customised();

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.Backup, result.Source);
        Assert.True(result.Settings.FirstRunCompleted);
        Assert.Contains("backup 2", result.Notice, StringComparison.Ordinal);
        Assert.Equal("{ not json", _fs.Files[s_paths.SettingsFile + ".corrupt"]);
    }

    [Fact]
    public void CorruptFileWithoutBackups_UsesDefaultsWithNotice()
    {
        _fs.Files[s_paths.SettingsFile] = "[1,2,3]";

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.Defaults, result.Source);
        Assert.Contains("could not be read", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedFile_IsTreatedAsUnreadable()
    {
        _fs.Files[s_paths.SettingsFile] = "{\"schemaVersion\":1,\"x\":\"" + new string('a', 1_100_000) + "\"}";

        Assert.Contains("larger than 1 MB", NewStore().Load().Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchema_IsReadOnly_AndNeverOverwritten()
    {
        var newer = JsonNode.Parse(Customised())!.AsObject();
        newer["schemaVersion"] = 99;
        _fs.Files[s_paths.SettingsFile] = newer.ToJsonString();
        var store = NewStore();

        var result = store.Load();

        Assert.True(result.IsReadOnly);
        Assert.Contains("newer version", result.Notice, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => store.Save(new AppSettings()));
        Assert.Equal(newer.ToJsonString(), _fs.Files[s_paths.SettingsFile]);
    }

    [Fact]
    public void MissingFileWithBackup_RestoresBackup()
    {
        _fs.Files[s_paths.SettingsFile + ".bak1"] = Customised();

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.Backup, result.Source);
        Assert.Contains("missing", result.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Backups_RotateOncePerSession_KeepAtMostFive()
    {
        for (var session = 0; session < 8; session++)
        {
            var store = NewStore();
            store.Save(new AppSettings { Discord = new DiscordSettings { ApplicationName = $"S{session}" } });
            store.Save(new AppSettings { Discord = new DiscordSettings { ApplicationName = $"S{session}b" } });
        }

        var store2 = NewStore();
        Assert.Equal(SettingsStore.BackupCount, store2.ExistingBackups().Count);
        Assert.Contains("\"S6b\"", _fs.Files[store2.BackupPath(1)], StringComparison.Ordinal);
        Assert.Contains("\"S2b\"", _fs.Files[store2.BackupPath(5)], StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreBackup_ReplacesCurrentSettings()
    {
        _fs.Files[s_paths.SettingsFile] = System.Text.Json.JsonSerializer.Serialize(new AppSettings(), SettingsJsonContext.Default.AppSettings);
        _fs.Files[s_paths.SettingsFile + ".bak3"] = Customised();
        var store = NewStore();
        store.Load();

        var restored = store.RestoreBackup(3);

        Assert.True(restored.Settings.FirstRunCompleted);
        Assert.True(NewStore().Load().Settings.FirstRunCompleted);
    }

    [Fact]
    public void GoldenV1File_MigratesApplicationIdToOwnApplication()
    {
        // Guards against accidental schema changes: a v1 file written by the previous release must always load,
        // and its Application ID keeps working as the Advanced own-application option (FR-RPC-07).
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-v1.json"));
        _fs.Files[s_paths.SettingsFile] = golden;

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.File, result.Source);
        Assert.Empty(result.Issues);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.True(result.Settings.Discord.UseOwnApplication);
        Assert.Equal("123456789012345678", result.Settings.Discord.OwnApplicationId);
        Assert.Null(result.Settings.Discord.DisplayName);
        Assert.Equal("dbd", result.Settings.Discord.SelectedApplication);
        Assert.Equal("killer-main", result.Settings.ActiveProfileId);
        Assert.Equal(5, result.Settings.Profiles.Count);
    }

    [Fact]
    public void GoldenV2File_Loads()
    {
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-v2.json"));
        _fs.Files[s_paths.SettingsFile] = golden;

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.File, result.Source);
        Assert.Empty(result.Issues);
        Assert.Equal("Fog Watcher", result.Settings.Discord.DisplayName);
        Assert.False(result.Settings.Discord.UseOwnApplication);
        Assert.Equal(string.Empty, result.Settings.Discord.OwnApplicationId);
        Assert.Equal(5, result.Settings.Profiles.Count);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);

        // D30 and design §11: every profile of an older file loads with the new switches on and no declared details.
        Assert.All(result.Settings.Profiles, p =>
        {
            Assert.True(p.Visibility.ShowMode);
            Assert.True(p.Visibility.ShowScene);
            Assert.True(p.Visibility.ShowResult);
#pragma warning disable CS0618 // The point of the test.
            Assert.Equal(Core.Game.Role.None, p.Manual.Role);
            Assert.Equal(string.Empty, p.Manual.Character);
#pragma warning restore CS0618
        });
    }

    [Fact]
    public void AV3FileWrittenBeforeTheRedesignSwitchesExisted_KeepsThemOn_AndLeavesThePicturesAlone()
    {
        // Review F14: a v3 file from the build that introduced pictures has no showMode, showScene or showResult, no screenReadingTipDismissed and
        // still carries "manual"; the v2 to v3 step does not run for it again, so the reader itself must not turn the missing switches off.
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-v2.json")))!.AsObject();
        document["schemaVersion"] = 3;
        document["pictures"] = new JsonObject();
        document["pictureSize"] = 1024;
        document["pictureRules"] = "Accepted";
        foreach (var profile in document["profiles"]!.AsArray().OfType<JsonObject>())
        {
            var visibility = profile["visibility"]!.AsObject();
            visibility.Remove("showMode");
            visibility.Remove("showScene");
            visibility.Remove("showResult");
        }

        document["lifecycle"]!.AsObject().Remove("screenReadingTipDismissed");
        _fs.Files[s_paths.SettingsFile] = document.ToJsonString();

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.File, result.Source);
        Assert.All(result.Settings.Profiles, p =>
        {
            Assert.True(p.Visibility.ShowMode);
            Assert.True(p.Visibility.ShowScene);
            Assert.True(p.Visibility.ShowResult);
        });
        Assert.False(result.Settings.Lifecycle.ScreenReadingTipDismissed);
        Assert.Equal(1024, result.Settings.PictureSize);
        Assert.Equal(PictureRules.Accepted, result.Settings.PictureRules);
    }

    [Fact]
    public void AFileWithoutTheStudyMember_ReadsTheAutomaticRunAsOn_WithoutAMigrationOrANotice()
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-v2.json")))!.AsObject();
        document["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        document["pictures"] = new JsonObject();
        document["pictureSize"] = 512;
        document["pictureRules"] = "Accepted";
        Assert.False(document.ContainsKey("study"));
        _fs.Files[s_paths.SettingsFile] = document.ToJsonString();

        var result = NewStore().Load();

        Assert.Equal(SettingsSource.File, result.Source);
        Assert.True(result.Settings.Study.LabelAfterPlay);
        Assert.False(result.Settings.Study.LabellingConfirmed);
        Assert.Equal(3, AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void TheStudySwitch_IsSavedAndLoadedAndASettingOfNullFallsBackToTheDefault()
    {
        NewStore().Save(new AppSettings { Study = new StudySettings { LabelAfterPlay = false } });
        Assert.Contains("\"labelAfterPlay\": false", _fs.Files[s_paths.SettingsFile], StringComparison.Ordinal);
        Assert.False(NewStore().Load().Settings.Study.LabelAfterPlay);

        var document = JsonNode.Parse(_fs.Files[s_paths.SettingsFile])!.AsObject();
        document["study"] = null;
        _fs.Files[s_paths.SettingsFile] = document.ToJsonString();

        Assert.True(NewStore().Load().Settings.Study.LabelAfterPlay);
    }

    [Fact]
    public void TheRecordingConfirmation_IsOffByDefault_IsSavedAndLoaded_AndAnOlderFileWithoutItReadsAsNotConfirmed()
    {
        Assert.False(new AppSettings().Study.RecordingConfirmed);

        NewStore().Save(new AppSettings { Study = new StudySettings { RecordingConfirmed = true } });

        Assert.Contains("\"recordingConfirmed\": true", _fs.Files[s_paths.SettingsFile], StringComparison.Ordinal);
        Assert.True(NewStore().Load().Settings.Study.RecordingConfirmed);

        NewStore().Save(new AppSettings { Study = new StudySettings { LabellingConfirmed = true } });
        _fs.Files[s_paths.SettingsFile] = _fs.Files[s_paths.SettingsFile].Replace("\"recordingConfirmed\": false,", string.Empty, StringComparison.Ordinal).Replace(",\n    \"recordingConfirmed\": false", string.Empty, StringComparison.Ordinal);
        Assert.False(NewStore().Load().Settings.Study.RecordingConfirmed);
    }

    [Fact]
    public void TheLabellingConfirmation_IsOffByDefault_AndIsSavedAndLoaded()
    {
        Assert.False(new AppSettings().Study.LabellingConfirmed);

        NewStore().Save(new AppSettings { Study = new StudySettings { LabellingConfirmed = true } });

        Assert.Contains("\"labellingConfirmed\": true", _fs.Files[s_paths.SettingsFile], StringComparison.Ordinal);
        var loaded = NewStore().Load().Settings.Study;
        Assert.True(loaded.LabellingConfirmed);
        Assert.True(loaded.LabelAfterPlay);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"unknownMember\": 1}")]
    [InlineData("{\"labellingConfirmed\": false, \"labelAfterPlay\": false}")]
    public void AnEmptyOrUnknownStudyObject_NeverCountsAsConfirmed_AndTheSwitchReadsOff(string study)
    {
        NewStore().Save(new AppSettings());
        var document = JsonNode.Parse(_fs.Files[s_paths.SettingsFile])!.AsObject();
        document["study"] = JsonNode.Parse(study);
        _fs.Files[s_paths.SettingsFile] = document.ToJsonString();

        var result = NewStore().Load();
        var loaded = result.Settings.Study;
        Assert.Equal(SettingsSource.File, result.Source);

        Assert.False(loaded.LabellingConfirmed);
        // Measured: the generated reader gives an explicit but empty study object the switch value false (not the member default true). That is the safe direction; the app always writes both members.
        Assert.False(loaded.LabelAfterPlay);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void AWrongTypedConfirmation_NeverCountsAsConfirmed(string value)
    {
        NewStore().Save(new AppSettings());
        var document = JsonNode.Parse(_fs.Files[s_paths.SettingsFile])!.AsObject();
        document["study"] = JsonNode.Parse("{\"labellingConfirmed\": " + value + "}");
        _fs.Files[s_paths.SettingsFile] = document.ToJsonString();

        Assert.False(NewStore().Load().Settings.Study.LabellingConfirmed);
    }

    [Fact]
    public void V1FileWithoutApplicationId_StaysOnTheCatalogApplication()
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-v1.json")))!.AsObject();
        document["discord"]!.AsObject()["applicationId"] = string.Empty;
        _fs.Files[s_paths.SettingsFile] = document.ToJsonString();

        var result = NewStore().Load();

        Assert.False(result.Settings.Discord.UseOwnApplication);
        Assert.Equal(string.Empty, result.Settings.Discord.OwnApplicationId);
    }
}
