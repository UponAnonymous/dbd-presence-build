// Covers: C2 (OCR-noise corpus), FR-DET-04 (OCR + closed vocabulary with margin rule; ambiguous -> unknown),
// DETECTION_VALIDATION §11 (vocabulary-matcher margin logic, O/0, l/1, rn/m, missing diacritics, truncation).
// Mechanics only: the noise is invented, so these tests say nothing about how well Windows OCR reads the game (UNKNOWN).
using System.Text.Json;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Text;

public sealed class OcrNoiseCorpusTests
{
    private static readonly Vocabulary s_vocabulary = Vocabulary.LoadEmbedded();

    public sealed record CorpusCase(string Id, VocabularyKind Kind, string Ocr, string Category, string Expect, string? ExpectedId, string? ExpectedGroup)
    {
        public override string ToString() => $"{Id} [{Category}] '{Ocr}' -> {Expect}";
    }

    public static IReadOnlyList<CorpusCase> Cases { get; } = Load();

    public static TheoryData<CorpusCase> AllCases()
    {
        var data = new TheoryData<CorpusCase>();
        foreach (var c in Cases)
        {
            data.Add(c);
        }

        return data;
    }

    private static List<CorpusCase> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", "ocr-noise-corpus.json")));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        return [.. document.RootElement.GetProperty("cases").EnumerateArray().Select(e => new CorpusCase(
            e.GetProperty("id").GetString()!,
            Enum.Parse<VocabularyKind>(e.GetProperty("kind").GetString()!),
            e.GetProperty("ocr").GetString()!,
            e.GetProperty("category").GetString()!,
            e.GetProperty("expect").GetString()!,
            e.TryGetProperty("expectedId", out var id) ? id.GetString() : null,
            e.TryGetProperty("expectedGroup", out var group) ? group.GetString() : null))];
    }

    [Fact]
    public void Corpus_IsLargeAndCoversEveryNoiseClassTheValidationPlanNames()
    {
        Assert.True(Cases.Count >= 150, $"only {Cases.Count} cases");
        Assert.Equal(Cases.Count, Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var category in new[] { "l/1/I", "O/0", "rn/m", "vv/w", "S/5", "B/8", "dropped diacritic", "casing", "truncation", "dropped apostrophe", "curly apostrophe", "dropped The", "menu text", "other language", "garbage", "empty", "too short" })
        {
            Assert.Contains(Cases, c => c.Category.Contains(category, StringComparison.Ordinal));
        }

        foreach (var expect in new[] { "match", "group", "nomatch", "empty", "tooshort" })
        {
            Assert.Contains(Cases, c => c.Expect == expect);
        }

        foreach (var kind in Enum.GetValues<VocabularyKind>())
        {
            Assert.Contains(Cases, c => c.Kind == kind && c.Expect == "match");
        }
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Matcher_BehavesAsExpected_OnEveryCorpusCase(CorpusCase testCase)
    {
        var result = s_vocabulary.MatcherFor(testCase.Kind).Match(testCase.Ocr);

        switch (testCase.Expect)
        {
            case "match":
                Assert.True(result.IsAccepted, $"{testCase}: got {result.Outcome} (distance {result.Distance}, runner-up {result.RunnerUpDistance})");
                Assert.Equal(testCase.ExpectedId, result.Entry!.Id);
                break;
            case "group":
                Assert.True(result.Outcome == MatchOutcome.GroupOnly, $"{testCase}: got {result.Outcome}");
                Assert.Equal(testCase.ExpectedGroup, result.Group);
                Assert.Null(result.Entry);
                break;
            case "ambiguous":
                Assert.Equal(MatchOutcome.Ambiguous, result.Outcome);
                break;
            case "nomatch":
                Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
                break;
            case "empty":
                Assert.Equal(MatchOutcome.Empty, result.Outcome);
                break;
            case "tooshort":
                Assert.Equal(MatchOutcome.TooShort, result.Outcome);
                break;
            default:
                Assert.Fail($"unknown expectation '{testCase.Expect}' in {testCase.Id}");
                break;
        }

        // Whatever the outcome, only an accepted match may carry an entry: abstentions never leak a guess.
        Assert.Equal(result.IsAccepted, result.Entry is not null);
    }

    [Fact]
    public void NoAbstainingCase_EverProducesAnEntry()
    {
        foreach (var c in Cases.Where(c => c.Expect != "match"))
        {
            Assert.Null(s_vocabulary.MatcherFor(c.Kind).Match(c.Ocr).Entry);
        }
    }

    [Fact]
    public void ARandomTextCorpus_NeverMatchesAnything()
    {
        // 150 deterministic pseudo-random strings per vocabulary of letters and digits: with a 20% edit threshold none may be accepted.
        var state = 12345u;
        var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789 ";
        foreach (var kind in Enum.GetValues<VocabularyKind>())
        {
            var matcher = s_vocabulary.MatcherFor(kind);
            for (var i = 0; i < 150; i++)
            {
                var chars = new char[6 + (i % 18)];
                for (var j = 0; j < chars.Length; j++)
                {
                    state = unchecked((state * 1664525u) + 1013904223u);
                    chars[j] = alphabet[(int)((state >> 16) % (uint)alphabet.Length)];
                }

                var text = new string(chars);
                var result = matcher.Match(text);
                Assert.False(result.IsAccepted, $"{kind}: '{text}' was accepted as {result.Entry?.Name}");
            }
        }
    }
}
