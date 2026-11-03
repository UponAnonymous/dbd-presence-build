using System.Windows;
using System.Windows.Controls;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.App.Views;

/// <summary>The Advanced text editor of Discord status. Code-behind only inserts a detail at the caret (a view concern).</summary>
public partial class StudioAdvancedView : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="StudioAdvancedView"/> class.</summary>
    public StudioAdvancedView()
    {
        InitializeComponent();
    }

    private void OnTokenClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TokenChip chip } && !TokenInsert.Insert(chip.Token) && DataContext is DiscordStatusViewModel status)
        {
            // No field focused yet: append to the top line.
            status.Studio.Details.Text += chip.Token;
        }
    }
}
