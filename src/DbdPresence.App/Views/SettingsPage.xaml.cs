using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.App.Views;

/// <summary>
/// Settings view. Code-behind covers three view concerns only: scrolling to a section (the in-page index and links from Home), Escape cancelling the
/// screen reading confirmation, and the two About buttons that act on the app window (run setup again, quit).
/// </summary>
public partial class SettingsPage : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="SettingsPage"/> class.</summary>
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel old)
        {
            old.SectionRequested -= OnSectionRequested;
        }

        if (e.NewValue is SettingsViewModel current)
        {
            current.SectionRequested += OnSectionRequested;
        }
    }

    private void OnSectionRequested(object? sender, SettingsSection section) =>
        _ = Dispatcher.BeginInvoke(() => Target(section).BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);

    private Border Target(SettingsSection section) => section switch
    {
        SettingsSection.ScreenReading => SectionScreenReading,
        SettingsSection.Look => SectionLook,
        SettingsSection.StartAndClose => SectionStartAndClose,
        SettingsSection.DiscordConnection => SectionDiscordConnection,
        SettingsSection.Advanced => SectionAdvanced,
        _ => SectionAbout,
    };

    private void OnIndexClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse<SettingsSection>(name, out var section))
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.Show(section);
            }
        }
    }

    private void OnSheetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsViewModel vm)
        {
            vm.ScreenReading.CancelTurnOnCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRunSetup(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenFirstRun();

    private void OnQuit(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.RequestQuit();
}
