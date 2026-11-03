using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// An RGBA test canvas: flat colour rectangles and text in <see cref="TestFont"/>. Positions are normalised, so one layout renders at any size.
/// It draws no game art; the frames it makes test mechanics only and are never evidence of accuracy.
/// </summary>
internal sealed class HudCanvas
{
    /// <summary>The smallest cell (pixels per font cell) the test text is drawn at.</summary>
    public const int MinLegibleCell = 2;

    private readonly byte[] _rgba;

    public HudCanvas(int width, int height, (byte R, byte G, byte B) background)
    {
        Width = width;
        Height = height;
        _rgba = new byte[width * height * 4];
        Fill(0, 0, width, height, background);
    }

    public int Width { get; }

    public int Height { get; }

    public HudCanvas Rect(NormalizedRect rect, (byte R, byte G, byte B) colour)
    {
        var p = rect.ToPixels(Width, Height);
        Fill(p.X, p.Y, p.Right, p.Bottom, colour);
        return this;
    }

    /// <summary>
    /// Draws white text lines inside a region, one per equal slot, left aligned, at a whole-pixel cell size that fits the slot and the width. When
    /// even the smallest legible size does not fit, the test text is too long for this frame size and the call throws.
    /// </summary>
    public HudCanvas TextIn(NormalizedRect region, params string[] lines)
    {
        var p = region.ToPixels(Width, Height);
        var slot = p.Height / (double)lines.Length;
        var longest = lines.Max(l => l.Length);
        var cell = (int)Math.Floor(Math.Min(slot * 0.6 / TestFont.Height, p.Width * 0.94 / ((longest * TestFont.Advance) - 1)));
        if (cell < MinLegibleCell)
        {
            throw new InvalidOperationException($"Test text does not fit in a {p.Width}x{p.Height} region at this frame size ({longest} characters, {lines.Length} lines).");
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var top = (int)Math.Round(p.Y + (i * slot) + ((slot - (TestFont.Height * cell)) / 2));
            DrawLine(lines[i], p.X + (int)Math.Round(p.Width * 0.03), top, cell);
        }

        return this;
    }

    /// <summary>Draws one line at a pixel position with a whole-pixel cell size.</summary>
    public void DrawLine(string text, int left, int top, int cell)
    {
        for (var i = 0; i < text.Length; i++)
        {
            for (var row = 0; row < TestFont.Height; row++)
            {
                for (var column = 0; column < TestFont.Width; column++)
                {
                    if (TestFont.Ink(text[i], column, row))
                    {
                        var x = left + (((i * TestFont.Advance) + column) * cell);
                        Fill(x, top + (row * cell), x + cell, top + ((row + 1) * cell), (255, 255, 255));
                    }
                }
            }
        }
    }

    public ImageBuffer ToImage() => new(Width, Height, PixelFormat.Rgba32, _rgba);

    private void Fill(int x0, int y0, int x1, int y1, (byte R, byte G, byte B) colour)
    {
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
            {
                var i = ((y * Width) + x) * 4;
                _rgba[i] = colour.R;
                _rgba[i + 1] = colour.G;
                _rgba[i + 2] = colour.B;
                _rgba[i + 3] = 255;
            }
        }
    }
}
