// Covers: FR-VIS-01..03 (privacy toggles, pause), FR-RPC-06 (not-running behaviour), D30 (nothing is declared by hand), FR-RPC-07
// (application id validation), FR-RPC-13 (application name), FR-LIFE-01 (start with Windows), FR-LIFE-04
// (notifications), FR-APP-01..03 (appearance), FR-LIFE-02 (shared controls used by tray and window).
using DbdPresence.Configuration;
using DbdPresence.Core.Settings;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class SettingsPagesTests
{
    private static readonly AppInfo s_app = new("1.0.0", "abc1234", IsSigned: false, @"C:\Users\me\AppData\Roaming\DBDPresence", @"C:\Users\me\AppData\Local\DBDPresence.Data");

    [Fact]
    public void Privacy_TogglesWriteTheActiveProfileAndPrivacySettings()
    {
        using var h = new Harness();
        using var privacy = new PrivacyViewModel(h.Settings, h.Dispatcher, s_app);

        privacy.ShowRole = false;
        privacy.ShowTimers = false;
        privacy.HideFullActivity = true;
        privacy.PauseShowsGeneric = true;
        privacy.ShowWhenNotRunning = true;

        var profile = h.Settings.Current.ActiveProfile;
        Assert.False(profile.Visibility.ShowRole);
        Assert.False(profile.Visibility.ShowTimers);
        Assert.True(h.Settings.Current.Privacy.HideFullActivity);
        Assert.Equal(PauseBehavior.Generic, profile.WhenPaused);
        Assert.Equal(NotRunningBehavior.ShowTemplate, profile.WhenNotRunning);
    }

    [Fact]
    public void Controls_PauseIsRuntimeOnly_AndDrivesTheCoordinator()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        using var controls = h.Controls();
        var updates = h.Settings.UpdateCount;

        controls.IsPaused = true;

        Assert.True(h.Coordinator.Paused);
        Assert.Equal(updates, h.Settings.UpdateCount);
        Assert.Contains("Paused", controls.TraySummary, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsDeclaredByHand_NoManualRoleCharacterOrModeInputExists()
    {
        // D30: the app reads these from the game; the old inputs on Home, in the tray and in the command bar are gone.
        Assert.Null(typeof(PresenceControlsViewModel).GetProperty("ManualRole"));
        Assert.Null(typeof(PresenceControlsViewModel).GetProperty("Roles"));
        Assert.Null(typeof(PresenceControlsViewModel).GetProperty("IsStreamerMode"));
        Assert.Null(typeof(HomeViewModel).GetProperty("Character"));
        Assert.Null(typeof(HomeViewModel).GetProperty("Mode"));
        Assert.Null(typeof(HomeViewModel).GetProperty("Modes"));
    }

    [Fact]
    public void Controls_ConnectionTest_ExplainsWhenNotConfigured()
    {
        using var h = new Harness(Harness.Unconfigured());
        h.Coordinator.Start();
        using var controls = h.Controls();

        controls.TestConnectionCommand.Execute(null);

        Assert.Contains("App ID", controls.ConnectionTestText, StringComparison.Ordinal);
        Assert.Equal("Not configured", controls.Connection.Text);
    }

    [Fact]
    public void Controls_TestConnectionHelp_UsesTheRealTestDuration()
    {
        using var h = new Harness();
        using var controls = h.Controls();

        var seconds = (int)PresenceCoordinator.TestDuration.TotalSeconds;

        Assert.Contains($"{seconds} seconds", controls.TestConnectionHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_AboutAndFirstRunStateTheSameCurrentBehaviour()
    {
        var about = new AboutViewModel(s_app);

        Assert.Contains(about.Disclaimer, d => d.Contains(AppCopy.ProcessCheckOnly, StringComparison.Ordinal));
        Assert.DoesNotContain(about.Disclaimer, d => d.Contains("later version", StringComparison.OrdinalIgnoreCase));
        // Whole-app design §12.12: screen reading ships (opt-in), so the copy no longer claims the app never looks at the screen.
        Assert.DoesNotContain("never looks at your screen", AppCopy.ProcessCheckOnly, StringComparison.Ordinal);
        Assert.Contains("only checks whether the game is running", AppCopy.ProcessCheckOnly, StringComparison.Ordinal);
        Assert.Contains("off until you turn it on", AppCopy.ProcessCheckOnly, StringComparison.Ordinal);
        Assert.Contains("only at the Dead by Daylight window", AppCopy.ProcessCheckOnly, StringComparison.Ordinal);
        Assert.Contains("never saves pictures", AppCopy.ProcessCheckOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Controls_SwitchingProfile_ChangesTheActiveProfile()
    {
        var settings = new AppSettings { Profiles = [BuiltInProfiles.Default, BuiltInProfiles.Streamer] };
        using var h = new Harness(settings);
        using var controls = h.Controls();

        controls.ActiveProfileId = BuiltInProfiles.Streamer.Id;

        Assert.Equal(BuiltInProfiles.Streamer.Id, h.Settings.Current.ActiveProfile.Id);
        Assert.Equal(2, controls.Profiles.Count);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345678901234567890123")]
    [InlineData("abcdefghijklmnopqr")]
    public void AppStartup_InvalidApplicationId_IsNotSaved(string id)
    {
        using var h = new Harness(Harness.Configured());
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.ApplicationId = id;

        Assert.NotNull(page.ApplicationIdError);
        Assert.Equal(TestIds.ApplicationId, h.Settings.Current.Discord.OwnApplicationId);
        Assert.Equal(id, page.ApplicationId);
    }

    [Fact]
    public void AppStartup_ValidApplicationId_IsSaved_Trimmed()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.ApplicationId = " 123456789012345678 ";

        Assert.Null(page.ApplicationIdError);
        Assert.Equal("123456789012345678", h.Settings.Current.Discord.OwnApplicationId);
    }

    [Fact]
    public void AppStartup_FreshInstall_ShowsTheCatalogNameAndKeepsAdvancedOff()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        Assert.Equal(DisplayNameKind.DeadByDaylight, page.SelectedDisplayName);
        Assert.False(page.IsCustomDisplayName);
        Assert.False(page.UseOwnApplication);
        Assert.Equal(MemberListDisplay.DiscordDefault, page.MemberListDisplay);
        Assert.Null(h.Settings.Current.Discord.DisplayName);
        Assert.Equal(["Dead by Daylight", "DBD", "Custom…"], page.DisplayNameChoices.Select(c => c.Label));
    }

    [Fact]
    public void AppStartup_QuickPicks_SaveTheDisplayName()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.SelectedDisplayName = DisplayNameKind.Dbd;
        Assert.Equal("DBD", h.Settings.Current.Discord.DisplayName);

        page.SelectedDisplayName = DisplayNameKind.DeadByDaylight;
        Assert.Equal("Dead by Daylight", h.Settings.Current.Discord.DisplayName);
    }

    [Fact]
    public void AppStartup_CustomName_IsTrimmedValidatedAndSaved()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.SelectedDisplayName = DisplayNameKind.Custom;
        Assert.True(page.IsCustomDisplayName);
        Assert.Equal("Dead by Daylight", page.CustomDisplayName);
        Assert.Null(page.DisplayNameError);

        page.CustomDisplayName = "  Fog   Watcher ";
        Assert.Equal("Fog Watcher", h.Settings.Current.Discord.DisplayName);

        page.CustomDisplayName = "   ";
        Assert.NotNull(page.DisplayNameError);
        Assert.Equal("Fog Watcher", h.Settings.Current.Discord.DisplayName);
        Assert.True(page.IsCustomDisplayName);

        page.CustomDisplayName = new string('x', 129);
        Assert.NotNull(page.DisplayNameError);
        Assert.Equal("Fog Watcher", h.Settings.Current.Discord.DisplayName);

        page.CustomDisplayName = new string('x', 128);
        Assert.Null(page.DisplayNameError);
        Assert.Equal(new string('x', 128), h.Settings.Current.Discord.DisplayName);
    }

    [Fact]
    public void AppStartup_CustomBoxStaysOpen_WhileTypingTextThatEqualsAQuickPick()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());
        page.SelectedDisplayName = DisplayNameKind.Custom;

        page.CustomDisplayName = "DBD";

        Assert.Equal("DBD", h.Settings.Current.Discord.DisplayName);
        Assert.Equal(DisplayNameKind.Custom, page.SelectedDisplayName);
    }

    [Fact]
    public void AppStartup_ReloadsAStoredDisplayName_AsQuickPickOrCustom()
    {
        using var quick = new Harness(new AppSettings { Discord = new DiscordSettings { DisplayName = "DBD" } });
        using var custom = new Harness(new AppSettings { Discord = new DiscordSettings { DisplayName = "Fog Watcher" } });

        using var quickPage = new AppStartupViewModel(quick.Controls(), quick.Settings, quick.Dispatcher, new FakeAutostart());
        using var customPage = new AppStartupViewModel(custom.Controls(), custom.Settings, custom.Dispatcher, new FakeAutostart());

        Assert.Equal(DisplayNameKind.Dbd, quickPage.SelectedDisplayName);
        Assert.Equal(DisplayNameKind.Custom, customPage.SelectedDisplayName);
        Assert.Equal("Fog Watcher", customPage.CustomDisplayName);
    }

    [Fact]
    public void AppStartup_MemberListChoice_IsSavedOnTheActiveProfile()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.MemberListDisplay = MemberListDisplay.Details;
        Assert.Equal(Core.Presence.StatusDisplay.Details, h.Settings.Current.ActiveProfile.StatusDisplay);

        page.MemberListDisplay = MemberListDisplay.DiscordDefault;
        Assert.Null(h.Settings.Current.ActiveProfile.StatusDisplay);
    }

    [Fact]
    public void AppStartup_UseOwnApplication_SwitchesTheConnectionTarget_AndValidatesTheId()
    {
        using var h = new Harness();
        h.Coordinator.Start();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.UseOwnApplication = true;
        Assert.True(h.Settings.Current.Discord.UseOwnApplication);
        Assert.False(h.Coordinator.Snapshot.IsConfigured);

        page.ApplicationId = "123";
        Assert.NotNull(page.ApplicationIdError);
        Assert.False(h.Coordinator.Snapshot.IsConfigured);

        page.ApplicationId = TestIds.ApplicationId;
        Assert.Null(page.ApplicationIdError);
        Assert.True(h.Coordinator.Snapshot.IsConfigured);
        Assert.Equal(TestIds.ApplicationId, h.Sink.ApplicationIds[^1]);

        page.UseOwnApplication = false;
        Assert.Equal("1563291325107937340", h.Sink.ApplicationIds[^1]);
        Assert.Equal(TestIds.ApplicationId, h.Settings.Current.Discord.OwnApplicationId);
    }

    [Fact]
    public void AppStartup_SetupGuidance_ExistsOnlyAsAdvancedSteps()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        Assert.Contains(page.SetupSteps, step => step.Contains("developers/applications", StringComparison.Ordinal));
        Assert.DoesNotContain(page.SetupSteps, step => step.Contains("Dead by Daylight", StringComparison.Ordinal));
    }

    [Fact]
    public void About_StatesTheUnofficialStatementWordForWord()
    {
        var about = new AboutViewModel(s_app);

        Assert.Equal(
            "Unofficial fan-made app — not affiliated with or endorsed by Behaviour Interactive. Dead by Daylight is a trademark of Behaviour Interactive Inc.",
            about.UnofficialStatement);
    }

    [Fact]
    public void AppStartup_ApplicationName_IsValidated()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.ApplicationName = new string('n', 33);
        Assert.NotNull(page.ApplicationNameError);
        Assert.Equal("DBD Presence", h.Settings.Current.Discord.ApplicationName);

        page.ApplicationName = "Dead by Daylight";
        Assert.Equal("Dead by Daylight", h.Settings.Current.Discord.ApplicationName);
    }

    [Fact]
    public void AppStartup_StartWithWindows_WritesTheRegistration()
    {
        using var h = new Harness();
        var autostart = new FakeAutostart();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, autostart);

        page.StartWithWindows = true;

        Assert.True(autostart.IsEnabled);
        Assert.True(h.Settings.Current.Lifecycle.StartWithWindows);
    }

    [Fact]
    public void AppStartup_StartWithWindowsFailure_ShowsTheRealState()
    {
        using var h = new Harness();
        var autostart = new FakeAutostart { FailWith = "Windows refused the change." };
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, autostart);

        page.StartWithWindows = true;

        Assert.False(page.StartWithWindows);
        Assert.Equal("Windows refused the change.", page.StartWithWindowsError);
        Assert.False(h.Settings.Current.Lifecycle.StartWithWindows);
    }

    [Fact]
    public void AppStartup_LifecycleOptionsAreSaved()
    {
        using var h = new Harness();
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new FakeAutostart());

        page.StartMinimised = true;
        page.CloseToTray = false;
        page.Notifications = NotificationLevel.All;

        var lifecycle = h.Settings.Current.Lifecycle;
        Assert.True(lifecycle.StartMinimised);
        Assert.False(lifecycle.CloseToTray);
        Assert.Equal(NotificationLevel.All, lifecycle.Notifications);
    }

    [Fact]
    public void Appearance_PartialHex_IsNotSaved_CompleteHexIs()
    {
        using var h = new Harness();
        using var page = new AppearanceViewModel(h.Settings, h.Dispatcher, new FakeThemeService());

        page.AccentHex = "#5B9";
        Assert.NotNull(page.AccentError);
        Assert.Equal("#D4A649", h.Settings.Current.Appearance.Accent);

        page.AccentHex = "#5b9bff";
        Assert.Null(page.AccentError);
        Assert.Equal("#5B9BFF", h.Settings.Current.Appearance.Accent);
    }

    [Fact]
    public void Appearance_ChoicesAreSaved()
    {
        using var h = new Harness();
        using var page = new AppearanceViewModel(h.Settings, h.Dispatcher, new FakeThemeService());

        page.Theme = ThemePreference.Light;
        page.Density = Density.Compact;
        page.FontScalePercent = 150;
        page.Motion = MotionPreference.Reduce;
        page.ChooseAccentCommand.Execute("#A371F7");

        var a = h.Settings.Current.Appearance;
        Assert.Equal(ThemePreference.Light, a.Theme);
        Assert.Equal(Density.Compact, a.Density);
        Assert.Equal(150, a.FontScalePercent);
        Assert.True(a.ReduceMotion);
        Assert.Equal("#A371F7", a.Accent);
        Assert.Equal(7, page.FontScales.Count);
    }

    [Fact]
    public void Appearance_ShowsTheThemesNotes()
    {
        using var h = new Harness();
        var theme = new FakeThemeService();
        using var page = new AppearanceViewModel(h.Settings, h.Dispatcher, theme);

        theme.Apply(new AppearanceSettings { Accent = "#202020" });

        Assert.Contains(page.ThemeNotes, n => n.Contains("adjusted", StringComparison.Ordinal));
    }

    [Fact]
    public void Home_ExplainsWhyNothingIsShown_WhenNotConfigured()
    {
        using var h = new Harness(Harness.Unconfigured());
        h.Coordinator.Start();
        using var controls = h.Controls();
        using var home = new HomeViewModel(controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time);

        Assert.True(home.Preview.IsEmpty);
        Assert.Equal("H5", home.Hero.Id);
        Assert.Equal("Finish setting up your own Discord app", home.Hero.Title);
        Assert.Equal(HeroActionKind.OpenAdvancedSettings, home.Hero.Action!.Kind);
        Assert.NotNull(home.EmptyText);
    }

    [Fact]
    public void Home_PreviewMatchesThePublishedPayload_AndTicks()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        using var controls = h.Controls();
        using var home = new HomeViewModel(controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time);

        h.Game.SetRunning(h.Time.GetUtcNow());

        // UI_SPEC §9.4: the card shows exactly the payload requested from Discord, field by field.
        var expected = ActivityPreview.From(h.Sink.Last, h.Coordinator.Application.Name, h.Settings.Current.ActiveProfile.Artwork, h.Time.GetUtcNow());
        Assert.Equal(expected with { Buttons = home.Preview.Buttons }, home.Preview);
        Assert.Equal(expected.Buttons, home.Preview.Buttons);
        Assert.Equal("00:00 elapsed", home.Preview.Elapsed);
        Assert.Equal(LivePreview.NotConnectedNote, home.DeliveryNote);

        h.Time.Advance(TimeSpan.FromSeconds(65));
        Assert.Equal("01:05 elapsed", home.Preview.Elapsed);

        h.Sink.SetHealth(new Discord.RpcHealth(Discord.RpcStatus.Connected, "discord-ipc-0", h.Time.GetUtcNow(), null, 1, null));
        Assert.Null(home.DeliveryNote);
    }

    [Fact]
    public void Home_WhileInactive_DoesNotTick()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        using var controls = h.Controls();
        using var home = new HomeViewModel(controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time);
        h.Game.SetRunning(h.Time.GetUtcNow());
        home.IsActive = false;
        var changes = 0;
        home.PropertyChanged += (_, _) => changes++;

        h.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Controls_ProfileListIsNotReplaced_ByUnrelatedChanges()
    {
        using var h = new Harness();
        using var controls = h.Controls();
        var before = controls.Profiles;

        h.Settings.Update(s => s with { Discord = s.Discord with { ApplicationName = "Something else" } });

        Assert.Same(before, controls.Profiles);
    }

    [Fact]
    public void Appearance_KeepsTheTypedCase_WhileSavingNormalised()
    {
        using var h = new Harness();
        using var page = new AppearanceViewModel(h.Settings, h.Dispatcher, new FakeThemeService());

        page.AccentHex = "#5b9bff";

        Assert.Equal("#5b9bff", page.AccentHex);
        Assert.Equal("#5B9BFF", h.Settings.Current.Appearance.Accent);
    }

    [Fact]
    public void Shell_HasFivePages_WithTheirShortcuts_AndTheNoticeIsDismissible()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app, notice: "Settings were restored from a backup.");
        var shell = kit.Shell;

        Assert.Equal("Settings were restored from a backup.", shell.Notice);
        shell.DismissNoticeCommand.Execute(null);
        Assert.Null(shell.Notice);
        Assert.Equal(["home", "status", "characters", "account", "settings"], shell.Pages.Select(p => p.Key));
        Assert.Equal(["Home", "Discord status", "Characters", "Account", "Settings"], shell.Pages.Select(p => p.Title));
        Assert.Equal([1, 2, 3, 4, 5], shell.Pages.Select(p => p.Shortcut));
        Assert.Equal("Discord status (Ctrl+2)", shell.Pages[1].ToolTipText);
        Assert.True(shell.SelectByShortcut(3));
        Assert.Equal("characters", shell.SelectedPage.Key);
        Assert.IsType<Characters.CharactersViewModel>(shell.SelectedPage.Page);
        Assert.False(shell.SelectByShortcut(6));
        Assert.True(shell.SelectByShortcut(5));
        Assert.Equal("settings", shell.SelectedPage.Key);
        Assert.True(shell.SelectByKey("account"));
        shell.NavigateCommand.Execute("home");
        Assert.Equal("home", shell.SelectedPage.Key);
    }

    [Theory]
    [InlineData("app", "settings")]
    [InlineData("privacy", "status")]
    [InlineData("presence", "status")]
    [InlineData("packs", "characters")]
    [InlineData("appearance", "settings")]
    [InlineData("about", "settings")]
    [InlineData("home", "home")]
    [InlineData("status", "status")]
    public void Shell_RetiredPageKeys_AreAliasesForTheirNewPage(string key, string expected)
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        kit.Shell.NavigateCommand.Execute(key);

        Assert.Equal(expected, kit.Shell.SelectedPage.Key);
    }

    [Fact]
    public void Shell_UnknownKey_IsRefused_AndKeepsThePage()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        kit.Shell.SelectByShortcut(2);

        Assert.False(kit.Shell.SelectByKey("nope"));

        Assert.Equal("status", kit.Shell.SelectedPage.Key);
    }

    [Fact]
    public void About_StatesTheBuildIsUnsigned()
    {
        var about = new AboutViewModel(s_app);

        Assert.Contains("not code-signed", about.SigningNotice, StringComparison.Ordinal);
        Assert.Contains(about.Disclaimer, d => d.Contains("No anti-cheat system has approved", StringComparison.Ordinal));
    }
}
