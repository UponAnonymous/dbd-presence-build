using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.App.Views;

/// <summary>Maps a status tone to a Segoe Fluent glyph (shown next to the text, so colour is never the only signal).</summary>
public sealed class ToneGlyphConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusTone.Good => "",      // CheckMark
        StatusTone.Caution => "",   // Warning
        StatusTone.Problem => "",   // ErrorBadge
        FieldSeverity.Error => "",
        FieldSeverity.Warning => "",
        _ => "",                    // Info
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps a status tone or field severity to a theme brush resource.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusTone.Good => "SuccessBrush",
            StatusTone.Caution or FieldSeverity.Warning => "WarningBrush",
            StatusTone.Problem or FieldSeverity.Error => "DangerBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current?.TryFindResource(key);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapses an element when the bound value is null, empty, false or zero; ConverterParameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            FieldSeverity f => f != FieldSeverity.None,
            _ => true,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.Ordinal))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound enum value equals the ConverterParameter (by name); used to show one first-run step.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (match ? Visibility.Visible : Visibility.Collapsed) : match;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Parses a #RRGGBB string to a brush for colour swatches (invalid text shows nothing).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && Presentation.Theming.ColorRgb.TryParse(hex.Trim(), out var c))
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        return System.Windows.Media.Brushes.Transparent;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps a status tone to the soft fill of the status headline (design §9.9): a tint of the tone colour, or the plain surface when neutral.</summary>
public sealed class ToneSoftBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusTone.Good => "SuccessSoftBrush",
            StatusTone.Caution => "WarningSoftBrush",
            StatusTone.Problem => "DangerSoftBrush",
            _ => "SurfaceBrush",
        };
        return Application.Current?.TryFindResource(key);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The one table that maps an icon name from the view-models to a Segoe Fluent / MDL2 glyph (design §9.5: one glyph, one meaning). The code points are
/// the same in both fonts; how they look on Windows 10 and 11 is PENDING-ENV until the Windows UI job captures them.
/// </summary>
public sealed class IconGlyphConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "Progress" => "\uE895",
        "Blocked" => "\uE733",
        "Error" => "\uE783",
        "Pause" => "\uE769",
        "Warning" => "\uE7BA",
        "Plug" => "\uE839",
        "Moon" => "\uE708",
        "Check" => "\uE73E",
        _ => "\uE946",
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the two bound values are equal (a nullable enum choice against a chip's own value); used for radio chips.</summary>
public sealed class ValuesEqualConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: 2 } && values[0] is not null && values[0] != DependencyProperty.UnsetValue && Equals(values[0], values[1]);

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Flips a boolean (the "Show nothing" radio button is the opposite of "Show that I'm playing").</summary>
public sealed class BoolNotConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Two-way choice of an enum value for a radio button: true when the value's name equals the ConverterParameter; checking the button sets it.</summary>
public sealed class EnumChoiceConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name && Enum.TryParse(targetType, name, out var parsed) ? parsed : Binding.DoNothing;
}

/// <summary>
/// The height left for a page body that fills the window: the viewport height minus the height of what sits above it and the page margins, but never
/// less than the ConverterParameter (the body's minimum). Past the minimum the page scrolls as a whole instead (design §5.1).
/// </summary>
public sealed class FillHeightConverter : IMultiValueConverter
{
    private const double PageMargins = 24 + 32;

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var minimum = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        if (values.Length < 2 || values[0] is not double viewport || values[1] is not double above || double.IsNaN(viewport) || double.IsNaN(above))
        {
            return minimum;
        }

        return Math.Max(minimum, viewport - above - PageMargins);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
