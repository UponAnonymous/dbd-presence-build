using System.Numerics;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Vision.Matching;

/// <summary>
/// 64-bit perceptual hashes. Similar images give hashes that differ in few bits (small Hamming distance); the hashes ignore
/// overall brightness and survive rescaling, which makes them cheap anchors for "is this the same screen layout".
/// </summary>
public static class PerceptualHash
{
    private const int DctSize = 32;
    private static readonly double[,] s_cosines = BuildCosines();

    /// <summary>Average hash: the image shrunk to 8 x 8, one bit per pixel brighter than the mean.</summary>
    public static ulong Average(ImageBuffer image)
    {
        var small = Shrink(image, 8, 8);
        var mean = Enumerable.Range(0, 64).Average(i => (double)small.GrayAt(i % 8, i / 8));
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (small.GrayAt(i % 8, i / 8) > mean)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    /// <summary>Difference hash: the image shrunk to 9 x 8, one bit per pixel that is brighter than its right neighbour.</summary>
    public static ulong Difference(ImageBuffer image)
    {
        var small = Shrink(image, 9, 8);
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (small.GrayAt(x, y) > small.GrayAt(x + 1, y))
                {
                    hash |= 1UL << ((y * 8) + x);
                }
            }
        }

        return hash;
    }

    /// <summary>DCT hash: the low-frequency 8 x 8 block of a 32 x 32 discrete cosine transform, one bit per coefficient above the median.</summary>
    public static ulong Dct(ImageBuffer image)
    {
        var small = Shrink(image, DctSize, DctSize);
        var pixels = new double[DctSize, DctSize];
        for (var y = 0; y < DctSize; y++)
        {
            for (var x = 0; x < DctSize; x++)
            {
                pixels[y, x] = small.GrayAt(x, y);
            }
        }

        var coefficients = new double[64];
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var y = 0; y < DctSize; y++)
                {
                    for (var x = 0; x < DctSize; x++)
                    {
                        sum += pixels[y, x] * s_cosines[u, x] * s_cosines[v, y];
                    }
                }

                coefficients[(v * 8) + u] = sum;
            }
        }

        // The DC term (overall brightness) is excluded from the median so a brightness change leaves the hash unchanged.
        var sorted = coefficients.Skip(1).Order().ToArray();
        var median = sorted[sorted.Length / 2];
        ulong hash = 0;
        for (var i = 1; i < 64; i++)
        {
            if (coefficients[i] > median)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    /// <summary>Number of differing bits between two hashes (0 to 64).</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Similarity in [0, 1]: 1 for identical hashes.</summary>
    public static double Similarity(ulong a, ulong b) => 1.0 - (Distance(a, b) / 64.0);

    private static ImageBuffer Shrink(ImageBuffer image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.ToGray().Resize(width, height);
    }

    private static double[,] BuildCosines()
    {
        var table = new double[8, DctSize];
        for (var u = 0; u < 8; u++)
        {
            for (var x = 0; x < DctSize; x++)
            {
                table[u, x] = Math.Cos(((2 * x) + 1) * u * Math.PI / (2 * DctSize));
            }
        }

        return table;
    }
}
