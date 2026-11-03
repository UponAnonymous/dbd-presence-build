using System.Runtime.InteropServices;
using DbdPresence.Core.Capture;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DbdPresence.Platform.Windows.Ocr;

/// <summary>
/// Windows.Media.Ocr restricted to the English recognition language (PLAN_V2 D17: the game is read in English only). Other
/// recognition languages are never used even if installed, so a user profile in another language cannot change what is read.
/// When English is not installed the result says so; nothing is guessed. The OCR engine runs on pixels only; it never
/// loads or opens anything by location.
/// </summary>
public sealed class WindowsEnglishOcr : ITextRecognizer
{
    private const string PreferredTag = "en-US";

    /// <summary>Lists the installed recognition language tags, for evidence and diagnostics.</summary>
    public static IReadOnlyList<string> InstalledLanguageTags() =>
        [.. OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)];

    /// <summary>Whether an English recognition language is installed.</summary>
    public static bool IsEnglishAvailable() => CreateEnglishEngine() is not null;

    /// <inheritdoc />
    public async Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            var engine = CreateEnglishEngine();
            if (engine is null)
            {
                return new TextRecognitionResult(
                    TextRecognitionStatus.EnglishLanguageMissing,
                    string.Empty,
                    "The English OCR language is not installed. Add it in Windows Settings under Time and language, Language and region.");
            }

            if (image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension)
            {
                return new TextRecognitionResult(TextRecognitionStatus.ImageTooLarge, string.Empty, $"The image is larger than {OcrEngine.MaxImageDimension} pixels on a side.");
            }

            var writer = new DataWriter();
            writer.WriteBytes(image.Bgra.ToArray());
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var text = string.Join('\n', result.Lines.Select(l => l.Text));
            return new TextRecognitionResult(TextRecognitionStatus.Recognized, text, $"Recognised with {engine.RecognizerLanguage.LanguageTag}.");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return new TextRecognitionResult(
                TextRecognitionStatus.Unavailable,
                string.Empty,
                $"Windows text recognition failed ({ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}): {ex.Message}");
        }
    }

    private static OcrEngine? CreateEnglishEngine()
    {
        var preferred = new Language(PreferredTag);
        if (OcrEngine.IsLanguageSupported(preferred))
        {
            var engine = OcrEngine.TryCreateFromLanguage(preferred);
            if (engine is not null)
            {
                return engine;
            }
        }

        var other = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return other is null ? null : OcrEngine.TryCreateFromLanguage(other);
    }
}
