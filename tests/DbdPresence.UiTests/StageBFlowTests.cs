// Covers (on Windows CI, real executable): FR-LIFE-07 (the four-step startup wizard), docs/design/whole-app.md §2 to §7 and §13 (five pages, headline, style cards,
// settings sections, wizard), D30 (no manual inputs), FR-APP-01 (both themes), FR-APP-03 (150% text),
// FR-APP-04 (minimum window size), FR-RPC-07 (shared application by default; own Application ID validation inside Advanced), FR-RPC-13 (display name
// persistence and the `name` field on the wire), settings persistence across a
// graceful restart (ADR-0007), NFR-A11Y-01 (keyboard shortcuts; focusable controls have automation names),
// UI_SPEC §9 visual evidence (screenshots of every page), FR-RPC-01/05 end to end against a fake Discord pipe
// (activity sent, then cleared on quit), and the close-without-tray path (FR-LIFE-02).
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

// One collection: tests run one after another, as only one app instance may run per user (FR-LIFE-03).
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace DbdPresence.UiTests;

public sealed class StageBFlowTests
{
    private static readonly string[] s_pages = ["home", "status", "characters", "account", "settings"];
    private readonly ITestOutputHelper _output;

    public StageBFlowTests(ITestOutputHelper output) => _output = output;

    private const string CatalogClientId = "1563291325107937340";

    private static string Seeded(string theme, int fontScale = 100) =>
        $$"""{ "schemaVersion": 2, "firstRunCompleted": true, "appearance": { "theme": "{{theme}}", "fontScalePercent": {{fontScale}} } }""";

    /// <summary>On Discord status: chooses "Custom…" in the name list and types <paramref name="text"/> (saved as you type).</summary>
    private static void SetCustomDisplayName(AppUnderTest app, string text)
    {
        app.Find("DisplayNameChoice").AsComboBox().Select("Custom…");
        Retry.WhileNull(() => app.TryFind("DisplayName"), TimeSpan.FromSeconds(10));
        app.Find("DisplayName").AsTextBox().Text = text;
    }

