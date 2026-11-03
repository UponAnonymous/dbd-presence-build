// Covers: FR-TPL-02 (syntax, errors with column positions), FR-TPL-04 (inline validation messages).
using DbdPresence.Core.Templates;

namespace DbdPresence.Core.Tests.Templates;

public sealed class TemplateParserTests
{
    [Fact]
    public void Parse_PlainText_IsSingleLiteral()
    {
        var parsed = TemplateParser.Parse("In a lobby");

        Assert.True(parsed.IsValid);
        Assert.Equal(new LiteralPart("In a lobby"), Assert.Single(parsed.Parts));
    }

    [Fact]
    public void Parse_TokensAndOptionalSegments_ProducesExpectedParts()
    {
        var parsed = TemplateParser.Parse("In a trial[ · {map}] as {role}");

        Assert.True(parsed.IsValid);
        Assert.Collection(
            parsed.Parts,
            p => Assert.Equal(new LiteralPart("In a trial"), p),
            p => Assert.Equal([new LiteralPart(" · "), new TokenPart("map")], Assert.IsType<OptionalPart>(p).Parts),
            p => Assert.Equal(new LiteralPart(" as "), p),
            p => Assert.Equal(new TokenPart("role"), p));
        Assert.Equal(["map", "role"], parsed.Tokens.Order());
    }

    [Fact]
    public void Parse_Escapes_BecomeLiterals()
    {
        var parsed = TemplateParser.Parse("{{literal}} [[x]]");

        Assert.True(parsed.IsValid);
        Assert.Equal(new LiteralPart("{literal} [x]"), Assert.Single(parsed.Parts));
    }

    [Fact]
    public void Parse_Alias_IsCanonicalised()
    {
        var parsed = TemplateParser.Parse("{eliminations} out");

        Assert.Equal(new TokenPart("status_dead"), parsed.Parts[0]);
    }

    [Theory]
    [InlineData("Hello {map", 7, "Unclosed '{'")]
    [InlineData("Hello }", 7, "Unexpected '}'")]
    [InlineData("A [b", 3, "Unclosed '['")]
    [InlineData("A ]", 3, "Unexpected ']'")]
    [InlineData("[a [b]]", 4, "cannot be nested")]
    [InlineData("x {}", 3, "Empty token")]
    [InlineData("x {mapp}", 3, "Unknown token {mapp}")]
    [InlineData("{hooks} hooks", 1, "not observable")]
    [InlineData("vs {killer}", 4, "opponent information")]
    public void Parse_Invalid_ReportsColumnAndMessage(string source, int column, string fragment)
    {
        var parsed = TemplateParser.Parse(source);

        Assert.False(parsed.IsValid);
        Assert.Empty(parsed.Parts);
        Assert.Contains(parsed.Errors, e => e.Column == column && e.Message.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_Null_IsEmptyValidTemplate()
    {
        var parsed = TemplateParser.Parse(null);

        Assert.True(parsed.IsValid);
        Assert.Empty(parsed.Parts);
    }

    [Fact]
    public void Parse_TokenNameIsTrimmed()
    {
        Assert.Equal(new TokenPart("map"), TemplateParser.Parse("{ map }").Parts[0]);
    }

    [Fact]
    public void Parse_RandomInput_NeverThrows()
    {
        var random = new Random(1234);
        const string alphabet = "{}[]ab {map}·";
        for (var i = 0; i < 2000; i++)
        {
            var chars = Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var parsed = TemplateParser.Parse(new string(chars));
            Assert.True(parsed.IsValid ^ !parsed.Errors.IsEmpty);
        }
    }
}
