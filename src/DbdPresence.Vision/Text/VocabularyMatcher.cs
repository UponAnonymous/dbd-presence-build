namespace DbdPresence.Vision.Text;

/// <summary>An on-screen spelling of an entry that differs from its display name (the game prints "LEON S. KENNEDY" for "Leon Scott Kennedy").</summary>
/// <param name="Name">The spelling as the game prints it (casing and punctuation do not matter to the matcher).</param>
/// <param name="SeenOnScreen">Provenance: when and where it was seen. Required, because an alias that nobody has seen is a guess.</param>
public sealed record VocabularyAlias(string Name, string SeenOnScreen);

/// <summary>One name in a closed vocabulary.</summary>
/// <param name="Id">Stable canonical id.</param>
/// <param name="Name">English display name.</param>
/// <param name="Group">Optional parent (a map's realm id). Names that cannot be told apart inside one group resolve to the group.</param>
/// <param name="Aliases">Other spellings that map to the same id. Each is compared exactly like the name, so it can never match a different entry.</param>
/// <param name="SeenOnScreen">Provenance of the name itself when it was read from the game screen rather than the wiki; null when not yet seen on screen.</param>
/// <param name="Code">
/// The game's character code of a killer (<c>K27</c>) or survivor (<c>S01</c>), the prefix of its portrait file names; null for every other kind.
/// A code maps to exactly one id and never changes (PLAN_V2_S5B §2.1).
/// </param>
public sealed record VocabularyEntry(string Id, string Name, string? Group = null, IReadOnlyList<VocabularyAlias>? Aliases = null, string? SeenOnScreen = null, string? Code = null)
{
    /// <summary>Gets the alias spellings (never null).</summary>
    public IReadOnlyList<VocabularyAlias> Aliases { get; } = Aliases ?? [];
}

/// <summary>What the matcher decided.</summary>
public enum MatchOutcome
{
    /// <summary>One entry is close enough and clearly ahead of every other entry.</summary>
    Accepted,

    /// <summary>Several entries of the same group are about equally close: only the group (realm) is known.</summary>
    GroupOnly,

    /// <summary>The best candidates are too close to tell apart: abstain.</summary>
    Ambiguous,

    /// <summary>Nothing in the vocabulary is close enough: abstain.</summary>
    NoMatch,

    /// <summary>The text is too short to identify anything: abstain.</summary>
    TooShort,

    /// <summary>The text had no letters or digits: abstain.</summary>
    Empty,
}

/// <summary>The matcher's answer for one piece of OCR text.</summary>
/// <param name="Outcome">What was decided.</param>
/// <param name="Entry">The accepted entry, only for <see cref="MatchOutcome.Accepted"/>.</param>
/// <param name="Group">The shared group, for <see cref="MatchOutcome.Accepted"/> (the entry's group) and <see cref="MatchOutcome.GroupOnly"/>.</param>
/// <param name="Distance">Normalised distance to the best candidate, or null when nothing was compared.</param>
/// <param name="RunnerUpDistance">Normalised distance to the closest <em>different</em> entry, or null when there is none.</param>
/// <param name="Normalized">The normalised text that was compared.</param>
public sealed record VocabularyMatch(MatchOutcome Outcome, VocabularyEntry? Entry, string? Group, double? Distance, double? RunnerUpDistance, string Normalized)
{
    /// <summary>Gets a value indicating whether a single entry was identified.</summary>
    public bool IsAccepted => Outcome == MatchOutcome.Accepted;
}

/// <summary>Thresholds of the vocabulary matcher. The defaults are initial guesses until the tuning split measures τ and μ.</summary>
/// <param name="MaxDistance">τ: the best candidate's normalised edit distance must not exceed this.</param>
/// <param name="MinMargin">μ: the closest different entry must be at least this much (normalised) further away than the best.</param>
/// <param name="MinEditMargin">Alternatively, the closest different entry may be at least this many edits further away. Long names that differ
/// by one word (two map names that differ only in "East" and "West") are only a few percent apart, so the normalised margin alone would make an
/// exact reading ambiguous.</param>
/// <param name="MinLength">Texts shorter than this (after normalisation) are never matched, unless the vocabulary itself has a shorter name.</param>
public sealed record VocabularyMatcherOptions(double MaxDistance = 0.20, double MinMargin = 0.10, int MinEditMargin = 2, int MinLength = 4)
{
    /// <summary>Throws when the thresholds are unusable.</summary>
    public void Validate()
    {
        if (MaxDistance is <= 0 or >= 1 || MinMargin is <= 0 or >= 1 || MinEditMargin < 1 || MinLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDistance), "Require 0 < tau < 1, 0 < mu < 1, MinEditMargin >= 1 and MinLength >= 1.");
        }
    }
}

