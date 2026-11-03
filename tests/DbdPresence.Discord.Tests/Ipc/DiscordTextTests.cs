// Covers: FR-RPC-08 (Discord's text is sanitised before it can reach health or the UI; security review S-M2).
using System.Text;
using DbdPresence.Discord.Ipc;

namespace DbdPresence.Discord.Tests.Ipc;

public sealed class DiscordTextTests
{
    [Fact]
    public void NewlinesAndTabsBecomeSingleSpaces()
    {
        Assert.Equal("first second third fourth", DiscordText.Sanitize("first\r\nsecond\t\tthird   \n\n fourth"));
    }

    [Fact]
    public void BidirectionalOverridesAndOtherFormatCharactersAreDropped()
    {
        // U+202E RIGHT-TO-LEFT OVERRIDE, U+2066 LEFT-TO-RIGHT ISOLATE, U+200B ZERO WIDTH SPACE, U+FEFF BOM.
        Assert.Equal("exe.txt", DiscordText.Sanitize("exe\u202E.\u2066txt\u200B\uFEFF"));
    }

    [Fact]
    public void LineAndParagraphSeparatorsAndControlCharactersAreDropped()
    {
        Assert.Equal("abc[31mdef", DiscordText.Sanitize("a\u2028b\u2029c\u001B[31m\u0000d\u0007ef"));
    }

    [Fact]
    public void LeadingAndTrailingWhitespaceIsRemoved()
    {
        Assert.Equal("hello", DiscordText.Sanitize("  \n hello \t "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("\u202E\u200B\u2028")]
    public void NothingPrintable_YieldsNull(string? raw)
    {
        Assert.Null(DiscordText.Sanitize(raw));
    }

    [Fact]
    public void ATenKilobyteMessage_IsCutToTwoHundredCharactersEndingInAnEllipsis()
    {
        var result = DiscordText.Sanitize(new string('x', 10_000));

        Assert.NotNull(result);
        Assert.Equal(DiscordText.MaxLength, result.Length);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
        Assert.Equal(new string('x', 199), result[..199]);
    }

    [Fact]
    public void ExactlyTwoHundredCharacters_AreKeptWithoutAnEllipsis()
    {
        var text = new string('y', 200);

        Assert.Equal(text, DiscordText.Sanitize(text));
    }

    [Fact]
    public void ACutNeverSplitsASurrogatePair()
    {
        // 198 characters, then a 2-char emoji ending exactly at 200, then one more character forces the cut.
        var result = DiscordText.Sanitize(new string('x', 198) + "😀" + "y");

        Assert.Equal(new string('x', 198) + "…", result);
        Assert.DoesNotContain(result!, char.IsSurrogate);
    }

    [Fact]
    public void EmojiAndNonLatinTextSurvive()
    {
        Assert.Equal("Ungültige Anwendung 😀 日本語", DiscordText.Sanitize("Ungültige Anwendung 😀 日本語"));
    }

    [Fact]
    public void QuotePresentsTheTextAsDiscordsOwnWords()
    {
        Assert.Equal("Discord said: \"Invalid Client ID\"", DiscordText.Quote("Invalid Client ID"));
    }

    [Fact]
    public void ParseFrame_SanitisesTheErrorMessage()
    {
        var payload = Encoding.UTF8.GetBytes("""{"cmd":"SET_ACTIVITY","evt":"ERROR","data":{"code":4000,"message":"bad\n\u202Ething\r\n"}}""");

        var message = IpcMessage.ParseFrame(payload);

        Assert.Equal("bad thing", message.Message);
        Assert.Equal(4000, message.Code);
    }

    [Fact]
    public void ParseFrame_CapsAHugeMessage()
    {
        var payload = Encoding.UTF8.GetBytes("{\"evt\":\"ERROR\",\"data\":{\"code\":1,\"message\":\"" + new string('m', 10_000) + "\"}}");

        var message = IpcMessage.ParseFrame(payload);

        Assert.Equal(DiscordText.MaxLength, message.Message!.Length);
    }

    [Fact]
    public void ParseClose_SanitisesTheMessage()
    {
        var payload = Encoding.UTF8.GetBytes("""{"code":4000,"message":"line one\nline\u2028two\u202E"}""");

        Assert.Equal("line one linetwo", IpcMessage.ParseClose(payload).Message);
    }
}
