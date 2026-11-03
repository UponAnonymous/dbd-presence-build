// Covers: FR-RPC-04 (stricter of characters and UTF-8 bytes; grapheme-safe truncation).
using System.Globalization;
using DbdPresence.Core.Presence;

namespace DbdPresence.Core.Tests.Presence;

public sealed class TextFitTests
{
    [Fact]
    public void Fit_ShortText_IsUnchanged()
    {
        Assert.Equal("In a trial", TextFit.Fit("In a trial", 128, 128));
    }

    [Fact]
    public void Fit_AsciiOverLimit_TruncatesWithEllipsisWithinBothLimits()
    {
        var result = TextFit.Fit(new string('a', 200), 128, 128);

        Assert.EndsWith(TextFit.Ellipsis, result, StringComparison.Ordinal);
        Assert.True(result.Length <= 128);
        Assert.True(TextFit.Utf8Length(result) <= 128);
    }

    [Fact]
    public void Fit_MultiByteText_RespectsByteLimitEvenWhenCharactersFit()
    {
        // 60 × "é" (2 bytes each) = 60 chars, 120 bytes; 70 × = 70 chars, 140 bytes > 128.
        var result = TextFit.Fit(new string('é', 70), 128, 128);

        Assert.True(TextFit.Utf8Length(result) <= 128);
        Assert.EndsWith(TextFit.Ellipsis, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("👩‍👩‍👧‍👦")] // ZWJ family sequence
    [InlineData("🏳️‍🌈")]
    [InlineData("e\u0301")] // combining acute accent
    [InlineData("🇬🇧")] // regional indicator pair
    public void Fit_NeverSplitsGraphemeClusters(string cluster)
    {
        var text = string.Concat(Enumerable.Repeat(cluster, 60));

        var result = TextFit.Fit(text, 128, 128);
        var body = result[..^TextFit.Ellipsis.Length];

        Assert.True(result.Length <= 128 && TextFit.Utf8Length(result) <= 128);
        var elements = StringInfo.GetTextElementEnumerator(body);
        while (elements.MoveNext())
        {
            Assert.Equal(cluster, elements.GetTextElement());
        }
    }

    [Fact]
    public void Fit_RightToLeftText_StaysWithinLimits()
    {
        var result = TextFit.Fit(string.Concat(Enumerable.Repeat("שלום ", 40)), 128, 128);

        Assert.True(TextFit.Fits(result, 128, 128));
    }
}
