// Covers: FR-AUTO-01 (Automatic is a small-image choice, rejected on the large image and repaired with a note), FR-AUTO-02 (status pictures are
// runtime-only and never stored in settings files), FR-AUTO-06 (the live-stats switch stays off by default).
using System.Text.Json;
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration.Tests;

public sealed class AutomaticImageSettingsTests
{
    private static AppSettings With(PresenceTemplate template, PresenceState state = PresenceState.Results) =>
        new()
        {
            Profiles = [BuiltInProfiles.Default with { Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [state] = template } } }],
        };

    [Fact]
    public void Automatic_OnTheSmallImage_IsKept()
    {
        var (settings, issues) = SettingsValidator.Validate(With(new PresenceTemplate { Details = "Viewing results", SmallImage = ImageSlot.Automatic }));

        Assert.Equal(ImageSlot.Automatic, settings.Profiles[0].Templates.ByState[PresenceState.Results].SmallImage);
        Assert.Empty(issues);
    }

    [Fact]
    public void Automatic_OnTheLargeImage_IsRepairedToTheRoleIcon_WithANote()
    {
        var (settings, issues) = SettingsValidator.Validate(With(new PresenceTemplate { Details = "Viewing results", LargeImage = ImageSlot.Automatic }));

        Assert.Equal(ImageSlot.Role, settings.Profiles[0].Templates.ByState[PresenceState.Results].LargeImage);
        Assert.Contains(issues, i => i.Message.Contains("only for the small picture", StringComparison.Ordinal));
    }

    [Fact]
    public void Automatic_RoundTripsAsTheWordAutomatic_LikeEveryOtherPictureChoice()
    {
        var source = With(new PresenceTemplate { Details = "Viewing results", SmallImage = ImageSlot.Automatic });

        var json = JsonSerializer.Serialize(source, SettingsJsonContext.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;

        Assert.Contains("\"Automatic\"", json, StringComparison.Ordinal);
        Assert.Equal(ImageSlot.Automatic, loaded.Profiles[0].Templates.ByState[PresenceState.Results].SmallImage);
    }

    [Fact]
    public void StatusPictures_AreNeverWrittenToTheSettingsFile()
    {
        var profile = BuiltInProfiles.Default with
        {
            Artwork = new ArtworkSettings { StatusImages = new Dictionary<string, string> { ["status-hooked"] = "https://cdn.arcavale.com/x.png" } },
        };

        var json = JsonSerializer.Serialize(new AppSettings { Profiles = [profile] }, SettingsJsonContext.Default.AppSettings);

        Assert.DoesNotContain("statusImages", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("status-hooked", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailedPreset_IsValidWithoutIssues_AndLiveStatsStayOffByDefault()
    {
        var (settings, issues) = SettingsValidator.Validate(new AppSettings());

        Assert.Empty(issues);
        Assert.False(settings.ActiveProfile.Visibility.ShowLiveMatchStats);
        Assert.Equal(ImageSlot.Automatic, settings.ActiveProfile.Templates.ByState[PresenceState.InTrialSurvivor].SmallImage);
        Assert.Equal(ImageSlot.Automatic, settings.ActiveProfile.Templates.ByState[PresenceState.Results].SmallImage);
    }
}
