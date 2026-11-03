// MEASUREMENT, not verification. Covers plan step 2 of docs/evidence/detection-speed-and-confidence.md: capture frame delivery
// and latency, process CPU, region crop and conversion cost, and Windows OCR time. Everything is measured against this test
// process's own WPF window (never the game) and appended to detection-benchmark.txt, which the Windows workflow uploads in the
// windows-evidence artifact. The tests assert only that something was measured (at least one frame, at least the expected number
// of timed calls); they never assert a speed, so a slow runner cannot turn them red. No accuracy is measured here: the crop and
// conversion cost uses a synthetic frame and says so.
using System.Diagnostics;
using System.Globalization;
using DbdPresence.Core.Capture;
using DbdPresence.Platform.Windows.Capture;
using DbdPresence.Platform.Windows.Ocr;
using DbdPresence.Vision.Imaging;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace DbdPresence.Platform.Windows.Tests.Capture;

[Collection(OwnWindowCaptureGroup.Name)]
public sealed class DetectionBenchmarkTests
{
    private static readonly TimeProvider s_time = TimeProvider.System;
    private static readonly TimeSpan s_measureWindow = TimeSpan.FromSeconds(5);
    private static readonly NormalizedRegion s_markerCell = new(0.1, 0.1, 0.1, 0.1);
    private static object? s_sink;

    /// <summary>Keeps measured results reachable so the runtime cannot drop the work being timed.</summary>
    internal static object? Sink => s_sink;

    [Fact]
    public async Task Measure_CaptureFrameDelivery_ProcessCpu_AndChangeLatency()
    {
        var ct = TestContext.Current.CancellationToken;
        using var window = CaptureTestWindow.ShowQuadrants();
        window.StartContinuousRedraw();
        BenchmarkReport.Line("[capture] window: 800x600 DIP quadrants, redrawn on every display refresh (a game-like source of frames)");

        // Phase 1: the same window with no capture session, so the cost of drawing it can be told apart from the cost of capturing it.
        var baseline = await RunForAsync(() => Task.Delay(s_measureWindow, ct));
        BenchmarkReport.Line($"[capture] CPU baseline, window redrawing, no capture: {Cpu(baseline)}");

        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, s_time);
        await WindowCaptureTests.StartOrSkipAsync(source, window);

        // Phase 2: capture running, no frame read by us. Counts what Windows delivers.
        var before = source.FramesArrived;
        var delivery = await RunForAsync(() => Task.Delay(s_measureWindow, ct));
        var delivered = source.FramesArrived - before;
        var fps = delivered / delivery.Wall.TotalSeconds;
        BenchmarkReport.Line($"[capture] frames delivered in {F(delivery.Wall.TotalSeconds)} s: {delivered} = {F(fps)} frames/s (limited by the window's own redraw rate and the display refresh)");
        BenchmarkReport.Line($"[capture] CPU with capture running, no reads: {Cpu(delivery)}");

        // Phase 3: capture running plus one 300x60-pixel region read about every 100 ms (10 samples/s), the cost a detector loop would add.
        var reads = new List<double>();
        var region = new NormalizedRegion(0.1, 0.1, 300 / 800.0, 60 / 600.0);
        var sampling = await RunForAsync(async () =>
        {
            var start = s_time.GetTimestamp();
            while (s_time.GetElapsedTime(start) < s_measureWindow)
            {
                var t = s_time.GetTimestamp();
                var sample = await source.CaptureAsync([region], ct);
                reads.Add(s_time.GetElapsedTime(t).TotalMilliseconds);
                s_sink = sample;
                await Task.Delay(100, ct);
            }
        });
        BenchmarkReport.Line($"[capture] CPU with capture running and {reads.Count} region reads (about 10 per s): {Cpu(sampling)}");
        BenchmarkReport.Line($"[capture] one CaptureAsync call (GPU to CPU copy of the whole {source.LatestContentSize.Width}x{source.LatestContentSize.Height} frame, then a crop): {BenchmarkStats.From(reads)}");
        BenchmarkReport.Line("[capture] CPU figures are for the whole test process (window drawing, test host, garbage collection included), not for capture alone; compare with the baseline line.");

