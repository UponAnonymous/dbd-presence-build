using System.IO;
using DbdPresence.Configuration;

namespace DbdPresence.App.Composition;

/// <summary>Resolves where settings and data live.</summary>
internal static class AppPaths
{
    /// <summary>
    /// Environment variable that redirects all settings and data into one folder. UI tests use it so they never
    /// touch the real profile; it is documented in ARCHITECTURE §8.
    /// </summary>
    public const string DataRootVariable = "DBDP_DATA_ROOT";

    public static SettingsPaths Resolve()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return string.IsNullOrWhiteSpace(root)
            ? SettingsPaths.ForCurrentUser()
            : new SettingsPaths(Path.Combine(Path.GetFullPath(root), "settings"), Path.Combine(Path.GetFullPath(root), "data"));
    }
}
