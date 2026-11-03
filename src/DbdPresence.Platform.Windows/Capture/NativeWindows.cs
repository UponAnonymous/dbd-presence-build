using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DbdPresence.Core.Capture;

namespace DbdPresence.Platform.Windows.Capture;

/// <summary>Window metadata for one top-level window (SAFETY_AND_PRIVACY §2.1 "Top-level windows of the PID").</summary>
/// <param name="Handle">The window handle (never used to send messages or change the window).</param>
/// <param name="Frame">Visible frame bounds (excludes the invisible resize border and shadow).</param>
/// <param name="Client">Client area in screen coordinates.</param>
/// <param name="Monitor">Bounds of the monitor the window is mostly on.</param>
/// <param name="Dpi">DPI of the window.</param>
/// <param name="IsMinimized">Whether the window is minimised.</param>
internal sealed record WindowInfo(nint Handle, ScreenRect Frame, ScreenRect Client, ScreenRect Monitor, uint Dpi, bool IsMinimized)
{
    public WindowDisplayMode DisplayMode => WindowModeClassifier.Classify(Frame, Client, Monitor);
}

/// <summary>
/// Read-only window metadata queries. Every call here only reads; nothing sends a message to, moves or changes a window,
/// and no process handle is opened (the allow-listed entry points are listed in the architecture tests).
/// </summary>
internal static partial class NativeWindows
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Finds the largest visible, uncloaked top-level window owned by <paramref name="processId"/>, or null.</summary>
    public static unsafe WindowInfo? FindLargestWindow(int processId)
    {
        var candidates = new List<nint>();
        var handle = GCHandle.Alloc((processId, candidates));
        try
        {
            EnumWindows(&CollectWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        WindowInfo? best = null;
        long bestArea = -1;
        foreach (var window in candidates)
        {
            var info = Describe(window);
            if (info is null)
            {
                continue;
            }

            var area = info.IsMinimized ? 0 : (long)info.Frame.Width * info.Frame.Height;
            if (area > bestArea)
            {
                best = info;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>Reads current metadata for a window, or null when the window no longer exists.</summary>
    public static WindowInfo? Describe(nint window)
    {
        if (!GetWindowRect(window, out var outer))
        {
            return null;
        }

        var minimized = IsIconic(window);
        var frame = DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out Rect bounds, Unsafe.SizeOf<Rect>()) == 0
            ? bounds.ToScreenRect()
            : outer.ToScreenRect();

        var client = default(ScreenRect);
        if (!minimized && GetClientRect(window, out var clientRect))
        {
            var origin = new Point { X = clientRect.Left, Y = clientRect.Top };
            if (ClientToScreen(window, ref origin))
            {
                client = new ScreenRect(origin.X, origin.Y, origin.X + clientRect.Right - clientRect.Left, origin.Y + clientRect.Bottom - clientRect.Top);
            }
        }

        var monitor = default(ScreenRect);
        var monitorHandle = MonitorFromWindow(window, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Unsafe.SizeOf<MonitorInfo>() };
        if (monitorHandle != 0 && GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            monitor = monitorInfo.Monitor.ToScreenRect();
        }

        return new WindowInfo(window, frame, client, monitor, GetDpiForWindow(window), minimized);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectWindow(nint window, nint state)
    {
        var (processId, list) = ((int, List<nint>))GCHandle.FromIntPtr(state).Target!;
        if (IsWindowVisible(window)
            && GetWindowThreadProcessId(window, out var owner) != 0
            && owner == (uint)processId
            && DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0
            && cloaked == 0)
        {
            list.Add(window);
        }

        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly ScreenRect ToScreenRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
