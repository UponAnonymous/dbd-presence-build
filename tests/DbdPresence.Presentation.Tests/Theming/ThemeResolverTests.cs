// Covers: FR-APP-01 (theme follows Windows / forced), FR-APP-02 (accent + tint fallbacks), FR-APP-03 (density, font scale,
// reduce motion, high contrast uses system colours).
using DbdPresence.Configuration;
using DbdPresence.Presentation.Theming;

namespace DbdPresence.Presentation.Tests.Theming;

public sealed class ThemeResolverTests
{
    private static readonly SystemThemeState s_darkSystem = new(AppsUseDarkTheme: true, HighContrast: false, AnimationsEnabled: true);
    private static readonly SystemThemeState s_lightSystem = s_darkSystem with { AppsUseDarkTheme = false };

    [Fact]
    public void SystemPreference_FollowsWindowsAppMode()
    {
        Assert.True(ThemeResolver.Resolve(new AppearanceSettings(), s_darkSystem).Palette.IsDark);
        Assert.False(ThemeResolver.Resolve(new AppearanceSettings(), s_lightSystem).Palette.IsDark);
    }

    [Theory]
    [InlineData(ThemePreference.Dark, false, true)]
    [InlineData(ThemePreference.Light, true, false)]
    public void ForcedPreference_IgnoresWindows(ThemePreference preference, bool windowsDark, bool expectDark)
    {
        var resolved = ThemeResolver.Resolve(new AppearanceSettings { Theme = preference }, s_darkSystem with { AppsUseDarkTheme = windowsDark });
        Assert.Equal(expectDark, resolved.Palette.IsDark);
    }

    [Fact]
    public void DefaultAccent_UsesThePalettesTunedGold_InBothThemes()
    {
        Assert.Equal(ThemePalette.Dark.AccentGold, ThemeResolver.Resolve(new AppearanceSettings(), s_darkSystem).Accent);
        Assert.Equal(ThemePalette.Light.AccentGold, ThemeResolver.Resolve(new AppearanceSettings(), s_lightSystem).Accent);
        Assert.Empty(ThemeResolver.Resolve(new AppearanceSettings(), s_lightSystem).Notes);
    }

    [Fact]
    public void InvalidAccent_FallsBackToGold_WithAnExplanation()
    {
        var resolved = ThemeResolver.Resolve(new AppearanceSettings { Accent = "not-a-colour" }, s_darkSystem);
        Assert.Equal(ThemePalette.Dark.AccentGold, resolved.Accent);
        Assert.Contains(resolved.Notes, n => n.Contains("default gold", StringComparison.Ordinal));
    }

    [Fact]
    public void LowContrastAccent_IsAdjusted_AndTheUserIsTold()
    {
        var resolved = ThemeResolver.Resolve(new AppearanceSettings { Accent = "#202020" }, s_darkSystem);
        Assert.NotEqual(ColorRgb.Parse("#202020"), resolved.Accent);
        Assert.All(resolved.Palette.Surfaces, s => Assert.True(ColorRgb.Contrast(resolved.Accent, s) >= AccentPolicy.MinAccentContrast));
        Assert.True(ColorRgb.Contrast(resolved.Accent, resolved.OnAccent) >= AccentPolicy.MinTextContrast);
        Assert.Contains(resolved.Notes, n => n.Contains("adjusted", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidTint_ChangesSurfaces_KeepsText()
    {
        var resolved = ThemeResolver.Resolve(new AppearanceSettings { SurfaceTint = "#3050A0" }, s_darkSystem);
        Assert.NotEqual(ThemePalette.Dark.Surface, resolved.Palette.Surface);
        Assert.Equal(ThemePalette.Dark.Text, resolved.Palette.Text);
        Assert.Empty(resolved.Notes);
    }

    [Fact]
    public void UnreadableTint_IsNotApplied_AndExplained()
    {
        var resolved = ThemeResolver.Resolve(new AppearanceSettings { SurfaceTint = "#FFFFFF" }, s_lightSystem with { AppsUseDarkTheme = true });
        Assert.Equal(ThemePalette.Dark.Surface, resolved.Palette.Surface);
        Assert.Single(resolved.Notes);
    }

    [Fact]
    public void HighContrast_UsesSystemColours_AndIgnoresAccentAndTint()
    {
        var hc = new HighContrastColors(ColorRgb.Parse("#000000"), ColorRgb.Parse("#FFFFFF"), ColorRgb.Parse("#1AEBFF"), ColorRgb.Parse("#000000"));
        var resolved = ThemeResolver.Resolve(
            new AppearanceSettings { Accent = "#A371F7", SurfaceTint = "#3050A0" },
            s_darkSystem with { HighContrast = true, Colors = hc });

        Assert.True(resolved.IsHighContrast);
        Assert.Equal(hc.Highlight, resolved.Accent);
        Assert.Equal(hc.HighlightText, resolved.OnAccent);
        Assert.Equal(hc.Window, resolved.Palette.Surface);
        Assert.Equal(hc.WindowText, resolved.Palette.Text);
        Assert.Equal(hc.WindowText, resolved.Palette.TextMuted);
        Assert.Equal(hc.WindowText, resolved.Focus);
    }

    [Theory]
    [InlineData(Density.Comfortable, 100, 32, 24, 14)]
    [InlineData(Density.Compact, 100, 32, 16, 14)]
    [InlineData(Density.Comfortable, 150, 48, 24, 21)]
    [InlineData(Density.Compact, 90, 32, 16, 12.6)]
    [InlineData(Density.Comfortable, 400, 48, 24, 21)]
    [InlineData(Density.Comfortable, 10, 32, 24, 12.6)]
    public void Metrics_ScaleWithDensityAndFont(Density density, int scale, double controlHeight, double cardPadding, double body)
    {
        // Design §9.1: controls are 32 DIP tall whatever the density; density changes card padding only (24 or 16).
        var metrics = ThemeResolver.MetricsFor(density, scale);
        Assert.Equal(controlHeight, metrics.ControlHeight);
        Assert.Equal(cardPadding, metrics.CardPadding);
        Assert.Equal(body, metrics.FontBody);
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ReduceMotion_FollowsWindowsUnlessSet(bool? setting, bool windowsAnimations, bool expected)
    {
        var resolved = ThemeResolver.Resolve(new AppearanceSettings { ReduceMotion = setting }, s_darkSystem with { AnimationsEnabled = windowsAnimations });
        Assert.Equal(expected, resolved.ReduceMotion);
    }

    [Theory]
    [InlineData("#D4A649")]
    [InlineData("#5B9BFF")]
    [InlineData("#E5484D")]
    [InlineData("#3FB950")]
    [InlineData("#A371F7")]
    public void EveryPreset_ResolvesReadably_InBothThemes(string preset)
    {
        foreach (var system in new[] { s_darkSystem, s_lightSystem })
        {
            var resolved = ThemeResolver.Resolve(new AppearanceSettings { Accent = preset }, system);
            Assert.True(ColorRgb.Contrast(resolved.Accent, resolved.OnAccent) >= AccentPolicy.MinTextContrast);
        }
    }
}
