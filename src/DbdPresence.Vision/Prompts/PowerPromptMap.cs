using System.Globalization;
using System.Text.Json;
using DbdPresence.Vision.Text;
using static DbdPresence.Vision.JsonFields;

namespace DbdPresence.Vision.Prompts;

/// <summary>Where a prompt entry came from.</summary>
public enum PromptSource
{
    /// <summary>Listed in the committed file, each with the date and place it was seen on the owner's screen.</summary>
    Seeded,

    /// <summary>Learned on this PC from the owner's own matches (see <see cref="LearnedPromptStore"/>); never shipped.</summary>
    Learned,
}

/// <summary>One prompt the map knows.</summary>
/// <param name="Id">A unique id; learned entries get a generated one.</param>
/// <param name="Text">The prompt as it is printed on screen.</param>
/// <param name="KillerId">The killer whose power prints this prompt.</param>
/// <param name="Source">Whether it is seeded or learned.</param>
/// <param name="SeenOnScreen">Where and when it was seen (seeded entries must say; learned entries say how many matches).</param>
public sealed record PromptEntry(string Id, string Text, string KillerId, PromptSource Source, string SeenOnScreen);

/// <summary>What a piece of recognised text matched.</summary>
/// <param name="Accepted">Whether exactly one prompt of the map was identified.</param>
/// <param name="PromptId">The prompt id, when accepted.</param>
/// <param name="KillerId">The killer of that prompt, when accepted.</param>
/// <param name="Score">1 minus the normalised edit distance to the prompt, when accepted; otherwise 0.</param>
public readonly record struct PromptMatch(bool Accepted, string? PromptId, string? KillerId, double Score)
{
    /// <summary>Gets a match that identified nothing.</summary>
    public static PromptMatch None { get; } = new(false, null, null, 0);
}

/// <summary>
/// The closed, versioned map from a power prompt to the killer whose power prints it. It holds only prompts that were seen on the owner's own screen
/// (the committed file states where and when); no website or wiki is read, because the publishers' terms forbid automated access (D31, D35). A prompt
/// the map does not hold is simply not recognised, and a prompt that two killers print would be useless as a name cue, so the map refuses to hold
/// one. Prompts learned on the owner's PC (<see cref="LearnedPromptStore"/>) can be merged in with <see cref="WithLearned"/>; they never replace a
/// seeded entry.
/// </summary>
public sealed class PowerPromptMap
{
    private const string ResourceName = "DbdPresence.Vision.Prompts.power-prompts.en.json";

    private readonly VocabularyMatcher _matcher;

    private PowerPromptMap(int version, DateOnly dated, IReadOnlyList<PromptEntry> entries)
    {
        Version = version;
        Dated = dated;
        Entries = entries;
        _matcher = new VocabularyMatcher(entries.Select(e => new VocabularyEntry(e.Id, e.Text)));
        KillerByPrompt = entries.ToDictionary(e => e.Id, e => e.KillerId, StringComparer.Ordinal);
    }

    /// <summary>Gets the version of the committed list (a change to the file bumps it).</summary>
    public int Version { get; }

    /// <summary>Gets the date the committed list was written.</summary>
    public DateOnly Dated { get; }

    /// <summary>Gets every entry, seeded first.</summary>
    public IReadOnlyList<PromptEntry> Entries { get; }

    /// <summary>Gets the killer of each prompt id.</summary>
    public IReadOnlyDictionary<string, string> KillerByPrompt { get; }

