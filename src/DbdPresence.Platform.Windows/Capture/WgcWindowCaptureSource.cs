using System.Runtime.InteropServices;
using DbdPresence.Core.Capture;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage.Streams;

namespace DbdPresence.Platform.Windows.Capture;

/// <summary>
/// Windows.Graphics.Capture of one window, found by its owning process id (ADR-0003, FR-CAP-06). Capture is passive: the only
/// things done to the target are a window-metadata lookup and the OS capture API; no process handle is opened here.
/// The frame pool is free-threaded; only the latest frame is kept and it is copied to the CPU when a sample is requested.
/// </summary>
public sealed class WgcWindowCaptureSource : IWindowCaptureSource
{
    private const int FramePoolBuffers = 2;

    /// <summary>How long the borderless request may take; Windows normally answers at once. Past this the border simply stays on.</summary>
    private static readonly TimeSpan s_borderRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly int _processId;
    private readonly TimeProvider _time;
    private readonly TimeSpan _frameTimeout;
    private readonly bool _requestBorderless;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _frameLock = new();

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private nint _window;
    private SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _latest;
    private Direct3D11CaptureFrame? _inUse;
    private TaskCompletionSource _frameArrived = NewSignal();
    private BorderOutcome _border = BorderOutcome.NotAttempted;
    private string _borderDetail = string.Empty;
    private volatile CaptureState _state = new(CaptureStatus.NotStarted, "Capture has not been started.");
    private long _contentSize;
    private long _framesArrived;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="WgcWindowCaptureSource"/> class.</summary>
    /// <param name="processId">Owning process id of the window to capture (looked up through the game-process detection).</param>
    /// <param name="time">Time source for timestamps and timeouts.</param>
    /// <param name="frameTimeout">How long to wait for a first frame; defaults to 3 seconds.</param>
    /// <param name="requestBorderless">
    /// Ask Windows to leave out the yellow capture border (ADR-0003 update 2026-11-02, owner request). The app and the study tool pass
    /// true; Windows decides (Settings, Privacy &amp; security, Screenshot borders) and the outcome is in <see cref="CaptureState.Border"/>.
    /// Below Windows 10.0.20348 the border cannot be turned off. Off by default so a caller has to ask for it.
    /// </param>
    public WgcWindowCaptureSource(int processId, TimeProvider time, TimeSpan? frameTimeout = null, bool requestBorderless = false)
    {
        _processId = processId;
        _time = time;
        _frameTimeout = frameTimeout ?? TimeSpan.FromSeconds(3);
        _requestBorderless = requestBorderless;
    }

    /// <summary>Gets the detail text of the border request (empty when none was made).</summary>
    public string BorderDetail => _borderDetail;

    /// <inheritdoc />
    public CaptureState State => _state;

    /// <summary>Gets the pixel size of the newest delivered frame (0 x 0 before the first), without reading any pixels.</summary>
    public (int Width, int Height) LatestContentSize
    {
        get
        {
            var packed = Interlocked.Read(ref _contentSize);
            return ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
        }
    }

    /// <summary>Gets how many frames Windows has delivered to this source since it was created (a counter for measurements only).</summary>
    internal long FramesArrived => Interlocked.Read(ref _framesArrived);

    /// <inheritdoc />
    public async Task<CaptureState> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await StartLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CaptureSample> CaptureAsync(IReadOnlyList<NormalizedRegion> regions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regions);
        foreach (var region in regions)
        {
            if (!region.IsValid)
            {
                throw new ArgumentException("Every region must be inside the unit square.", nameof(regions));
            }
        }

