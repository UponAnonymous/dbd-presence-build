using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DbdPresence.App.Views;

/// <summary>Groups the items of a list by a property of the item (the Characters list groups tiles by role). A layout concern only, so it lives in the view.</summary>
public static class ListGrouping
{
    /// <summary>The name of the item property to group by.</summary>
    public static readonly DependencyProperty ByProperty =
        DependencyProperty.RegisterAttached("By", typeof(string), typeof(ListGrouping), new PropertyMetadata(null, OnChanged));

    /// <summary>Gets the group property name.</summary>
    public static string? GetBy(DependencyObject element) => (string?)element.GetValue(ByProperty);

    /// <summary>Sets the group property name.</summary>
    public static void SetBy(DependencyObject element, string? value) => element.SetValue(ByProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        list.Items.GroupDescriptions.Clear();
        if (e.NewValue is string { Length: > 0 } name)
        {
            list.Items.GroupDescriptions.Add(new PropertyGroupDescription(name));
        }
    }
}
