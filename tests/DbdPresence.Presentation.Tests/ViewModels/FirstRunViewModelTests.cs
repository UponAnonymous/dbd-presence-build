// Covers: FR-LIFE-07 and docs/design/whole-app.md §13 (the four-step startup wizard: Welcome, Connect Discord, Sign in, Ready), D32 (sign-in and the invite
// code are the lock screen, so the wizard's Sign in step has no skip and no optional account), FR-RPC-07 (no Application ID in the wizard), D16 (no role icon
// pack step), D30 (screen reading is the only choice on the last step).
using DbdPresence.Configuration;
using DbdPresence.Core.Online;
using DbdPresence.Discord;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class FirstRunViewModelTests
{
    private static (FirstRunViewModel Flow, FakeAccountService Account, ScreenReadingViewModel Screen) Create(Harness h, bool signedIn = true)
    {
        var accountService = new FakeAccountService();
        if (signedIn)
        {
            accountService.Set(OnlineState.Active, "Ana");
        }

        var controls = h.Controls();
        var account = new AccountViewModel(accountService, h.Dispatcher);
        var screen = new ScreenReadingViewModel(h.Settings, h.Dispatcher);
        var home = new HomeViewModel(controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time);
        return (new FirstRunViewModel(h.Settings, controls, account, screen, home), accountService, screen);
    }

    private static void Walk(FirstRunViewModel flow, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            flow.NextCommand.Execute(null);
        }
    }

    [Fact]
    public void OpensOnlyUntilCompleted()
    {
        using var fresh = new Harness();
        using var done = new Harness(new AppSettings { FirstRunCompleted = true });

        Assert.True(Create(fresh).Flow.IsOpen);
        Assert.False(Create(done).Flow.IsOpen);
    }

    [Fact]
    public void HasExactlyTheFourDesignedSteps_InOrder_WithNoPackStep()
    {
        Assert.Equal([FirstRunStep.Welcome, FirstRunStep.ConnectDiscord, FirstRunStep.SignIn, FirstRunStep.Ready], Enum.GetValues<FirstRunStep>());
        Assert.DoesNotContain(Enum.GetNames<FirstRunStep>(), n => n.Contains("Pack", StringComparison.Ordinal));
        using var h = new Harness();
        Assert.Equal(4, Create(h).Flow.StepCount);
    }

    [Fact]
    public void WalksTheSteps_WithTheDesignedButtons_AndFinishingIsRemembered()
    {
        using var h = new Harness();
        var (flow, _, _) = Create(h);
        var completed = false;
        flow.Completed += (_, _) => completed = true;

        Assert.Equal(1, flow.StepNumber);
        Assert.Equal("Get started", flow.NextLabel);
        Assert.False(flow.BackCommand.CanExecute(null));
        Assert.Equal("Skip setup", flow.SkipLabel);

        flow.NextCommand.Execute(null);
        Assert.Equal(FirstRunStep.ConnectDiscord, flow.Step);
        Assert.Equal("Next", flow.NextLabel);
        Assert.True(flow.BackCommand.CanExecute(null));

        flow.NextCommand.Execute(null);
        Assert.Equal(FirstRunStep.SignIn, flow.Step);
        Assert.False(flow.CanSkip); // D32: no optional sign-in
        Assert.Equal(string.Empty, flow.SkipLabel);

        flow.NextCommand.Execute(null);
        Assert.True(flow.IsLastStep);
        Assert.Equal("Finish", flow.NextLabel);
        Assert.Equal(4, flow.StepNumber);
        Assert.False(flow.CanSkip); // the last step has Back and Finish only

        flow.NextCommand.Execute(null);

        Assert.False(flow.IsOpen);
        Assert.True(completed);
        Assert.True(h.Settings.Current.FirstRunCompleted);
        Assert.False(h.Settings.Current.Detection.Enabled); // Not now is the default
    }

    [Fact]
    public void SkipSetup_OnTheWelcomeScreen_CompletesWithDefaults()
    {
        using var h = new Harness();
        var (flow, _, _) = Create(h);

        flow.SkipCommand.Execute(null);

        Assert.False(flow.IsOpen);
        Assert.True(h.Settings.Current.FirstRunCompleted);
        Assert.Null(h.Settings.Current.Discord.DisplayName);
        Assert.False(h.Settings.Current.Discord.UseOwnApplication);
        Assert.False(h.Settings.Current.Detection.Enabled);
    }

    [Fact]
    public void ConnectDiscord_ChecksByItself_AndOffersSkipOnlyWhileDiscordIsNotOpen()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        var (flow, _, _) = Create(h);
        Walk(flow, 1);
        Assert.Equal(FirstRunStep.ConnectDiscord, flow.Step);

        h.Sink.SetHealth(new RpcHealth(RpcStatus.Connecting, null, null, null, 0, null));
        Assert.Equal(DiscordCheck.Looking, flow.Discord);

        h.Sink.SetHealth(new RpcHealth(RpcStatus.Disconnected, null, null, "Discord is not running.", 0, h.Time.GetUtcNow().AddSeconds(8)));
        Assert.Equal(DiscordCheck.NotOpen, flow.Discord);
        Assert.Equal("Skip for now", flow.SkipLabel);
        Assert.True(flow.SkipCommand.CanExecute(null));

        h.Sink.SetHealth(new RpcHealth(RpcStatus.Connected, "discord-ipc-0", h.Time.GetUtcNow(), null, 0, null));
        Assert.Equal(DiscordCheck.Connected, flow.Discord);
        Assert.False(flow.CanSkip);
        Assert.True(flow.NextCommand.CanExecute(null)); // Next works in every state
    }

    [Fact]
    public void SkipForNow_OnTheDiscordStep_MovesOnWithoutFinishing()
    {
        using var h = new Harness(Harness.Configured());
        h.Coordinator.Start();
        var (flow, _, _) = Create(h);
        Walk(flow, 1);
        h.Sink.SetHealth(new RpcHealth(RpcStatus.Disconnected, null, null, "Discord is not running.", 0, h.Time.GetUtcNow().AddSeconds(8)));

        flow.SkipCommand.Execute(null);

        Assert.Equal(FirstRunStep.SignIn, flow.Step);
        Assert.True(flow.IsOpen);
        Assert.False(h.Settings.Current.FirstRunCompleted);
    }

    [Fact]
    public void SignInStep_WhenTheSessionEnded_IsNotShownAsSignedInGood_AndSaysWhatIsTrue()
    {
        using var h = new Harness();
        var (flow, account, _) = Create(h);
        Assert.True(flow.SignedInIsGood);

        account.Set(OnlineState.OfflineWithGrace, "Ana", features: false, needsSignIn: true, notice: AccountSnapshot.SessionEndedNotice);

        Assert.False(flow.SignedInIsGood);
        Assert.StartsWith("Your session ended.", flow.SignedInLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Signed in as", flow.SignedInLine, StringComparison.Ordinal);
    }

    [Fact]
    public void SignInStep_ShowsWhoTheAppIsUnlockedFor_AndTheNoticeSummaryWithTheFullNoticeOnRequest()
    {
        using var h = new Harness();
        var (flow, _, _) = Create(h);
        Walk(flow, 2);

        Assert.Equal(FirstRunStep.SignIn, flow.Step);
        Assert.StartsWith("Signed in as Ana.", flow.SignedInLine, StringComparison.Ordinal);
        Assert.Equal(3, flow.NoticeSummary.Count);
        Assert.Equal(AppCopy.AccountNotice.Count, flow.NoticeAll.Count);
        Assert.False(flow.ShowFullNotice);

        flow.ToggleNoticeCommand.Execute(null);

        Assert.True(flow.ShowFullNotice);
    }

    [Fact]
    public void ReadyStep_ScreenReadingIsOffByDefault_AndTurningItOnNeedsTheTick()
    {
        using var h = new Harness();
        var (flow, _, screen) = Create(h);
        Walk(flow, 3);
        Assert.True(flow.IsLastStep);
        Assert.True(flow.ChoseNotNow);
        Assert.True(flow.CanFinish);

        flow.TurnOnScreenReading = true;

        Assert.False(flow.CanFinish);
        Assert.False(flow.NextCommand.CanExecute(null));
        Assert.Contains("I understand", flow.FinishCaption, StringComparison.Ordinal);

        flow.Understood = true;

        Assert.True(flow.CanFinish);
        Assert.Equal(string.Empty, flow.FinishCaption);
        flow.NextCommand.Execute(null);
        Assert.True(h.Settings.Current.Detection.Enabled);
        Assert.True(h.Settings.Current.Detection.RiskAcknowledged);
        Assert.True(h.Settings.Current.FirstRunCompleted);
        Assert.True(screen.IsOn);
    }

    [Fact]
    public void ReadyStep_NotNow_LeavesScreenReadingOff_EvenIfTheBoxWasTickedBefore()
    {
        using var h = new Harness();
        var (flow, _, _) = Create(h);
        Walk(flow, 3);
        flow.TurnOnScreenReading = true;
        flow.Understood = true;

        flow.ChoseNotNow = true;
        flow.NextCommand.Execute(null);

        Assert.False(h.Settings.Current.Detection.Enabled);
    }

    [Fact]
    public void Open_RestartsFromWelcome_WithScreenReadingBackToNotNow()
    {
        using var h = new Harness(new AppSettings { FirstRunCompleted = true });
        var (flow, _, _) = Create(h);
        flow.Open();
        Walk(flow, 3);
        flow.TurnOnScreenReading = true;

        flow.Open();

        Assert.True(flow.IsOpen);
        Assert.Equal(FirstRunStep.Welcome, flow.Step);
        Assert.True(flow.ChoseNotNow);
        Assert.False(flow.CanGoBack);
    }
}