/// <summary>
/// Fuzzy-matches OCR text to a closed vocabulary (ADR-0004, FR-DET-04). OCR output is never trusted directly: it is
/// normalised and compared to every entry by edit distance after folding OCR-confusable characters. A match is
/// <b>accepted only if</b> <c>best ≤ τ</c> and the closest entry with a different id is clearly further away: by at least <c>μ</c>
/// (normalised) or by at least <c>MinEditMargin</c> edits. If the leading candidates are too close and all belong to one group
/// (the same realm), the answer is the group, otherwise the matcher abstains. Unmatched, ambiguous or too-short text never produces a guess.
/// </summary>
public sealed class VocabularyMatcher
{
    private readonly List<Candidate> _candidates = [];
    private readonly VocabularyMatcherOptions _options;
    private readonly int _minLength;

    /// <summary>Initializes a new instance of the <see cref="VocabularyMatcher"/> class.</summary>
    public VocabularyMatcher(IEnumerable<VocabularyEntry> entries, VocabularyMatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _options = options ?? new VocabularyMatcherOptions();
        _options.Validate();
        foreach (var entry in entries)
        {
            var keys = new List<string>();
            foreach (var spelling in entry.Aliases.Select(a => a.Name).Prepend(entry.Name))
            {
                var name = TextNormalizer.Normalize(spelling);
                if (name.Length == 0)
                {
                    throw new ArgumentException($"Vocabulary entry '{entry.Id}' has a name or alias with no usable characters.", nameof(entries));
                }

                keys.Add(TextNormalizer.Skeleton(name));
                if (name.StartsWith("the ", StringComparison.Ordinal) && name.Length - 4 >= 5)
                {
                    keys.Add(TextNormalizer.Skeleton(name[4..])); // OCR often drops a leading "The".
                }
            }

            _candidates.Add(new Candidate(entry, keys));
        }

        if (_candidates.Count == 0)
        {
            throw new ArgumentException("A vocabulary needs at least one entry.", nameof(entries));
        }

        // A vocabulary that really contains a very short name (a mode called "2v8") must be able to match it exactly.
        _minLength = Math.Min(_options.MinLength, _candidates.Min(c => c.Keys.Min(k => k.Length)));
    }

    /// <summary>Gets the number of entries.</summary>
    public int Count => _candidates.Count;

    /// <summary>Matches one piece of OCR text.</summary>
    public VocabularyMatch Match(string? ocrText)
    {
        var normalized = TextNormalizer.Normalize(ocrText);
        if (normalized.Length == 0)
        {
            return new VocabularyMatch(MatchOutcome.Empty, null, null, null, null, normalized);
        }

        if (normalized.Length < _minLength)
        {
            return new VocabularyMatch(MatchOutcome.TooShort, null, null, null, null, normalized);
        }

        var skeleton = TextNormalizer.Skeleton(normalized);
        var ranked = _candidates
            .Select(c => Rank(c, skeleton))
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.Edits)
            .ThenBy(r => r.Entry.Id, StringComparer.Ordinal)
            .ToList();

        var best = ranked[0];
        double? runnerUpDistance = ranked.Count > 1 ? ranked[1].Distance : null;

        if (best.Distance > _options.MaxDistance)
        {
            return new VocabularyMatch(MatchOutcome.NoMatch, null, null, best.Distance, runnerUpDistance, normalized);
        }

        if (ranked.Count == 1 || Separated(best, ranked[1]))
        {
            return new VocabularyMatch(MatchOutcome.Accepted, best.Entry, best.Entry.Group, best.Distance, runnerUpDistance, normalized);
        }

        // Too close to call. If every entry inside the margin belongs to one group, the group is still known.
        var contenders = ranked.TakeWhile(r => ReferenceEquals(r.Entry, best.Entry) || !Separated(best, r)).ToList();
        var group = contenders[0].Entry.Group;
        if (group is not null && contenders.All(c => string.Equals(c.Entry.Group, group, StringComparison.Ordinal)))
        {
            return new VocabularyMatch(MatchOutcome.GroupOnly, null, group, best.Distance, runnerUpDistance, normalized);
        }

        return new VocabularyMatch(MatchOutcome.Ambiguous, null, null, best.Distance, runnerUpDistance, normalized);
    }

    private static Ranked Rank(Candidate candidate, string skeleton)
    {
        var best = candidate.Keys
            .Select(k => (Edits: TextNormalizer.Levenshtein(skeleton, k), Longest: Math.Max(skeleton.Length, k.Length)))
            .OrderBy(k => (double)k.Edits / k.Longest)
            .First();
        return new Ranked(candidate.Entry, best.Edits, (double)best.Edits / best.Longest);
    }

    private bool Separated(Ranked best, Ranked other) =>
        other.Distance - best.Distance >= _options.MinMargin || other.Edits - best.Edits >= _options.MinEditMargin;

    private sealed record Ranked(VocabularyEntry Entry, int Edits, double Distance);

    private sealed record Candidate(VocabularyEntry Entry, IReadOnlyList<string> Keys);
}
