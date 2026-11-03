using DbdPresence.Configuration;

namespace DbdPresence.Presentation.Theming;

/// <summary>Windows high-contrast system colours (only meaningful while high contrast is on).</summary>
/// <param name="Window">Window background.</param>
/// <param name="WindowText">Window text.</param>
/// <param name="Highlight">Selection/emphasis background.</param>
/// <param name="HighlightText">Text on <paramref name="Highlight"/>.</param>
public sealed record HighContrastColors(ColorRgb Window, ColorRgb WindowText, ColorRgb Highlight, ColorRgb HighlightText);

/// <summary>What Windows currently reports about theme, contrast and animation.</summary>
/// <param name="AppsUseDarkTheme">True when Windows' app mode is dark.</param>
/// <param name="HighContrast">True when a high-contrast theme is active; <paramref name="Colors"/> is then required.</param>
/// <param name="AnimationsEnabled">True when Windows client-area animations are on.</param>
/// <param name="Colors">System colours for high contrast.</param>
public sealed record SystemThemeState(bool AppsUseDarkTheme, bool HighContrast, bool AnimationsEnabled, HighContrastColors? Colors = null);

/// <summary>Sizes derived from density and font scale (design/whole-app.md §9.1 and §9.2).</summary>
/// <param name="ControlHeight">Height of buttons, inputs and nav items.</param>
/// <param name="CardPadding">Inner padding of panels.</param>
/// <param name="FontCaption">Caption size.</param>
/// <param name="FontBody">Body size.</param>
/// <param name="FontSubtitle">Subtitle size.</param>
/// <param name="FontTitle">Page title size.</param>
/// <param name="FontDisplay">Display size.</param>
public sealed record ThemeMetrics(double ControlHeight, double CardPadding, double FontCaption, double FontBody, double FontSubtitle, double FontTitle, double FontDisplay);

/// <summary>The fully resolved look the WPF layer publishes as resources.</summary>
/// <param name="Palette">Colours (tinted when a valid tint is set; system colours under high contrast).</param>
/// <param name="Accent">Accent colour.</param>
/// <param name="OnAccent">Text colour on the accent.</param>
/// <param name="Focus">Focus ring colour.</param>
/// <param name="Metrics">Sizes.</param>
/// <param name="ReduceMotion">True when transitions must be skipped.</param>
/// <param name="IsHighContrast">True when Windows high contrast overrides app colours.</param>
/// <param name="Notes">User-facing explanations of anything not applied as requested.</param>
public sealed record ResolvedTheme(
    ThemePalette Palette, ColorRgb Accent, ColorRgb OnAccent, ColorRgb Focus, ThemeMetrics Metrics, bool ReduceMotion, bool IsHighContrast, IReadOnlyList<string> Notes);

/// <summary>Turns appearance settings plus the Windows state into a <see cref="ResolvedTheme"/> (FR-APP-01..04).</summary>
public static class ThemeResolver
{
    /// <summary>Smallest supported font scale percentage.</summary>
    public const int MinFontScale = 90;

    /// <summary>Largest supported font scale percentage.</summary>
    public const int MaxFontScale = 150;

    /// <summary>Resolves the theme. Never throws for bad user values; it falls back and explains in <see cref="ResolvedTheme.Notes"/>.</summary>
    public static ResolvedTheme Resolve(AppearanceSettings appearance, SystemThemeState system)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(system);

        var metrics = MetricsFor(appearance.Density, appearance.FontScalePercent);
        var reduceMotion = appearance.ReduceMotion ?? !system.AnimationsEnabled;

        if (system.HighContrast && system.Colors is { } hc)
        {
            return new ResolvedTheme(HighContrastPalette(hc), hc.Highlight, hc.HighlightText, hc.WindowText, metrics, reduceMotion, IsHighContrast: true,
                ["Windows high contrast is on, so the app uses your system colours and ignores the accent and tint."]);
        }

        var dark = appearance.Theme switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => system.AppsUseDarkTheme,
        };
        var notes = new List<string>();
        var (palette, tintProblem) = AccentPolicy.ApplyTint(dark ? ThemePalette.Dark : ThemePalette.Light, appearance.SurfaceTint);
        if (tintProblem is not null)
        {
            notes.Add(tintProblem);
        }

        var (accent, accentProblem) = AccentPolicy.Resolve(appearance.Accent, palette);
        if (accent is null)
        {
            notes.Add($"{accentProblem} Using the default gold.");
            (accent, _) = AccentPolicy.Resolve(new AppearanceSettings().Accent, palette);
        }
        else if (accent.WasAdjusted)
        {
            notes.Add($"Your accent was adjusted to {accent.Accent} so it stays readable on the {(dark ? "dark" : "light")} theme.");
        }

        return new ResolvedTheme(palette, accent!.Accent, accent.OnAccent, palette.AccentBlue, metrics, reduceMotion, IsHighContrast: false, notes);
    }

    /// <summary>Computes sizes for a density and font scale (the scale is clamped to 90–150%).</summary>
    public static ThemeMetrics MetricsFor(Density density, int fontScalePercent)
    {
        var scale = Math.Clamp(fontScalePercent, MinFontScale, MaxFontScale) / 100.0;
        var compact = density == Density.Compact;

        // Design §9.1: controls are 32 DIP tall in both densities (density changes padding only). They grow with the font so scaled text never clips.
        var controlHeight = Math.Round(32 * Math.Max(1.0, scale));
        return new ThemeMetrics(
            ControlHeight: controlHeight,
            CardPadding: compact ? 16 : 24,
            FontCaption: Math.Round(12 * scale, 1),
            FontBody: Math.Round(14 * scale, 1),
            FontSubtitle: Math.Round(16 * scale, 1),
            FontTitle: Math.Round(20 * scale, 1),
            FontDisplay: Math.Round(28 * scale, 1));
    }

    private static ThemePalette HighContrastPalette(HighContrastColors hc) => new()
    {
        IsDark = hc.Window.RelativeLuminance < 0.5,
        Bg = hc.Window,
        Surface = hc.Window,
        SurfaceRaised = hc.Window,
        SurfaceHover = hc.Window,
        Border = hc.WindowText,
        BorderStrong = hc.WindowText,
        Text = hc.WindowText,
        TextMuted = hc.WindowText,
        AccentGold = hc.Highlight,
        AccentBlue = hc.Highlight,
        Success = hc.WindowText,
        Warning = hc.WindowText,
        Danger = hc.WindowText,
        SuccessSoft = hc.Window,
        WarningSoft = hc.Window,
        DangerSoft = hc.Window,
        AccentSoft = hc.Window,
        PreviewCard = hc.Window,
        PreviewText = hc.WindowText,
        PreviewMuted = hc.WindowText,
    };
}