    /// <summary>Loads the list embedded in this assembly.</summary>
    /// <param name="isKillerId">Whether an id is a killer of the closed vocabulary; every entry's killer must pass.</param>
    public static PowerPromptMap LoadEmbedded(Func<string, bool> isKillerId)
    {
        using var stream = typeof(PowerPromptMap).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), isKillerId);
    }

    /// <summary>Parses and validates the list (schema version 1).</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="isKillerId">Whether an id is a killer of the closed vocabulary.</param>
    /// <exception cref="FormatException">The list is malformed or inconsistent.</exception>
    public static PowerPromptMap Parse(string json, Func<string, bool> isKillerId)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(isKillerId);
        using var document = JsonFields.Parse(json, "The power prompt list");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Int(root, "schemaVersion") != 1 || Str(root, "language") != "en")
        {
            throw new FormatException("Unsupported power prompt list: expected schemaVersion 1 and language 'en'.");
        }

        var version = Int(root, "version");
        if (version < 1 || !DateOnly.TryParseExact(Str(root, "dated"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dated))
        {
            throw new FormatException("'version' must be at least 1 and 'dated' a yyyy-MM-dd date.");
        }

        if (Str(root, "provenance").Length == 0)
        {
            throw new FormatException("The list must state its provenance.");
        }

        var entries = new List<PromptEntry>();
        foreach (var item in Items(root, "prompts"))
        {
            var seen = Str(item, "seenOnScreen");
            if (seen.Length == 0)
            {
                throw new FormatException("A prompt that was not seen on screen must not be listed: 'seenOnScreen' is required.");
            }

            entries.Add(new PromptEntry(Str(item, "id"), Str(item, "text"), Str(item, "killer"), PromptSource.Seeded, seen));
        }

        Validate(entries, isKillerId);
        return new PowerPromptMap(version, dated, entries);
    }

    /// <summary>
    /// Returns a map with the learned prompts added. A learned prompt whose text is already in the map (seeded or learned for another killer) is
    /// dropped: a text may point at one killer only.
    /// </summary>
    /// <param name="learned">Prompts the owner's app trusts (see <see cref="LearnedPromptStore.Trusted"/>).</param>
    public PowerPromptMap WithLearned(IEnumerable<LearnedPrompt> learned)
    {
        ArgumentNullException.ThrowIfNull(learned);
        var entries = Entries.Where(e => e.Source == PromptSource.Seeded).ToList();
        var taken = entries.Select(e => Key(e.Text)).ToHashSet(StringComparer.Ordinal);
        var n = 0;
        foreach (var prompt in learned.OrderBy(l => l.KillerId, StringComparer.Ordinal).ThenBy(l => l.Text, StringComparer.Ordinal))
        {
            var key = Key(prompt.Text);
            if (key.Length == 0 || !taken.Add(key))
            {
                continue;
            }

            entries.Add(new PromptEntry(
                string.Create(CultureInfo.InvariantCulture, $"learned/{prompt.KillerId}/{++n}"),
                prompt.Text,
                prompt.KillerId,
                PromptSource.Learned,
                string.Create(CultureInfo.InvariantCulture, $"learned on this PC from {prompt.Matches} matches")));
        }

        return new PowerPromptMap(Version, Dated, entries);
    }

    /// <summary>Matches recognised text (several lines are allowed; the best accepted line counts).</summary>
    /// <param name="ocrText">The recognised text of the prompt region.</param>
    public PromptMatch Match(string? ocrText)
    {
        var best = PromptMatch.None;
        foreach (var line in PromptText.Lines(ocrText))
        {
            var cleaned = PromptText.WithoutKeyGlyphs(line);
            var match = _matcher.Match(cleaned);
            if (!match.IsAccepted)
            {
                continue;
            }

            var score = Math.Clamp(1.0 - (match.Distance ?? 1.0), 0.0, 1.0);
            if (score > best.Score)
            {
                best = new PromptMatch(true, match.Entry!.Id, KillerByPrompt[match.Entry.Id], score);
            }
        }

        return best;
    }

    private static string Key(string text) => TextNormalizer.Skeleton(TextNormalizer.Normalize(text));

    private static void Validate(List<PromptEntry> entries, Func<string, bool> isKillerId)
    {
        if (entries.Count == 0)
        {
            throw new FormatException("The list holds no prompt.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Id.Length == 0 || !ids.Add(e.Id))
            {
                throw new FormatException($"Prompt id '{e.Id}' is empty or repeated.");
            }

            if (!isKillerId(e.KillerId))
            {
                throw new FormatException($"Prompt '{e.Id}' names a killer that is not in the closed list.");
            }

            var key = Key(e.Text);
            if (key.Length == 0)
            {
                throw new FormatException($"Prompt '{e.Id}' has no usable characters.");
            }

            if (!texts.Add(key))
            {
                throw new FormatException($"Prompt '{e.Id}' repeats the text of another prompt; a text must point at one killer only.");
            }
        }
    }
}
