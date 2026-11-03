using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DbdPresence.App.Views;

/// <summary>
/// For text boxes that save when focus leaves (so a half-typed value is never applied), pressing Enter also saves.
/// </summary>
public static class CommitOnEnter
{
    /// <summary>Turns Enter-to-save on.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CommitOnEnter), new PropertyMetadata(false, OnChanged));

    /// <summary>Gets whether Enter saves.</summary>
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Sets whether Enter saves.</summary>
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox box && e.NewValue is true)
        {
            box.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    args.Handled = true;
                }
            };
        }
    }
}
