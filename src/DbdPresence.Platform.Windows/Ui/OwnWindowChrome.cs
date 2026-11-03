using System.Runtime.InteropServices;

namespace DbdPresence.Platform.Windows.Ui;

/// <summary>
/// Title-bar styling for the app's <b>own</b> windows (SAFETY_AND_PRIVACY §2.2). The handle is checked to belong
/// to this process before any attribute is set, so this can never touch another program's window.
/// </summary>
public static partial class OwnWindowChrome
{
    private const uint DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Asks Windows to draw the title bar dark or light. Returns false when unsupported or not our window.</summary>
    public static bool TrySetDarkTitleBar(nint windowHandle, bool dark)
    {
        if (windowHandle == 0 || !IsOwnWindow(windowHandle))
        {
            return false;
        }

        var value = dark ? 1 : 0;
        return DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) == 0;
    }

    internal static bool IsOwnWindow(nint windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId != 0 && processId == (uint)Environment.ProcessId;
    }

    [LibraryImport("dwmapi")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);

    [LibraryImport("user32")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
