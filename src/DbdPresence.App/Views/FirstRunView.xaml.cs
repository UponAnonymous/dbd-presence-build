using System.Windows.Controls;

namespace DbdPresence.App.Views;

/// <summary>First-run overlay. Keyboard focus cycles inside it while it is open.</summary>
public partial class FirstRunView : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="FirstRunView"/> class.</summary>
    public FirstRunView()
    {
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus to the primary button.</summary>
    public void FocusPrimary() => NextButton.Focus();
}
