// Covers: FR-ONL-08 (remote configuration in the app shell: an announcement banner in plain text, dismissible per announcement; a calm
// "please update" note when the app is below the minimum version, never blocking presence, D8), FR-ONL-05, FR-APP-* (shell notices).
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class ShellBannerTests : IDisposable
{
    private static readonly AppInfo s_app = new("0.3.0+abc1234", "abc1234", IsSigned: false, @"C:\settings", @"C:\data");

    private readonly Harness _host = new();
    private readonly FakeConfig _config = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }

        _host.Dispose();
    }

    private ShellViewModel Shell(string version = "0.3.0+abc1234", bool withConfig = true)
    {
        var kit = new AppKit(_host, s_app, notice: null, config: withConfig ? _config : null, version: version);
        _owned.Add(kit);
        return kit.Shell;
    }

    [Fact]
    public void NoConfigAtAll_ShowsNoBannerAndNoUpdateNote()
    {
        var shell = Shell();

        Assert.Null(shell.Announcement);
        Assert.Null(shell.UpdateNotice);
    }

    [Fact]
    public void WithoutAConfigSource_TheShellStillWorks()
    {
        var shell = Shell(withConfig: false);

        Assert.Null(shell.Announcement);
        Assert.Null(shell.UpdateNotice);
        shell.DismissAnnouncementCommand.Execute(null);
        shell.DismissUpdateNoticeCommand.Execute(null);
    }

    [Fact]
    public void TheAnnouncement_IsShownAsPlainText()
    {
        _config.Set(banner: "Maintenance on Friday <b>evening</b>.");
        var shell = Shell();

        Assert.Equal("Maintenance on Friday <b>evening</b>.", shell.Announcement); // text, never markup: the view binds it to a TextBlock
    }

    [Fact]
    public void DismissingTheAnnouncement_HidesIt_AndKeepsItHiddenAfterARestart()
    {
        _config.Set(banner: "Maintenance on Friday.");
        var shell = Shell();

        shell.DismissAnnouncementCommand.Execute(null);

        Assert.Null(shell.Announcement);
        Assert.Equal(_config.Current.AnnouncementId, _host.Settings.Current.Lifecycle.DismissedAnnouncement);
        var restarted = Shell();
        Assert.Null(restarted.Announcement);
    }

    [Fact]
    public void ADifferentAnnouncement_ShowsAgain_AfterTheOldOneWasDismissed()
    {
        _config.Set(banner: "Maintenance on Friday.");
        var shell = Shell();
        shell.DismissAnnouncementCommand.Execute(null);

        _config.Set(banner: "Maintenance moved to Saturday.");

        Assert.Equal("Maintenance moved to Saturday.", shell.Announcement);

        _config.Set(banner: "Maintenance on Friday."); // the dismissed text again: still dismissed
        Assert.Null(shell.Announcement);
    }

    [Fact]
    public void TheBannerFollowsTheConfig_WhenItChangesOrExpires()
    {
        var shell = Shell();
        Assert.Null(shell.Announcement);

        _config.Set(banner: "Hello");
        Assert.Equal("Hello", shell.Announcement);

        _config.Set(banner: null);
        Assert.Null(shell.Announcement);
    }

    [Theory]
    [InlineData("0.3.0+abc1234", "0.3.1", true)]
    [InlineData("0.3.0", "0.10.0", true)]
    [InlineData("0.3.0+abc1234", "0.3.0", false)]
    [InlineData("1.0.0", "0.9.9", false)]
    [InlineData("0.3.0-beta.1+abc", "0.3.1", true)]
    [InlineData("unknown", "9.9.9", false)]
    [InlineData("", "9.9.9", false)]
    public void UpdateNote_ShowsOnlyWhenTheRunningVersionIsBelowTheMinimum(string running, string minimum, bool expected)
    {
        _config.Set(minimumVersion: minimum);
        var shell = Shell(running);

        Assert.Equal(expected ? ShellViewModel.UpdateNoticeText : null, shell.UpdateNotice);
    }

    [Fact]
    public void TheUpdateNote_IsCalm_DismissibleAndNeverStopsPresence()
    {
        _config.Set(minimumVersion: "9.0.0");
        var shell = Shell();

        Assert.Contains("Please update when you can", shell.UpdateNotice, StringComparison.Ordinal);
        Assert.Contains("keeps working", shell.UpdateNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("must", shell.UpdateNotice, StringComparison.OrdinalIgnoreCase);

        shell.DismissUpdateNoticeCommand.Execute(null);

        Assert.Null(shell.UpdateNotice);
        _config.Set(minimumVersion: "9.0.0", banner: "x");
        Assert.Null(shell.UpdateNotice); // dismissed for this run
    }

    [Fact]
    public void TheShell_StopsListening_WhenDisposed()
    {
        var shell = Shell();
        shell.Dispose();

        _config.Set(banner: "Late");

        Assert.Null(shell.Announcement);
    }

    private sealed class FakeConfig : IRemoteConfigSource
    {
        public event EventHandler? Changed;

        public RemoteConfigSnapshot Current { get; private set; } = RemoteConfigSnapshot.SafeDefaults;

        public void Set(string? banner = null, string? minimumVersion = null)
        {
            Current = new RemoteConfigSnapshot(false, false, true, [], null, true, banner, minimumVersion);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
