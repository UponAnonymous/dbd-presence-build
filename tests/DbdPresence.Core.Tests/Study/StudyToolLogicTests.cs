// Covers: study tool (QUALITY_GATES C6 data collection, SAFETY_AND_PRIVACY section 3): lossless PNG output that strict validation
// accepts, the 2-second cadence, one extra frame per settled window resize, and the 2 GB per-session cap.
using System.IO.Compression;
using DbdPresence.Core.Images;
using DbdPresence.StudyCapture;

namespace DbdPresence.Core.Tests.Study;

public sealed class StudyToolLogicTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PngEncoder_ProducesAFileThatStrictValidationAcceptsWithTheRightPixels()
    {
        // 3x2 image: B,G,R,A per pixel.
        byte[] bgra =
        [
            0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255,
            10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255,
        ];

        var png = PngEncoder.Encode(3, 2, bgra);

        Assert.True(StrictPng.TryValidate(png, out var width, out var height, out var failure), failure);
        Assert.Equal((3, 2), (width, height));
        Assert.Equal(
            new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 30, 20, 10, 60, 50, 40, 90, 80, 70 }.Select(b => (int)b),
            ReadScanlines(png).Select(b => (int)b));
    }

    [Fact]
    public void PngEncoder_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(2, 2, new byte[15]));
    }

    [Fact]
    public void Policy_SavesFirstFrameThenEveryTwoSeconds()
    {
        var policy = new StudyCapturePolicy();

        Assert.Equal(FrameReason.Scheduled, policy.Decide(s_t0, 1920, 1080));
        policy.RecordSaved(s_t0, 1920, 1080, 1000);

        Assert.Equal(FrameReason.None, policy.Decide(s_t0.AddMilliseconds(1999), 1920, 1080));
        Assert.Equal(FrameReason.Scheduled, policy.Decide(s_t0.AddSeconds(2), 1920, 1080));
    }

    [Fact]
    public void Policy_TakesOneExtraFrameOnlyAfterANewSizeSettles()
    {
        var policy = new StudyCapturePolicy();
        policy.RecordSaved(s_t0, 1920, 1080, 1000);

        // A drag-resize passes through several sizes: nothing is saved while the size keeps changing.
        Assert.Equal(FrameReason.None, policy.Decide(s_t0.AddMilliseconds(100), 1700, 900));
        Assert.Equal(FrameReason.None, policy.Decide(s_t0.AddMilliseconds(400), 1500, 800));
        Assert.Equal(FrameReason.None, policy.Decide(s_t0.AddMilliseconds(1300), 1500, 800));
        Assert.Equal(FrameReason.Resize, policy.Decide(s_t0.AddMilliseconds(1400), 1500, 800));

        policy.RecordSaved(s_t0.AddMilliseconds(1400), 1500, 800, 1000);

        Assert.Equal(FrameReason.None, policy.Decide(s_t0.AddMilliseconds(1500), 1500, 800));
        Assert.Equal(FrameReason.Scheduled, policy.Decide(s_t0.AddMilliseconds(3400), 1500, 800));
    }

    [Fact]
    public void Policy_AScheduledFrameWinsOverAResizeFrameWhenBothAreDue()
    {
        var policy = new StudyCapturePolicy();
        policy.RecordSaved(s_t0, 1920, 1080, 1000);
        policy.Decide(s_t0.AddMilliseconds(1000), 1280, 720);

        Assert.Equal(FrameReason.Scheduled, policy.Decide(s_t0.AddSeconds(3), 1280, 720));
    }

    [Fact]
    public void Policy_StopsAtTwoGigabytes()
    {
        var policy = new StudyCapturePolicy();
        policy.RecordSaved(s_t0, 1920, 1080, StudyCapturePolicy.MaxSessionBytes - 1);
        Assert.False(policy.CapReached);

        policy.RecordSaved(s_t0.AddSeconds(2), 1920, 1080, 1);

        Assert.True(policy.CapReached);
        Assert.Equal(FrameReason.None, policy.Decide(s_t0.AddSeconds(10), 1920, 1080));
        Assert.Equal(2L * 1024 * 1024 * 1024, StudyCapturePolicy.MaxSessionBytes);
    }

    [Fact]
    public void Policy_IgnoresEmptySizes()
    {
        Assert.Equal(FrameReason.None, new StudyCapturePolicy().Decide(s_t0, 0, 0));
    }

    private static byte[] ReadScanlines(byte[] png)
    {
        using var data = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
            {
                data.Write(png, offset + 8, length);
            }

            offset += 12 + length;
        }

        data.Position = 0;
        using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        using var result = new MemoryStream();
        zlib.CopyTo(result);
        return result.ToArray();
    }
}
