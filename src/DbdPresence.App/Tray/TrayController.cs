using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using DbdPresence.Configuration;
using DbdPresence.Presentation.Detection;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Study;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DbdPresence.App.Tray;

/// <summary>
/// The notification-area icon and menu (UI_SPEC §6, FR-LIFE-02, FR-LIFE-04). The icon badge and the tooltip
/// both state the status, so colour is never the only signal.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly PresenceControlsViewModel _controls;
    private readonly ISettingsService _settings;
    private readonly Action _showWindow;
    private readonly Action _quit;
    private readonly IDetectionStatusSource _detection;
    private readonly IStudyRecorder? _recorder;
    private readonly BitmapFrame _connected = Load("tray-connected.ico");
    private readonly BitmapFrame _disconnected = Load("tray-disconnected.ico");
    private readonly BitmapFrame _paused = Load("tray-paused.ico");
    private StatusTone? _lastTone;

    public TrayController(PresenceControlsViewModel controls, ISettingsService settings, Action showWindow, Action quit, IDetectionStatusSource detection, IStudyRecorder? recorder)
    {
        _controls = controls;
        _detection = detection;
        _recorder = recorder;
        _settings = settings;
        _showWindow = showWindow;
        _quit = quit;
        _icon = new TaskbarIcon
        {
            NoLeftClickDelay = true,
            ContextMenu = BuildMenu(),
        };
        AutomationProperties(_icon.ContextMenu, "TrayMenu", "DBD Presence menu");
        _icon.TrayLeftMouseUp += (_, _) => _showWindow();
        _controls.PropertyChanged += OnControlsChanged;
        _settings.SaveFailed += OnSaveFailed;
        _detection.StatusChanged += OnCaptureChanged;
        if (_recorder is not null)
        {
            _recorder.StatusChanged += OnCaptureChanged;
        }

        Refresh();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Explains, the first time only, that closing the window keeps the app running (UI_SPEC §2).</summary>
    public void ExplainCloseToTray()
    {
        if (_settings.Current.Lifecycle.CloseToTrayExplained)
        {
            return;
        }

        // Shown even when notifications are off: without it the app would seem to have vanished.
        _icon.ShowNotification("DBD Presence is still running", "Your Discord status keeps updating. Use the tray icon to open the window or quit.", NotificationIcon.Info);
        _ = _settings.Update(s => s with { Lifecycle = s.Lifecycle with { CloseToTrayExplained = true } });
    }

    /// <summary>Tells the owner that an automatic run has made a spot-check ready (FR-LAB-04); shown unless notifications are off.</summary>
    public void NotifySpotCheckReady() =>
        Notify("Spot-check ready", "About 2 minutes. Open the Study page to do it.", NotificationIcon.Info, NotificationLevel.CriticalOnly);

    public void Dispose()
    {
        _controls.PropertyChanged -= OnControlsChanged;
        _settings.SaveFailed -= OnSaveFailed;
        _detection.StatusChanged -= OnCaptureChanged;
        if (_recorder is not null)
        {
            _recorder.StatusChanged -= OnCaptureChanged;
        }

        _icon.Dispose();
    }

    private static BitmapFrame Load(string name) =>
        BitmapFrame.Create(new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute));

    private static void AutomationProperties(FrameworkElement element, string id, string name)
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
        System.Windows.Automation.AutomationProperties.SetName(element, name);
    }

    private ContextMenu BuildMenu()
    {
        // Design §14: Open, Pause (checkable), Quit. Nothing else: role and mode are detected, Private is a style, profiles live in Advanced.
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open DBD Presence", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _showWindow();

        var pause = new MenuItem { Header = "Pause", IsCheckable = true };
        pause.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(PresenceControlsViewModel.IsPaused)) { Source = _controls, Mode = BindingMode.TwoWay });

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => _quit();

        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    private void OnControlsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PresenceControlsViewModel.Connection) or nameof(PresenceControlsViewModel.IsPaused) or nameof(PresenceControlsViewModel.TraySummary))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var tone = _controls.Connection.Tone;
        _icon.IconSource = _controls.IsPaused ? _paused : tone == StatusTone.Good ? _connected : _disconnected;

        // Windows truncates tray tooltips at 127 characters. While the game window is read or recorded the tooltip says so (the yellow
        // capture border is requested off since 2026-11-02, SAFETY_AND_PRIVACY section 5).
        var capture = CaptureIndicator.Words(_detection.Status.Run, _recorder?.Status.IsActive == true);
        _icon.ToolTipText = CaptureIndicator.Tooltip(_controls.TraySummary, capture);

        if (_lastTone is { } previous && previous != tone && _controls.IsConfigured)
        {
            if (tone == StatusTone.Good)
            {
                Notify("Connected to Discord", "Your activity is being shown again.", NotificationIcon.Info, NotificationLevel.All);
            }
            else if (previous == StatusTone.Good)
            {
                Notify("Lost connection to Discord", _controls.Connection.Detail ?? "Retrying automatically.", NotificationIcon.Warning, NotificationLevel.All);
            }
        }

        _lastTone = tone;
    }

    // Raised on any thread by the detection loop and the recorder; the icon belongs to the UI thread.
    private void OnCaptureChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.InvokeAsync(Refresh);

    private void OnSaveFailed(object? sender, SettingsSaveFailedEventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(() => Notify(
            "Settings could not be saved",
            "Your changes apply now but may be lost when the app closes. Check that the settings folder is writable.",
            NotificationIcon.Error,
            NotificationLevel.CriticalOnly));

    private void Notify(string title, string message, NotificationIcon kind, NotificationLevel minimum)
    {
        var level = _settings.Current.Lifecycle.Notifications;
        if (level == NotificationLevel.Off || level < minimum)
        {
            return;
        }

        _icon.ShowNotification(title, message, kind);
    }
}
