// Covers: red-team L1 (what wsl.exe prints is decoded from what the bytes are, UTF-16 or UTF-8, so a list of names is never read as noise and so as "empty"; only plausible names count; a warning is not a
// machine; the last line of a message is found without NUL characters). docs/owner/free-ci.md.
using System.Text;

namespace DbdPresence.LocalCi.Tests;

public sealed class WslOutputTests
{
    [Fact]
    public void Utf16LittleEndian_WithAndWithoutAByteOrderMark_IsRead()
    {
        var plain = Encoding.Unicode.GetBytes("Ubuntu\r\nDebian\r\n");
        var withMark = Encoding.Unicode.GetPreamble().Concat(plain).ToArray();

        Assert.Equal("Ubuntu\r\nDebian\r\n", WslOutput.Decode(plain));
        Assert.Equal("Ubuntu\r\nDebian\r\n", WslOutput.Decode(withMark));
    }

    [Fact]
    public void Utf16BigEndian_WithAndWithoutAMark_IsRead()
    {
        var plain = Encoding.BigEndianUnicode.GetBytes("Ubuntu\n");
        var withMark = Encoding.BigEndianUnicode.GetPreamble().Concat(plain).ToArray();

        Assert.Equal("Ubuntu\n", WslOutput.Decode(plain));
        Assert.Equal("Ubuntu\n", WslOutput.Decode(withMark));
    }

    [Fact]
    public void Utf8_WithAndWithoutAMark_IsRead()
    {
        var plain = Encoding.UTF8.GetBytes("Ubuntu-\u00e9\n");
        var withMark = Encoding.UTF8.GetPreamble().Concat(plain).ToArray();

        Assert.Equal("Ubuntu-\u00e9\n", WslOutput.Decode(plain));
        Assert.Equal("Ubuntu-\u00e9\n", WslOutput.Decode(withMark));
    }

    [Fact]
    public void ShortAndEmptyInput_IsRead()
    {
        Assert.Equal(string.Empty, WslOutput.Decode([]));
        Assert.Equal("a", WslOutput.Decode("a"u8));
        Assert.Equal("a", WslOutput.Decode(Encoding.Unicode.GetBytes("a")));
    }

    [Fact]
    public void NamesAreFound_NoMatterTheEncoding_SoAWrongGuessCanNeverMakeAnEmptyList()
    {
        string[] names = ["Ubuntu", "dbd-ci-job-20261029120000", "dbd-ci-base"];
        var text = string.Concat(names.Select(n => n + "\r\n"));

        foreach (var bytes in new[] { Encoding.Unicode.GetBytes(text), Encoding.UTF8.GetBytes(text), Encoding.BigEndianUnicode.GetBytes(text) })
        {
            Assert.Equal(names, WslOutput.ParseDistributions(WslOutput.Decode(bytes)));
        }
    }

    [Fact]
    public void OnlyPlausibleNamesCount_ANoticeIsNotAMachine()
    {
        const string Text = "\uFEFFUbuntu\r\nWSL has a new version\r\n  dbd-ci-base  \r\n-d\r\n\r\n.hidden\r\nA_b.c-d\r\n";

        Assert.Equal(["Ubuntu", "dbd-ci-base", "A_b.c-d"], WslOutput.ParseDistributions(Text));
    }

    [Fact]
    public void ANameThatIsTooLong_IsNotAMachine()
    {
        Assert.Equal([new string('a', 64)], WslOutput.ParseDistributions(new string('a', 64) + "\n" + new string('a', 65) + "\n"));
    }

    [Fact]
    public void NulCharactersAndCarriageReturns_AreRemovedBeforeParsing()
    {
        var utf16ReadAsUtf8 = string.Join('\0', "Ubuntu".ToCharArray()) + "\0\r\0\n\0";

        Assert.Equal(["Ubuntu"], WslOutput.ParseDistributions(utf16ReadAsUtf8));
    }

    [Theory]
    [InlineData("first\nsecond\nlast message\n", "last message")]
    [InlineData("only\n", "only")]
    [InlineData("a\r\nb\r\n\r\n", "b")]
    [InlineData("", "no message")]
    [InlineData("\n\n  \n", "no message")]
    [InlineData("e\0r\0r\0\n", "err")]
    public void TheLastLine_IsFoundWithoutNulCharacters(string output, string expected)
    {
        Assert.Equal(expected, WslOutput.LastLine(output));
    }

    [Fact]
    public void StrippingNuls_AlsoRemovesCarriageReturns_AndKeepsTheRest()
    {
        Assert.Equal("w s\n", WslOutput.StripNuls("w\0 \0s\r\n"));
    }

    [Fact]
    public void NullInput_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => WslOutput.ParseDistributions(null!));
        Assert.Throws<ArgumentNullException>(() => WslOutput.LastLine(null!));
        Assert.Throws<ArgumentNullException>(() => WslOutput.StripNuls(null!));
    }
}
