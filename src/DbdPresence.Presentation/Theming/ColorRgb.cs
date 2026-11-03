using System.Globalization;

namespace DbdPresence.Presentation.Theming;

/// <summary>An sRGB colour with WCAG 2.x luminance and contrast maths (NFR-A11Y-02).</summary>
public readonly record struct ColorRgb(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c>.</summary>
    public static ColorRgb Parse(string hex)
    {
        if (!TryParse(hex, out var color))
        {
            throw new FormatException($"'{hex}' is not a #RRGGBB colour.");
        }

        return color;
    }

    /// <summary>Tries to parse <c>#RRGGBB</c>.</summary>
    public static bool TryParse(string? hex, out ColorRgb color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new ColorRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Gets the WCAG relative luminance (0–1).</summary>
    public double RelativeLuminance => (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));

    /// <summary>Returns the WCAG contrast ratio (1–21) between two colours.</summary>
    public static double Contrast(ColorRgb a, ColorRgb b)
    {
        var (lighter, darker) = a.RelativeLuminance >= b.RelativeLuminance
            ? (a.RelativeLuminance, b.RelativeLuminance)
            : (b.RelativeLuminance, a.RelativeLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Linear interpolation towards <paramref name="other"/> (0 = this, 1 = other).</summary>
    public ColorRgb Mix(ColorRgb other, double amount)
    {
        static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + ((to - from) * t));
        var t = Math.Clamp(amount, 0, 1);
        return new ColorRgb(Lerp(R, other.R, t), Lerp(G, other.G, t), Lerp(B, other.B, t));
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
