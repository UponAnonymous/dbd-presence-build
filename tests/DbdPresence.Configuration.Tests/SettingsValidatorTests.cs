// Covers: ADR-0007 validation, FR-RPC-07 (app id), FR-PROC-03 (process names), FR-APP-02/03, FR-CAP-03,
// FR-ART-01, FR-RPC-09, FR-PROF-01, NFR-PRIV-01 (detection requires acknowledgement), FR-APP-04 (window placement).
using DbdPresence.Core.Game;
using DbdPresence.Core.Settings;

namespace DbdPresence.Configuration.Tests;

public sealed class SettingsValidatorTests
{
    private static (AppSettings Settings, IReadOnlyList<SettingsIssue> Issues) Validate(AppSettings settings) =>
        SettingsValidator.Validate(settings);

    [Fact]
    public void Defaults_AreValidWithoutIssues()
    {
        var (_, issues) = Validate(new AppSettings());

        Assert.Empty(issues);
    }

    [Fact]
    public void DefaultsAreSafe_NoCaptureNoLiveStatsNoAutostart()
    {
        var settings = new AppSettings();

        Assert.False(settings.Detection.Enabled);
        Assert.False(settings.ActiveProfile.Visibility.ShowLiveMatchStats);
        Assert.False(settings.Lifecycle.StartWithWindows);
        Assert.Equal(string.Empty, settings.Discord.OwnApplicationId);
        Assert.False(settings.Discord.UseOwnApplication);
        Assert.Null(settings.Discord.DisplayName);
        Assert.Equal("dbd", settings.Discord.SelectedApplication);
        Assert.Equal(NotRunningBehavior.Clear, settings.ActiveProfile.WhenNotRunning);
    }

