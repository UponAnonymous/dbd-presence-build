using System.Windows.Controls;
using System.Windows.Input;

namespace DbdPresence.App.Views;

/// <summary>View for the Characters page view-model; binding only. Ctrl+F moves focus to the search box.</summary>
public partial class CharactersPage : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="CharactersPage"/> class.</summary>
    public CharactersPage()
    {
        InitializeComponent();
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        _ = SearchInput.Focus();
        SearchInput.SelectAll();
    }
}
