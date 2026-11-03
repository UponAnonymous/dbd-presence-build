// Covers: QUALITY_GATES C3 (capture adapter reads expected pixel colours from requested regions of our own WPF test window),
// FR-CAP-06 (window found by owning PID), S-14 (resize and DPI change follow), ADR-0003 (honest states, border behaviour).
// The target is always this test process's own window, never DBD. If the runner cannot create a capture session the reason is
// recorded in capture-evidence.txt and the test is skipped: C3 is then PENDING-ENV, not PASS.
using System.Diagnostics;
using DbdPresence.Core.Capture;
using DbdPresence.Platform.Windows.Capture;
using Windows.Graphics.Capture;

namespace DbdPresence.Platform.Windows.Tests.Capture;

[Collection(OwnWindowCaptureGroup.Name)]
public sealed class WindowCaptureTests
{
    private static readonly NormalizedRegion s_topLeftCell = new(0.1, 0.1, 0.3, 0.3);
    private static readonly NormalizedRegion s_topRightCell = new(0.6, 0.1, 0.3, 0.3);
    private static readonly NormalizedRegion s_bottomLeftCell = new(0.1, 0.6, 0.3, 0.3);
    private static readonly NormalizedRegion s_bottomRightCell = new(0.6, 0.6, 0.3, 0.3);

    [Fact]
    public async Task OwnWindow_RequestedRegionsHaveTheExpectedColours()
    {
        using var window = CaptureTestWindow.ShowQuadrants();
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);
        await StartOrSkipAsync(source, window);

        var sample = await source.CaptureAsync([s_topLeftCell, s_topRightCell, s_bottomLeftCell, s_bottomRightCell], TestContext.Current.CancellationToken);

