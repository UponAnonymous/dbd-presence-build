using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Presentation.Theming;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>Reduce-motion choice: follow Windows, or force on/off.</summary>
public enum MotionPreference
{
    /// <summary>Follow Windows' animation setting.</summary>
    FollowWindows,

    /// <summary>Always reduce motion.</summary>
    Reduce,

    /// <summary>Always animate.</summary>
    Animate,
}

/// <summary>An accent preset swatch.</summary>
/// <param name="Hex">The colour.</param>
/// <param name="Name">Its name (also the automation name).</param>
public sealed record AccentSwatch(string Hex, string Name);

/// <summary>Appearance (FR-APP-01..03): theme, accent, surface tint, density, font scale and motion. Changes apply live.</summary>
public sealed partial class AppearanceViewModel : SettingsPageViewModel
{
    private readonly IThemeService _theme;

    /// <summary>Initializes a new instance of the <see cref="AppearanceViewModel"/> class.</summary>
    public AppearanceViewModel(ISettingsService settings, IUiDispatcher dispatcher, IThemeService theme)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        _theme.ThemeChanged += OnAppliedThemeChanged;
        Reload();
        RefreshNotes();
    }

    /// <summary>Gets the theme choices.</summary>
    public IReadOnlyList<Choice<ThemePreference>> Themes { get; } =
        [new(ThemePreference.System, "Use Windows setting"), new(ThemePreference.Dark, "Dark"), new(ThemePreference.Light, "Light")];

    /// <summary>Gets the density choices.</summary>
    public IReadOnlyList<Choice<Density>> Densities { get; } = [new(Density.Comfortable, "Comfortable"), new(Density.Compact, "Compact")];

    /// <summary>Gets the font scale choices (90–150%).</summary>
    public IReadOnlyList<Choice<int>> FontScales { get; } =
        [.. Enumerable.Range(0, 7).Select(i => 90 + (i * 10)).Select(p => new Choice<int>(p, string.Create(CultureInfo.InvariantCulture, $"{p}%")))];

    /// <summary>Gets the motion choices.</summary>
    public IReadOnlyList<Choice<MotionPreference>> Motions { get; } =
        [new(MotionPreference.FollowWindows, "Use Windows setting"), new(MotionPreference.Reduce, "Reduce motion"), new(MotionPreference.Animate, "Allow animations")];

    /// <summary>Gets the accent presets.</summary>
    public IReadOnlyList<AccentSwatch> AccentPresets { get; } = [.. AccentPolicy.Presets.Select(p => new AccentSwatch(p.Hex, p.Name))];

    /// <summary>Gets or sets the theme.</summary>
    [ObservableProperty]
    public partial ThemePreference Theme { get; set; }

    /// <summary>Gets or sets the accent hex being edited.</summary>
    [ObservableProperty]
    public partial string AccentHex { get; set; } = string.Empty;

    /// <summary>Gets the problem with <see cref="AccentHex"/>, if it cannot be saved as typed.</summary>
    [ObservableProperty]
    public partial string? AccentError { get; private set; }

    /// <summary>Gets or sets the surface tint hex (empty = none).</summary>
    [ObservableProperty]
    public partial string SurfaceTint { get; set; } = string.Empty;

    /// <summary>Gets the problem with <see cref="SurfaceTint"/>, if it cannot be saved as typed.</summary>
    [ObservableProperty]
    public partial string? TintError { get; private set; }

    /// <summary>Gets or sets the density.</summary>
    [ObservableProperty]
    public partial Density Density { get; set; }

    /// <summary>Gets or sets the font scale percentage.</summary>
    [ObservableProperty]
    public partial int FontScalePercent { get; set; } = 100;

    /// <summary>Gets or sets the motion preference.</summary>
    [ObservableProperty]
    public partial MotionPreference Motion { get; set; }

    /// <summary>Gets explanations of anything the theme could not apply as chosen (accent adjusted, tint rejected, high contrast).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ThemeNotes { get; private set; } = [];

    /// <summary>Selects a preset accent.</summary>
    [RelayCommand]
    private void ChooseAccent(string hex) => AccentHex = hex;

    /// <summary>Removes the surface tint.</summary>
    [RelayCommand]
    private void ClearTint() => SurfaceTint = string.Empty;

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        var a = settings.Appearance;
        Theme = a.Theme;
        // Only replace the text when it means a different colour, so typing case is kept and the caret does not jump.
        if (AccentError is null && !string.Equals(AccentHex.Trim(), a.Accent, StringComparison.OrdinalIgnoreCase))
        {
            AccentHex = a.Accent;
        }

        if (TintError is null && !string.Equals(SurfaceTint.Trim(), a.SurfaceTint, StringComparison.OrdinalIgnoreCase))
        {
            SurfaceTint = a.SurfaceTint;
        }

        Density = a.Density;
        FontScalePercent = a.FontScalePercent;
        Motion = a.ReduceMotion switch
        {
            true => MotionPreference.Reduce,
            false => MotionPreference.Animate,
            null => MotionPreference.FollowWindows,
        };
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _theme.ThemeChanged -= OnAppliedThemeChanged;
        }

        base.Dispose(disposing);
    }

    partial void OnThemeChanged(ThemePreference value) => Appearance(a => a with { Theme = value });

    partial void OnAccentHexChanged(string value)
    {
        if (IsLoading)
        {
            return;
        }

        // Only a complete colour is saved; the theme then reports whether it had to adjust it.
        var hex = value.Trim();
        AccentError = ColorRgb.TryParse(hex, out _) ? null : "Enter a colour as #RRGGBB, for example #D4A649.";
        if (AccentError is null)
        {
            Appearance(a => a with { Accent = hex.ToUpperInvariant() });
        }
    }

    partial void OnSurfaceTintChanged(string value)
    {
        if (IsLoading)
        {
            return;
        }

        var hex = value.Trim();
        TintError = hex.Length == 0 || ColorRgb.TryParse(hex, out _) ? null : "Enter a tint as #RRGGBB, or leave it empty for none.";
        if (TintError is null)
        {
            Appearance(a => a with { SurfaceTint = hex.ToUpperInvariant() });
        }
    }

    partial void OnDensityChanged(Density value) => Appearance(a => a with { Density = value });

    partial void OnFontScalePercentChanged(int value) => Appearance(a => a with { FontScalePercent = value });

    partial void OnMotionChanged(MotionPreference value) => Appearance(a => a with
    {
        ReduceMotion = value switch
        {
            MotionPreference.Reduce => true,
            MotionPreference.Animate => false,
            _ => null,
        },
    });

    private void Appearance(Func<AppearanceSettings, AppearanceSettings> change) => Apply(s => s with { Appearance = change(s.Appearance) });

    private void OnAppliedThemeChanged(object? sender, EventArgs e) => RefreshNotes();

    private void RefreshNotes() => ThemeNotes = _theme.Current.Notes;
}
