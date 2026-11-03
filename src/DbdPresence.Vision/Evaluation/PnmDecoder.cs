using System.Globalization;
using System.Text;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Evaluation;

/// <summary>
/// Reads binary PGM (P5, 8-bit gray) and PPM (P6, 8-bit colour) files, and writes them. These plain formats let the evaluator and its
/// tests exchange frames without any imaging library; real recordings use JPEG or PNG, decoded by the Windows adapter (PENDING-ENV).
/// </summary>
public sealed class PnmDecoder : IFrameDecoder
{
    private const int MaxDimension = 16384;

    /// <inheritdoc />
    public ImageBuffer Decode(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>Decodes an in-memory P5 or P6 file.</summary>
    /// <exception cref="FormatException">The data is not a supported PNM file.</exception>
    public static ImageBuffer Decode(ReadOnlySpan<byte> data)
    {
        var position = 0;
        var magic = Token(data, ref position);
        if (magic is not ("P5" or "P6"))
        {
            throw new FormatException("Only binary PGM (P5) and PPM (P6) are supported.");
        }

        var width = Number(data, ref position, "width");
        var height = Number(data, ref position, "height");
        var maxValue = Number(data, ref position, "maximum value");
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || maxValue != 255)
        {
            throw new FormatException("Image size is out of range or the maximum value is not 255.");
        }

        position++; // Exactly one whitespace byte separates the header from the pixels.
        var channels = magic == "P5" ? 1 : 3;
        var expected = checked(width * height * channels);
        if (data.Length - position < expected)
        {
            throw new FormatException("The pixel data is shorter than the header says.");
        }

        var pixels = data.Slice(position, expected);
        if (channels == 1)
        {
            return new ImageBuffer(width, height, PixelFormat.Gray8, pixels);
        }

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = pixels[i * 3];
            rgba[(i * 4) + 1] = pixels[(i * 3) + 1];
            rgba[(i * 4) + 2] = pixels[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        return new ImageBuffer(width, height, PixelFormat.Rgba32, rgba);
    }

    /// <summary>Encodes an image as P5 (gray) or P6 (colour; alpha is dropped).</summary>
    public static byte[] Encode(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var gray = image.Format == PixelFormat.Gray8;
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{(gray ? "P5" : "P6")}\n{image.Width} {image.Height}\n255\n"));
        var body = new byte[image.Width * image.Height * (gray ? 1 : 3)];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            if (gray)
            {
                body[i] = image.Pixels[i];
            }
            else
            {
                body[i * 3] = image.Pixels[i * 4];
                body[(i * 3) + 1] = image.Pixels[(i * 4) + 1];
                body[(i * 3) + 2] = image.Pixels[(i * 4) + 2];
            }
        }

        return [.. header, .. body];
    }

    private static int Number(ReadOnlySpan<byte> data, ref int position, string what) =>
        int.TryParse(Token(data, ref position), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"The {what} is not a number.");

    private static string Token(ReadOnlySpan<byte> data, ref int position)
    {
        while (position < data.Length && (IsSpace(data[position]) || data[position] == (byte)'#'))
        {
            if (data[position] == (byte)'#')
            {
                while (position < data.Length && data[position] != (byte)'\n')
                {
                    position++;
                }
            }
            else
            {
                position++;
            }
        }

        var start = position;
        while (position < data.Length && !IsSpace(data[position]))
        {
            position++;
        }

        return position == start ? throw new FormatException("The header ended early.") : Encoding.ASCII.GetString(data[start..position]);
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';
}
