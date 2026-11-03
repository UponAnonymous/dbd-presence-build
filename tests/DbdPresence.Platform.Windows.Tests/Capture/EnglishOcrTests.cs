// Covers: QUALITY_GATES C4 (Windows OCR adapter reads known English text rendered in our own test window, via the capture adapter),
// PLAN_V2 D17 (English only). Language-pack availability is recorded in capture-evidence.txt; with no English pack the test is
// skipped and C4 is PENDING-ENV, not PASS.
using DbdPresence.Core.Capture;
using DbdPresence.Platform.Windows.Capture;
using DbdPresence.Platform.Windows.Ocr;

namespace DbdPresence.Platform.Windows.Tests.Capture;

[Collection(OwnWindowCaptureGroup.Name)]
public sealed class EnglishOcrTests
{
    private const string Sentence = "Generators Remaining 5";

    [Fact]
    public async Task RenderedEnglishText_IsCapturedAndRecognised()
    {
        CaptureEvidence.Record($"C4 installed OCR languages: [{string.Join(", ", WindowsEnglishOcr.InstalledLanguageTags())}]; English available: {WindowsEnglishOcr.IsEnglishAvailable()}");
        if (!WindowsEnglishOcr.IsEnglishAvailable())
        {
            Assert.Skip("PENDING-ENV (C4): the English OCR language pack is not installed on this runner.");
        }

        using var window = CaptureTestWindow.ShowText(Sentence);
        using var source = new WgcWindowCaptureSource(CaptureTestWindow.ProcessId, TimeProvider.System);
        await WindowCaptureTests.StartOrSkipAsync(source, window);
        var sample = await source.CaptureAsync([new NormalizedRegion(0, 0, 1, 1)], TestContext.Current.CancellationToken);

        var result = await new WindowsEnglishOcr().RecognizeAsync(sample.Regions.Single(), TestContext.Current.CancellationToken);

        CaptureEvidence.Record($"C4 OCR: {result.Status}: '{result.Text}' ({result.Detail})");
        Assert.Equal(TextRecognitionStatus.Recognized, result.Status);
        Assert.Contains("Generators", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remaining", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OcrOfARenderedImageWithoutTheCaptureStep_AlsoWorks()
    {
        if (!WindowsEnglishOcr.IsEnglishAvailable())
        {
            Assert.Skip("PENDING-ENV (C4): the English OCR language pack is not installed on this runner.");
        }

        // A blank white image must recognise as "no text", not fail: the adapter reports Recognized with empty text.
        var white = new PixelBuffer(200, 80, Enumerable.Repeat((byte)255, 200 * 80 * 4).ToArray());

        var result = await new WindowsEnglishOcr().RecognizeAsync(white, TestContext.Current.CancellationToken);

        Assert.Equal(TextRecognitionStatus.Recognized, result.Status);
        Assert.True(string.IsNullOrWhiteSpace(result.Text));
    }

    [Fact]
    public async Task ImageLargerThanTheEngineLimit_IsRefusedWithAReason()
    {
        if (!WindowsEnglishOcr.IsEnglishAvailable())
        {
            Assert.Skip("PENDING-ENV (C4): the English OCR language pack is not installed on this runner.");
        }

        var width = (int)global::Windows.Media.Ocr.OcrEngine.MaxImageDimension + 1;
        var tooWide = new PixelBuffer(width, 1, new byte[width * 4]);

        var result = await new WindowsEnglishOcr().RecognizeAsync(tooWide, TestContext.Current.CancellationToken);

        Assert.Equal(TextRecognitionStatus.ImageTooLarge, result.Status);
    }
}
