using System.Collections.Immutable;
using System.Text;

namespace DbdPresence.Core.Templates;

/// <summary>
/// Parses presence templates (FR-TPL-02). Grammar:
/// <c>{token}</c> inserts a value; <c>[ … ]</c> is an optional segment (no nesting) that disappears
/// when any token inside it has no value; <c>{{ }} [[ ]]</c> are literal braces and brackets.
/// </summary>
public static class TemplateParser
{
    /// <summary>Parses <paramref name="source"/>. Never throws for malformed input; errors are returned.</summary>
    public static ParsedTemplate Parse(string? source)
    {
        source ??= string.Empty;
        var errors = ImmutableArray.CreateBuilder<TemplateError>();
        var top = ImmutableArray.CreateBuilder<TemplatePart>();
        ImmutableArray<TemplatePart>.Builder? optional = null;
        var optionalStart = 0;
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                (optional ?? top).Add(new LiteralPart(literal.ToString()));
                literal.Clear();
            }
        }

        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            switch (c)
            {
                case '{' when next == '{':
                case '}' when next == '}':
                case '[' when next == '[':
                case ']' when next == ']':
                    literal.Append(c);
                    i += 2;
                    continue;
                case '{':
                    {
                        var close = source.IndexOf('}', i + 1);
                        if (close < 0)
                        {
                            errors.Add(new(i + 1, "Unclosed '{' — add '}' or write '{{' for a literal brace."));
                            i = source.Length;
                            continue;
                        }

                        var name = source[(i + 1)..close].Trim();
                        if (TokenError(name) is { } message)
                        {
                            errors.Add(new(i + 1, message));
                        }
                        else
                        {
                            FlushLiteral();
                            (optional ?? top).Add(new TokenPart(Canonical(name)));
                        }

                        i = close + 1;
                        continue;
                    }

                case '}':
                    errors.Add(new(i + 1, "Unexpected '}' — write '}}' for a literal brace."));
                    i++;
                    continue;
                case '[':
                    if (optional is not null)
                    {
                        errors.Add(new(i + 1, "Optional segments cannot be nested — close the previous '[' first."));
                        i++;
                        continue;
                    }

                    FlushLiteral();
                    optional = ImmutableArray.CreateBuilder<TemplatePart>();
                    optionalStart = i;
                    i++;
                    continue;
                case ']':
                    if (optional is null)
                    {
                        errors.Add(new(i + 1, "Unexpected ']' — write ']]' for a literal bracket."));
                        i++;
                        continue;
                    }

                    FlushLiteral();
                    top.Add(new OptionalPart(optional.ToImmutable()));
                    optional = null;
                    i++;
                    continue;
                default:
                    literal.Append(c);
                    i++;
                    continue;
            }
        }

        if (optional is not null)
        {
            errors.Add(new(optionalStart + 1, "Unclosed '[' — add ']' to end the optional segment."));
        }

        FlushLiteral();
        return errors.Count > 0
            ? new ParsedTemplate(source, [], errors.ToImmutable())
            : new ParsedTemplate(source, top.ToImmutable(), []);
    }

    private static string? TokenError(string name)
    {
        if (name.Length == 0)
        {
            return "Empty token '{}' — choose a token such as {map}.";
        }

        if (TemplateTokens.Rejected.TryGetValue(name, out var why))
        {
            return $"{{{name}}} is not supported: {why}";
        }

        return TemplateTokens.Known.ContainsKey(name) || TemplateTokens.Aliases.ContainsKey(name)
            ? null
            : $"Unknown token {{{name}}} — available: {string.Join(", ", TemplateTokens.Known.Keys.Order(StringComparer.Ordinal).Select(k => $"{{{k}}}"))}.";
    }

    private static string Canonical(string name) =>
        TemplateTokens.Aliases.TryGetValue(name, out var canonical) ? canonical : name;
}
