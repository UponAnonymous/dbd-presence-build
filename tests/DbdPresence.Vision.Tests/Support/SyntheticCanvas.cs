using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// Renders synthetic test frames from shapes and a tiny hand-made 5 x 7 pixel font. The glyphs are invented for tests and resemble no game
/// asset. Everything is drawn in normalised coordinates, so the same layout can be rendered at any resolution. Frames from this class test
/// mechanics only; they are never evidence of accuracy.
/// </summary>
internal sealed class SyntheticCanvas
{
    private static readonly Dictionary<char, string[]> s_font = new()
    {
        ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['C'] = ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['I'] = ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
        ['K'] = ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['S'] = ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
        ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
        ['U'] = ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ['6'] = ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    };

    private readonly byte[] _pixels;

    public SyntheticCanvas(int width, int height, byte background)
    {
        Width = width;
        Height = height;
        _pixels = new byte[width * height];
        Array.Fill(_pixels, background);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Fills a rectangle given in normalised coordinates.</summary>
    public SyntheticCanvas Rect(double x, double y, double w, double h, byte luma)
    {
        var r = new NormalizedRect(x, y, w, h).ToPixels(Width, Height);
        for (var py = r.Y; py < r.Bottom; py++)
        {
            for (var px = r.X; px < r.Right; px++)
            {
                _pixels[(py * Width) + px] = luma;
            }
        }

        return this;
    }

    /// <summary>Fills an ellipse inscribed in a normalised rectangle.</summary>
    public SyntheticCanvas Ellipse(double x, double y, double w, double h, byte luma)
    {
        var r = new NormalizedRect(x, y, w, h).ToPixels(Width, Height);
        double cx = r.X + (r.Width / 2.0);
        double cy = r.Y + (r.Height / 2.0);
        for (var py = r.Y; py < r.Bottom; py++)
        {
            for (var px = r.X; px < r.Right; px++)
            {
                var dx = (px + 0.5 - cx) / (r.Width / 2.0);
                var dy = (py + 0.5 - cy) / (r.Height / 2.0);
                if ((dx * dx) + (dy * dy) <= 1.0)
                {
                    _pixels[(py * Width) + px] = luma;
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Draws text-like glyphs. <paramref name="x"/>, <paramref name="y"/> and <paramref name="glyphHeight"/> are normalised to the frame
    /// height-independent layout: the glyph height is a fraction of the frame height and the width follows the 5:7 shape.
    /// </summary>
    public SyntheticCanvas Text(double x, double y, double glyphHeight, string text, byte luma)
    {
        var cell = glyphHeight * Height / 7.0; // pixels per font cell
        var left = x * Width;
        var top = y * Height;
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = s_font[text[i]];
            for (var row = 0; row < 7; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    if (glyph[row][col] != '1')
                    {
                        continue;
                    }

                    var x0 = (int)Math.Round(left + (((i * 6) + col) * cell));
                    var x1 = (int)Math.Round(left + (((i * 6) + col + 1) * cell));
                    var y0 = (int)Math.Round(top + (row * cell));
                    var y1 = (int)Math.Round(top + ((row + 1) * cell));
                    for (var py = Math.Max(0, y0); py < Math.Min(Height, Math.Max(y1, y0 + 1)); py++)
                    {
                        for (var px = Math.Max(0, x0); px < Math.Min(Width, Math.Max(x1, x0 + 1)); px++)
                        {
                            _pixels[(py * Width) + px] = luma;
                        }
                    }
                }
            }
        }

        return this;
    }

    /// <summary>Adds a brightness offset and deterministic salt-and-pepper noise (1 in <paramref name="oneIn"/> pixels).</summary>
    public SyntheticCanvas Disturb(int brightness, int seed, int oneIn = 100)
    {
        var state = (uint)seed;
        for (var i = 0; i < _pixels.Length; i++)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            var v = Math.Clamp(_pixels[i] + brightness, 0, 255);
            if (oneIn > 0 && (state >> 8) % (uint)oneIn == 0)
            {
                v = (state & 0x1000000) == 0 ? 0 : 255;
            }

            _pixels[i] = (byte)v;
        }

        return this;
    }

    public ImageBuffer ToImage() => new(Width, Height, PixelFormat.Gray8, _pixels);
}
