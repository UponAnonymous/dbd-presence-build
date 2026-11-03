namespace DbdPresence.Configuration;

/// <summary>Where user data lives (ARCHITECTURE §8). Outside the install folder, so updates never touch it.</summary>
public sealed record SettingsPaths(string SettingsDirectory, string DataDirectory)
{
    /// <summary>Gets the settings file path.</summary>
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Gets the log directory.</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Gets the dataset directory (opt-in recordings only).</summary>
    public string DatasetDirectory => Path.Combine(DataDirectory, "datasets");

    /// <summary>The per-user default locations: %APPDATA%\DBDPresence and %LOCALAPPDATA%\DBDPresence.Data.</summary>
    public static SettingsPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DBDPresence"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBDPresence.Data"));
}
