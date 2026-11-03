namespace DbdPresence.Core.Capture;

/// <summary>Screen rectangle in pixels (left, top, right, bottom, as Windows reports it).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the width.</summary>
    public int Width => Right - Left;

    /// <summary>Gets the height.</summary>
    public int Height => Bottom - Top;
}

/// <summary>Compares window and monitor bounds (display modes of PLAN_V2 D17; study metadata). Pure, so it is unit tested.</summary>
public static class WindowModeClassifier
{
    /// <summary>
    /// Classifies the window. <paramref name="client"/> is the client area in screen coordinates; a window whose client area
    /// is its whole outer rectangle has no frame.
    /// </summary>
    public static WindowDisplayMode Classify(ScreenRect window, ScreenRect client, ScreenRect monitor)
    {
        if (window.Width <= 0 || window.Height <= 0 || monitor.Width <= 0 || monitor.Height <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return WindowDisplayMode.Unknown;
        }

        return client == monitor && window == client ? WindowDisplayMode.FullscreenOrBorderless : WindowDisplayMode.Windowed;
    }
}
