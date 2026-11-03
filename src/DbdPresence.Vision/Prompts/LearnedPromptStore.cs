using System.Text.Json;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Text;
using static DbdPresence.Vision.JsonFields;

namespace DbdPresence.Vision.Prompts;

/// <summary>A prompt the owner's app trusts: it was tied to exactly one killer in at least <see cref="LearnedPromptStore.MinMatches"/> matches.</summary>
/// <param name="Text">The prompt text (upper case, letters and spaces).</param>
/// <param name="KillerId">The killer.</param>
/// <param name="Matches">In how many matches it was tied to that killer.</param>
public sealed record LearnedPrompt(string Text, string KillerId, int Matches);

/// <summary>
/// The learning path for power prompts, kept on the owner's PC only. When a match ends with the killer confirmed by the scoreboard (the owner's role,
/// the owner's row and the killer row all agreeing), the prompts that recurred in that match are tied to that killer. A prompt becomes
/// <see cref="Trusted"/> only with two kinds of evidence (review F4): it was tied to exactly one killer in at least <see cref="MinMatches"/> matches,
/// and it was absent from at least one match the owner played as a different killer. Generic prompts that every killer prints (picking up a survivor,
/// hooking) show up for several killers or in the other killer's matches, so they never become a name cue, not even for a new owner's first killer.
/// Matches against a killer are not observed, so the second kind of evidence comes from the owner's own matches as another killer only.
/// What the store holds: prompt-shaped texts (at most <see cref="PromptText.MaxLength"/> characters and <see cref="PromptText.MaxWords"/> words, letters
/// and spaces), each of which recurred within a match and passed the name filters of the pipeline (the player column, the owner's name, the character
/// lists and every name the scoreboard lists), a count of matches per killer id and a counter. Anything that is not shaped like a prompt is dropped here
/// again. It does not write files; the owner's app saves <see cref="ToJson"/> next to its settings and loads it with <see cref="FromJson"/>.
/// </summary>
public sealed class LearnedPromptStore : IPromptObservationSink
{
    /// <summary>How many matches must tie a prompt to a killer before the prompt is trusted (an initial guess).</summary>
    public const int MinMatches = 2;

    /// <summary>The most distinct prompts the store keeps; further new prompts are not recorded.</summary>
    public const int MaxPrompts = 256;

    /// <summary>The most prompts one match may add (the pipeline holds at most this many candidates).</summary>
    public const int MaxPromptsPerMatch = 16;

    /// <summary>The most killers whose matches are counted.</summary>
    public const int MaxKillers = 64;

    /// <summary>The most unrecognised-killer matches counted.</summary>
    private const int MaxCounter = 1_000_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, int>> _matchesByPromptAndKiller = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _matchesByKiller = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textByKey = new(StringComparer.Ordinal);
    private int _unrecognisedKillerMatches;

    /// <summary>Gets how many matches ended with the owner as a killer the closed list does not name, and prompts were seen.</summary>
    public int UnrecognisedKillerMatches
    {
        get
        {
            lock (_gate)
            {
                return _unrecognisedKillerMatches;
            }
        }
    }

    /// <inheritdoc />
    public void PromptsAssociated(PromptAssociation association)
    {
        ArgumentNullException.ThrowIfNull(association);
        lock (_gate)
        {
            if (!_matchesByKiller.ContainsKey(association.KillerId) && _matchesByKiller.Count >= MaxKillers)
            {
                return;
            }

            _matchesByKiller[association.KillerId] = Math.Min(MaxCounter, _matchesByKiller.GetValueOrDefault(association.KillerId) + 1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in association.Prompts.Take(MaxPromptsPerMatch))
            {
                var text = raw is null ? null : PromptText.Candidate(raw);
                if (text is null)
                {
                    continue; // not shaped like a prompt: a sentence, a number, a name-length string (review L3)
                }

                var key = Key(text);
                if (key.Length == 0 || !seen.Add(key))
                {
                    continue; // one match counts once, however the text was spelled
                }

                if (!_matchesByPromptAndKiller.TryGetValue(key, out var byKiller))
                {
                    if (_matchesByPromptAndKiller.Count >= MaxPrompts)
                    {
                        continue;
                    }

                    byKiller = new Dictionary<string, int>(StringComparer.Ordinal);
                    _matchesByPromptAndKiller[key] = byKiller;
                    _textByKey[key] = text;
                }

                byKiller[association.KillerId] = Math.Min(MaxCounter, byKiller.GetValueOrDefault(association.KillerId) + 1);
            }
        }
    }