        CaptureEvidence.Record($"C3 capture state: {sample.State.Status}; frame {sample.State.FrameWidth}x{sample.State.FrameHeight}; dpi {sample.State.Dpi}; mode {sample.State.DisplayMode}; border {sample.State.Border}");
        Assert.Equal(CaptureStatus.Ready, sample.State.Status);
        Assert.Equal(4, sample.Regions.Count);
        AssertUniform(sample.Regions[0], 255, 0, 0);
        AssertUniform(sample.Regions[1], 0, 255, 0);
        AssertUniform(sample.Regions[2], 0, 0, 255);
        AssertUniform(sample.Regions[3], 255, 255, 255);
        Assert.Equal(WindowDisplayMode.Windowed, sample.State.DisplayMode);
        Assert.True(sample.State.Dpi >= 96);
    }

    [Fact]
    public async Task FramedOwnWindow_IsCutToTheClientArea_SoTheTitleBarNeverShiftsTheRegions()
    {
        // A windowed game is captured with its title bar and border (the owner's recordings are 1922x1112 for a 1920x1080 game).
        // The frame must be only the client area: its four corner pixels are the four quadrant colours, which a title bar on top would break.
        using var window = CaptureTestWindow.ShowFramedQuadrants();
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);
        await StartOrSkipAsync(source, window);

        var sample = await source.CaptureFullFrameAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sample.Frame);
        var frame = sample.Frame;
        CaptureEvidence.Record($"client area: frame {frame.Width}x{frame.Height}; client-area-only {sample.State.ClientAreaOnly}; mode {sample.State.DisplayMode}");
        Assert.True(sample.State.ClientAreaOnly);
        Assert.Equal(WindowDisplayMode.Windowed, sample.State.DisplayMode);
        Assert.True(frame.Width < 800 && frame.Height < 600 - 20, $"the frame {frame.Width}x{frame.Height} is not smaller than the 800x600 outer window by a title bar");
        AssertPixel(frame, 0, 0, 255, 0, 0);
        AssertPixel(frame, frame.Width - 1, 0, 0, 255, 0);
        AssertPixel(frame, 0, frame.Height - 1, 0, 0, 255);
        AssertPixel(frame, frame.Width - 1, frame.Height - 1, 255, 255, 255);
        var crop = (await source.CaptureAsync([s_topLeftCell], TestContext.Current.CancellationToken)).Regions.Single();
        AssertUniform(crop, 255, 0, 0);
    }

    [Fact]
    public async Task UnframedOwnWindow_IsStillReportedAsClientArea()
    {
        using var window = CaptureTestWindow.ShowQuadrants();
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);
        var state = await StartOrSkipAsync(source, window);

        Assert.True(state.ClientAreaOnly);
    }

    [Fact]
    public async Task OwnWindow_ResizeIsFollowedAndRegionsStayNormalised()
    {
        using var window = CaptureTestWindow.ShowQuadrants(640, 480);
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);
        await StartOrSkipAsync(source, window);
        var first = await source.CaptureFullFrameAsync(TestContext.Current.CancellationToken);

        window.Resize(960, 540);
        FullFrameSample resized = first;
        for (var i = 0; i < 100 && (resized.Frame is null || resized.Frame.Width == first.Frame?.Width); i++)
        {
            // The window keeps presenting frames while we wait, as the game does; a static window may present none after the resize.
            window.Repaint();
            await Task.Delay(100, TestContext.Current.CancellationToken);
            resized = await source.CaptureFullFrameAsync(TestContext.Current.CancellationToken);
        }

        Assert.NotNull(first.Frame);
        Assert.NotNull(resized.Frame);
        Assert.True(
            resized.Frame.Width > first.Frame.Width,
            $"frame stayed {first.Frame.Width} wide after the window was resized; newest content size {source.LatestContentSize}; state {resized.State.Status}: {resized.State.Detail}");
        var crop = (await source.CaptureAsync([s_topLeftCell], TestContext.Current.CancellationToken)).Regions.Single();
        AssertUniform(crop, 255, 0, 0);
        CaptureEvidence.Record($"C3 resize: {first.Frame.Width}x{first.Frame.Height} -> {resized.Frame.Width}x{resized.Frame.Height}");
    }

    [Fact]
    public async Task ProcessWithoutAWindow_ReportsWindowNotFoundOrUnsupported_NeverReady()
    {
        // No test window is open in this test; the process owns no visible window of its own.
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);

        var state = await source.StartAsync(TestContext.Current.CancellationToken);

        Assert.Contains(state.Status, new[] { CaptureStatus.WindowNotFound, CaptureStatus.Unsupported });
        Assert.False(string.IsNullOrWhiteSpace(state.Detail));
    }

    [Fact]
    public async Task OwnWindow_RecordsWhetherTheCaptureBorderCanBeTurnedOff()
    {
        using var window = CaptureTestWindow.ShowQuadrants();
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System, requestBorderless: true);
        var state = await StartOrSkipAsync(source, window);

        CaptureEvidence.Record($"ADR-0003 border: outcome {state.Border}; detail: {source.BorderDetail}; OS {Environment.OSVersion.Version}");
        Assert.Contains(state.Border, new[] { BorderOutcome.Disabled, BorderOutcome.Refused, BorderOutcome.NotAvailable });
        Assert.False(string.IsNullOrWhiteSpace(source.BorderDetail));
    }

    [Fact]
    public async Task OwnWindow_DefaultSessionKeepsTheBorder()
    {
        using var window = CaptureTestWindow.ShowQuadrants();
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);
        var state = await StartOrSkipAsync(source, window);

        Assert.Contains(state.Border, new[] { BorderOutcome.Required, BorderOutcome.NotAvailable });
    }

    /// <summary>How long a test window may take to deliver its first frame with content before the test fails.</summary>
    internal static readonly TimeSpan FirstContentTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan s_firstContentPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_firstContentRestart = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts the capture session and waits, for a bounded time, until a frame with content has arrived.
    /// <para>
    /// Why the wait exists (run 40113716761, <c>EnglishOcrTests.RenderedEnglishText_IsCapturedAndRecognised</c>: <c>Expected: Ready, Actual: BlackFrames</c>,
    /// evidence line "start state: BlackFrames: Frames arrive but are entirely black", and the same test passed on the next run): a capture session
    /// delivers an initial frame as soon as it starts. When the test window has been shown but Windows has not composed its first painted frame yet
    /// (the runner is busy, or the window is still being placed), that initial frame is entirely black, and <see cref="WgcWindowCaptureSource.StartAsync"/>
    /// reports exactly that, honestly. The previous helper asserted <c>Ready</c> straight after the start, so the one unlucky timing failed the test.
    /// A window that does not change presents no further frame by itself, so the helper nudges the window to present (<see cref="CaptureTestWindow.Repaint"/>,
    /// a one-unit colour change far below every tolerance) and, every two seconds, restarts the session so the initial frame is taken again.
    /// </para>
    /// <para>
    /// Nothing is weakened: after the timeout the test fails with the last state and the elapsed time. It is never skipped for black frames, because a
    /// window that never shows content is a real failure of the capture path. The timing explanation is inferred from the log above and cannot be
    /// reproduced on Linux: PENDING-ENV until the Windows CI runs show the wait is no longer needed to fail.
    /// </para>
    /// </summary>
    internal static async Task<CaptureState> StartOrSkipAsync(WgcWindowCaptureSource source, CaptureTestWindow window)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(window);
        var ct = TestContext.Current.CancellationToken;
        CaptureEvidence.Record($"GraphicsCaptureSession.IsSupported = {GraphicsCaptureSession.IsSupported()}; OS {Environment.OSVersion.Version}");
        var state = await source.StartAsync(ct);
        CaptureEvidence.Record($"start state: {state.Status}: {state.Detail}");
        if (state.Status is CaptureStatus.Unsupported or CaptureStatus.Blocked)
        {
            Assert.Skip($"PENDING-ENV (C3): this runner cannot create a capture session: {state.Status}: {state.Detail}");
        }

        if (state.Status == CaptureStatus.BlackFrames)
        {
            var started = Stopwatch.StartNew();
            var lastRestart = TimeSpan.Zero;
            var restarts = 0;
            while (state.Status == CaptureStatus.BlackFrames && started.Elapsed < FirstContentTimeout)
            {
                window.Repaint();
                await Task.Delay(s_firstContentPoll, ct);
                if (started.Elapsed - lastRestart >= s_firstContentRestart)
                {
                    lastRestart = started.Elapsed;
                    restarts++;
                    state = await source.StartAsync(ct);
                }
                else
                {
                    state = (await source.CaptureFullFrameAsync(ct)).State;
                }
            }

            CaptureEvidence.Record($"first frame with content: {state.Status} after {started.Elapsed.TotalMilliseconds:0} ms ({restarts} session restarts)");
        }

        Assert.Equal(CaptureStatus.Ready, state.Status);
        return state;
    }

    private static void AssertPixel(PixelBuffer frame, int x, int y, int r, int g, int b)
    {
        var (pr, pg, pb, _) = frame.GetPixel(x, y);
        Assert.True(
            Math.Abs(pr - r) <= 6 && Math.Abs(pg - g) <= 6 && Math.Abs(pb - b) <= 6,
            $"pixel ({x},{y}) was ({pr},{pg},{pb}), expected about ({r},{g},{b})");
    }

    private static void AssertUniform(PixelBuffer crop, int r, int g, int b)
    {
        for (var y = 0; y < crop.Height; y++)
        {
            for (var x = 0; x < crop.Width; x++)
            {
                var (pr, pg, pb, _) = crop.GetPixel(x, y);
                Assert.True(
                    Math.Abs(pr - r) <= 6 && Math.Abs(pg - g) <= 6 && Math.Abs(pb - b) <= 6,
                    $"pixel ({x},{y}) was ({pr},{pg},{pb}), expected about ({r},{g},{b})");
            }
        }
    }
}
