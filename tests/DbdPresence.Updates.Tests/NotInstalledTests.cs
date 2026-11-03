// Covers: FR-ONL-10 and UI_SPEC §5 (a copy that was not installed, such as the UI-test build or a development build, shows a plain "not
// installed" state on the Settings page and never throws or contacts anything), Windows CI run 86 (the Settings page crashed the app; the
// cause was a two-way ProgressBar binding to UpdatesViewModel.Progress, which XamlBindingRulesTests now guards).
using DbdPresence.App.Updates;
using DbdPresence.Core.Online;
using DbdPresence.Packs.Releases;
using DbdPresence.Presentation.ViewModels;
using Velopack.Locators;

namespace DbdPresence.Updates.Tests;

public sealed class NotInstalledTests : IDisposable
{
    private readonly UpdateFixture _f = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task NotInstalledCopy_ShowsThePlainState_AndNothingThrowsOrConnects()
    {
        // No installed version: Velopack's IsInstalled is false, as in a copy that was not installed with the setup file.
        var locator = new NotInstalledLocator(_f.PackagesDir);
        _f.Transport.Feed = _f.Feed([_f.Publish("1.1.0")], version: 10);
        var applied = 0;
        var shutdowns = 0;
        using var updater = new AppUpdater(_f.Service(), ReleaseVersion.Parse("1.0.0"), locator, _f.Time, _f.Log, () => shutdowns++, _ => applied++);
        using var viewModel = new UpdatesViewModel(updater, new ImmediateDispatcher());

        updater.Start();
        _f.Time.Advance(TimeSpan.FromDays(1));
        await updater.CheckNowAsync(Ct);
        updater.RestartToUpdate();
        viewModel.CheckCommand.Execute(null);

        Assert.Equal(AppUpdateState.NotInstalled, updater.Current.State);
        Assert.Equal(UpdatesViewModel.NotInstalledText, viewModel.StatusText);
        Assert.False(viewModel.CanCheck);
        Assert.False(viewModel.CheckCommand.CanExecute(null));
        Assert.False(viewModel.IsReadyToRestart);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(0, viewModel.Progress);
        Assert.Equal(0, _f.Transport.FeedRequests);
        Assert.Equal((0, 0), (applied, shutdowns));
    }

    [Fact]
    public void UnreadableVersion_ShowsThePlainState()
    {
        using var updater = new AppUpdater(_f.Service(), null, new TestVelopackLocator("DbdPresence", "1.0.0", _f.PackagesDir, null!), _f.Time, _f.Log, () => { });
        using var viewModel = new UpdatesViewModel(updater, new ImmediateDispatcher());

        Assert.Equal(UpdatesViewModel.NotInstalledText, viewModel.StatusText);
        Assert.False(viewModel.CanCheck);
    }

    /// <summary>Velopack's view of a copy that was not installed: no installed version.</summary>
    private sealed class NotInstalledLocator(string packagesDir) : TestVelopackLocator("DbdPresence", "1.0.0", packagesDir, null!)
    {
        public override Velopack.SemanticVersion CurrentlyInstalledVersion => null!;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
