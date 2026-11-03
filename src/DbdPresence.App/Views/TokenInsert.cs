using System.Windows;
using System.Windows.Controls;

namespace DbdPresence.App.Views;

/// <summary>
/// Remembers the template text box that last had focus so a token chip can insert "{token}" at its caret
/// (UI_SPEC §3 token palette). Purely a view concern; the view-model sees an ordinary text change.
/// </summary>
public static class TokenInsert
{
    /// <summary>Marks a text box as a token target.</summary>
    public static readonly DependencyProperty IsTargetProperty = DependencyProperty.RegisterAttached(
        "IsTarget", typeof(bool), typeof(TokenInsert), new PropertyMetadata(false, OnIsTargetChanged));

    private static WeakReference<TextBox>? s_last;

    /// <summary>Gets whether the element is a token target.</summary>
    public static bool GetIsTarget(DependencyObject element) => (bool)element.GetValue(IsTargetProperty);

    /// <summary>Sets whether the element is a token target.</summary>
    public static void SetIsTarget(DependencyObject element, bool value) => element.SetValue(IsTargetProperty, value);

    /// <summary>Inserts <paramref name="token"/> into the last focused target; returns false when there is none.</summary>
    public static bool Insert(string token)
    {
        if (s_last is null || !s_last.TryGetTarget(out var box) || !box.IsVisible)
        {
            return false;
        }

        var caret = box.CaretIndex;
        box.SelectedText = token;
        box.CaretIndex = caret + token.Length;
        box.SelectionLength = 0;
        box.Focus();
        return true;
    }

    private static void OnIsTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox box && e.NewValue is true)
        {
            box.GotKeyboardFocus += (_, _) => s_last = new WeakReference<TextBox>(box);
        }
    }
}
