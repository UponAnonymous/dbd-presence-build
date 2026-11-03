// Covers: C2 (perceptual hashing). Mechanics only: synthetic pixels.
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Matching;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Matching;

public sealed class PerceptualHashTests
{
    private static ImageBuffer Layout(SceneKind scene, int width, int brightness = 0, int noise = 0) =>
        SyntheticScenes.Render(scene, width, brightness, seed: 3, noiseOneIn: noise);

    public static TheoryData<string> Algorithms => ["average", "difference", "dct"];

    private static ulong Hash(string algorithm, ImageBuffer image) => algorithm switch
    {
        "average" => PerceptualHash.Average(image),
        "difference" => PerceptualHash.Difference(image),
        _ => PerceptualHash.Dct(image),
    };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void IdenticalImages_HaveDistanceZero(string algorithm)
    {
        var a = Hash(algorithm, Layout(SceneKind.Results, 640));
        var b = Hash(algorithm, Layout(SceneKind.Results, 640));

        Assert.Equal(0, PerceptualHash.Distance(a, b));
        Assert.Equal(1.0, PerceptualHash.Similarity(a, b));
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void TheSameLayoutAtAnotherResolution_StaysClose_WhileDifferentLayoutsDiffer(string algorithm)
    {
        var small = Hash(algorithm, Layout(SceneKind.Results, 320));
        var large = Hash(algorithm, Layout(SceneKind.Results, 960));
        var other = Hash(algorithm, Layout(SceneKind.Lobby, 960));

        var near = PerceptualHash.Distance(small, large);
        var far = PerceptualHash.Distance(large, other);

        Assert.True(near <= 6, $"{algorithm}: same layout differs by {near} bits");
        Assert.True(far > near, $"{algorithm}: a different layout ({far} bits) must be further than the same layout ({near} bits)");
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void AUniformBrightnessChange_LeavesTheHashAlmostUnchanged(string algorithm)
    {
        var plain = Hash(algorithm, Layout(SceneKind.Results, 640));
        var brighter = Hash(algorithm, Layout(SceneKind.Results, 640, brightness: 25));

        Assert.True(PerceptualHash.Distance(plain, brighter) <= 4);
    }

    [Fact]
    public void Distance_CountsDifferingBits()
    {
        Assert.Equal(0, PerceptualHash.Distance(0xFFUL, 0xFFUL));
        Assert.Equal(64, PerceptualHash.Distance(0UL, ulong.MaxValue));
        Assert.Equal(3, PerceptualHash.Distance(0b1010UL, 0b0101UL ^ 0b1000UL));
        Assert.Equal(0.5, PerceptualHash.Similarity(0UL, 0x00000000FFFFFFFFUL));
    }

    [Fact]
    public void AverageHash_OfAHalfBrightImage_SetsExactlyTheBrightHalf()
    {
        var image = ImageBuffer.FromFunction(64, 64, (x, _) => (byte)(x < 32 ? 0 : 255));

        var hash = PerceptualHash.Average(image);

        // 8 x 8 cells; the right four columns of each of the 8 rows are above the mean.
        Assert.Equal(32, System.Numerics.BitOperations.PopCount(hash));
        Assert.Equal(0xF0F0F0F0F0F0F0F0UL, hash);
    }

    [Fact]
    public void DifferenceHash_OfAHorizontalRamp_IsAllZeroOrAllOne()
    {
        var rising = ImageBuffer.FromFunction(90, 80, (x, _) => (byte)(x * 255 / 89));
        var falling = ImageBuffer.FromFunction(90, 80, (x, _) => (byte)(255 - (x * 255 / 89)));

        Assert.Equal(0UL, PerceptualHash.Difference(rising)); // each pixel is darker than its right neighbour
        Assert.Equal(ulong.MaxValue, PerceptualHash.Difference(falling));
    }
}
