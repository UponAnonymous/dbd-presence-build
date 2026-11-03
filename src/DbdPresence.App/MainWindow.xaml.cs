using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DbdPresence.App.Theming;
using DbdPresence.Configuration;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.App;

/// <summary>
/// Main window (UI_SPEC §2): restores its bounds when they still fit a monitor, hides to the tray on close
/// (when enabled), supports Ctrl+1..9 page shortcuts, and collapses the navigation rail below 1000 DIP.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Whether the navigation rail shows icons only.</summary>
    public static readonly DependencyProperty IsNavCompactProperty =
        DependencyProperty.Register(nameof(IsNavCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false, OnCompactChanged));

    /// <summary>The navigation rail minimum width; the rail grows past it when a larger font scale needs the room.</summary>
    public static readonly DependencyProperty NavWidthProperty =
        DependencyProperty.Register(nameof(NavWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(ExpandedNavWidth));

    private const double ExpandedNavWidth = 200;
    private const double CompactNavWidth = 64;
    private const double CompactBelow = 1000;
    private const double DefaultWidth = 1120;
    private const double DefaultHeight = 720;
    private readonly ShellViewModel _shell;
    private readonly FirstRunViewModel _firstRun;
    private readonly UnlockViewModel _unlock;
    private readonly SettingsService _settings;
    private readonly ThemeManager _theme;
    private readonly Action _hiddenToTray;
    private readonly Action _quit;
    private bool _quitting;
    private bool _placed;

    internal MainWindow(ShellViewModel shell, FirstRunViewModel firstRun, UnlockViewModel unlock, SettingsService settings, ThemeManager theme, Action hiddenToTray, Action quit)
    {
        _shell = shell;
        _firstRun = firstRun;
        _unlock = unlock;
        _settings = settings;
        _theme = theme;
        _hiddenToTray = hiddenToTray;
        _quit = quit;
        InitializeComponent();
        DataContext = shell;
        FirstRun.DataContext = firstRun;
        Unlock.DataContext = unlock;
        firstRun.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunViewModel.IsOpen))
            {
                ApplyFirstRunModality();
            }
        };
        unlock.PropertyChanged += (_, _) => ApplyFirstRunModality();
        Loaded += (_, _) => ApplyFirstRunModality();
        IsVisibleChanged += (_, _) => shell.Pages.Select(p => p.Page).OfType<HomeViewModel>().ToList().ForEach(h => h.IsActive = IsVisible);
        _theme.ThemeChanged += (_, _) => _theme.StyleTitleBar(this);
        SourceInitialized += (_, _) => _theme.StyleTitleBar(this);
        SizeChanged += (_, _) => IsNavCompact = ActualWidth < CompactBelow;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Gets or sets a value indicating whether the navigation rail shows icons only.</summary>
    public bool IsNavCompact
    {
        get => (bool)GetValue(IsNavCompactProperty);
        set => SetValue(IsNavCompactProperty, value);
    }

    /// <summary>Gets or sets the navigation rail width.</summary>
    public double NavWidth
    {
        get => (double)GetValue(NavWidthProperty);
        set => SetValue(NavWidthProperty, value);
    }

    /// <summary>Shows the window (restoring it from the tray or minimised state) and brings it to the front.</summary>
    internal void ShowAndActivate()
    {
        if (!_placed)
        {
            Place();
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
    }

    /// <summary>Re-opens first-run setup (About page).</summary>
    internal void OpenFirstRun() => _firstRun.Open();

    /// <summary>Quits the app (About page).</summary>
    internal void RequestQuit() => _quit();

    /// <summary>Closes the window as part of quitting the app.</summary>
    internal void CloseForQuit()
    {
        _quitting = true;
        SavePlacement();
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_quitting)
        {
            return;
        }

        SavePlacement();
        e.Cancel = true;
        if (_settings.Current.Lifecycle.CloseToTray)
        {
            Hide();
            _hiddenToTray();
        }
        else
        {
            // Quit on the next dispatcher turn: closing the window again from inside Closing is not allowed.
            _ = Dispatcher.BeginInvoke(_quit);
        }
    }

    /// <summary>
    /// While the lock screen or first-run setup shows, what is behind it is inert (not tabbable or reachable) and focus starts on its primary
    /// button. The lock screen (D32) comes first; first-run setup follows once the app is unlocked.
    /// </summary>
    private void ApplyFirstRunModality()
    {
        var locked = _unlock.IsShown;
        var open = _firstRun.IsOpen;
        var covered = open || locked;
        ShellRoot.IsEnabled = !covered;
        FirstRun.IsEnabled = !locked;
        System.Windows.Automation.AutomationProperties.SetIsOffscreenBehavior(ShellRoot,
            covered ? System.Windows.Automation.IsOffscreenBehavior.Offscreen : System.Windows.Automation.IsOffscreenBehavior.Default);
        System.Windows.Automation.AutomationProperties.SetIsOffscreenBehavior(FirstRun,
            locked ? System.Windows.Automation.IsOffscreenBehavior.Offscreen : System.Windows.Automation.IsOffscreenBehavior.Default);
        if (locked)
        {
            _ = Dispatcher.BeginInvoke(() => Unlock.FocusPrimary(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else if (open)
        {
            _ = Dispatcher.BeginInvoke(() => FirstRun.FocusPrimary(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            _ = Navigation.Focus();
        }
    }

    private static void OnCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MainWindow)d).NavWidth = e.NewValue is true ? CompactNavWidth : ExpandedNavWidth;

    private static bool Fits(WindowPlacement p)
    {
        // At least a 100 x 50 DIP strip of the title bar must be on the virtual screen to be reachable.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(p.Left, p.Top, p.Width, 50);
        titleBar.Intersect(screen);
        return !titleBar.IsEmpty && titleBar.Width >= 100 && titleBar.Height >= 30;
    }

    private void Place()
    {
        _placed = true;
        var work = SystemParameters.WorkArea;
        if (_settings.Current.Lifecycle.Window is { } saved && Fits(saved))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
            return;
        }

        // Default 1120 x 720, clamped to 90% of the work area on small screens (never below the minimum size).
        Width = Math.Max(MinWidth, Math.Min(DefaultWidth, work.Width * 0.9));
        Height = Math.Max(MinHeight, Math.Min(DefaultHeight, work.Height * 0.9));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
    }

    private void SavePlacement()
    {
        if (!_placed)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        var placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        if (placement != _settings.Current.Lifecycle.Window)
        {
            _ = _settings.Update(s => s with { Lifecycle = s.Lifecycle with { Window = placement } });
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || FirstRun.IsVisible || Unlock.IsVisible)
        {
            return;
        }

        var number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
            _ => 0,
        };
        if (number > 0 && _shell.SelectByShortcut(number))
        {
            e.Handled = true;
        }
    }
}
