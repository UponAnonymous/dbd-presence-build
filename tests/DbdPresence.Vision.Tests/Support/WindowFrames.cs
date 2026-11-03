using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Tests.Support;

/// <summary>
/// Draws a window frame (a flat title bar and a thin border) around a synthetic picture, the way the owner's windowed recordings look (a 1920x1080
/// game inside a 1922x1112 capture). MECHANICS ONLY: flat colours around drawn text, no game artwork.
/// </summary>
internal static class WindowFrames
{
    public static readonly (byte R, byte G, byte B) White = (255, 255, 255);
    public static readonly (byte R, byte G, byte B) BorderGrey = (96, 96, 96);

    /// <summary>
    /// Puts <paramref name="game"/> inside a frame. Row 0 and the border columns and rows are <paramref name="border"/>; the rows between row 0
    /// and the game are the title bar in <paramref name="bar"/>, with a dark "caption" block at the left (title text and icon) that the heuristic must not need.
    /// </summary>
    public static ImageBuffer Around(ImageBuffer game, int titleBar = 31, int border = 1, (byte R, byte G, byte B)? bar = null, (byte R, byte G, byte B)? edge = null)
    {
        var barColour = bar ?? White;
        var borderColour = edge ?? BorderGrey;
        var width = game.Width + (2 * border);
        var height = game.Height + titleBar + border;
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                (byte R, byte G, byte B) colour;
                if (y == 0 || x < border || x >= width - border || y >= height - border)
                {
                    colour = borderColour;
                }
                else if (y < titleBar)
                {
                    colour = x < 200 && y is > 6 and < 24 ? ((byte)30, (byte)30, (byte)30) : barColour;
                }
                else
                {
                    colour = game.RgbAt(x - border, y - titleBar);
                }

                var i = ((y * width) + x) * 4;
                data[i] = colour.R;
                data[i + 1] = colour.G;
                data[i + 2] = colour.B;
                data[i + 3] = 255;
            }
        }

        return new ImageBuffer(width, height, PixelFormat.Rgba32, data);
    }

    /// <summary>The same pixels as two images' bytes, for equality checks.</summary>
    public static bool SamePixels(ImageBuffer a, ImageBuffer b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a.RgbAt(x, y) != b.RgbAt(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
