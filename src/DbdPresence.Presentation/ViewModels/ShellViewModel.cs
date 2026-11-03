using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Labelling;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>A navigation destination.</summary>
/// <param name="Key">Stable id (also the automation id suffix).</param>
/// <param name="Title">Label.</param>
/// <param name="Glyph">Segoe Fluent Icons glyph.</param>
/// <param name="Shortcut">Ctrl+number shortcut, 1–9.</param>
/// <param name="Page">The page view-model.</param>
public sealed record NavItem(string Key, string Title, string Glyph, int Shortcut, object Page)
{
    /// <summary>Gets the tooltip: the page name and its shortcut (the rail shows icons only when narrow).</summary>
    public string ToolTipText => $"{Title} (Ctrl+{Shortcut})";

    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>
/// The main window (design §2): five pages (and, only for the owner's own account, a sixth, Study), each answering one question (Home, Discord status, Characters, Account, Settings), and the shared command
/// bar with the connection pill and Pause. The old keys "app" and "privacy" (and the other retired page keys) still work as aliases, so commands
/// written for the eight-page shell keep navigating.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    /// <summary>The calm note shown when the running version is below the owner's minimum (D8: presence is never blocked).</summary>
    public const string UpdateNoticeText = "A newer version of DBD Presence is available. Please update when you can. Your Discord status keeps working in the meantime.";

