namespace DbdPresence.Presentation.Theming;

/// <summary>The resolved accent: the colour used for emphasis and the text colour placed on it.</summary>
/// <param name="Accent">Accent colour.</param>
/// <param name="OnAccent">Text colour on the accent (≥ 4.5:1).</param>
/// <param name="WasAdjusted">True when the requested colour was darkened/lightened to stay readable.</param>
public sealed record ResolvedAccent(ColorRgb Accent, ColorRgb OnAccent, bool WasAdjusted);

/// <summary>Validates custom accents and surface tints against the theme (FR-APP-02; WCAG non-text contrast 3:1).</summary>
public static class AccentPolicy
{
    /// <summary>Minimum contrast of an accent against every surface (WCAG 1.4.11 non-text).</summary>
    public const double MinAccentContrast = 3.0;

    /// <summary>Minimum contrast of text on the accent (WCAG 1.4.3).</summary>
    public const double MinTextContrast = 4.5;

    /// <summary>The preset accent choices (hex, display name).</summary>
    public static readonly IReadOnlyList<(string Hex, string Name)> Presets =
    [
        ("#D4A649", "Fog gold"), ("#5B9BFF", "Entity blue"), ("#E5484D", "Blood red"), ("#3FB950", "Generator green"), ("#A371F7", "Aura violet"),
    ];

    /// <summary>
    /// Resolves an accent for a palette, or explains why it is unusable. The default gold uses the palette's
    /// own (contrast-tuned) gold so light and dark themes both pass.
    /// </summary>
    public static (ResolvedAccent? Accent, string? Problem) Resolve(string accentHex, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!ColorRgb.TryParse(accentHex, out var accent))
        {
            return (null, "Enter a colour as #RRGGBB.");
        }

        if (string.Equals(accentHex, "#D4A649", StringComparison.OrdinalIgnoreCase))
        {
            accent = palette.AccentGold;
        }

        // Too close to the background? Shift towards white (dark theme) or black (light theme) in small steps,
        // keeping the hue, until it reaches 3:1 against every surface.
        var target = palette.IsDark ? new ColorRgb(0xFF, 0xFF, 0xFF) : new ColorRgb(0x00, 0x00, 0x00);
        var adjusted = accent;
        var step = 0;
        while (palette.Surfaces.Min(s => ColorRgb.Contrast(adjusted, s)) < MinAccentContrast && step < 12)
        {
            step++;
            adjusted = accent.Mix(target, step * 0.05);
        }

        if (palette.Surfaces.Min(s => ColorRgb.Contrast(adjusted, s)) < MinAccentContrast)
        {
            return (null, $"This colour cannot be made readable on the {(palette.IsDark ? "dark" : "light")} theme. Choose a {(palette.IsDark ? "lighter" : "darker")} shade.");
        }

        var black = new ColorRgb(0x0E, 0x11, 0x16);
        var white = new ColorRgb(0xFF, 0xFF, 0xFF);
        var onAccent = ColorRgb.Contrast(adjusted, black) >= ColorRgb.Contrast(adjusted, white) ? black : white;
        return ColorRgb.Contrast(adjusted, onAccent) < MinTextContrast
            ? (null, "No text colour reads clearly on this accent. Choose a lighter or darker shade.")
            : (new ResolvedAccent(adjusted, onAccent, step > 0), null);
    }

    /// <summary>Applies an optional surface tint (blended 12%) and verifies text contrast is preserved.</summary>
    public static (ThemePalette Palette, string? Problem) ApplyTint(ThemePalette palette, string? tintHex)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (string.IsNullOrEmpty(tintHex))
        {
            return (palette, null);
        }

        if (!ColorRgb.TryParse(tintHex, out var tint))
        {
            return (palette, "Enter the tint as #RRGGBB.");
        }

        var tinted = palette with
        {
            Bg = palette.Bg.Mix(tint, 0.12),
            Surface = palette.Surface.Mix(tint, 0.12),
            SurfaceRaised = palette.SurfaceRaised.Mix(tint, 0.12),
            SurfaceHover = palette.SurfaceHover.Mix(tint, 0.12),
        };
        var textOk = tinted.Surfaces.All(s => ColorRgb.Contrast(tinted.Text, s) >= MinTextContrast && ColorRgb.Contrast(tinted.TextMuted, s) >= MinTextContrast);
        var bordersOk = tinted.Surfaces.All(s => ColorRgb.Contrast(tinted.BorderStrong, s) >= MinAccentContrast);
        return textOk && bordersOk ? (tinted, null) : (palette, "This tint makes text or control borders too hard to read; it was not applied.");
    }
}
