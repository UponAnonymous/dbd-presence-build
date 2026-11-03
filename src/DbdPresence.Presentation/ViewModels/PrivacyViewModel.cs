using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Configuration;
using DbdPresence.Core.Settings;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// What to show (FR-VIS-01..04, design §4.1): one switch for every part of the game that can reach Discord, plus the pause and game-closed choices. The
/// Private style (formerly streamer mode) and screen reading moved to the Style cards and to Settings, each in one place.
/// </summary>
public sealed partial class PrivacyViewModel : SettingsPageViewModel
{
    /// <summary>Initializes a new instance of the <see cref="PrivacyViewModel"/> class.</summary>
    public PrivacyViewModel(ISettingsService settings, IUiDispatcher dispatcher, AppInfo app)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(app);
        SettingsFolder = app.SettingsFolder;
        DataFolder = app.DataFolder;
        PrivacyNoticePath = app.PrivacyNoticePath;
        Reload();
    }

    /// <summary>Gets where settings are stored.</summary>
    public string SettingsFolder { get; }

    /// <summary>Gets where logs and local data are stored.</summary>
    public string DataFolder { get; }

    /// <summary>Gets where the full privacy notice file is installed.</summary>
    public string PrivacyNoticePath { get; }

    /// <summary>Gets or sets a value indicating whether where the user is (menu, lobby, match, results) may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowScene { get; set; }

    /// <summary>Gets or sets a value indicating whether the game mode may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowMode { get; set; }

    /// <summary>Gets or sets a value indicating whether the map and realm may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowMap { get; set; }

    /// <summary>Gets or sets a value indicating whether live match details (generators left, survivors on hooks) may be shown. Off by default.</summary>
    [ObservableProperty]
    public partial bool ShowLiveMatchStats { get; set; }

    /// <summary>Gets or sets a value indicating whether the match result may be shown after the match.</summary>
    [ObservableProperty]
    public partial bool ShowResult { get; set; }

    /// <summary>Gets or sets a value indicating whether the map and match details wait until the match ends.</summary>
    [ObservableProperty]
    public partial bool PostMatchDetailsOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether the role may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowRole { get; set; }

    /// <summary>Gets or sets a value indicating whether the character may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowCharacter { get; set; }

    /// <summary>Gets or sets a value indicating whether timers may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowTimers { get; set; }

    /// <summary>Gets or sets a value indicating whether buttons may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowButtons { get; set; }

    /// <summary>Gets or sets a value indicating whether the small image may be shown.</summary>
    [ObservableProperty]
    public partial bool ShowSmallImage { get; set; }

    /// <summary>Gets or sets a value indicating whether the whole activity is hidden.</summary>
    [ObservableProperty]
    public partial bool HideFullActivity { get; set; }

    /// <summary>Gets or sets a value indicating whether pausing shows a generic activity instead of clearing it.</summary>
    [ObservableProperty]
    public partial bool PauseShowsGeneric { get; set; }

    /// <summary>Gets or sets a value indicating whether a template is shown while the game is not running.</summary>
    [ObservableProperty]
    public partial bool ShowWhenNotRunning { get; set; }


    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        var profile = settings.ActiveProfile;
        var v = profile.Visibility;
        ShowScene = v.ShowScene;
        ShowMode = v.ShowMode;
        ShowMap = v.ShowMap;
        ShowLiveMatchStats = v.ShowLiveMatchStats;
        ShowResult = v.ShowResult;
        PostMatchDetailsOnly = v.PostMatchDetailsOnly;
        ShowRole = v.ShowRole;
        ShowCharacter = v.ShowCharacter;
        ShowTimers = v.ShowTimers;
        ShowButtons = v.ShowButtons;
        ShowSmallImage = v.ShowSmallImage;
        HideFullActivity = settings.Privacy.HideFullActivity;
        PauseShowsGeneric = profile.WhenPaused == PauseBehavior.Generic;
        ShowWhenNotRunning = profile.WhenNotRunning == NotRunningBehavior.ShowTemplate;
    }

    partial void OnShowSceneChanged(bool value) => Visibility(v => v with { ShowScene = value });

    partial void OnShowModeChanged(bool value) => Visibility(v => v with { ShowMode = value });

    partial void OnShowMapChanged(bool value) => Visibility(v => v with { ShowMap = value });

    partial void OnShowLiveMatchStatsChanged(bool value) => Visibility(v => v with { ShowLiveMatchStats = value });

    partial void OnShowResultChanged(bool value) => Visibility(v => v with { ShowResult = value });

    partial void OnPostMatchDetailsOnlyChanged(bool value) => Visibility(v => v with { PostMatchDetailsOnly = value });

    partial void OnShowRoleChanged(bool value) => Visibility(v => v with { ShowRole = value });

    partial void OnShowCharacterChanged(bool value) => Visibility(v => v with { ShowCharacter = value });

    partial void OnShowTimersChanged(bool value) => Visibility(v => v with { ShowTimers = value });

    partial void OnShowButtonsChanged(bool value) => Visibility(v => v with { ShowButtons = value });

    partial void OnShowSmallImageChanged(bool value) => Visibility(v => v with { ShowSmallImage = value });

    partial void OnHideFullActivityChanged(bool value) => Apply(s => s with { Privacy = s.Privacy with { HideFullActivity = value } });

    partial void OnPauseShowsGenericChanged(bool value) =>
        Apply(s => WithActiveProfile(s, p => p with { WhenPaused = value ? PauseBehavior.Generic : PauseBehavior.Clear }));

    partial void OnShowWhenNotRunningChanged(bool value) =>
        Apply(s => WithActiveProfile(s, p => p with { WhenNotRunning = value ? NotRunningBehavior.ShowTemplate : NotRunningBehavior.Clear }));

    private void Visibility(Func<VisibilitySettings, VisibilitySettings> change) =>
        Apply(s => WithActiveProfile(s, p => p with { Visibility = change(p.Visibility) }));
}
