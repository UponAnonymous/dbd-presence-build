using DbdPresence.Core.Capture;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// A fake <see cref="ITextRecognizer"/> for Linux tests: it reads text drawn with <see cref="TestFont"/> at whole-pixel cell sizes back from the
/// pixels (white ink on any darker or coloured background, any size). It stands in for Windows OCR, which runs only on Windows CI, and it is exact
/// by construction: it says nothing about how well Windows OCR reads the game (UNKNOWN, PENDING-ENV). Its job is to prove the geometry and the
/// wiring: that the text of a frame is found in the profile's regions at several frame sizes and reaches the detectors. It counts its calls.
/// </summary>
internal sealed class BitmapFontOcr : ITextRecognizer
{
    private const int InkThreshold = 200;

    /// <summary>Gets the number of recognitions run.</summary>
    public int Calls { get; private set; }

    /// <summary>
    /// Gets or sets a transform applied to every recognised text, to stand in for the confusions a real recogniser makes (a letter read as a
    /// digit, a dropped letter). The transforms used by the tests are invented here; they say nothing about how Windows OCR reads the game
    /// (UNKNOWN, PENDING-ENV). Null reads exactly.
    /// </summary>
    public Func<string, string>? Noise { get; set; }

    /// <summary>Gets or sets the status to return instead of recognising (null recognises).</summary>
    public TextRecognitionStatus? ForceStatus { get; set; }

    public Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
    {
        Calls++;
        if (ForceStatus is { } status)
        {
            return Task.FromResult(new TextRecognitionResult(status, string.Empty, "forced by the test"));
        }

        var text = Read(image);
        return Task.FromResult(new TextRecognitionResult(TextRecognitionStatus.Recognized, Noise is null ? text : Noise(text), "fake bitmap-font recognition"));
    }

    public static string Read(PixelBuffer image)
    {
        var ink = new bool[image.Width * image.Height];
        var bgra = image.Bgra.Span;
        for (var i = 0; i < ink.Length; i++)
        {
            ink[i] = bgra[i * 4] >= InkThreshold && bgra[(i * 4) + 1] >= InkThreshold && bgra[(i * 4) + 2] >= InkThreshold;
        }

        var lines = new List<string>();
        var y = 0;
        while (y < image.Height)
        {
            if (!RowHasInk(ink, image.Width, y))
            {
                y++;
                continue;
            }

            var top = y;
            while (y < image.Height && RowHasInk(ink, image.Width, y))
            {
                y++;
            }

            var text = ReadLine(ink, image.Width, top, y - 1);
            if (text.Length > 0)
            {
                lines.Add(text);
            }
        }

        return string.Join('\n', lines);
    }

    private static bool RowHasInk(bool[] ink, int width, int y)
    {
        for (var x = 0; x < width; x++)
        {
            if (ink[(y * width) + x])
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadLine(bool[] ink, int width, int top, int bottom)
    {
        var height = bottom - top + 1;
        if (height % TestFont.Height != 0)
        {
            return string.Empty; // not text drawn by the test font (or clipped by the crop)
        }

        var cell = height / TestFont.Height;
        var minX = width;
        var maxX = -1;
        for (var row = top; row <= bottom; row++)
        {
            for (var x = 0; x < width; x++)
            {
                if (ink[(row * width) + x])
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                }
            }
        }

        var glyphs = ((maxX - minX) / (TestFont.Advance * cell)) + 1;
        var chars = new char[glyphs];
        for (var g = 0; g < glyphs; g++)
        {
            chars[g] = ReadGlyph(ink, width, minX + (g * TestFont.Advance * cell), top, cell);
        }

        return new string(chars).Trim();
    }

    private static char ReadGlyph(bool[] ink, int width, int left, int top, int cell)
    {
        var bits = new bool[TestFont.Width * TestFont.Height];
        for (var row = 0; row < TestFont.Height; row++)
        {
            for (var column = 0; column < TestFont.Width; column++)
            {
                var x = left + (column * cell) + (cell / 2);
                var y = top + (row * cell) + (cell / 2);
                bits[(row * TestFont.Width) + column] = x >= 0 && x < width && ink[(y * width) + x];
            }
        }

        foreach (var (c, pattern) in TestFont.Patterns)
        {
            if (bits.AsSpan().SequenceEqual(pattern))
            {
                return c;
            }
        }

        return '?';
    }
}
