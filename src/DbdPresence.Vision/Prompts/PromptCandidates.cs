using DbdPresence.Vision.Screens;

namespace DbdPresence.Vision.Prompts;

/// <summary>
/// The prompt-shaped texts that appeared at the bottom centre of the trial HUD during one match, held in memory until the scoreboard names the killer.
/// A text becomes a candidate only if it passed every filter that keeps other players' names out of the learning path: it has the shape of a prompt
/// (<see cref="PromptText.Candidate"/>), it is not a character name, and it contains none of the words read from the player column or the owner's name
/// of the same frame. It is passed on only after it appeared at least <see cref="MinSightings"/> separate times (a power prompt comes back each time
/// the power is used; a one-off notice does not). The set is bounded and is cleared at the start of the next match.
/// </summary>
internal sealed class PromptCandidates
{
    /// <summary>How many separate appearances make a text recur (an initial guess).</summary>
    public const int MinSightings = 2;

    /// <summary>The most distinct texts held.</summary>
    public const int MaxCandidates = 16;

    private readonly Dictionary<string, int> _sightings = new(StringComparer.Ordinal);
    private HashSet<string> _present = new(StringComparer.Ordinal);

    /// <summary>Gets how many distinct texts are held.</summary>
    public int Count => _sightings.Count;

    /// <summary>Feeds the prompt-shaped lines of one trial frame.</summary>
    /// <param name="candidates">Candidate forms of the lines read from the prompt region (empty when nothing was read).</param>
    /// <param name="forbiddenWords">Words read elsewhere on this frame that name people (player column rows, the owner's name), as read.</param>
    /// <param name="characters">The character lists; a whole line that is a character name is dropped.</param>
    public void Observe(IReadOnlyList<string> candidates, IEnumerable<string> forbiddenWords, HudVocabulary characters)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(forbiddenWords);
        ArgumentNullException.ThrowIfNull(characters);
        var forbidden = NameSkeletonGuard.ForbiddenForms(forbiddenWords);
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!IsAllowed(candidate, forbidden, characters))
            {
                continue;
            }

            current.Add(candidate);
            if (_present.Contains(candidate))
            {
                continue; // still on screen from the previous frame: one sighting, not two
            }

            if (_sightings.TryGetValue(candidate, out var seen))
            {
                _sightings[candidate] = seen + 1;
            }
            else if (_sightings.Count < MaxCandidates)
            {
                _sightings[candidate] = 1;
            }
        }

        _present = current;
    }

    /// <summary>Returns the texts that appeared at least <see cref="MinSightings"/> separate times, in a fixed order.</summary>
    public IReadOnlyList<string> Recurring() =>
        [.. _sightings.Where(s => s.Value >= MinSightings).Select(s => s.Key).Order(StringComparer.Ordinal)];

    /// <summary>Returns the recurring texts that also pass the name filter against <paramref name="forbiddenWords"/> (names read later, such as the whole scoreboard).</summary>
    /// <param name="forbiddenWords">Names read on the frame that decides; a text containing one of them is dropped.</param>
    /// <param name="characters">The character lists.</param>
    public IReadOnlyList<string> Recurring(IEnumerable<string> forbiddenWords, HudVocabulary characters)
    {
        ArgumentNullException.ThrowIfNull(forbiddenWords);
        ArgumentNullException.ThrowIfNull(characters);
        var forbidden = NameSkeletonGuard.ForbiddenForms(forbiddenWords);
        return [.. Recurring().Where(text => IsAllowed(text, forbidden, characters))];
    }

    /// <summary>Forgets everything (a new match starts).</summary>
    public void Clear()
    {
        _sightings.Clear();
        _present = new HashSet<string>(StringComparer.Ordinal);
    }

    private static bool IsAllowed(string candidate, HashSet<string> forbidden, HudVocabulary characters)
    {
        if (NameSkeletonGuard.ContainsPersonName(candidate, forbidden))
        {
            return false;
        }

        return !characters.Survivors.Match(candidate).IsAccepted && !characters.Killers.Match(candidate).IsAccepted;
    }
}