        var (state, frame, at) = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return new CaptureSample(state, [], at);
        }

        var crops = regions.Select(r => frame.Crop(r.ToPixels(frame.Width, frame.Height))).ToArray();
        return new CaptureSample(state, crops, at);
    }

    /// <inheritdoc />
    public async Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken)
    {
        var (state, frame, at) = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        return new FullFrameSample(state, frame, at);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Teardown();
            _state = _state with { Status = CaptureStatus.Stopped, Detail = "Capture was stopped." };
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CaptureState Failed(CaptureStatus status, string detail, WindowInfo? window = null, BorderOutcome border = BorderOutcome.NotAttempted) =>
        new(status, detail, 0, 0, window?.Dpi ?? 0, window?.DisplayMode ?? WindowDisplayMode.Unknown, border);

    private async Task<CaptureState> StartLockedAsync(CancellationToken cancellationToken)
    {
        Teardown();
        if (!GraphicsCaptureSession.IsSupported())
        {
            return _state = Failed(CaptureStatus.Unsupported, "Windows.Graphics.Capture is not supported on this system.");
        }

        var window = NativeWindows.FindLargestWindow(_processId);
        if (window is null)
        {
            return _state = Failed(CaptureStatus.WindowNotFound, "No visible window of the game was found.");
        }

        if (window.IsMinimized)
        {
            return _state = Failed(CaptureStatus.Minimized, "The game window is minimised; Windows delivers no content until it is restored.", window);
        }

        try
        {
            _window = window.Handle;
            _item = CaptureInterop.CreateItemForWindow(window.Handle);
            _item.Closed += OnItemClosed;
            _device = CaptureInterop.CreateDevice();
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBuffers, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            await ApplyBorderRequestAsync(_session).ConfigureAwait(false);
            _session.StartCapture();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Teardown();
            return _state = Failed(
                CaptureStatus.Blocked,
                $"Windows would not start capturing this window ({ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}): {ex.Message}",
                window,
                _border);
        }

        return _state = await EvaluateAsync(window, cancellationToken).ConfigureAwait(false) ?? _state;
    }

    private async Task ApplyBorderRequestAsync(GraphicsCaptureSession session)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            _border = BorderOutcome.NotAvailable;
            _borderDetail = "IsBorderRequired needs Windows 10.0.20348 or later; the capture border stays on.";
            return;
        }

        if (!_requestBorderless)
        {
            _border = BorderOutcome.Required;
            _borderDetail = "The border was not requested to be turned off.";
            return;
        }

        // Asked once per source: a restart of the session (resize, window lost and found) reuses the answer, so the wait below happens at most once.
        if (_border is BorderOutcome.Disabled or BorderOutcome.Refused)
        {
            if (_border == BorderOutcome.Disabled)
            {
                session.IsBorderRequired = false;
            }

            return;
        }

        using var timeout = new CancellationTokenSource();
        try
        {
            // Windows answers from the owner's setting (Settings, Privacy & security, Screenshot borders; for an unpackaged program the
            // "desktop apps" switch). The capture must never wait long on it, so after a few seconds the border simply stays on.
            var request = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            if (await Task.WhenAny(request, Task.Delay(s_borderRequestTimeout, _time, timeout.Token)).ConfigureAwait(false) != request)
            {
                _ = request.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _border = BorderOutcome.Refused;
                _borderDetail = "Windows did not answer the borderless capture request in time; the border stays on.";
                return;
            }

            await timeout.CancelAsync().ConfigureAwait(false);
            var access = await request.ConfigureAwait(false);
            if (access != AppCapabilityAccessStatus.Allowed)
            {
                _border = BorderOutcome.Refused;
                _borderDetail = $"Windows answered {access} to the borderless capture request; the border stays on. To hide it, allow desktop apps to turn off the screenshot border in Windows Settings (Privacy & security, Screenshot borders).";
                return;
            }

            session.IsBorderRequired = false;
            _border = BorderOutcome.Disabled;
            _borderDetail = "Windows allowed borderless capture.";
        }