    /// <summary>Retired page keys and the page that took over their content (design §11).</summary>
    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal)
    {
        ["app"] = "settings",
        ["appearance"] = "settings",
        ["about"] = "settings",
        ["privacy"] = "status",
        ["presence"] = "status",
        ["packs"] = "characters",
    };

    private const string StudyKey = "study";
    private readonly IRemoteConfigSource? _config;
    private readonly HomeViewModel _home;
    private readonly Characters.CharactersViewModel _characters;
    private readonly SettingsViewModel _settingsPage;
    private readonly ISettingsService? _settings;
    private readonly IUiDispatcher? _dispatcher;
    private readonly string _appVersion;
    private readonly StudyViewModel? _study;
    private readonly ObservableCollection<NavItem> _pages;
    private bool _updateNoticeDismissed;

    /// <summary>Initializes a new instance of the <see cref="ShellViewModel"/> class.</summary>
    public ShellViewModel(
        PresenceControlsViewModel controls,
        HomeViewModel home,
        DiscordStatusViewModel status,
        Characters.CharactersViewModel characters,
        AccountViewModel account,
        SettingsViewModel settingsPage,
        string? notice = null,
        IRemoteConfigSource? config = null,
        ISettingsService? settings = null,
        IUiDispatcher? dispatcher = null,
        string appVersion = "",
        StudyViewModel? study = null)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(settingsPage);
        Controls = controls;
        _home = home;
        _settingsPage = settingsPage;
        _pages =
        [
            new("home", "Home", "\uE80F", 1, home),
            new("status", "Discord status", "\uE8BD", 2, status),
            new("characters", "Characters", "\uE716", 3, characters),
            new("account", "Account", "\uE77B", 4, account),
            new("settings", "Settings", "\uE713", 5, settingsPage),
        ];
        _study = study;
        if (study is not null)
        {
            study.AvailabilityChanged += OnStudyAvailabilityChanged;
            ApplyStudyAvailability();
        }

        SelectedPage = Pages[0];
        home.LinkRequested += OnHomeLink;
        _characters = characters;
        characters.NavigateRequested += OnCharactersNavigate;
        Notice = notice;
        _config = config;
        _settings = settings;
        _dispatcher = dispatcher;
        _appVersion = appVersion;
        if (config is not null)
        {
            config.Changed += OnRemoteChanged;
            if (settings is not null)
            {
                settings.Changed += OnSettingsChanged;
            }

            RefreshAnnouncements();
        }
    }

    /// <summary>Gets the owner's announcement (plain text), or null when there is none or the user dismissed this one.</summary>
    [ObservableProperty]
    public partial string? Announcement { get; private set; }

    /// <summary>Gets the "please update" note, or null when the version is current, unknown or the note was dismissed.</summary>
    [ObservableProperty]
    public partial string? UpdateNotice { get; private set; }

    /// <summary>Hides the announcement. A different announcement text shows again; the choice is kept in the settings.</summary>
    [RelayCommand]
    private void DismissAnnouncement()
    {
        if (_config?.Current.AnnouncementId is { } id && _settings is not null)
        {
            _ = _settings.Update(s => s with { Lifecycle = s.Lifecycle with { DismissedAnnouncement = id } });
        }

        Announcement = null;
    }

    /// <summary>Hides the update note for this run.</summary>
    [RelayCommand]
    private void DismissUpdateNotice()
    {
        _updateNoticeDismissed = true;
        UpdateNotice = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_study is not null)
        {
            _study.AvailabilityChanged -= OnStudyAvailabilityChanged;
        }

        _home.LinkRequested -= OnHomeLink;
        _characters.NavigateRequested -= OnCharactersNavigate;
        if (_config is not null)
        {
            _config.Changed -= OnRemoteChanged;
        }

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnStudyAvailabilityChanged(object? sender, EventArgs e) => OnUiThread(ApplyStudyAvailability);

    /// <summary>Adds the Study page when the account is the owner's own and removes it otherwise. The item does not exist for anyone else, and a selected page that goes away is replaced by Home.</summary>
    private void ApplyStudyAvailability()
    {
        if (_study is null)
        {
            return;
        }

        var present = _pages.FirstOrDefault(p => p.Key == StudyKey);
        if (_study.IsAvailable && present is null)
        {
            _pages.Add(new NavItem(StudyKey, "Study", "\uE7C3", 6, _study));
        }
        else if (!_study.IsAvailable && present is not null)
        {
            var wasSelected = ReferenceEquals(SelectedPage, present);
            _pages.Remove(present);
            if (wasSelected)
            {
                SelectedPage = _pages[0];
            }
        }
    }

    private void OnRemoteChanged(object? sender, EventArgs e) => OnUiThread(RefreshAnnouncements);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => OnUiThread(RefreshAnnouncements);

    private void OnUiThread(Action action)
    {
        if (_dispatcher is null)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private void RefreshAnnouncements()
    {
        if (_config is null)
        {
            return;
        }

        var current = _config.Current;
        var dismissed = _settings?.Current.Lifecycle.DismissedAnnouncement ?? string.Empty;
        Announcement = current.Banner is not null && !string.Equals(current.AnnouncementId, dismissed, StringComparison.Ordinal) ? current.Banner : null;
        UpdateNotice = !_updateNoticeDismissed && current.IsUpdateNeeded(_appVersion) ? UpdateNoticeText : null;
    }

    /// <summary>Gets the shared command-bar controls.</summary>
    public PresenceControlsViewModel Controls { get; }

    /// <summary>Gets the pages.</summary>
    public IReadOnlyList<NavItem> Pages => _pages;

    /// <summary>Gets or sets the selected page.</summary>
    [ObservableProperty]
    public partial NavItem SelectedPage { get; set; }

    /// <summary>Gets a one-time notice about startup (for example, settings restored from a backup), if any.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Hides the startup notice.</summary>
    [RelayCommand]
    private void DismissNotice() => Notice = null;

    /// <summary>Selects the page with the given shortcut number; returns false when there is none.</summary>
    public bool SelectByShortcut(int number)
    {
        var page = Pages.FirstOrDefault(p => p.Shortcut == number);
        if (page is null)
        {
            return false;
        }

        SelectedPage = page;
        return true;
    }

    /// <summary>Navigates to a page by key (for links between pages).</summary>
    [RelayCommand]
    private void Navigate(string key) => SelectByKey(key);

    private void OnCharactersNavigate(object? sender, string key) => SelectByKey(key);

    private void OnHomeLink(object? sender, HomeLink link)
    {
        switch (link)
        {
            case HomeLink.Status:
                SelectByKey("status");
                break;
            case HomeLink.Account:
                SelectByKey("account");
                break;
            case HomeLink.SettingsScreenReading:
                SelectByKey("settings");
                _settingsPage.Show(SettingsSection.ScreenReading);
                break;
            case HomeLink.TurnOnScreenReading:
                SelectByKey("settings");
                _settingsPage.Show(SettingsSection.ScreenReading);
                _settingsPage.ScreenReading.BeginTurnOn();
                break;
            default:
                SelectByKey("settings");
                _settingsPage.Show(SettingsSection.Advanced);
                break;
        }
    }

    /// <summary>Selects a page by key or by a retired alias ("app", "privacy"); returns false when there is none.</summary>
    public bool SelectByKey(string key)
    {
        var resolved = key is not null && s_aliases.TryGetValue(key, out var target) ? target : key;
        var page = Pages.FirstOrDefault(p => string.Equals(p.Key, resolved, StringComparison.Ordinal));
        if (page is null)
        {
            return false;
        }

        SelectedPage = page;
        return true;
    }
}
