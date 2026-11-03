// Covers: docs/design/whole-app.md §3 (Home: status headline H1 to H10 with one button and two facts, at most two next steps, one plain line about
// screen reading, no per-detail learning view for users), §3.4 (HiddenReason codes turned into plain sentences), §7 (Settings, screen reading
// confirmation), D30 (everything automatic), D32.
using DbdPresence.Core.Composition;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Discord;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class HomeAndSettingsViewModelTests
{
    private static readonly AppInfo s_app = new("1.0.0", "abc1234", IsSigned: false, "settings", "data");

    private static RpcHealth Health(RpcStatus status, Harness h, bool attempted = true) =>
        new(status, status == RpcStatus.Connected ? "discord-ipc-0" : null, null, attempted ? "x" : null, 0, attempted && status == RpcStatus.Disconnected ? h.Time.GetUtcNow().AddSeconds(8) : null);

    [Fact]
    public void Headline_Starting_BeforeAnyAttempt()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();

        h.Sink.SetHealth(new RpcHealth(RpcStatus.Disconnected, null, null, null, 0, null));

        Assert.Equal("H1", kit.Home.Hero.Id);
        Assert.Equal("Starting up", kit.Home.Hero.Title);
        Assert.Equal(["Checking…", "Checking…"], kit.Home.Hero.Facts.Select(f => f.Value));
    }

    [Fact]
    public void Headline_AccessWithdrawn_PointsToAccount()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        kit.AccountService.Set(OnlineState.Revoked);

        Assert.Equal("H2", kit.Home.Hero.Id);
        Assert.Equal(HeroActionKind.OpenAccount, kit.Home.Hero.Action!.Kind);
        HomeLink? link = null;
        kit.Home.LinkRequested += (_, l) => link = l;
        kit.Home.RunHeroActionCommand.Execute(null);
        Assert.Equal(HomeLink.Account, link);
    }

    [Fact]
    public void Headline_SettingsNotSaved_PointsToWhereTheyAreSaved()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        h.Settings.RaiseSaveFailed(new IOException("disk full"));

        Assert.Equal("H3", kit.Home.Hero.Id);
        Assert.Equal("Your changes can't be saved", kit.Home.Hero.Title);
        HomeLink? link = null;
        kit.Home.LinkRequested += (_, l) => link = l;
        kit.Home.RunHeroActionCommand.Execute(null);
        Assert.Equal(HomeLink.SettingsDataFolder, link);
    }

    [Theory]
    [InlineData(false, "Discord shows nothing until you resume.")]
    [InlineData(true, "Discord only shows that you're playing until you resume.")]
    public void Headline_Paused_OffersResume(bool generic, string detail)
    {
        using var h = new Harness(Harness.Configured(new Configuration.AppSettings { Profiles = [Configuration.BuiltInProfiles.Default with { WhenPaused = generic ? Core.Settings.PauseBehavior.Generic : Core.Settings.PauseBehavior.Clear }] }));
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        kit.Controls.IsPaused = true;

        Assert.Equal("H4", kit.Home.Hero.Id);
        Assert.Equal(detail, kit.Home.Hero.Detail);
        kit.Home.RunHeroActionCommand.Execute(null);
        Assert.False(h.Coordinator.Paused);
    }

    [Fact]
    public void Headline_OwnAppWithoutId_OpensAdvancedSettings()
    {
        using var h = new Harness(Harness.Unconfigured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();

        Assert.Equal("H5", kit.Home.Hero.Id);
        Assert.Equal("Open Advanced settings", kit.Home.Hero.Action!.Label);
        Assert.Equal("Not set up", kit.Home.Hero.Facts[0].Value);
    }

    [Fact]
    public void Headline_DiscordNotOpen_ShowsTheRetryCountdown_AndHasNoButton()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();

        h.Sink.SetHealth(Health(RpcStatus.Disconnected, h));

        Assert.Equal("H6", kit.Home.Hero.Id);
        Assert.Equal("Open Discord on this PC", kit.Home.Hero.Title);
        Assert.Contains("Next try in 8 s.", kit.Home.Hero.Detail, StringComparison.Ordinal);
        Assert.Null(kit.Home.Hero.Action);
        Assert.Equal("Not open", kit.Home.Hero.Facts[0].Value);
    }

    [Fact]
    public void Headline_Connecting_AndConnectionProblem()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();

        h.Sink.SetHealth(Health(RpcStatus.Connecting, h, attempted: false));
        Assert.Equal("H7", kit.Home.Hero.Id);

        h.Sink.SetHealth(Health(RpcStatus.Faulted, h));
        Assert.Equal("H7", kit.Home.Hero.Id);
        Assert.StartsWith("Discord didn't answer.", kit.Home.Hero.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Headline_Connected_FollowsTheGame_H8_H9_H10()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        Assert.Equal("H8", kit.Home.Hero.Id);
        Assert.Equal("Ready when you are", kit.Home.Hero.Title);
        Assert.Equal(["Connected", "Not running"], kit.Home.Hero.Facts.Select(f => f.Value));

        h.Game.Set(new GameLifecycle { State = GameState.Launching });
        Assert.Equal("H9", kit.Home.Hero.Id);

        h.Game.SetRunning(h.Time.GetUtcNow());
        Assert.Equal("H10", kit.Home.Hero.Id);
        Assert.Equal("You're live on Discord", kit.Home.Hero.Title);
        Assert.StartsWith("Running since", kit.Home.Hero.Facts[1].Value, StringComparison.Ordinal);
        Assert.Empty(kit.Home.NextSteps); // never in the healthy live state
    }

    [Fact]
    public void Headline_ShowsTheConfirmedCharacter_WhenThereIsOne()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));
        h.Game.SetRunning(h.Time.GetUtcNow());

        var hero = HeroStatusBuilder.Build(new HeroInputs(h.Coordinator.Snapshot, h.Time.GetUtcNow(), TimeZoneInfo.Utc, false, false, false, false, "Meg Thomas"));

        Assert.Equal("Your friends can see you're playing as Meg Thomas.", hero.Detail);
    }

    [Theory]
    [InlineData(RpcStatus.Connected, false)]
    [InlineData(RpcStatus.Connected, true)]
    [InlineData(RpcStatus.Disconnected, false)]
    public void Headline_ButtonAlwaysHasALabel_OrThereIsNoButton(RpcStatus status, bool running)
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(status, h));
        if (running)
        {
            h.Game.SetRunning(h.Time.GetUtcNow());
        }

        var action = kit.Home.Hero.Action;
        Assert.True(action is null || !string.IsNullOrWhiteSpace(action.Label));
    }

    [Fact]
    public void ClosedGame_SaysNothingIsOnDiscordOnce_NotAlsoAsAHint()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        Assert.True(kit.Home.IsEmpty);
        Assert.Equal("Nothing on Discord right now. Your status appears when you start the game.", kit.Home.EmptyText);
        Assert.DoesNotContain(kit.Home.Hints, hint => hint.Id == "game-closed");
    }

    [Fact]
    public void Headline_ClosedGame_SaysTakingABreakOnlyWhenThatIsOn()
    {
        using var h = new Harness(Harness.Configured(new Configuration.AppSettings { Profiles = [Configuration.BuiltInProfiles.Default with { WhenNotRunning = Core.Settings.NotRunningBehavior.ShowTemplate }] }));
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        Assert.Equal("Discord shows that you're taking a break.", kit.Home.Hero.Detail);
    }

    [Fact]
    public void ScreenReadingOff_SuggestsTurningItOn_ThenNotNowIsRemembered()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        var step = Assert.Single(kit.Home.NextSteps);
        Assert.Equal("Turn on screen reading…", step.ActionLabel);
        Assert.Equal("Not now", step.DismissLabel);
        Assert.Equal("Next step", kit.Home.NextStepsTitle);

        kit.Home.DismissNextStepCommand.Execute(HomeViewModel.NextStepIds.ScreenReading);

        Assert.Empty(kit.Home.NextSteps);
        Assert.True(h.Settings.Current.Lifecycle.ScreenReadingTipDismissed);
    }

    [Fact]
    public void TheNextStep_OpensTheConfirmation_ThroughTheShell_AndNeverTurnsAnythingOnByItself()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Sink.SetHealth(Health(RpcStatus.Connected, h));

        kit.Home.RunNextStepCommand.Execute(HomeViewModel.NextStepIds.ScreenReading);

        Assert.Equal("settings", kit.Shell.SelectedPage.Key);
        Assert.True(kit.ScreenReading.IsConfirming);
        Assert.False(kit.ScreenReading.IsOn);
        Assert.False(h.Settings.Current.Detection.Enabled);
    }

    [Fact]
    public void WhatTheAppSees_IsOnePlainLine_AndNeverAPerDetailLearningList()
    {
        using var h = new Harness(Harness.Configured());
        using var kit = new AppKit(h, s_app);

        Assert.Equal("Screen reading is off. Discord shows only that you're playing and for how long.", kit.Home.SeenLine);
        Assert.Null(typeof(HomeViewModel).GetProperty("Rows"));
        Assert.Null(typeof(HomeViewModel).GetProperty("Seen"));
        Assert.DoesNotContain("Learning", kit.Home.SeenLine, StringComparison.Ordinal);

        h.Settings.Update(s => s with { Detection = s.Detection with { Enabled = true, RiskAcknowledged = true } });

        Assert.DoesNotContain("Screen reading is off", kit.Home.SeenLine, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatTheAppSees_WithoutScreenReadingInTheBuild_SaysSo()
    {
        using var h = new Harness(Harness.Configured());
        using var controls = h.Controls();
        using var home = new HomeViewModel(controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time);

        Assert.Equal("Screen reading isn't part of this version yet. Discord shows only that you're playing and for how long.", home.SeenLine);
        Assert.Empty(home.NextSteps);
    }

    [Fact]
    public void Reasons_AreListedThreeAtATime_AndShowAllListsEveryOne()
    {
        using var h = new Harness(Harness.Configured(), Role.Killer);
        using var kit = new AppKit(h, s_app);
        h.Coordinator.Start();
        h.Game.SetRunning(h.Time.GetUtcNow());
        kit.Privacy.ShowMode = false;
        h.Settings.Update(s => s with { Profiles = [s.ActiveProfile with { Visibility = s.ActiveProfile.Visibility with { ShowMap = false, ShowTimers = false, ShowCharacter = false } }] });
        h.Coordinator.Recompute();

        Assert.True(kit.Home.ShownReasons.Count <= 3);
        if (kit.Home.ShowAllVisible)
        {
            kit.Home.ShowAllReasonsNowCommand.Execute(null);
            Assert.Equal(kit.Home.Reasons.All.Count, kit.Home.ShownReasons.Count);
            Assert.False(kit.Home.ShowAllVisible);
        }

        Assert.All(kit.Home.Reasons.All, r => Assert.DoesNotContain("template", r, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Plain reasons ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HiddenReasonCode.HiddenCompletely, null, "You chose to hide your activity completely.")]
    [InlineData(HiddenReasonCode.PausedGeneric, null, "Paused, so only “Playing Dead by Daylight” is shown.")]
    [InlineData(HiddenReasonCode.PrivateStyle, null, "Private style: details are kept off Discord.")]
    [InlineData(HiddenReasonCode.DetailNotConfirmed, "Map", "Map: the app is still learning to read it.")]
    [InlineData(HiddenReasonCode.MapHiddenByUser, null, "Map: hidden by you.")]
    [InlineData(HiddenReasonCode.MapAfterMatch, null, "Map: shown when the match ends.")]
    [InlineData(HiddenReasonCode.LiveStatsOff, null, "Live match details are off.")]
    [InlineData(HiddenReasonCode.TimerHiddenByUser, null, "Match timer: hidden by you.")]
    [InlineData(HiddenReasonCode.TimerStartNotSeen, null, "Match timer: hidden because the app didn't see the match start.")]
    [InlineData(HiddenReasonCode.RolePictureNeedsRole, null, "Role picture: shown once the app knows your role.")]
    [InlineData(HiddenReasonCode.PictureMayNotExist, "app_logo", "Picture “app_logo” may not exist in your own Discord app.")]
    [InlineData(HiddenReasonCode.StatusPictureMissing, "On the hook", "On the hook: your pack has no picture for this, so your role icon is shown.")]
    [InlineData(HiddenReasonCode.NothingToShow, null, "Nothing to show for this moment, so Discord shows nothing.")]
    public void PlainReasons_TurnEachCodeIntoOneSentence(HiddenReasonCode code, string? subject, string expected) =>
        Assert.Equal(expected, PlainReasons.Describe(new HiddenReason(code, subject)));

    [Fact]
    public void PlainReasons_GameClosedAndTextFallback_AreNotListedAsReasons()
    {
        Assert.Null(PlainReasons.Describe(new HiddenReason(HiddenReasonCode.GameNotRunning)));
        Assert.Null(PlainReasons.Describe(new HiddenReason(HiddenReasonCode.TextFallback)));
        Assert.Equal("Nothing on Discord right now. Your status appears when you start the game.", PlainReasons.EmptyCard([new HiddenReason(HiddenReasonCode.GameNotRunning)]));
        Assert.Equal("Nothing on Discord: you chose to hide your activity completely.", PlainReasons.EmptyCard([new HiddenReason(HiddenReasonCode.HiddenCompletely)]));
        Assert.Null(PlainReasons.EmptyCard([new HiddenReason(HiddenReasonCode.MapHiddenByUser)]));
    }

    [Fact]
    public void PlainReasons_ShowThreeMostImportantFirst_WithoutRepeats_AndCountTheRest()
    {
        var list = PlainReasons.For(
        [
            new HiddenReason(HiddenReasonCode.TimerHiddenByUser), new HiddenReason(HiddenReasonCode.HiddenCompletely), new HiddenReason(HiddenReasonCode.MapHiddenByUser),
            new HiddenReason(HiddenReasonCode.LiveStatsOff), new HiddenReason(HiddenReasonCode.MapHiddenByUser), new HiddenReason(HiddenReasonCode.TextFallback),
        ]);

        Assert.Equal(["You chose to hide your activity completely.", "Map: hidden by you.", "Live match details are off."], list.Shown);
        Assert.Equal(4, list.All.Count);
        Assert.True(list.HasMore);
        Assert.Equal("Show all (4)", list.ShowAllLabel);
    }

    [Fact]
    public void EveryReasonSentence_FollowsTheCopyRules()
    {
        string[] banned = ["!", "simply", "just", "easily", "please", "template", "token", "situation", "asset", "bytes"];
        foreach (var code in Enum.GetValues<HiddenReasonCode>())
        {
            var text = PlainReasons.Describe(new HiddenReason(code, "Map"));
            if (text is null)
            {
                continue;
            }

            foreach (var word in banned)
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ---- Screen reading and Settings ----------------------------------------------------------------------------------

    [Fact]
    public void ScreenReading_NeedsTheConfirmationAndTheTick_AndCancelChangesNothing()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        var screen = kit.ScreenReading;
        Assert.Equal("Off", screen.StatusText);
        Assert.Equal(4, screen.ConfirmLines.Count);

        screen.BeginTurnOn();
        Assert.True(screen.IsConfirming);
        Assert.False(screen.ConfirmTurnOnCommand.CanExecute(null));
        screen.CancelTurnOnCommand.Execute(null);
        Assert.False(screen.IsConfirming);
        Assert.False(h.Settings.Current.Detection.Enabled);

        screen.BeginTurnOn();
        screen.Understood = true;
        Assert.True(screen.ConfirmTurnOnCommand.CanExecute(null));
        screen.ConfirmTurnOnCommand.Execute(null);

        Assert.True(screen.IsOn);
        Assert.False(screen.IsConfirming);
        Assert.True(h.Settings.Current.Detection.Enabled);
        Assert.True(h.Settings.Current.Detection.RiskAcknowledged);
        Assert.False(screen.BeginTurnOnCommand.CanExecute(null));

        screen.TurnOffCommand.Execute(null);
        Assert.False(h.Settings.Current.Detection.Enabled);
        Assert.False(h.Settings.Current.Detection.RiskAcknowledged);
        Assert.Equal("Off", screen.StatusText);
    }

    [Fact]
    public void ScreenReading_WithoutTheFeatureInTheBuild_SaysSo_AndCannotBeTurnedOn()
    {
        using var h = new Harness();
        using var screen = new ScreenReadingViewModel(h.Settings, h.Dispatcher);

        Assert.False(screen.IsAvailable);
        Assert.Equal("Not part of this version yet", screen.StatusText);
        Assert.False(screen.BeginTurnOnCommand.CanExecute(null));
    }

    [Fact]
    public void ScreenReading_StatusLine_FollowsTheLoop()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        var source = new FakeDetectionStatus();
        using var screen = new ScreenReadingViewModel(h.Settings, h.Dispatcher, source);
        h.Settings.Update(s => s with { Detection = s.Detection with { Enabled = true, RiskAcknowledged = true } });

        source.Set(Presentation.Detection.DetectionStatus.Off with { Run = Presentation.Detection.DetectionRunState.Reading });
        Assert.Equal("On · reading Dead by Daylight", screen.StatusText);

        source.Set(Presentation.Detection.DetectionStatus.Off with { Run = Presentation.Detection.DetectionRunState.WaitingForGame });
        Assert.Equal("On · waiting for the game", screen.StatusText);
    }

    [Fact]
    public void Settings_ReduceMotionIsASwitch_ThatFollowsWindowsWhenOff()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);

        Assert.False(kit.SettingsPage.ReduceMotion);
        kit.SettingsPage.ReduceMotion = true;
        Assert.True(h.Settings.Current.Appearance.ReduceMotion);
        kit.SettingsPage.ReduceMotion = false;
        Assert.Null(h.Settings.Current.Appearance.ReduceMotion);
    }

    [Fact]
    public void Settings_ShowOpensTheAdvancedSection_AndAsksTheViewToScroll()
    {
        using var h = new Harness();
        using var kit = new AppKit(h, s_app);
        SettingsSection? asked = null;
        kit.SettingsPage.SectionRequested += (_, s) => asked = s;

        kit.SettingsPage.Show(SettingsSection.Advanced);

        Assert.True(kit.SettingsPage.AdvancedOpen);
        Assert.Equal(SettingsSection.Advanced, asked);
    }

    [Fact]
    public void Tray_Summary_FollowsTheDesign()
    {
        using var h = new Harness(Harness.Configured());
        using var controls = h.Controls();
        h.Coordinator.Start();

        h.Sink.SetHealth(Health(RpcStatus.Connected, h));
        controls.RefreshStatus();
        Assert.Equal("DBD Presence · Discord connected · Game not running", controls.TraySummary);

        h.Game.SetRunning(h.Time.GetUtcNow());
        Assert.Equal("DBD Presence · Discord connected · Playing", controls.TraySummary);

        controls.IsPaused = true;
        Assert.Equal("DBD Presence · Discord connected · Paused", controls.TraySummary);
    }
}
