using CommunityToolkit.Mvvm.ComponentModel;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>The sections of the Settings page, in page order (design §7). The in-page index jumps to them; it is a convenience, not navigation.</summary>
public enum SettingsSection
{
    /// <summary>Screen reading.</summary>
    ScreenReading,

    /// <summary>Look.</summary>
    Look,

    /// <summary>Start and close.</summary>
    StartAndClose,

    /// <summary>Discord connection.</summary>
    DiscordConnection,

    /// <summary>Advanced.</summary>
    Advanced,

    /// <summary>About.</summary>
    About,
}

/// <summary>
/// The Settings page (design §7): how the app behaves, in six sections. It composes the existing page models, so every control keeps its validation,
/// live apply and tests: <see cref="ScreenReading"/>, <see cref="Appearance"/> (Look), <see cref="AppStartup"/> (Start and close, the connection
/// test, the own-Discord-app option) and <see cref="About"/>. Home links here with <see cref="Show"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Initializes a new instance of the <see cref="SettingsViewModel"/> class.</summary>
    public SettingsViewModel(
        PresenceControlsViewModel controls, ScreenReadingViewModel screenReading, AppearanceViewModel appearance, AppStartupViewModel appStartup,
        AboutViewModel about, PrivacyViewModel privacy, DetectionViewModel? detection = null, UpdatesViewModel? updates = null)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(screenReading);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(appStartup);
        ArgumentNullException.ThrowIfNull(about);
        ArgumentNullException.ThrowIfNull(privacy);
        Controls = controls;
        ScreenReading = screenReading;
        Appearance = appearance;
        AppStartup = appStartup;
        About = about;
        Privacy = privacy;
        Detection = detection;
        Updates = updates;
        appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppearanceViewModel.Motion))
            {
                OnPropertyChanged(nameof(ReduceMotion));
            }
        };
    }

    /// <summary>Raised when the page should scroll a section into view (the view does the scrolling).</summary>
    public event EventHandler<SettingsSection>? SectionRequested;

    /// <summary>Gets the shared controls (connection status and the test status).</summary>
    public PresenceControlsViewModel Controls { get; }

    /// <summary>Gets the screen reading section.</summary>
    public ScreenReadingViewModel ScreenReading { get; }

    /// <summary>Gets the Look section.</summary>
    public AppearanceViewModel Appearance { get; }

    /// <summary>Gets Start and close, the Discord connection and the own-Discord-app option.</summary>
    public AppStartupViewModel AppStartup { get; }

    /// <summary>Gets the About section.</summary>
    public AboutViewModel About { get; }

    /// <summary>Gets where the data is kept (read-only paths).</summary>
    public PrivacyViewModel Privacy { get; }

    /// <summary>Gets the owner's diagnostics rows, or null in a build without screen reading. Shown only behind the Diagnostics expander.</summary>
    public DetectionViewModel? Detection { get; }

    /// <summary>Gets the signed updater's state and buttons (FR-ONL-10), or null in a build without the updater (tests).</summary>
    public UpdatesViewModel? Updates { get; }

    /// <summary>Gets or sets a value indicating whether the Advanced section is open.</summary>
    [ObservableProperty]
    public partial bool AdvancedOpen { get; set; }

    /// <summary>Gets or sets a value indicating whether the Diagnostics expander inside Advanced is open.</summary>
    [ObservableProperty]
    public partial bool DiagnosticsOpen { get; set; }

    /// <summary>Gets or sets a value indicating whether "Reduce motion" is on. Off follows Windows (the three-way list is in Advanced).</summary>
    public bool ReduceMotion
    {
        get => Appearance.Motion == MotionPreference.Reduce;
        set
        {
            if (value == ReduceMotion)
            {
                return;
            }

            Appearance.Motion = value ? MotionPreference.Reduce : MotionPreference.FollowWindows;
            OnPropertyChanged();
        }
    }

    /// <summary>Opens a section (Advanced opens its expander) and asks the view to scroll to it.</summary>
    /// <param name="section">The section.</param>
    public void Show(SettingsSection section)
    {
        if (section == SettingsSection.Advanced)
        {
            AdvancedOpen = true;
        }

        SectionRequested?.Invoke(this, section);
    }
}
