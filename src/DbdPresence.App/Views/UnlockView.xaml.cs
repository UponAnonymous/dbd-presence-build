using System.Windows.Controls;

namespace DbdPresence.App.Views;

/// <summary>The lock screen (D32). Keyboard focus cycles inside it while it is shown.</summary>
public partial class UnlockView : UserControl
{
    /// <summary>Initializes a new instance of the <see cref="UnlockView"/> class.</summary>
    public UnlockView()
    {
        InitializeComponent();
    }

    /// <summary>Moves keyboard focus to the sign-in button when it is shown, otherwise to the first control.</summary>
    public void FocusPrimary()
    {
        if (!SignInButton.IsVisible || !SignInButton.Focus())
        {
            _ = MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        }
    }
}
