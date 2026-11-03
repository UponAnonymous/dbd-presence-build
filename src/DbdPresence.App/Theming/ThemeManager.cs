using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DbdPresence.Configuration;
using DbdPresence.Platform.Windows.Ui;
using DbdPresence.Presentation.Theming;
using DbdPresence.Presentation.ViewModels;
using Microsoft.Win32;

namespace DbdPresence.App.Theming;

/// <summary>
/// Publishes the resolved theme (ThemeResolver, unit-tested in Presentation) as application resources that
/// Themes/Controls.xaml consumes through DynamicResource, and re-applies it when Windows' colour mode, high
/// contrast or animation settings change (FR-APP-01..03).
/// </summary>
internal sealed class ThemeManager : IThemeService, IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private AppearanceSettings _appearance;

    public ThemeManager(ResourceDictionary resources, ISettingsService settings)
    {
        _resources = resources;
        _settings = settings;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _appearance = settings.Current.Appearance;
        Current = Apply();
        settings.Changed += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    /// <summary>Raised on the UI thread after resources change.</summary>
    public event EventHandler? ThemeChanged;

    public ResolvedTheme Current { get; private set; }

    public void Update(AppearanceSettings appearance)
    {
        if (appearance == _appearance)
        {
            return;
        }

        _appearance = appearance;
        Refresh();
    }

    /// <summary>Matches the window's title bar to the theme (no-op where Windows doesn't support it).</summary>
    public void StyleTitleBar(Window window) =>
        OwnWindowChrome.TrySetDarkTitleBar(new WindowInteropHelper(window).Handle, Current.Palette.IsDark);

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    private static SystemThemeState ReadSystem()
    {
        var highContrast = SystemParameters.HighContrast;
        var colors = highContrast
            ? new HighContrastColors(ToRgb(SystemColors.WindowColor), ToRgb(SystemColors.WindowTextColor), ToRgb(SystemColors.HighlightColor), ToRgb(SystemColors.HighlightTextColor))
            : null;
        return new SystemThemeState(SystemThemeProbe.AppsUseDarkTheme(), highContrast, SystemParameters.ClientAreaAnimation, colors);
    }

    private static ColorRgb ToRgb(Color color) => new(color.R, color.G, color.B);

    private static SolidColorBrush Brush(ColorRgb color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void Refresh()
    {
        Current = Apply();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private ResolvedTheme Apply()
    {
        var theme = ThemeResolver.Resolve(_appearance, ReadSystem());
        var p = theme.Palette;
        _resources["BgBrush"] = Brush(p.Bg);
        _resources["SurfaceBrush"] = Brush(p.Surface);
        _resources["SurfaceRaisedBrush"] = Brush(p.SurfaceRaised);
        _resources["SurfaceHoverBrush"] = Brush(p.SurfaceHover);
        _resources["BorderSubtleBrush"] = Brush(p.Border);
        _resources["BorderStrongBrush"] = Brush(p.BorderStrong);
        _resources["TextBrush"] = Brush(p.Text);
        _resources["TextMutedBrush"] = Brush(p.TextMuted);
        _resources["GoldBrush"] = Brush(p.AccentGold);
        _resources["AccentBrush"] = Brush(theme.Accent);
        _resources["OnAccentBrush"] = Brush(theme.OnAccent);
        _resources["FocusBrush"] = Brush(theme.Focus);
        _resources["SuccessBrush"] = Brush(p.Success);
        _resources["WarningBrush"] = Brush(p.Warning);
        _resources["DangerBrush"] = Brush(p.Danger);
        _resources["SuccessSoftBrush"] = Brush(p.SuccessSoft);
        _resources["WarningSoftBrush"] = Brush(p.WarningSoft);
        _resources["DangerSoftBrush"] = Brush(p.DangerSoft);
        _resources["AccentSoftBrush"] = Brush(p.AccentSoft);
        _resources["PreviewCardBrush"] = Brush(p.PreviewCard);
        _resources["PreviewTextBrush"] = Brush(p.PreviewText);
        _resources["PreviewMutedBrush"] = Brush(p.PreviewMuted);

        var m = theme.Metrics;
        _resources["ControlHeight"] = m.ControlHeight;
        _resources["CardPadding"] = new Thickness(m.CardPadding);
        _resources["FontSizeCaption"] = m.FontCaption;
        _resources["FontSizeBody"] = m.FontBody;
        _resources["FontSizeSubtitle"] = m.FontSubtitle;
        _resources["FontSizeTitle"] = m.FontTitle;
        _resources["FontSizeDisplay"] = m.FontDisplay;
        _resources["ReduceMotion"] = theme.ReduceMotion;
        return theme;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Current.Appearance != e.Previous.Appearance)
        {
            _ = _dispatcher.InvokeAsync(() => Update(e.Current.Appearance));
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
        {
            _ = _dispatcher.InvokeAsync(Refresh);
        }
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
        {
            _ = _dispatcher.InvokeAsync(Refresh);
        }
    }
}
