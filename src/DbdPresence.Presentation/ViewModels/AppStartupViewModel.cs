using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Configuration;
using DbdPresence.Core.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>The quick picks for the "Playing ___" text.</summary>
public enum DisplayNameKind
{
    /// <summary>"Dead by Daylight".</summary>
    DeadByDaylight,

    /// <summary>"DBD".</summary>
    Dbd,

    /// <summary>Text the user types.</summary>
    Custom,
}

/// <summary>
/// App &amp; Startup (FR-RPC-07, FR-RPC-11, FR-RPC-13, FR-LIFE-*): the name shown after "Playing", the member-list choice,
/// the connection test, the Advanced own-application option, and how the app starts, closes and notifies.
/// </summary>
public sealed partial class AppStartupViewModel : SettingsPageViewModel
{
    /// <summary>The quick-pick text for <see cref="DisplayNameKind.DeadByDaylight"/>.</summary>
    public const string DeadByDaylightName = "Dead by Daylight";

    /// <summary>The quick-pick text for <see cref="DisplayNameKind.Dbd"/>.</summary>
    public const string DbdName = "DBD";

    private const string PortableCaption = "Portable copy: moving or deleting this folder stops it starting with Windows; the app removes a broken entry on its next start.";
    private const int MaxApplicationName = 32;
    private readonly IAutostart _autostart;
    private bool _loadingAutostart;