#pragma warning disable CA1031 // Border boundary: whatever the optional borderless request throws leaves the border on and never stops the capture itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _border = BorderOutcome.Refused;
            _borderDetail = $"The borderless request failed ({ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}); the border stays on.";
        }
    }

    /// <summary>Waits for a frame and reports whether it holds content. Returns null only if nothing changed the state.</summary>
    private async Task<CaptureState?> EvaluateAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (!await WaitForFrameAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failed(
                CaptureStatus.Blocked,
                "The capture session started but Windows delivered no frame in time (the window may be protected or not yet drawn).",
                window,
                _border);
        }

        var (state, _, _) = await ReadFrameLockedAsync(window, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async Task<bool> WaitForFrameAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (_frameLock)
        {
            if (_latest is not null)
            {
                return true;
            }

            signal = _frameArrived.Task;
        }

        var completed = await Task.WhenAny(signal, Task.Delay(_frameTimeout, _time, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return completed == signal;
    }

    private async Task<(CaptureState State, PixelBuffer? Frame, DateTimeOffset At)> ReadFrameAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var window = _window == 0 ? null : NativeWindows.Describe(_window);
            if (window is null || _session is null)
            {
                var restarted = await StartLockedAsync(cancellationToken).ConfigureAwait(false);
                if (!restarted.IsReady)
                {
                    return (restarted, null, _time.GetUtcNow());
                }

                window = NativeWindows.Describe(_window);
                if (window is null)
                {
                    return (_state = Failed(CaptureStatus.WindowNotFound, "The game window disappeared."), null, _time.GetUtcNow());
                }
            }

            if (window.IsMinimized)
            {
                return (_state = Failed(CaptureStatus.Minimized, "The game window is minimised.", window, _border), null, _time.GetUtcNow());
            }

            if (!await WaitForFrameAsync(cancellationToken).ConfigureAwait(false))
            {
                return (_state = Failed(CaptureStatus.Blocked, "Windows delivered no frame in time.", window, _border), null, _time.GetUtcNow());
            }

            return await ReadFrameLockedAsync(window, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(CaptureState State, PixelBuffer? Frame, DateTimeOffset At)> ReadFrameLockedAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        Direct3D11CaptureFrame frame;
        lock (_frameLock)
        {
            if (_latest is null)
            {
                return (_state = Failed(CaptureStatus.Blocked, "No frame is available.", window, _border), null, _time.GetUtcNow());
            }

            frame = _latest;
            _inUse = frame;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = frame.ContentSize;
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken).ConfigureAwait(false);
            var pixels = ToPixelBuffer(bitmap, content.Width, content.Height);
            var at = _time.GetUtcNow();
            if (!TryCutToClientArea(window, ref pixels))
            {
                return (_state = Failed(
                    CaptureStatus.Blocked,
                    "The game window's client area could not be worked out from its bounds, so the frame is not used (title bar and border would shift every region).",
                    window,
                    _border), null, at);
            }

            var detail = pixels.IsEffectivelyBlack()
                ? "Frames arrive but are entirely black. Exclusive fullscreen or protected content is possible; exclusive fullscreen support is UNKNOWN (ADR-0003)."
                : "Frames are being delivered.";
            var status = pixels.IsEffectivelyBlack() ? CaptureStatus.BlackFrames : CaptureStatus.Ready;
            _state = new CaptureState(status, detail, pixels.Width, pixels.Height, window.Dpi, window.DisplayMode, _border, ClientAreaOnly: true);
            return (_state, status == CaptureStatus.Ready ? pixels : null, at);
        }
        finally
        {
            lock (_frameLock)
            {
                // A frame that was replaced while it was being read is released here, by its reader.
                _inUse = null;
                if (!ReferenceEquals(frame, _latest))
                {
                    frame.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// A windowed game is captured with its title bar and border (the owner's recordings are 1922x1112 for a 1920x1080 game), which would
    /// shift every normalised region. The frame is cut to the client area from the window metadata the allow-list permits. A window with
    /// no frame (borderless or fullscreen) is already the client area. Returns false when the metadata and the frame disagree.
    /// </summary>
    private static bool TryCutToClientArea(WindowInfo window, ref PixelBuffer pixels)
    {
        if (window.DisplayMode == WindowDisplayMode.FullscreenOrBorderless)
        {
            return true;
        }

        if (ClientAreaCrop.Compute(window.Frame, window.Client, pixels.Width, pixels.Height) is not { } rect)
        {
            return false;
        }

        if (rect.X != 0 || rect.Y != 0 || rect.Width != pixels.Width || rect.Height != pixels.Height)
        {
            pixels = pixels.Crop(rect);
        }

        return true;
    }

    private static PixelBuffer ToPixelBuffer(SoftwareBitmap bitmap, int contentWidth, int contentHeight)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var length = width * height * PixelBuffer.BytesPerPixel;
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)length);
        bitmap.CopyToBuffer(buffer);
        var bytes = new byte[length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }

        var full = new PixelBuffer(width, height, bytes);
        var cropWidth = Math.Min(Math.Max(contentWidth, 1), width);
        var cropHeight = Math.Min(Math.Max(contentHeight, 1), height);
        return cropWidth == width && cropHeight == height ? full : full.Crop(new PixelRect(0, 0, cropWidth, cropHeight));
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        Interlocked.Increment(ref _framesArrived);
        var content = frame.ContentSize;
        Interlocked.Exchange(ref _contentSize, ((long)content.Width << 32) | (uint)content.Height);
        if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
        {
            // The window was resized (or moved to another DPI). This frame was drawn into a surface of the old size, so it
            // shows only part of the window, and the previous frame has the old size: neither may be read, or normalised
            // regions would map onto the wrong pixels. The pool follows the new size, and readers wait for its first frame.
            _poolSize = content;
            if (_device is not null)
            {
                sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBuffers, content);
            }

            frame.Dispose();
            lock (_frameLock)
            {
                ReleaseLatestLocked();
                _frameArrived = NewSignal();
            }

            return;
        }

        TaskCompletionSource signal;
        lock (_frameLock)
        {
            ReleaseLatestLocked();
            _latest = frame;
            signal = _frameArrived;
        }

        signal.TrySetResult();
    }

    /// <summary>Drops the latest frame; one that a reader still holds is released by that reader. Call under <c>_frameLock</c>.</summary>
    private void ReleaseLatestLocked()
    {
        if (_latest is not null && !ReferenceEquals(_latest, _inUse))
        {
            _latest.Dispose();
        }

        _latest = null;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) =>
        _state = Failed(CaptureStatus.WindowNotFound, "The game window was closed.");

    private void Teardown()
    {
        _session?.Dispose();
        _session = null;
        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
            _pool.Dispose();
            _pool = null;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        lock (_frameLock)
        {
            ReleaseLatestLocked();
            _frameArrived = NewSignal();
        }

        _device?.Dispose();
        _device = null;
        _window = 0;
    }
}
