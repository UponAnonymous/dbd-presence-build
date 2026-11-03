using DbdPresence.Vision.Prompts;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Screens;

/// <summary>
/// The closed lists the HUD detectors match against, built once from a <see cref="Vocabulary"/>: killers, survivors, realms, maps (each map
/// belongs to one realm), event modes and result outcomes. A map and the realm printed with it must belong together; the link is the realm
/// each vocabulary map entry carries (from the wiki pages cited in the vocabulary's source block).
/// </summary>
public sealed class HudVocabulary
{
    private readonly Dictionary<string, string> _realmOfMap;
    private readonly HashSet<string> _killerIds;
    private readonly HashSet<string> _survivorIds;
    private readonly HashSet<string> _eventIds;
    private readonly Dictionary<string, VocabularyEntry> _maps;
    private readonly Vocabulary _vocabulary;

    private HudVocabulary(Vocabulary vocabulary, PowerPromptMap? prompts)
    {
        Killers = vocabulary.MatcherFor(VocabularyKind.Killer);
        Survivors = vocabulary.MatcherFor(VocabularyKind.Survivor);
        Realms = vocabulary.MatcherFor(VocabularyKind.Realm);
        Maps = vocabulary.MatcherFor(VocabularyKind.Map);
        EventModes = vocabulary.MatcherFor(VocabularyKind.EventMode);
        Outcomes = vocabulary.MatcherFor(VocabularyKind.ResultOutcome);
        _maps = vocabulary.Entries(VocabularyKind.Map).ToDictionary(m => m.Id, StringComparer.Ordinal);
        _realmOfMap = vocabulary.Entries(VocabularyKind.Map).ToDictionary(m => m.Id, m => m.Group!, StringComparer.Ordinal);
        _killerIds = [.. vocabulary.Entries(VocabularyKind.Killer).Select(e => e.Id)];
        _survivorIds = [.. vocabulary.Entries(VocabularyKind.Survivor).Select(e => e.Id)];
        _eventIds = [.. vocabulary.Entries(VocabularyKind.EventMode).Select(e => e.Id)];
        Prompts = prompts ?? PowerPromptMap.LoadEmbedded(_killerIds.Contains);
        _vocabulary = vocabulary;
    }

    /// <summary>Gets the killer matcher.</summary>
    public VocabularyMatcher Killers { get; }

    /// <summary>Gets the survivor matcher.</summary>
    public VocabularyMatcher Survivors { get; }

    /// <summary>Gets the realm matcher.</summary>
    public VocabularyMatcher Realms { get; }

    /// <summary>Gets the map matcher (entries carry their realm id as group).</summary>
    public VocabularyMatcher Maps { get; }

    /// <summary>Gets the event-mode matcher.</summary>
    public VocabularyMatcher EventModes { get; }

    /// <summary>Gets the result-outcome matcher.</summary>
    public VocabularyMatcher Outcomes { get; }

    /// <summary>Gets the closed map from power prompts to killers (the committed list, plus any prompts the owner's app learned).</summary>
    public PowerPromptMap Prompts { get; }

    /// <summary>Builds the lists from a vocabulary.</summary>
    public static HudVocabulary Create(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        return new HudVocabulary(vocabulary, null);
    }

    /// <summary>Returns a copy that uses another prompt map (the committed list merged with the owner's learned prompts).</summary>
    /// <param name="prompts">The map; every killer in it must be a killer of this vocabulary.</param>
    public HudVocabulary WithPromptMap(PowerPromptMap prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Entries.Any(e => !_killerIds.Contains(e.KillerId)))
        {
            throw new ArgumentException("The prompt map names a killer that is not in this vocabulary.", nameof(prompts));
        }

        return new HudVocabulary(_vocabulary, prompts);
    }

    /// <summary>
    /// Returns lists built from another vocabulary (the embedded one plus the signed roster's new characters) that keep this one's prompt map. The live loop
    /// uses it to start recognising new characters without a restart.
    /// </summary>
    /// <param name="vocabulary">The new vocabulary; every killer of the prompt map must still be a killer in it (the roster is append-only).</param>
    /// <exception cref="ArgumentException">The prompt map names a killer the new vocabulary lacks.</exception>
    public HudVocabulary WithVocabulary(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        var killers = vocabulary.Entries(VocabularyKind.Killer).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (Prompts.Entries.Any(e => !killers.Contains(e.KillerId)))
        {
            throw new ArgumentException("The prompt map names a killer that is not in the new vocabulary.", nameof(vocabulary));
        }

        return new HudVocabulary(vocabulary, Prompts);
    }

    /// <summary>Returns the realm id of a map id, or null when the map is not in the vocabulary.</summary>
    public string? RealmOfMap(string mapId) => _realmOfMap.GetValueOrDefault(mapId);

    /// <summary>Returns the vocabulary entry of a map id, or null.</summary>
    public VocabularyEntry? MapEntry(string mapId) => _maps.GetValueOrDefault(mapId);


    /// <summary>
    /// The name the game prints for a killer, survivor, event mode or map id ("Realm - Map" for a map), or null for an unknown id. Used to show a
    /// reviewer the names it will see on screen next to the ids it must answer with.
    /// </summary>
    /// <param name="id">A character, event mode or map id.</param>
    /// <returns>The display name, or null.</returns>
    public string? DisplayNameOf(string id)
    {
        if (_maps.TryGetValue(id, out var map))
        {
            var realm = _vocabulary.Entries(VocabularyKind.Realm).FirstOrDefault(r => r.Id == map.Group);
            return realm is null ? map.Name : realm.Name + " - " + map.Name;
        }

        foreach (var kind in (ReadOnlySpan<VocabularyKind>)[VocabularyKind.Killer, VocabularyKind.Survivor, VocabularyKind.EventMode])
        {
            var entry = _vocabulary.Entries(kind).FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                return entry.Name;
            }
        }

        return null;
    }

    /// <summary>Gets the ids of all killers, in order.</summary>
    public IReadOnlyList<string> KillerIds => [.. _killerIds.Order(StringComparer.Ordinal)];

    /// <summary>Gets the ids of all survivors, in order.</summary>
    public IReadOnlyList<string> SurvivorIds => [.. _survivorIds.Order(StringComparer.Ordinal)];

    /// <summary>Gets the ids of all event modes, in order.</summary>
    public IReadOnlyList<string> EventModeIds => [.. _eventIds.Order(StringComparer.Ordinal)];

    /// <summary>Gets the ids of all maps, in order.</summary>
    public IReadOnlyList<string> MapIds => [.. _maps.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Returns whether the id is a killer.</summary>
    public bool IsKillerId(string id) => _killerIds.Contains(id);

    /// <summary>Returns whether the id is a survivor.</summary>
    public bool IsSurvivorId(string id) => _survivorIds.Contains(id);

    /// <summary>Returns whether the id is a killer or a survivor.</summary>
    public bool IsCharacterId(string id) => IsKillerId(id) || IsSurvivorId(id);

    /// <summary>Returns whether the id is an event mode.</summary>
    public bool IsEventModeId(string id) => _eventIds.Contains(id);
}
