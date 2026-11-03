using Microsoft.Win32;

namespace DbdPresence.Platform.Windows.Ui;

/// <summary>Reads the Windows app colour mode (Settings → Personalisation → Colours).</summary>
public static class SystemThemeProbe
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Returns true when Windows apps use the dark mode. Missing or unreadable values mean light, which is the
    /// Windows default.
    /// </summary>
    public static bool AppsUseDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
