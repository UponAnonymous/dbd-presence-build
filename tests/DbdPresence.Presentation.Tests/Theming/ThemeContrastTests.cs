// Covers: NFR-A11Y-02 (WCAG contrast of every token pair in both themes), FR-APP-02 (accent policy).
using DbdPresence.Presentation.Theming;

namespace DbdPresence.Presentation.Tests.Theming;

public sealed class ThemeContrastTests
{
    public static TheoryData<string> Themes => ["Dark", "Light"];

    private static ThemePalette Palette(string name) => name == "Dark" ? ThemePalette.Dark : ThemePalette.Light;

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextTokens_MeetAA_OnEverySurface(string theme)
    {
        var palette = Palette(theme);
        foreach (var surface in palette.Surfaces)
        {
            foreach (var (name, color) in new[] { ("Text", palette.Text), ("TextMuted", palette.TextMuted), ("Gold", palette.AccentGold), ("Blue", palette.AccentBlue), ("Success", palette.Success), ("Warning", palette.Warning), ("Danger", palette.Danger) })
            {
                var ratio = ColorRgb.Contrast(color, surface);
                Assert.True(ratio >= 4.5, $"{theme} {name} {color} on {surface}: {ratio:0.00}:1 < 4.5:1");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void SoftFills_CarryReadableTextAndTheirOwnSemanticColour(string theme)
    {
        // Design §9.9: the status headline and the Testing mark sit on a soft tint of their semantic colour. Text and the semantic colour must stay readable on it.
        var palette = Palette(theme);
        var pairs = new[]
        {
            ("SuccessSoft", palette.SuccessSoft, palette.Success),
            ("WarningSoft", palette.WarningSoft, palette.Warning),
            ("DangerSoft", palette.DangerSoft, palette.Danger),
            ("AccentSoft", palette.AccentSoft, palette.AccentGold),
        };
        foreach (var (name, soft, semantic) in pairs)
        {
            Assert.True(ColorRgb.Contrast(palette.Text, soft) >= 4.5, $"{theme} Text on {name}");
            Assert.True(ColorRgb.Contrast(semantic, soft) >= 4.5, $"{theme} semantic colour on {name}");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ControlBoundaries_MeetNonTextContrast(string theme)
    {
        var palette = Palette(theme);
        foreach (var surface in palette.Surfaces)
        {
            Assert.True(ColorRgb.Contrast(palette.BorderStrong, surface) >= 3, $"{theme} BorderStrong on {surface}");
            Assert.True(ColorRgb.Contrast(palette.AccentBlue, surface) >= 3, $"{theme} focus ring on {surface}");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void PreviewCard_TextIsReadable(string theme)
    {
        var palette = Palette(theme);

        Assert.True(ColorRgb.Contrast(palette.PreviewText, palette.PreviewCard) >= 4.5);
        Assert.True(ColorRgb.Contrast(palette.PreviewMuted, palette.PreviewCard) >= 4.5);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryPresetAccent_ResolvesToAReadableAccent(string theme)
    {
        var palette = Palette(theme);
        foreach (var (hex, name) in AccentPolicy.Presets)
        {
            var (accent, problem) = AccentPolicy.Resolve(hex, palette);

            Assert.True(problem is null, $"{theme} {name}: {problem}");
            Assert.All(palette.Surfaces, s => Assert.True(ColorRgb.Contrast(accent!.Accent, s) >= 3));
            Assert.True(ColorRgb.Contrast(accent!.Accent, accent.OnAccent) >= 4.5);
        }
    }

    [Fact]
    public void LowContrastAccent_IsAdjustedAndFlagged()
    {
        var (accent, problem) = AccentPolicy.Resolve("#E6E9EE", ThemePalette.Light);

        Assert.Null(problem);
        Assert.True(accent!.WasAdjusted);
        Assert.True(ThemePalette.Light.Surfaces.All(s => ColorRgb.Contrast(accent.Accent, s) >= 3));
    }

    [Theory]
    [InlineData("gold")]
    [InlineData("#12345")]
    [InlineData("")]
    public void MalformedAccent_IsRejectedWithMessage(string hex)
    {
        var (accent, problem) = AccentPolicy.Resolve(hex, ThemePalette.Dark);

        Assert.Null(accent);
        Assert.Contains("#RRGGBB", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Tint_ThatHurtsReadability_IsNotApplied()
    {
        var (palette, problem) = AccentPolicy.ApplyTint(ThemePalette.Light, "#000000");
        var (dark, darkProblem) = AccentPolicy.ApplyTint(ThemePalette.Dark, "#4060A0");

        Assert.NotNull(problem);
        Assert.Same(ThemePalette.Light, palette);
        Assert.Null(darkProblem);
        Assert.NotEqual(ThemePalette.Dark.Bg, dark.Bg);
    }

    [Fact]
    public void ContrastMath_MatchesKnownValues()
    {
        Assert.Equal(21.0, ColorRgb.Contrast(ColorRgb.Parse("#000000"), ColorRgb.Parse("#FFFFFF")), 2);
        Assert.Equal(1.0, ColorRgb.Contrast(ColorRgb.Parse("#777777"), ColorRgb.Parse("#777777")), 2);
        Assert.Equal(4.48, ColorRgb.Contrast(ColorRgb.Parse("#777777"), ColorRgb.Parse("#FFFFFF")), 2);
    }

    [Theory]
    [InlineData("# 12345")]
    [InlineData("#12345G")]
    [InlineData("#-12345")]
    [InlineData("#1234567")]
    [InlineData("123456")]
    public void ColourParsing_IsStrict(string text) => Assert.False(ColorRgb.TryParse(text, out _));
}
