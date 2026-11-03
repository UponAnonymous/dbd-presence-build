// Covers: FR-PACK-14 (own links: https only, at most 512 characters, no credentials, no IP literal, no owner hosts, localhost, .local or single-label
// names; validated as text and never fetched), PLAN_V2 section 2.5, PLAN_V2_S5B section 2.6.
using DbdPresence.Core.Pictures;

namespace DbdPresence.Core.Tests.Pictures;

public sealed class LinkPolicyTests
{
    [Theory]
    [InlineData("https://example.com/portrait.png")]
    [InlineData("  https://Example.COM/a/b.jpg?x=1  ")]
    [InlineData("https://images.example.co.uk:8443/a.png")]
    [InlineData("https://xn--bcher-kva.example/a.png")]
    public void Check_AcceptsPublicHttpsLinks(string link)
    {
        var check = LinkPolicy.Check(link);

        Assert.True(check.IsValid);
        Assert.Null(check.Message);
        Assert.StartsWith("https://", check.Normalised, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com/a.png", LinkProblem.NotHttps)]
    [InlineData("ftp://example.com/a.png", LinkProblem.NotHttps)]
    [InlineData("//example.com/a.png", LinkProblem.NotHttps)]
    [InlineData("example.com/a.png", LinkProblem.NotHttps)]
    [InlineData("javascript:alert(1)", LinkProblem.NotHttps)]
    [InlineData("file:///C:/a.png", LinkProblem.NotHttps)]
    [InlineData("", LinkProblem.NotALink)]
    [InlineData("   ", LinkProblem.NotALink)]
    [InlineData("https://", LinkProblem.NotALink)]
    [InlineData("https://user:pw@example.com/a.png", LinkProblem.HasCredentials)]
    [InlineData("https://user@example.com/a.png", LinkProblem.HasCredentials)]
    [InlineData("https://127.0.0.1/a.png", LinkProblem.IpAddress)]
    [InlineData("https://192.168.1.5/a.png", LinkProblem.IpAddress)]
    [InlineData("https://[::1]/a.png", LinkProblem.IpAddress)]
    [InlineData("https://[2001:db8::1]/a.png", LinkProblem.IpAddress)]
    [InlineData("https://2130706433/a.png", LinkProblem.IpAddress)]
    [InlineData("https://1.2.3.4.5/a.png", LinkProblem.IpAddress)] // review F10, mutant M2: parsed as a DNS name by .NET, refused by the last-label rule
    [InlineData("https://999.1.1.1/a.png", LinkProblem.IpAddress)]
    [InlineData("https://example.0xff/a.png", LinkProblem.IpAddress)]
    [InlineData("https://localhost/a.png", LinkProblem.LocalHost)]
    [InlineData("https://printer.local/a.png", LinkProblem.LocalHost)]
    [InlineData("https://intranet/a.png", LinkProblem.LocalHost)]
    [InlineData("https://api.arcavale.com/v1/me", LinkProblem.OwnerHost)]
    [InlineData("https://cdn.arcavale.com/u/abc.png", LinkProblem.OwnerHost)]
    [InlineData("https://arcavale.com/a.png", LinkProblem.OwnerHost)]
    [InlineData("https://example.com/a b.png", LinkProblem.BadCharacters)]
    [InlineData("https://example.com/a\nb.png", LinkProblem.BadCharacters)]
    public void Check_RefusesWithTheRightReason(string link, LinkProblem expected)
    {
        var check = LinkPolicy.Check(link);

        Assert.False(check.IsValid);
        Assert.Equal(expected, check.Problem);
        Assert.Null(check.Normalised);
        Assert.False(string.IsNullOrEmpty(check.Message));
    }

    [Fact]
    public void Check_RefusesMoreThan512Characters_AcceptsExactly512()
    {
        var prefix = "https://example.com/";
        var exactly = prefix + new string('a', PictureLimits.MaxLinkLength - prefix.Length);

        Assert.True(LinkPolicy.Check(exactly).IsValid);
        Assert.Equal(LinkProblem.TooLong, LinkPolicy.Check(exactly + "a").Problem);
    }

    [Fact]
    public void Check_DoesNotAcceptAnArcavaleLookalikeAsOwnerHost()
    {
        Assert.True(LinkPolicy.Check("https://notarcavale.com/a.png").IsValid);
        Assert.Equal(LinkProblem.OwnerHost, LinkPolicy.Check("https://CDN.Arcavale.COM/a.png").Problem);
    }
}
