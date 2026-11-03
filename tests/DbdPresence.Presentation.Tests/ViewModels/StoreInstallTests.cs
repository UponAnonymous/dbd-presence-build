// Covers: FR-LIFE-01 (start with Windows through the Microsoft Store package's startup task; a task the user turned off cannot be turned on from
// code and the page says where to switch it), FR-ONL-10 (the Store package never updates itself: "Updates come from the Microsoft Store"), and the
// Uninstall button on the About section (opens Windows' Installed apps for every install kind; RELEASE_CHECKLIST §6).
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class StoreInstallTests
{
    private static readonly AppInfo s_app = new("1.0.0", "abc1234", IsSigned: true, "settings", "data");

    private sealed class FakeLauncher(bool result) : IInstalledAppsLauncher
    {
        public int Calls { get; private set; }

        public Task<bool> OpenInstalledAppsAsync()
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    [Theory]
    [InlineData(StartupTaskStatus.Enabled, true)]
    [InlineData(StartupTaskStatus.EnabledByPolicy, true)]
    [InlineData(StartupTaskStatus.Disabled, false)]
    [InlineData(StartupTaskStatus.DisabledByUser, false)]
    [InlineData(StartupTaskStatus.DisabledByPolicy, false)]
    [InlineData(StartupTaskStatus.Unavailable, false)]
    public void StartupTask_IsEnabledOnlyWhenWindowsHasItOn(StartupTaskStatus status, bool enabled) =>
        Assert.Equal(enabled, StartupTaskRules.IsEnabled(status));

    [Theory]
    [InlineData(StartupTaskStatus.Disabled, true)]
    [InlineData(StartupTaskStatus.Enabled, true)]
    [InlineData(StartupTaskStatus.DisabledByUser, false)]
    [InlineData(StartupTaskStatus.DisabledByPolicy, false)]
    [InlineData(StartupTaskStatus.EnabledByPolicy, false)]
    [InlineData(StartupTaskStatus.Unavailable, false)]
    public void StartupTask_OnlyPlainStatesMayBeChangedFromCode(StartupTaskStatus status, bool canChange) =>
        Assert.Equal(canChange, StartupTaskRules.CanRequestChange(status));

    [Fact]
    public void StartupTask_ErrorTextTellsTheUserWhereToTurnItOn()
    {
        Assert.Null(StartupTaskRules.ErrorFor(StartupTaskStatus.Enabled, wanted: true));
        Assert.Null(StartupTaskRules.ErrorFor(StartupTaskStatus.Disabled, wanted: false));
        Assert.Equal(StartupTaskRules.DisabledByUserText, StartupTaskRules.ErrorFor(StartupTaskStatus.DisabledByUser, wanted: true));
        Assert.Contains("Windows Settings", StartupTaskRules.DisabledByUserText, StringComparison.Ordinal);
        Assert.Contains("Startup", StartupTaskRules.DisabledByUserText, StringComparison.Ordinal);
        Assert.Equal(StartupTaskRules.PolicyText, StartupTaskRules.ErrorFor(StartupTaskStatus.DisabledByPolicy, wanted: true));
        Assert.Equal(StartupTaskRules.PolicyText, StartupTaskRules.ErrorFor(StartupTaskStatus.EnabledByPolicy, wanted: false));
        Assert.Equal(StartupTaskRules.UnavailableText, StartupTaskRules.ErrorFor(StartupTaskStatus.Unavailable, wanted: true));
    }

    [Fact]
    public void StoreManagedUpdater_SaysUpdatesComeFromTheStore_AndOffersNoCheck()
    {
        var updater = new StoreManagedUpdates();
        using var viewModel = new UpdatesViewModel(updater, new ImmediateDispatcher());

        viewModel.CheckCommand.Execute(null);
        viewModel.RestartCommand.Execute(null);

        Assert.Equal(AppUpdateState.StoreManaged, updater.Current.State);
        Assert.Equal("Updates come from the Microsoft Store.", viewModel.StatusText);
        Assert.False(viewModel.CanCheck);
        Assert.False(viewModel.IsReadyToRestart);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task StoreManagedUpdater_CheckDoesNothing()
    {
        var updater = new StoreManagedUpdates();

        await updater.CheckNowAsync(TestContext.Current.CancellationToken);
        updater.RestartToUpdate();

        Assert.Equal(AppUpdateState.StoreManaged, updater.Current.State);
    }

    [Fact]
    public void AutostartCaption_IsThePortableWarningUnlessTheInstallKindSaysOtherwise()
    {
        using var h = new Harness();
        var plain = new FakeAutostart();
        using var portable = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, plain);

        Assert.StartsWith("Portable copy", portable.StartWithWindowsCaption, StringComparison.Ordinal);
        using var store = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, new StoreAutostart());
        Assert.Equal(StartupTaskRules.Caption, store.StartWithWindowsCaption);
    }

    [Fact]
    public void StartWithWindows_ShowsTheStartupTaskMessage_AndKeepsTheRealState()
    {
        using var h = new Harness();
        var autostart = new StoreAutostart { Reply = StartupTaskRules.DisabledByUserText };
        using var page = new AppStartupViewModel(h.Controls(), h.Settings, h.Dispatcher, autostart);

        page.StartWithWindows = true;

        Assert.Equal(StartupTaskRules.DisabledByUserText, page.StartWithWindowsError);
        Assert.False(page.StartWithWindows);
        Assert.False(h.Settings.Current.Lifecycle.StartWithWindows);
    }

    [Fact]
    public async Task Uninstall_OpensInstalledApps_AndExplainsTheOrder()
    {
        var launcher = new FakeLauncher(result: true);
        var about = new AboutViewModel(s_app, launcher);

        await about.UninstallCommand.ExecuteAsync(null);

        Assert.True(about.CanUninstall);
        Assert.Equal(1, launcher.Calls);
        Assert.Null(about.UninstallError);
        Assert.Contains("delete your online account on the Account page", AboutViewModel.UninstallText, StringComparison.Ordinal);
        Assert.Contains("Installed apps", AboutViewModel.UninstallText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_WhenWindowsDoesNotOpenThePage_SaysWhereToGo()
    {
        var launcher = new FakeLauncher(result: false);
        var about = new AboutViewModel(s_app, launcher);

        await about.UninstallCommand.ExecuteAsync(null);

        Assert.Equal(AboutViewModel.UninstallFailedText, about.UninstallError);

        launcher = new FakeLauncher(result: true);
        about = new AboutViewModel(s_app, launcher);
        await about.UninstallCommand.ExecuteAsync(null);
        Assert.Null(about.UninstallError);
    }

    [Fact]
    public async Task Uninstall_WithoutALauncher_IsHiddenAndDoesNothing()
    {
        var about = new AboutViewModel(s_app);

        await about.UninstallCommand.ExecuteAsync(null);

        Assert.False(about.CanUninstall);
        Assert.Null(about.UninstallError);
    }

    private sealed class StoreAutostart : IAutostart
    {
        public bool IsEnabled { get; private set; }

        public string? Reply { get; set; }

        public string? Caption => StartupTaskRules.Caption;

        public string? SetEnabled(bool enabled)
        {
            if (Reply is not null)
            {
                return Reply;
            }

            IsEnabled = enabled;
            return null;
        }
    }
}
