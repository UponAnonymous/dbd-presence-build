using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace DbdPresence.Platform.Windows.Capture;

/// <summary>
/// The two interop seams Windows.Graphics.Capture needs and the projection does not provide (ADR-0003 addendum):
/// a Direct3D 11 device wrapped for WinRT, and <c>IGraphicsCaptureItemInterop::CreateForWindow</c>. Both are declared once, here,
/// and listed by name in the architecture tests. The classic COM interface has no function-pointer calls, which the
/// architecture tests forbid (<c>calli</c>).
/// </summary>
internal static partial class CaptureInterop
{
    private const uint DriverTypeHardware = 1;
    private const uint DriverTypeWarp = 5;
    private const uint CreateBgraSupport = 0x20;
    private const uint SdkVersion = 7;

    private static readonly Guid s_dxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid s_captureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>Creates a Direct3D device for capture; falls back to the software rasteriser when no GPU is available.</summary>
    public static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(0, DriverTypeHardware, 0, CreateBgraSupport, 0, 0, SdkVersion, out var native, 0, 0);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(0, DriverTypeWarp, 0, CreateBgraSupport, 0, 0, SdkVersion, out native, 0, 0);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in s_dxgiDevice, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    /// <summary>Creates a capture item for one window. The window handle is only passed to the OS capture API.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(nint window)
    {
        using var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
        var iid = s_captureItem;
        var pointer = interop.CreateForWindow(window, ref iid);
        return GraphicsCaptureItem.FromAbi(pointer);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);

        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, nint achievedLevel, nint immediateContext);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
