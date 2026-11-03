using System.Collections.Immutable;

namespace DbdPresence.Core.Templates;

/// <summary>A piece of a template.</summary>
public abstract record TemplatePart;

/// <summary>Literal text.</summary>
/// <param name="Text">The text, with escapes already resolved.</param>
public sealed record LiteralPart(string Text) : TemplatePart;

/// <summary>A token reference (canonical name).</summary>
/// <param name="Name">Canonical token name.</param>
public sealed record TokenPart(string Name) : TemplatePart;

/// <summary>An optional segment: rendered only if every token inside it has a value.</summary>
/// <param name="Parts">Literal and token parts inside the brackets.</param>
public sealed record OptionalPart(ImmutableArray<TemplatePart> Parts) : TemplatePart;

/// <summary>A template error with a 1-based column for inline display (FR-TPL-04).</summary>
/// <param name="Column">1-based column of the problem.</param>
/// <param name="Message">What is wrong and how to fix it.</param>
public sealed record TemplateError(int Column, string Message);

/// <summary>The result of parsing a template string.</summary>
public sealed class ParsedTemplate
{
    internal ParsedTemplate(string source, ImmutableArray<TemplatePart> parts, ImmutableArray<TemplateError> errors)
    {
        Source = source;
        Parts = parts;
        Errors = errors;
    }

    /// <summary>Gets the original text.</summary>
    public string Source { get; }

    /// <summary>Gets the parsed parts (empty when there are errors).</summary>
    public ImmutableArray<TemplatePart> Parts { get; }

    /// <summary>Gets the parse errors.</summary>
    public ImmutableArray<TemplateError> Errors { get; }

    /// <summary>Gets a value indicating whether the template parsed without errors.</summary>
    public bool IsValid => Errors.IsEmpty;

    /// <summary>Gets the canonical tokens used anywhere in the template.</summary>
    public IEnumerable<string> Tokens => Parts.SelectMany(Flatten).OfType<TokenPart>().Select(t => t.Name).Distinct(StringComparer.Ordinal);

    private static IEnumerable<TemplatePart> Flatten(TemplatePart part) =>
        part is OptionalPart optional ? optional.Parts : [part];
}
