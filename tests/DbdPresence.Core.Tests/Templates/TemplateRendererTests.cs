// Covers: FR-TPL-03 (tokens only from confirmed values; optional segments drop; mandatory tokens fail the field).
using DbdPresence.Core.Templates;

namespace DbdPresence.Core.Tests.Templates;

public sealed class TemplateRendererTests
{
    private static string? Render(string template, params (string Key, string Value)[] values) =>
        TemplateRenderer.Render(TemplateParser.Parse(template), values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Render_AllValuesPresent_SubstitutesTokens()
    {
        Assert.Equal("In a trial · Coal Tower", Render("In a trial[ · {map}]", ("map", "Coal Tower")));
    }

    [Fact]
    public void Render_OptionalSegmentWithMissingToken_IsDropped()
    {
        Assert.Equal("In a trial", Render("In a trial[ · {map}]"));
    }

    [Fact]
    public void Render_MandatoryTokenMissing_ReturnsNull()
    {
        Assert.Null(Render("Hunting on {map}"));
    }

    [Fact]
    public void Render_WhitespaceOnlyValue_CountsAsMissing()
    {
        Assert.Null(Render("{map}", ("map", "   ")));
    }

    [Fact]
    public void Render_InvalidTemplate_ReturnsNull()
    {
        Assert.Null(Render("{map", ("map", "x")));
    }

    [Theory]
    [InlineData("[{role} · ]{character}", "Meg")]
    [InlineData("Playing  [{map}]  ·", "Playing")]
    [InlineData("[{map}] - In a trial", "In a trial")]
    public void Render_DroppedSegments_LeaveNoDanglingSeparatorsOrDoubleSpaces(string template, string expected)
    {
        Assert.Equal(expected, Render(template, ("character", "Meg")));
    }

    [Fact]
    public void Render_ValuesAreTrimmed()
    {
        Assert.Equal("As Killer", Render("As {role}", ("role", "  Killer ")));
    }

    [Fact]
    public void Render_EveryDocumentedTokenCanBeRendered()
    {
        foreach (var token in TemplateTokens.Known.Keys)
        {
            Assert.Equal("v", Render($"{{{token}}}", (token, "v")));
        }
    }
}