    /// <inheritdoc />
    public void KillerNotRecognised(UnmatchedKillerPrompts report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _unrecognisedKillerMatches = Math.Min(MaxCounter, _unrecognisedKillerMatches + 1);
        }
    }

    /// <summary>
    /// Returns the prompts that may be used as a name cue: tied to exactly one killer in at least <see cref="MinMatches"/> matches, and absent from at
    /// least one match the owner played as a different killer.
    /// </summary>
    public IReadOnlyList<LearnedPrompt> Trusted()
    {
        lock (_gate)
        {
            return [.. _matchesByPromptAndKiller
                .Where(p => p.Value.Count == 1 && p.Value.Single().Value >= MinMatches && AbsentFromOtherKillersMatch(p.Value.Single().Key))
                .Select(p => new LearnedPrompt(_textByKey[p.Key], p.Value.Single().Key, p.Value.Single().Value))
                .OrderBy(l => l.KillerId, StringComparer.Ordinal)
                .ThenBy(l => l.Text, StringComparer.Ordinal)];
        }
    }

    /// <summary>Whether the owner played a match as a killer other than <paramref name="killerId"/> (the prompt, tied to one killer only, was not in it).</summary>
    private bool AbsentFromOtherKillersMatch(string killerId) => _matchesByKiller.Any(k => k.Value > 0 && !string.Equals(k.Key, killerId, StringComparison.Ordinal));

    /// <summary>Returns how many distinct prompts the store holds, trusted or not.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _matchesByPromptAndKiller.Count;
            }
        }
    }

    /// <summary>Writes the whole store as JSON (every prompt held, trusted or not).</summary>
    public string ToJson() => Serialize(trustedOnly: false);

    /// <summary>
    /// Writes the store for the owner's app to save on this PC: only the trusted prompts (tied to one killer in at least <see cref="MinMatches"/> matches, absent
    /// from a match as another killer) with the per-killer counts they need. Text seen once, or tied to several killers, may be anything on screen and is
    /// never written to disk (round-2 QA review F7); it stays in memory until the app closes.
    /// </summary>
    public string ToPersistedJson() => Serialize(trustedOnly: true);

    private string Serialize(bool trustedOnly)
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                unrecognisedKillerMatches = _unrecognisedKillerMatches,
                matchesByKiller = _matchesByKiller.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new { killer = k.Key, matches = k.Value }).ToArray(),
                prompts = _matchesByPromptAndKiller
                    .Where(p => !trustedOnly || (p.Value.Count == 1 && p.Value.Single().Value >= MinMatches && AbsentFromOtherKillersMatch(p.Value.Single().Key)))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new { text = _textByKey[p.Key], killers = p.Value.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new { killer = k.Key, matches = k.Value }).ToArray() })
                    .ToArray(),
            });
        }
    }

    /// <summary>Reads a store written by <see cref="ToJson"/>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="isKillerId">Whether an id is a killer of the closed vocabulary; entries for other ids are refused.</param>
    /// <exception cref="FormatException">The text is malformed, too large or names an unknown killer.</exception>
    public static LearnedPromptStore FromJson(string json, Func<string, bool> isKillerId)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(isKillerId);
        if (json.Length > 256 * 1024)
        {
            throw new FormatException("The learned prompt file is too large.");
        }

        var store = new LearnedPromptStore();
        using var document = JsonFields.Parse(json, "The learned prompt file");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Int(root, "schemaVersion") != 1)
        {
            throw new FormatException("Unsupported learned prompt file: expected schemaVersion 1.");
        }

        store._unrecognisedKillerMatches = Math.Clamp(Int(root, "unrecognisedKillerMatches"), 0, MaxCounter);
        if (root.TryGetProperty("matchesByKiller", out _))
        {
            foreach (var entry in Items(root, "matchesByKiller"))
            {
                var killer = Str(entry, "killer");
                var matches = Int(entry, "matches");
                if (!isKillerId(killer) || matches < 1 || matches > MaxCounter || store._matchesByKiller.Count >= MaxKillers || !store._matchesByKiller.TryAdd(killer, matches))
                {
                    throw new FormatException("The learned prompt file names an unknown killer, repeats one or has an impossible match count.");
                }
            }
        }

        foreach (var prompt in Items(root, "prompts"))
        {
            var text = PromptText.Candidate(Str(prompt, "text")) ?? throw new FormatException("A learned prompt does not have the shape of a prompt.");
            var key = Key(text);
            if (store._matchesByPromptAndKiller.Count >= MaxPrompts || store._matchesByPromptAndKiller.ContainsKey(key))
            {
                throw new FormatException("The learned prompt file repeats a prompt or holds too many.");
            }

            var byKiller = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in Items(prompt, "killers"))
            {
                var killer = Str(entry, "killer");
                var matches = Int(entry, "matches");
                if (!isKillerId(killer) || matches < 1 || matches > MaxCounter || !byKiller.TryAdd(killer, matches))
                {
                    throw new FormatException("A learned prompt names an unknown killer, repeats one or has an impossible count.");
                }
            }

            if (byKiller.Count == 0)
            {
                throw new FormatException("A learned prompt names no killer.");
            }

            store._matchesByPromptAndKiller[key] = byKiller;
            store._textByKey[key] = text;
        }

        return store;
    }

    private static string Key(string text) => TextNormalizer.Skeleton(TextNormalizer.Normalize(text));
}
