using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DbdPresence.App.Views;

/// <summary>
/// Lets the themed Expander itself be the keyboard stop: Space or Enter toggles it while it has focus. The Expander keeps its stock
/// automation peer, so a screen reader announces its name and its Expanded or Collapsed state (ExpandCollapse pattern), and Left/Right arrows
/// collapse and expand it. (The header button inside the template is not focusable, so the focused element and the announced element are the same.)
/// </summary>
public static class ExpanderKeyboard
{
    /// <summary>Turns the keyboard handling on for an Expander.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ExpanderKeyboard), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Gets whether the keyboard handling is on.</summary>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Sets whether the keyboard handling is on.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander)
        {
            return;
        }

        expander.KeyDown -= OnKeyDown;
        if (e.NewValue is true)
        {
            expander.KeyDown += OnKeyDown;
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Only keys pressed on the Expander itself; keys from controls inside the expanded body are theirs.
        if (sender is not Expander expander || !ReferenceEquals(e.OriginalSource, expander))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space or Key.Enter:
                expander.IsExpanded = !expander.IsExpanded;
                e.Handled = true;
                break;
            case Key.Right when !expander.IsExpanded:
                expander.IsExpanded = true;
                e.Handled = true;
                break;
            case Key.Left when expander.IsExpanded:
                expander.IsExpanded = false;
                e.Handled = true;
                break;
        }
    }
}
