using DbdPresence.Presentation.Theming;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>Runs work on the UI thread (the WPF dispatcher in the app; inline in tests).</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);
}

/// <summary>Start-with-Windows registration (FR-LIFE-01).</summary>
public interface IAutostart
{
    /// <summary>Gets a value indicating whether the app is registered to start with Windows.</summary>
    bool IsEnabled { get; }

    /// <summary>Registers or unregisters. Returns an error message, or null on success.</summary>
    string? SetEnabled(bool enabled);

    /// <summary>Gets a sentence shown under the switch that is specific to how the app was installed, or null for the default one.</summary>
    string? Caption => null;
}

/// <summary>Opens Windows' Installed apps page, where DBD Presence is uninstalled (all install kinds).</summary>
public interface IInstalledAppsLauncher
{
    /// <summary>Opens the page. Returns true when Windows opened it.</summary>
    /// <returns>True on success.</returns>
    Task<bool> OpenInstalledAppsAsync();
}

/// <summary>The theme currently applied by the app.</summary>
public interface IThemeService
{
    /// <summary>Raised on the UI thread after the theme changes.</summary>
    event EventHandler? ThemeChanged;

    /// <summary>Gets the applied theme.</summary>
    ResolvedTheme Current { get; }
}

/// <summary>Build facts shown on the About page.</summary>
/// <param name="Version">Product version.</param>
/// <param name="Commit">Source commit (short), or "unknown" for local builds.</param>
/// <param name="IsSigned">True only when the executable carries a real code-signing certificate.</param>
/// <param name="SettingsFolder">Where settings live.</param>
/// <param name="DataFolder">Where logs and local data live.</param>
/// <param name="PrivacyNoticePath">Where the full privacy notice file is installed (shown as text; the app never opens files or links itself).</param>
public sealed record AppInfo(string Version, string Commit, bool IsSigned, string SettingsFolder, string DataFolder, string PrivacyNoticePath = "");

/// <summary>A value with a display label, for option lists.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Label">The label shown to the user.</param>
public sealed record Choice<T>(T Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}
