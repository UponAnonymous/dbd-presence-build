using Microsoft.Win32;

namespace DbdPresence.Platform.Windows.Startup;

/// <summary>
/// Per-user "start with Windows" through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// (FR-LIFE-01; FEASIBILITY T12). Opt-in only. The value points to a stable launcher path and is removed
/// automatically when it points to a file that no longer exists (ARCHITECTURE §8).
/// </summary>
public sealed class AutostartRegistry
{
    /// <summary>The command-line switch passed when started by Windows.</summary>
    public const string AutostartArgument = "--autostart";

    private const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _keyPath;
    private readonly string _valueName;

    /// <summary>Initializes a new instance of the <see cref="AutostartRegistry"/> class.</summary>
    /// <param name="valueName">Registry value name.</param>
    /// <param name="keyPath">Key under HKCU (tests use a scratch key).</param>
    public AutostartRegistry(string valueName = "DBD Presence", string keyPath = DefaultRunKey)
    {
        _valueName = valueName;
        _keyPath = keyPath;
    }

    /// <summary>Returns the executable path currently registered, or null.</summary>
    public string? RegisteredExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(_valueName) is string command ? ExecutableFrom(command) : null;
    }

    /// <summary>Registers <paramref name="executablePath"/> to start at logon.</summary>
    public void Enable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(_valueName, $"\"{executablePath}\" {AutostartArgument}", RegistryValueKind.String);
    }

    /// <summary>Removes the registration if present.</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Makes the registry match the setting. Returns true when a stale entry (missing target) was removed.
    /// </summary>
    public bool Reconcile(bool shouldStartWithWindows, string executablePath)
    {
        var registered = RegisteredExecutable();
        var stale = registered is not null && !File.Exists(registered);
        if (!shouldStartWithWindows || stale)
        {
            Disable();
        }

        if (shouldStartWithWindows && (registered is null || stale || !string.Equals(registered, executablePath, StringComparison.OrdinalIgnoreCase)))
        {
            Enable(executablePath);
        }

        return stale;
    }

    private static string ExecutableFrom(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            return close > 0 ? trimmed[1..close] : trimmed.Trim('"');
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
