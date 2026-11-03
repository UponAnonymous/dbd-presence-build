// Covers: FR-PACK-10 (helper exit codes map to typed results with user-facing messages; output dimension cap),
// FR-ART-03 (too large and unreadable images produce a clear message).
using System.Buffers.Binary;
using DbdPresence.Core.Images;

namespace DbdPresence.Core.Tests.Images;

public sealed class SafeImageResultTests
{
    [Theory]
    [InlineData(2, SafeImageStatus.BadInput)]
    [InlineData(3, SafeImageStatus.TooLarge)]
    [InlineData(4, SafeImageStatus.DecodeFailed)]
    [InlineData(5, SafeImageStatus.ResourceLimit)]
    [InlineData(6, SafeImageStatus.OutputTooLarge)]
    [InlineData(-1, SafeImageStatus.DecodeFailed)]
    [InlineData(255, SafeImageStatus.DecodeFailed)]
    public void ExitCodes_MapToTypedResultsWithMessages(int code, SafeImageStatus expected)
    {
        var result = SafeImageResult.FromExitCode(code);

        Assert.Equal(expected, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.UserMessage));
        Assert.Null(result.PngBytes);
    }

    [Fact]
    public void ResourceLimitExit_HasItsOwnUserMessage()
    {
        var result = SafeImageResult.FromExitCode(SafeImageResult.ExitResourceLimit);

        Assert.Equal(5, SafeImageResult.ExitResourceLimit);
        Assert.Contains("too much memory or time", result.UserMessage, StringComparison.Ordinal);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void EveryStatus_HasADistinctMessage()
    {
        var messages = Enum.GetValues<SafeImageStatus>().Select(SafeImageResult.MessageFor).ToArray();

        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Success_CarriesTheBytesAndSize()
    {
        var result = SafeImageResult.Success([1, 2, 3], 10, 20);

        Assert.Equal(SafeImageStatus.Ok, result.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.PngBytes);
        Assert.Equal((10, 20), (result.Width, result.Height));
    }

    [Fact]
    public void PngHeader_ReadsTheSizeFromIhdr()
    {
        Assert.True(PngHeader.TryReadSize(Png(640, 480), out var w, out var h));
        Assert.Equal((640, 480), (w, h));
    }

    [Theory]
    [InlineData(0u, 5u)]
    [InlineData(5u, 0u)]
    [InlineData(uint.MaxValue, 5u)]
    public void PngHeader_RejectsImpossibleSizes(uint width, uint height) =>
        Assert.False(PngHeader.TryReadSize(Png(width, height), out _, out _));

    [Fact]
    public void PngHeader_RejectsNonPngAndTruncatedData()
    {
        Assert.False(PngHeader.TryReadSize([0xFF, 0xD8, 0xFF, 0xE0], out _, out _));
        Assert.False(PngHeader.TryReadSize(Png(8, 8).AsSpan(0, 20), out _, out _));
        Assert.False(PngHeader.TryReadSize([], out _, out _));
    }

    private static byte[] Png(uint width, uint height)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), height);
        return data;
    }
}