    [Theory]
    [InlineData("1234567890123456", false)]
    [InlineData("12345678901234567", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("123456789012345678901", false)]
    [InlineData("12345678901234567a", false)]
    [InlineData("", true)]
    public void OwnApplicationId_IsSnowflakeOrEmpty(string id, bool valid)
    {
        var (settings, issues) = Validate(new AppSettings { Discord = new DiscordSettings { OwnApplicationId = id } });

        Assert.Equal(valid ? id : string.Empty, settings.Discord.OwnApplicationId);
        Assert.Equal(valid, issues.Count == 0);
    }

    [Theory]
    [InlineData("Dead by Daylight", "Dead by Daylight")]
    [InlineData("  DBD  ", "DBD")]
    [InlineData("Fog   Watcher", "Fog Watcher")]
    [InlineData("A\u202EB\u0000C", "ABC")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void DisplayName_IsTrimmedAndSanitised(string? typed, string? expected)
    {
        var (settings, issues) = Validate(new AppSettings { Discord = new DiscordSettings { DisplayName = typed } });

        Assert.Equal(expected, settings.Discord.DisplayName);
        Assert.Empty(issues);
    }

    [Fact]
    public void DisplayName_AtLimitIsKept_OverLimitIsResetWithIssue()
    {
        var atLimit = new string('x', 128);

        var kept = Validate(new AppSettings { Discord = new DiscordSettings { DisplayName = atLimit } });
        var over = Validate(new AppSettings { Discord = new DiscordSettings { DisplayName = atLimit + "y" } });

        Assert.Equal(atLimit, kept.Settings.Discord.DisplayName);
        Assert.Empty(kept.Issues);
        Assert.Null(over.Settings.Discord.DisplayName);
        Assert.Contains(over.Issues, i => i.Path == "discord.displayName");
    }

    [Theory]
    [InlineData("dbd", true)]
    [InlineData("fog-presence", true)]
    [InlineData("", false)]
    [InlineData("Has Space", false)]
    public void SelectedApplication_MustBeAKey(string key, bool valid)
    {
        var (settings, issues) = Validate(new AppSettings { Discord = new DiscordSettings { SelectedApplication = key } });

        Assert.Equal(valid ? key : "dbd", settings.Discord.SelectedApplication);
        Assert.Equal(valid, issues.Count == 0);
    }

    [Fact]
    public void ResolvedApplication_UsesCatalogUnlessOwnApplicationIsOn()
    {
        var catalog = new BundledApplicationCatalog();

        var shared = ResolvedApplication.From(new DiscordSettings { OwnApplicationId = "123456789012345678" }, catalog);
        var own = ResolvedApplication.From(new DiscordSettings { UseOwnApplication = true, OwnApplicationId = "123456789012345678", ApplicationName = "Mine" }, catalog);
        var ownMissing = ResolvedApplication.From(new DiscordSettings { UseOwnApplication = true }, catalog);
        var unknownKey = ResolvedApplication.From(new DiscordSettings { SelectedApplication = "gone" }, catalog);

        Assert.Equal("1563291325107937340", shared.ApplicationId);
        Assert.Equal("Dead by Daylight", shared.Name);
        Assert.False(shared.IsOwn);
        Assert.Equal("123456789012345678", own.ApplicationId);
        Assert.Equal("Mine", own.Name);
        Assert.Null(ownMissing.ApplicationId);
        Assert.False(ownMissing.IsConfigured);
        Assert.Equal("1563291325107937340", unknownKey.ApplicationId);
    }

    [Fact]
    public void BundledCatalog_HasTheLabelledDefault()
    {
        var entry = Assert.Single(new BundledApplicationCatalog().Applications);

        Assert.Equal("dbd", entry.Key);
        Assert.Equal("Dead by Daylight", entry.Name);
        Assert.Contains("Unofficial", entry.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateProfileIds_AreRenamed_AndUnknownActiveFallsBack()
    {
        var (settings, issues) = Validate(new AppSettings
        {
            ActiveProfileId = "missing",
            Profiles = [BuiltInProfiles.Default, BuiltInProfiles.Default with { Name = "Copy" }],
        });

        Assert.Equal(["default", "default-2"], settings.Profiles.Select(p => p.Id));
        Assert.Equal("default", settings.ActiveProfileId);
        Assert.Contains(issues, i => i.Path == "activeProfileId");
    }

    [Fact]
    public void NoProfiles_RestoresDefault()
    {
        var (settings, issues) = Validate(new AppSettings { Profiles = [] });

        Assert.Single(settings.Profiles);
        Assert.Contains(issues, i => i.Path == "profiles");
    }

    [Fact]
    public void Appearance_InvalidValues_AreResetIndividually()
    {
        var (settings, issues) = Validate(new AppSettings
        {
            Appearance = new AppearanceSettings { Accent = "gold", FontScalePercent = 135, Theme = (ThemePreference)99, SurfaceTint = "#12345" },
        });

        Assert.Equal("#D4A649", settings.Appearance.Accent);
        Assert.Equal(100, settings.Appearance.FontScalePercent);
        Assert.Equal(ThemePreference.System, settings.Appearance.Theme);
        Assert.Equal(string.Empty, settings.Appearance.SurfaceTint);
        Assert.Equal(4, issues.Count);
    }

    [Fact]
    public void ExtraProcessNames_OnlyValidExecutables_AtMostTen()
    {
        string[] names = ["Good.exe", "..\\evil.exe", "noext", "Good.exe", "C:\\x.exe", .. Enumerable.Range(0, 20).Select(i => $"p{i}.exe")];

        var (settings, issues) = Validate(new AppSettings { Lifecycle = new LifecycleSettings { ExtraProcessNames = names } });

        Assert.Equal(10, settings.Lifecycle.ExtraProcessNames.Count);
        Assert.Equal("Good.exe", settings.Lifecycle.ExtraProcessNames[0]);
        Assert.DoesNotContain(settings.Lifecycle.ExtraProcessNames, n => n.Contains('\\', StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Path == "lifecycle.extraProcessNames");
    }

    [Fact]
    public void Detection_CannotBeEnabledWithoutAcknowledgement_AndIntervalsAreClamped()
    {
        var (settings, _) = Validate(new AppSettings
        {
            Detection = new DetectionSettings { Enabled = true, RiskAcknowledged = false, MenuScanIntervalMs = 50, TrialScanIntervalMs = 99999 },
        });

        Assert.False(settings.Detection.Enabled);
        Assert.Equal(1000, settings.Detection.MenuScanIntervalMs);
        Assert.Equal(1000, settings.Detection.TrialScanIntervalMs);
    }

    [Fact]
    public void Templates_InvalidButtonsAndImagesRemoved_SyntaxErrorsKept_LengthBounded()
    {
        var template = new PresenceTemplate
        {
            Details = "In a lobby {broken",
            State = new string('x', 2000),
            LargeImage = new ImageSlot(ImageSource.Fixed, "file:///C:/x.png"),
            Buttons = [new("ok", "https://ok.example"), new("bad", "http://bad.example"), new("ok2", "https://ok2.example"), new("ok3", "https://ok3.example")],
        };
        var profile = BuiltInProfiles.Default with { Templates = new TemplateSet { ByState = new Dictionary<PresenceState, PresenceTemplate> { [PresenceState.Lobby] = template } } };

        var (settings, issues) = Validate(new AppSettings { Profiles = [profile] });

        var validated = settings.Profiles[0].Templates.ByState[PresenceState.Lobby];
        Assert.Equal("In a lobby {broken", validated.Details);
        Assert.Equal(SettingsValidator.TemplateSourceMax, validated.State.Length);
        Assert.Equal(ImageSlot.AppLogo, validated.LargeImage);
        Assert.Equal(["ok", "ok2"], validated.Buttons.Select(b => b.Label));
        Assert.True(issues.Count >= 4);
    }

    [Fact]
    public void Artwork_InvalidReferencesReset_HttpsAccepted_ConfirmedKeysFiltered()
    {
        var profile = BuiltInProfiles.Default with
        {
            Artwork = new ArtworkSettings
            {
                RoleKiller = "Bad Key",
                RoleSurvivor = "https://cdn.example.com/s.png",
                Maps = new Dictionary<string, string> { ["coal-tower"] = "map_coal", ["bad"] = "file://x" },
                ConfirmedUploadedKeys = ["app_logo", "https://x.example/a.png", "Not Valid"],
            },
        };

        var (settings, _) = Validate(new AppSettings { Profiles = [profile] });

        var artwork = settings.Profiles[0].Artwork;
        Assert.Equal("role_killer", artwork.RoleKiller);
        Assert.Equal("https://cdn.example.com/s.png", artwork.RoleSurvivor);
        Assert.Equal(["coal-tower"], artwork.Maps.Keys);
        Assert.Equal(["app_logo"], artwork.ConfirmedUploadedKeys);
    }

    [Fact]
    public void UndefinedEnums_InProfile_AreReset()
    {
        var profile = BuiltInProfiles.Default with { WhenPaused = (PauseBehavior)9 };

        var (settings, issues) = Validate(new AppSettings { Profiles = [profile] });

        Assert.Equal(PauseBehavior.Clear, settings.Profiles[0].WhenPaused);
        Assert.Single(issues);
    }

    [Fact]
    public void DeclaredDetailsFromOlderFiles_AreClearedWithoutAFinding()
    {
        // D30: role, character and mode are never declared by hand. An older file may still carry them (even invalid ones); they load, and are reset.
#pragma warning disable CS0618 // The point of the test.
        var profile = BuiltInProfiles.Default with
        {
            Manual = new ManualDetails { Role = Role.Killer, Character = new string('x', 90), Mode = (GameMode)42, RoleSource = RoleSource.Manual },
        };

        var (settings, issues) = Validate(new AppSettings { Profiles = [profile] });

        Assert.Equal(new ManualDetails(), settings.Profiles[0].Manual);
#pragma warning restore CS0618
        Assert.Empty(issues);
    }

    [Fact]
    public void NewDocuments_HaveTheNewSwitchesOn()
    {
        var (settings, issues) = Validate(new AppSettings());

        var visibility = settings.Profiles[0].Visibility;
        Assert.True(visibility.ShowMap);
        Assert.True(visibility.ShowMode);
        Assert.True(visibility.ShowScene);
        Assert.True(visibility.ShowResult);
        Assert.False(settings.Lifecycle.ScreenReadingTipDismissed);
        Assert.Empty(issues);
    }

    [Fact]
    public void WindowPlacement_IsKept_AndClampedToTheMinimumSize()
    {
        var (settings, issues) = SettingsValidator.Validate(new AppSettings
        {
            Lifecycle = new LifecycleSettings { Window = new WindowPlacement(-1900, 40, 640, 480, Maximized: true) },
        });

        Assert.Empty(issues);
        Assert.Equal(new WindowPlacement(-1900, 40, 880, 600, true), settings.Lifecycle.Window);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1000, 700)]
    [InlineData(0, double.PositiveInfinity, 1000, 700)]
    [InlineData(0, 0, -5, 700)]
    [InlineData(5_000_000, 0, 1000, 700)]
    public void WindowPlacement_Invalid_IsDropped_WithAnIssue(double left, double top, double width, double height)
    {
        var (settings, issues) = SettingsValidator.Validate(new AppSettings
        {
            Lifecycle = new LifecycleSettings { Window = new WindowPlacement(left, top, width, height, false) },
        });

        Assert.Null(settings.Lifecycle.Window);
        Assert.Contains(issues, i => i.Path == "lifecycle.window");
    }
}