    /// <summary>Initializes a new instance of the <see cref="AppStartupViewModel"/> class.</summary>
    public AppStartupViewModel(PresenceControlsViewModel controls, ISettingsService settings, IUiDispatcher dispatcher, IAutostart autostart)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(autostart);
        Controls = controls;
        _autostart = autostart;
        Reload();
    }

    /// <summary>Gets the shared controls (connection status and the connection test).</summary>
    public PresenceControlsViewModel Controls { get; }

    /// <summary>Gets the notification choices.</summary>
    public IReadOnlyList<Choice<NotificationLevel>> NotificationLevels { get; } =
        [new(NotificationLevel.Off, "Off"), new(NotificationLevel.CriticalOnly, "Only problems that need you"), new(NotificationLevel.All, "Connection changes too")];

    /// <summary>Gets the quick picks for the name shown after "Playing".</summary>
    public IReadOnlyList<Choice<DisplayNameKind>> DisplayNameChoices { get; } =
        [new(DisplayNameKind.DeadByDaylight, DeadByDaylightName), new(DisplayNameKind.Dbd, DbdName), new(DisplayNameKind.Custom, "Custom…")];

    /// <summary>Gets the member-list choices (FR-RPC-11).</summary>
    public IReadOnlyList<Choice<MemberListDisplay>> MemberListDisplays { get; } = MemberListChoices.All;

    /// <summary>Gets the steps to create a Discord application (plain text; the app opens no links itself). Shown only inside Advanced.</summary>
    public IReadOnlyList<string> SetupSteps { get; } =
    [
        "Open discord.com/developers/applications in your browser and sign in.",
        "Choose “New Application” and give it a name of your own. Do not use the game's name, logos or Behaviour artwork.",
        "On the General Information page, copy the Application ID.",
        "Paste it here. It is a public identifier, not a password or token.",
        "Optional: upload images under Rich Presence → Art Assets named app_logo, role_survivor and role_killer; the templates refer to those names.",
    ];

    /// <summary>Gets or sets which quick pick is selected for the name shown after "Playing".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomDisplayName))]
    public partial DisplayNameKind SelectedDisplayName { get; set; }

    /// <summary>Gets a value indicating whether the custom text box applies.</summary>
    public bool IsCustomDisplayName => SelectedDisplayName == DisplayNameKind.Custom;

    /// <summary>Gets or sets the custom text shown after "Playing" (1–128 characters).</summary>
    [ObservableProperty]
    public partial string CustomDisplayName { get; set; } = string.Empty;

    /// <summary>Gets the problem with the custom text, if any (it is saved only when valid).</summary>
    [ObservableProperty]
    public partial string? DisplayNameError { get; private set; }

    /// <summary>Gets or sets what Discord's member list shows next to your name.</summary>
    [ObservableProperty]
    public partial MemberListDisplay MemberListDisplay { get; set; }

    /// <summary>Gets or sets a value indicating whether the user's own Discord application is used (Advanced).</summary>
    [ObservableProperty]
    public partial bool UseOwnApplication { get; set; }

    /// <summary>Gets or sets the own Application ID being edited.</summary>
    [ObservableProperty]
    public partial string ApplicationId { get; set; } = string.Empty;

    /// <summary>Gets the problem with the typed Application ID (it is saved only when valid or empty).</summary>
    [ObservableProperty]
    public partial string? ApplicationIdError { get; private set; }

    /// <summary>Gets or sets the own application's name used in the preview heading.</summary>
    [ObservableProperty]
    public partial string ApplicationName { get; set; } = string.Empty;

    /// <summary>Gets the problem with the application name, if any.</summary>
    [ObservableProperty]
    public partial string? ApplicationNameError { get; private set; }

    /// <summary>Gets the sentence under the "Start with Windows" switch (it depends on how the app was installed).</summary>
    public string StartWithWindowsCaption => _autostart.Caption ?? PortableCaption;

    /// <summary>Gets or sets a value indicating whether the app starts with Windows.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>Gets the problem with the startup registration, if it failed.</summary>
    [ObservableProperty]
    public partial string? StartWithWindowsError { get; private set; }

    /// <summary>Gets or sets a value indicating whether the app starts in the tray.</summary>
    [ObservableProperty]
    public partial bool StartMinimised { get; set; }

    /// <summary>Gets or sets a value indicating whether closing the window keeps the app running in the tray.</summary>
    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    /// <summary>Gets or sets the notification level.</summary>
    [ObservableProperty]
    public partial NotificationLevel Notifications { get; set; }

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        var discord = settings.Discord;
        LoadDisplayName(discord.DisplayName);
        MemberListDisplay = MemberListChoices.FromStored(settings.ActiveProfile.StatusDisplay);
        UseOwnApplication = discord.UseOwnApplication;
        if (ApplicationIdError is null && !string.Equals(ApplicationId.Trim(), discord.OwnApplicationId, StringComparison.Ordinal))
        {
            ApplicationId = discord.OwnApplicationId;
        }

        if (ApplicationNameError is null && !string.Equals(ApplicationName.Trim(), discord.ApplicationName, StringComparison.Ordinal))
        {
            ApplicationName = discord.ApplicationName;
        }

        var lifecycle = settings.Lifecycle;
        StartMinimised = lifecycle.StartMinimised;
        CloseToTray = lifecycle.CloseToTray;
        Notifications = lifecycle.Notifications;

        _loadingAutostart = true;
        try
        {
            StartWithWindows = lifecycle.StartWithWindows;
        }
        finally
        {
            _loadingAutostart = false;
        }
    }

    partial void OnSelectedDisplayNameChanged(DisplayNameKind value)
    {
        if (IsLoading)
        {
            return;
        }

        switch (value)
        {
            case DisplayNameKind.DeadByDaylight:
                DisplayNameError = null;
                Apply(s => s with { Discord = s.Discord with { DisplayName = DeadByDaylightName } });
                break;
            case DisplayNameKind.Dbd:
                DisplayNameError = null;
                Apply(s => s with { Discord = s.Discord with { DisplayName = DbdName } });
                break;
            default:
                // Switching to Custom starts from what is shown now, so nothing changes until the text is edited.
                if (string.IsNullOrWhiteSpace(CustomDisplayName))
                {
                    CustomDisplayName = Settings.Current.Discord.DisplayName ?? DeadByDaylightName;
                }
                else
                {
                    CommitCustomDisplayName(CustomDisplayName);
                }

                break;
        }
    }

    partial void OnCustomDisplayNameChanged(string value)
    {
        if (!IsLoading && IsCustomDisplayName)
        {
            CommitCustomDisplayName(value);
        }
    }

    partial void OnMemberListDisplayChanged(MemberListDisplay value) =>
        Apply(s => WithActiveProfile(s, p => p with { StatusDisplay = MemberListChoices.ToStored(value) }));

    partial void OnUseOwnApplicationChanged(bool value) => Apply(s => s with { Discord = s.Discord with { UseOwnApplication = value } });

    partial void OnApplicationIdChanged(string value)
    {
        if (IsLoading)
        {
            return;
        }

        var id = value.Trim();
        ApplicationIdError = SettingsValidator.IsValidApplicationId(id)
            ? null
            : "An Application ID is 17–20 digits. Copy it from the General Information page of your Discord application.";
        if (ApplicationIdError is null)
        {
            Apply(s => s with { Discord = s.Discord with { OwnApplicationId = id } });
        }
    }

    partial void OnApplicationNameChanged(string value)
    {
        if (IsLoading)
        {
            return;
        }

        var name = value.Trim();
        ApplicationNameError = name.Length is 0 or > MaxApplicationName
            ? $"Use 1–{MaxApplicationName} characters, matching your Discord application's name."
            : null;
        if (ApplicationNameError is null)
        {
            Apply(s => s with { Discord = s.Discord with { ApplicationName = name } });
        }
    }

    private void LoadDisplayName(string? stored)
    {
        // Keep the custom box open while the user is typing, even when the text equals a quick pick.
        if (SelectedDisplayName == DisplayNameKind.Custom && (DisplayNameError is not null || string.Equals(CustomDisplayName.Trim(), stored, StringComparison.Ordinal)))
        {
            return;
        }

        // Not set means Discord shows the application's own name, which is "Dead by Daylight" for the bundled application.
        if (stored is null || string.Equals(stored, DeadByDaylightName, StringComparison.Ordinal))
        {
            SelectedDisplayName = DisplayNameKind.DeadByDaylight;
        }
        else if (string.Equals(stored, DbdName, StringComparison.Ordinal))
        {
            SelectedDisplayName = DisplayNameKind.Dbd;
        }
        else
        {
            SelectedDisplayName = DisplayNameKind.Custom;
            if (DisplayNameError is null && !string.Equals(CustomDisplayName.Trim(), stored, StringComparison.Ordinal))
            {
                CustomDisplayName = stored;
            }
        }
    }

    private void CommitCustomDisplayName(string typed)
    {
        var issues = new List<SettingsIssue>();
        var name = SettingsValidator.ValidateDisplayName(typed, issues);
        DisplayNameError = name is null
            ? $"Type 1–{DiscordLimits.TextMax} characters to show after “Playing”."
            : null;
        if (name is not null)
        {
            Apply(s => s with { Discord = s.Discord with { DisplayName = name } });
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (IsLoading || _loadingAutostart)
        {
            return;
        }

        StartWithWindowsError = _autostart.SetEnabled(value);
        if (StartWithWindowsError is null)
        {
            Apply(s => s with { Lifecycle = s.Lifecycle with { StartWithWindows = value } });
        }
        else
        {
            // Show the real state rather than the requested one.
            _loadingAutostart = true;
            try
            {
                StartWithWindows = _autostart.IsEnabled;
            }
            finally
            {
                _loadingAutostart = false;
            }
        }
    }

    partial void OnStartMinimisedChanged(bool value) => Apply(s => s with { Lifecycle = s.Lifecycle with { StartMinimised = value } });

    partial void OnCloseToTrayChanged(bool value) => Apply(s => s with { Lifecycle = s.Lifecycle with { CloseToTray = value } });

    partial void OnNotificationsChanged(NotificationLevel value) => Apply(s => s with { Lifecycle = s.Lifecycle with { Notifications = value } });
}
