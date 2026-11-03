using DbdPresence.Core.Online;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>Every page model of the five-page app wired to fakes, the way the app root wires them.</summary>
internal sealed class AppKit : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public AppKit(Harness h, AppInfo app, string? notice = null, IRemoteConfigSource? config = null, string version = "", bool signedIn = true, DbdPresence.Presentation.Labelling.StudyViewModel? study = null)
    {
        Host = h;
        Controls = Own(h.Controls());
        var accountService = new FakeAccountService();
        if (signedIn)
        {
            accountService.Set(OnlineState.Active, "Ana");
        }

        AccountService = accountService;
        Account = Own(new AccountViewModel(accountService, h.Dispatcher));
        ScreenReading = Own(new ScreenReadingViewModel(h.Settings, h.Dispatcher, new FakeDetectionStatus()));
        Home = Own(new HomeViewModel(Controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time, detection: null, ScreenReading, Account));
        AppStartup = Own(new AppStartupViewModel(Controls, h.Settings, h.Dispatcher, new FakeAutostart()));
        Privacy = Own(new PrivacyViewModel(h.Settings, h.Dispatcher, app));
        Studio = Own(new PresenceStudioViewModel(h.Settings, h.Catalog, h.Dispatcher, h.Time, null, h.Coordinator));
        Status = Own(new DiscordStatusViewModel(Controls, h.Coordinator, h.Settings, h.Dispatcher, h.Time, Privacy, AppStartup, Studio, ScreenReading));
        Appearance = Own(new AppearanceViewModel(h.Settings, h.Dispatcher, new FakeThemeService()));
        SettingsPage = new SettingsViewModel(Controls, ScreenReading, Appearance, AppStartup, new AboutViewModel(app), Privacy);
        var storage = new InMemoryLocalStorage();
        Characters = Own(new Characters.CharactersViewModel(
            h.Settings,
            h.Dispatcher,
            PackFixtures.Roster,
            new DbdPresence.Packs.Local.PackLibrary(storage, CharactersHarness.Root, new FakeImageLoader(storage), PackFixtures.Roster),
            new DbdPresence.Packs.Pictures.PictureStore(storage, CharactersHarness.Root, new FakeFitter(), h.Time),
            new FakeFilePicker()));
        Shell = Own(new ShellViewModel(Controls, Home, Status, Characters, Account, SettingsPage, notice, config, h.Settings, h.Dispatcher, version, study));
    }

    public Harness Host { get; }

    public PresenceControlsViewModel Controls { get; }

    public FakeAccountService AccountService { get; }

    public AccountViewModel Account { get; }

    public ScreenReadingViewModel ScreenReading { get; }

    public HomeViewModel Home { get; }

    public AppStartupViewModel AppStartup { get; }

    public PrivacyViewModel Privacy { get; }

    public PresenceStudioViewModel Studio { get; }

    public DiscordStatusViewModel Status { get; }

    public AppearanceViewModel Appearance { get; }

    public SettingsViewModel SettingsPage { get; }

    public Characters.CharactersViewModel Characters { get; }

    public ShellViewModel Shell { get; }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }
    }

    private T Own<T>(T item)
        where T : IDisposable
    {
        _owned.Add(item);
        return item;
    }
}

/// <summary>A screen reading status source that never changes: reading, so the Settings status line says it is on.</summary>
internal sealed class FakeDetectionStatus : DbdPresence.Presentation.Detection.IDetectionStatusSource
{
    public event EventHandler? StatusChanged;

    public DbdPresence.Presentation.Detection.DetectionStatus Status { get; private set; } = DbdPresence.Presentation.Detection.DetectionStatus.Off;

    public void Set(DbdPresence.Presentation.Detection.DetectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
