using System.Windows;
using System.Windows.Controls;

namespace DbdPresence.App.Views;

/// <summary>
/// Two columns that stack when the area is narrower than <see cref="StackBelowProperty"/> (design §3.1, §4.1, §15: every row wraps at 150 % text, the
/// two-column areas stack). The grid has exactly two children, three columns (first, gap, second) and two rows; wide, both sit in row 0; narrow, both
/// span all columns, one under the other. <see cref="ReverseWhenStackedProperty"/> puts the second child on top when stacked (the preview column).
/// A layout concern only, so it lives in the view.
/// </summary>
public static class ResponsiveColumns
{
    /// <summary>The width below which the columns stack.</summary>
    public static readonly DependencyProperty StackBelowProperty =
        DependencyProperty.RegisterAttached("StackBelow", typeof(double), typeof(ResponsiveColumns), new PropertyMetadata(0.0, OnChanged));

    /// <summary>Whether the second child goes on top when stacked.</summary>
    public static readonly DependencyProperty ReverseWhenStackedProperty =
        DependencyProperty.RegisterAttached("ReverseWhenStacked", typeof(bool), typeof(ResponsiveColumns), new PropertyMetadata(false));

    /// <summary>The minimum height of the grid while its columns are stacked (both parts need room); 0 leaves the rows and the height alone.</summary>
    public static readonly DependencyProperty StackedMinHeightProperty =
        DependencyProperty.RegisterAttached("StackedMinHeight", typeof(double), typeof(ResponsiveColumns), new PropertyMetadata(0.0));

    /// <summary>Gets the minimum height while stacked.</summary>
    public static double GetStackedMinHeight(DependencyObject element) => (double)element.GetValue(StackedMinHeightProperty);

    /// <summary>Sets the minimum height while stacked.</summary>
    public static void SetStackedMinHeight(DependencyObject element, double value) => element.SetValue(StackedMinHeightProperty, value);

    /// <summary>Gets the stacking width.</summary>
    public static double GetStackBelow(DependencyObject element) => (double)element.GetValue(StackBelowProperty);

    /// <summary>Sets the stacking width.</summary>
    public static void SetStackBelow(DependencyObject element, double value) => element.SetValue(StackBelowProperty, value);

    /// <summary>Gets whether the second child goes on top when stacked.</summary>
    public static bool GetReverseWhenStacked(DependencyObject element) => (bool)element.GetValue(ReverseWhenStackedProperty);

    /// <summary>Sets whether the second child goes on top when stacked.</summary>
    public static void SetReverseWhenStacked(DependencyObject element, bool value) => element.SetValue(ReverseWhenStackedProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Grid grid)
        {
            grid.SizeChanged -= OnSizeChanged;
            grid.SizeChanged += OnSizeChanged;
            Arrange(grid);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Arrange((Grid)sender);

    private static void Arrange(Grid grid)
    {
        if (grid.Children.Count != 2 || grid.ActualWidth <= 0)
        {
            return;
        }

        var stacked = grid.ActualWidth < GetStackBelow(grid);
        var first = grid.Children[0];
        var second = grid.Children[1];
        var reverse = GetReverseWhenStacked(grid);
        var top = stacked && reverse ? second : first;
        var bottom = stacked && reverse ? first : second;
        if (grid.RowDefinitions.Count == 2 && GetStackedMinHeight(grid) > 0)
        {
            // Wide: the first row fills the height and the second stays empty. Stacked: the top part takes what it needs and the bottom part fills the rest.
            grid.RowDefinitions[0].Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            grid.MinHeight = stacked ? GetStackedMinHeight(grid) : 0;
        }

        if (stacked)
        {
            Place(top, row: 0, column: 0, span: 3);
            Place(bottom, row: 1, column: 0, span: 3);
            bottom.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 16, 0, 0));
            top.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        }
        else
        {
            Place(first, row: 0, column: 0, span: 1);
            Place(second, row: 0, column: 2, span: 1);
            first.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            second.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        }
    }

    private static void Place(UIElement element, int row, int column, int span)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
    }
}