        // Phase 4: latency from a known change in the window to the first frame we read that shows it. The reader polls back to back,
        // so the figure includes one read (the CaptureAsync line above) on top of Windows' own delivery delay.
        var latencies = new List<double>();
        var missed = 0;
        const int Trials = 30;
        for (var i = 0; i < Trials; i++)
        {
            var on = i % 2 == 0;
            var start = s_time.GetTimestamp();
            window.SetMarker(on);
            var seen = false;
            while (!seen && s_time.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                var sample = await source.CaptureAsync([s_markerCell], ct);
                seen = sample.Regions.Count == 1 && ShowsMarker(sample.Regions[0], on);
            }

            if (seen)
            {
                latencies.Add(s_time.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                missed++;
            }

            await Task.Delay(250, ct);
        }

        BenchmarkReport.Line($"[capture] latency, pixel change in the window to the first read frame showing it ({Trials} trials, {missed} not seen within 2 s): {BenchmarkStats.From(latencies)}");

        // Sanity only: something was delivered. No speed is asserted.
        Assert.True(delivered > 0, "Windows delivered no capture frame during the measurement window.");
    }

    [Fact]
    public void Measure_RegionCropAndConversion_On1080pFrame()
    {
        // Mechanics and timing only: a synthetic opaque 1920x1080 frame (random colours, alpha 255, as the capture adapter delivers).
        var bytes = new byte[1920 * 1080 * PixelBuffer.BytesPerPixel];
        new Random(12345).NextBytes(bytes);
        for (var i = 3; i < bytes.Length; i += PixelBuffer.BytesPerPixel)
        {
            bytes[i] = 255;
        }

        var frame = new PixelBuffer(1920, 1080, bytes);
        var rect = new NormalizedRegion(0.4, 0.45, 300 / 1920.0, 60 / 1080.0).ToPixels(frame.Width, frame.Height);
        var half = new NormalizedRegion(0, 0, 0.5, 0.5).ToPixels(frame.Width, frame.Height);
        var cropped = frame.Crop(rect);

        var crop = TimeCalls(() => s_sink = frame.Crop(rect), warmup: 20, iterations: 200);
        var convert = TimeCalls(() => s_sink = cropped.ToImageBuffer(), warmup: 20, iterations: 200);
        var both = TimeCalls(() => s_sink = frame.Crop(rect).ToImageBuffer(), warmup: 20, iterations: 200);
        var halfConvert = TimeCalls(() => s_sink = frame.Crop(half).ToImageBuffer(), warmup: 5, iterations: 30);
        var fullConvert = TimeCalls(() => s_sink = frame.ToImageBuffer(), warmup: 3, iterations: 20);

        BenchmarkReport.Line("[crop] synthetic opaque 1920x1080 BGRA frame (timing only, no accuracy)");
        BenchmarkReport.Line($"[crop] PixelBuffer.Crop {rect.Width}x{rect.Height}: {crop}");
        BenchmarkReport.Line($"[crop] CaptureConversions.ToImageBuffer {rect.Width}x{rect.Height}: {convert}");
        BenchmarkReport.Line($"[crop] crop + conversion {rect.Width}x{rect.Height}: {both}");
        BenchmarkReport.Line($"[crop] crop + conversion {half.Width}x{half.Height} (a quarter of the frame): {halfConvert}");
        BenchmarkReport.Line($"[crop] conversion of the whole 1920x1080 frame: {fullConvert}");

        Assert.Equal(200, both.Count);
    }