    [Fact]
    public void FreshProfile_ShowsTheFourStepWizard_AndFinishRevealsTheShell()
    {
        using var app = AppUnderTest.Launch();

        Assert.Contains("Get started", app.Find("FirstRunNext").Name, StringComparison.Ordinal);
        Assert.NotNull(app.Find("WizardTitle"));
        app.CaptureWindow("b-wizard-1");
        Assert.True(app.IsShown("FirstRunSkip")); // "Skip setup": every default works
        // D32: sign-in and the invite code are the lock screen, which came before this; the wizard has no way to skip them or continue offline.
        Assert.Null(app.TryFind("FirstRunSignIn"));
        Assert.Null(app.TryFind("FirstRunContinueOffline"));
        Assert.False(app.IsShown("FirstRunBack"));

        app.Find("FirstRunNext").AsButton().Invoke(); // 2. Connect to Discord: the app checks by itself
        Assert.True(Retry.WhileNull(() => app.TryFind("DiscordLooking") ?? app.TryFind("DiscordConnected") ?? app.TryFind("DiscordNotOpen"), TimeSpan.FromSeconds(10)).Success);
        Assert.Null(app.TryFind("FirstRunApplicationId")); // no Application ID in the wizard (FR-RPC-07)
        app.CaptureWindow("b-wizard-2");

        app.Find("FirstRunNext").AsButton().Invoke(); // 3. Sign in: who the app is unlocked for
        Assert.NotNull(app.Find("WizardSignedIn"));
        Assert.False(app.IsShown("FirstRunSkip"));
        app.CaptureWindow("b-wizard-3");

        app.Find("FirstRunNext").AsButton().Invoke(); // 4. Ready
        Assert.NotNull(app.Find("WizardReadingNotNow"));
        Assert.Contains("Finish", app.Find("FirstRunNext").Name, StringComparison.Ordinal);
        app.CaptureWindow("b-wizard-4");

        app.Find("FirstRunNext").AsButton().Invoke();
        Assert.NotNull(app.Find("nav-home"));
        Assert.Null(app.TryFind("SetupPanel")); // the catalog application is always configured
        app.Quit();
        Assert.Contains("\"firstRunCompleted\": true", File.ReadAllText(app.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void FreshProfile_SkipSetup_RevealsTheShell_WithScreenReadingOff()
    {
        using var app = AppUnderTest.Launch();

        app.Find("FirstRunSkip").AsButton().Invoke();

        Assert.NotNull(app.Find("nav-home"));
        app.Quit();
        var saved = File.ReadAllText(app.SettingsFile);
        Assert.Contains("\"firstRunCompleted\": true", saved, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": false", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_ShowsTheHeadline_AndNoManualInputsOrStreamerSwitch()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);

        Assert.NotNull(app.Find("HeroTitle"));
        Assert.NotNull(app.Find("SeenLine"));
        foreach (var gone in new[] { "ManualRole", "ManualCharacter", "ManualMode", "StreamerToggle", "ProfileSelector", "TemplateUsed" })
        {
            Assert.Null(app.TryFind(gone));
        }

        Assert.NotNull(app.Find("PauseToggle"));
        app.Quit();
    }

    [Fact]
    public void DiscordStatus_PrivateStyle_IsSavedAsStreamerMode()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using (var app = AppUnderTest.Launch(root))
        {
            app.Navigate("status");
            Assert.NotNull(app.Find("StyleCard-Detailed"));
            Assert.NotNull(app.Find("ShowMode"));
            Assert.NotNull(app.Find("ShowScene"));
            Assert.NotNull(app.Find("ShowResult"));
            app.Find("StyleCard-Private").Click();
            Assert.True(Retry.WhileFalse(() => app.Find("StyleCard-Private").AsRadioButton().IsChecked == true, TimeSpan.FromSeconds(5)).Success);
            app.CaptureWindow("b-status-private");
            app.Quit();
        }

        Assert.Contains("\"streamerMode\": true", File.ReadAllText(Path.Combine(root, "settings", "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordStatus_AutomaticBadge_IsOfferedForTheSmallBadgeOnly_ExplainedInPlainWords_AndSaved()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using (var app = AppUnderTest.Launch(root))
        {
            app.Navigate("status");
            Assert.NotNull(app.Find("SmallBadge-Automatic"));
            Assert.Null(app.TryFind("BigPicture-Automatic"));
            Assert.Contains("role icon", app.Find("AutomaticCaption").Name, StringComparison.Ordinal);
            app.ClickInView("SmallBadge-None");
            Assert.True(Retry.WhileFalse(() => app.Find("SmallBadge-None").AsRadioButton().IsChecked == true, TimeSpan.FromSeconds(5)).Success);
            app.ClickInView("SmallBadge-Automatic");
            Assert.True(Retry.WhileFalse(() => app.Find("SmallBadge-Automatic").AsRadioButton().IsChecked == true, TimeSpan.FromSeconds(5)).Success);
            app.CaptureWindow("b-status-automatic");
            app.Quit();
        }

        Assert.Contains("\"source\": \"Automatic\"", File.ReadAllText(Path.Combine(root, "settings", "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_ScreenReading_NeedsTheConfirmationAndTheTick()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("settings");

        app.Find("ScreenReadingTurnOn").AsButton().Invoke();
        Assert.NotNull(app.Find("ScreenReadingSheetTitle"));
        Assert.False(app.Find("ScreenReadingConfirm").IsEnabled);
        app.CaptureWindow("b-settings-confirm");
        app.Find("ScreenReadingUnderstood").AsCheckBox().Toggle();
        Assert.True(Retry.WhileFalse(() => app.Find("ScreenReadingConfirm").IsEnabled, TimeSpan.FromSeconds(5)).Success);
        app.Find("ScreenReadingConfirm").AsButton().Invoke();

        Assert.True(Retry.WhileFalse(() => app.Find("ScreenReadingStatus").Name.StartsWith("On", StringComparison.Ordinal), TimeSpan.FromSeconds(10)).Success);
        app.Quit();
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void EveryPage_RendersAndIsCaptured(string theme)
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded(theme));
        using var app = AppUnderTest.Launch(root);

        foreach (var page in s_pages)
        {
            app.Navigate(page);
            var shot = app.CaptureWindow($"b-{theme.ToLowerInvariant()}-{page}");
            _output.WriteLine(shot);
        }

        // Minimum supported size (FR-APP-04): every page must still render (reviewed on the screenshots).
        app.MainWindow.Patterns.Transform.Pattern.Resize(880, 600);
        foreach (var page in s_pages)
        {
            app.Navigate(page);
            app.CaptureWindow($"b-{theme.ToLowerInvariant()}-min-{page}");
        }

        app.Quit();
    }

    [Fact]
    public void LargestTextScale_RendersTheBusiestPages()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark", fontScale: 150));
        using var app = AppUnderTest.Launch(root);

        foreach (var page in new[] { "home", "status", "settings" })
        {
            app.Navigate(page);
            app.CaptureWindow($"b-dark-150-{page}");
        }

        app.Quit();
    }

    [Fact]
    public void Settings_PersistAcrossAGracefulRestart()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using (var app = AppUnderTest.Launch(root))
        {
            app.Navigate("status");
            SetCustomDisplayName(app, "Fog Watcher");
            app.Find("ShowTimers").AsCheckBox().Toggle();
            app.Quit();
        }

        using var again = AppUnderTest.Launch(root);
        again.Navigate("status");
        Assert.Equal("Custom…", again.Find("DisplayNameChoice").AsComboBox().SelectedItem?.Text);
        Assert.Equal("Fog Watcher", again.Find("DisplayName").AsTextBox().Text);
        Assert.Equal(FlaUI.Core.Definitions.ToggleState.Off, again.Find("ShowTimers").AsCheckBox().ToggleState);
        again.Quit();
    }

    [Fact]
    public void InvalidApplicationId_ShowsAnInlineError_AndIsNotSaved()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("settings");
        Assert.Null(app.TryFind("ApplicationId")); // inside the collapsed Advanced section
        app.Find("AdvancedExpander").Patterns.ExpandCollapse.Pattern.Expand();

        // The ID is saved when the box loses focus (or on Enter), never per keystroke.
        var id = app.Find("ApplicationId").AsTextBox();
        id.Focus();
        id.Text = "12345";
        app.Find("ApplicationName").Focus();

        var error = app.Find("ApplicationIdError");
        Assert.Contains("17–20 digits", error.Name, StringComparison.Ordinal);
        app.CaptureWindow("b-dark-app-invalid-id");
        app.Quit();
        Assert.DoesNotContain("12345", File.ReadAllText(app.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlNumber_SwitchesPages()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.MainWindow.Focus();

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2);

        Assert.True(Retry.WhileNull(() => app.TryFind("DiscordStatusPage"), TimeSpan.FromSeconds(5)).Success);
        app.Quit();
    }

    [Fact]
    public void KeyboardTraversal_FocusesOnlyNamedControls()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);
        app.Navigate("settings");
        app.Find("nav-settings").Focus();

        var unnamed = new List<string>();
        var seen = 0;
        for (var i = 0; i < 30; i++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            Wait.UntilInputIsProcessed();
            var focused = app.MainWindow.Automation.FocusedElement();
            if (focused is null)
            {
                continue;
            }

            seen++;
            if (string.IsNullOrWhiteSpace(focused.Properties.Name.ValueOrDefault))
            {
                unnamed.Add($"{focused.ControlType} '{focused.AutomationId}'");
            }

            if (i == 3)
            {
                app.CaptureWindow("b-dark-focus-sample");
            }
        }

        _output.WriteLine($"Focused {seen} elements; unnamed: {string.Join(", ", unnamed)}");
        Assert.True(seen > 5, "Tab did not move focus through the page.");
        Assert.Empty(unnamed);
        app.Quit();
    }

    [Fact]
    public void ConnectionStatus_IsExposedToAssistiveTechnology()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, Seeded("Dark"));
        using var app = AppUnderTest.Launch(root);

        var status = app.Find("ConnectionStatus");

        Assert.NotEqual("Not configured", status.Name); // a fresh install uses the catalog application
        Assert.Null(app.TryFind("SetupPanel"));
        Assert.NotNull(app.Find("HeroTitle"));
        Assert.NotNull(app.Find("EmptyCardText")); // the game is closed, so the card says why nothing is on Discord
        app.Quit();
    }

    [Fact]
    public void ClosingTheWindow_WithCloseToTrayOff_QuitsAndSaves()
    {
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, """{ "schemaVersion": 2, "firstRunCompleted": true, "lifecycle": { "closeToTray": false } }""");
        using var app = AppUnderTest.Launch(root);
        app.Navigate("status");
        SetCustomDisplayName(app, "Closed by X");

        app.CloseWindowExpectingExit();

        Assert.Contains("Closed by X", File.ReadAllText(app.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discord_ReceivesTheCatalogClientId_TheDisplayName_AndTheActivity_AndItIsClearedOnQuit()
    {
        await using var discord = new FakeDiscordPipeServer();
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, """
            { "schemaVersion": 2, "firstRunCompleted": true,
              "discord": { "displayName": "Fog Watcher", "selectedApplication": "dbd", "useOwnApplication": false },
              "profiles": [ { "id": "default", "name": "Default", "whenNotRunning": "ShowTemplate" } ] }
            """);
        using var app = AppUnderTest.Launch(root);

        Assert.True(discord.WaitFor(d => d.Activities.Any(a => a is not null), TimeSpan.FromSeconds(30)), "No activity reached the fake Discord.");
        Assert.Equal(CatalogClientId, discord.ClientId);
        var shown = discord.Activities.Last(a => a is not null)!.Value;
        Assert.Equal("Taking a break from the Fog", shown.GetProperty("details").GetString());
        Assert.Equal("Fog Watcher", shown.GetProperty("name").GetString());
        Assert.True(Retry.WhileFalse(() => app.Find("ConnectionStatus").Name == "Connected to Discord", TimeSpan.FromSeconds(10)).Success);
        app.CaptureWindow("b-dark-home-connected");

        app.Quit();

        Assert.True(discord.WaitFor(d => d.ClientDisconnected, TimeSpan.FromSeconds(10)), "The app did not close the pipe.");
        Assert.Null(discord.Activities[^1]);
    }

    [Fact]
    public async Task V1Settings_WithAnApplicationId_KeepConnectingWithThatId()
    {
        await using var discord = new FakeDiscordPipeServer();
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, """
            { "schemaVersion": 1, "firstRunCompleted": true,
              "discord": { "applicationId": "123456789012345678", "applicationName": "Dead by Daylight" },
              "profiles": [ { "id": "default", "name": "Default", "whenNotRunning": "ShowTemplate" } ] }
            """);
        using var app = AppUnderTest.Launch(root);

        Assert.True(discord.WaitFor(d => d.Activities.Any(a => a is not null), TimeSpan.FromSeconds(30)), "No activity reached the fake Discord.");
        Assert.Equal("123456789012345678", discord.ClientId);
        app.Quit();
    }

    [Fact]
    public async Task RunningGame_IsShownOnHome_AndSentToDiscordWithItsStartTime()
    {
        await using var discord = new FakeDiscordPipeServer();
        using var game = StandInGame.Start();
        var root = AppUnderTest.NewDataRoot();
        AppUnderTest.Seed(root, """
            { "schemaVersion": 2, "firstRunCompleted": true }
            """);
        using var app = AppUnderTest.Launch(root);

        Assert.True(Retry.WhileNull(() => app.MainWindow.FindFirstDescendant(cf => cf.ByName("You're live on Discord")), TimeSpan.FromSeconds(20)).Success);
        Assert.True(discord.WaitFor(d => d.Activities.Any(a => a?.GetProperty("details").GetString() == "Playing Dead by Daylight"), TimeSpan.FromSeconds(30)));
        var running = discord.Activities.Last(a => a is not null)!.Value;
        Assert.True(running.GetProperty("timestamps").GetProperty("start").GetInt64() > 0);
        app.CaptureWindow("b-dark-home-game-running");

        game.Stop();
        Assert.True(discord.WaitFor(d => d.Activities[^1] is null, TimeSpan.FromSeconds(30)), "The activity was not cleared after the game exited.");
        app.Quit();
    }
}
