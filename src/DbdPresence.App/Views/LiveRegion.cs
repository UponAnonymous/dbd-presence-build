using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DbdPresence.App.Views;

/// <summary>
/// Makes a TextBlock a polite live region (UI_SPEC §7): screen readers announce its text when it changes. WPF only
/// announces when the element's peer raises <see cref="AutomationEvents.LiveRegionChanged"/>, which this does.
/// </summary>
public static class LiveRegion
{
    /// <summary>Turns announcements on for a TextBlock.</summary>
    public static readonly DependencyProperty AnnounceProperty = DependencyProperty.RegisterAttached(
        "Announce", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false, OnAnnounceChanged));

    private static readonly DependencyPropertyDescriptor s_text =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    /// <summary>Gets whether announcements are on.</summary>
    public static bool GetAnnounce(DependencyObject element) => (bool)element.GetValue(AnnounceProperty);

    /// <summary>Sets whether announcements are on.</summary>
    public static void SetAnnounce(DependencyObject element, bool value) => element.SetValue(AnnounceProperty, value);

    private static void OnAnnounceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || e.NewValue is not true)
        {
            return;
        }

        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        text.Loaded += (_, _) => s_text.AddValueChanged(text, OnTextChanged);
        text.Unloaded += (_, _) => s_text.RemoveValueChanged(text, OnTextChanged);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock text && text.IsVisible)
        {
            UIElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