    [Fact]
    public async Task Measure_WindowsEnglishOcr_On300x60Crop()
    {
        var ct = TestContext.Current.CancellationToken;
        BenchmarkReport.Line($"[ocr] OcrEngine.MaxImageDimension = {WinOcrEngine.MaxImageDimension}");
        BenchmarkReport.Line($"[ocr] installed languages: [{string.Join(", ", WindowsEnglishOcr.InstalledLanguageTags())}]; English available: {WindowsEnglishOcr.IsEnglishAvailable()}");
        if (!WindowsEnglishOcr.IsEnglishAvailable())
        {
            BenchmarkReport.Line("[ocr] PENDING-ENV: the English OCR language pack is not installed on this runner; no OCR timing taken.");
            Assert.Skip("PENDING-ENV: the English OCR language pack is not installed on this runner.");
        }

        using var window = CaptureTestWindow.ShowText("Generators Left 5", 300, 60, 28);
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, s_time);
        await WindowCaptureTests.StartOrSkipAsync(source, window);
        var sample = await source.CaptureAsync([new NormalizedRegion(0, 0, 1, 1)], ct);
        var crop = sample.Regions.Single();
        BenchmarkReport.Line($"[ocr] crop captured from our own window: {crop.Width}x{crop.Height} pixels, black Arial text on white");

        var ocr = new WindowsEnglishOcr();
        var cold = s_time.GetTimestamp();
        var first = await ocr.RecognizeAsync(crop, ct);
        var coldMs = s_time.GetElapsedTime(cold).TotalMilliseconds;
        BenchmarkReport.Line($"[ocr] first call (cold): {F(coldMs)} ms, status {first.Status}");

        var calls = new List<double>();
        var recognised = 0;
        var withText = 0;
        string lastText = string.Empty;
        for (var i = 0; i < 40; i++)
        {
            var t = s_time.GetTimestamp();
            var result = await ocr.RecognizeAsync(crop, ct);
            calls.Add(s_time.GetElapsedTime(t).TotalMilliseconds);
            recognised += result.Status == TextRecognitionStatus.Recognized ? 1 : 0;
            withText += string.IsNullOrWhiteSpace(result.Text) ? 0 : 1;
            lastText = result.Text;
        }

        BenchmarkReport.Line($"[ocr] WindowsEnglishOcr.RecognizeAsync, warm, as shipped (creates the engine on each call): {BenchmarkStats.From(calls)}");
        BenchmarkReport.Line($"[ocr] of {calls.Count} calls, status Recognized: {recognised}, non-empty text: {withText}; last text: '{lastText.ReplaceLineEndings(" / ")}'");

        // For the design question "create the engine once?": the creation cost alone, taken from IsEnglishAvailable (it builds an engine).
        var create = TimeCalls(() => s_sink = WindowsEnglishOcr.IsEnglishAvailable(), warmup: 2, iterations: 30);
        BenchmarkReport.Line($"[ocr] engine creation alone (IsEnglishAvailable): {create}");

        // Sanity only: the calls ran. No speed and no text is asserted (recognition is covered by EnglishOcrTests).
        Assert.Equal(40, calls.Count);
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Cpu((TimeSpan Wall, TimeSpan Cpu) run) =>
        $"{F(run.Cpu.TotalMilliseconds)} ms of process CPU in {F(run.Wall.TotalSeconds)} s wall = {F(100 * run.Cpu.TotalSeconds / run.Wall.TotalSeconds)}% of one core";

    private static bool ShowsMarker(PixelBuffer cell, bool on)
    {
        var (_, g, _, _) = cell.GetPixel(cell.Width / 2, cell.Height / 2);
        return on ? g > 200 : g < 50;
    }

    private static async Task<(TimeSpan Wall, TimeSpan Cpu)> RunForAsync(Func<Task> body)
    {
        var cpuStart = ProcessCpu();
        var start = s_time.GetTimestamp();
        await body();
        var wall = s_time.GetElapsedTime(start);
        return (wall, ProcessCpu() - cpuStart);
    }

    private static TimeSpan ProcessCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static BenchmarkStats TimeCalls(Action action, int warmup, int iterations)
    {
        for (var i = 0; i < warmup; i++)
        {
            action();
        }

        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var start = s_time.GetTimestamp();
            action();
            samples.Add(s_time.GetElapsedTime(start).TotalMilliseconds);
        }

        return BenchmarkStats.From(samples);
    }
}
