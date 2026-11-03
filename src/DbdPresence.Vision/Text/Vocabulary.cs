using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Detection;
using static DbdPresence.Vision.JsonFields;

namespace DbdPresence.Vision.Text;

/// <summary>The kinds of closed vocabulary.</summary>
public enum VocabularyKind
{
    /// <summary>Killer display names such as "The Trapper".</summary>
    Killer,

    /// <summary>Survivor display names.</summary>
    Survivor,

    /// <summary>Realm display names.</summary>
    Realm,

    /// <summary>Map display names, each grouped by realm.</summary>
    Map,

    /// <summary>Game mode names.</summary>
    Mode,

    /// <summary>Limited-time event mode names such as "Haunted by Daylight". The list changes often and can be extended (<see cref="Vocabulary.WithEventModes"/>).</summary>
    EventMode,

    /// <summary>Match result outcomes printed on the result screen ("Escaped").</summary>
    ResultOutcome,
}

/// <summary>Where the vocabulary came from, kept with the data so the credit travels with it.</summary>
/// <param name="Name">Source name.</param>
/// <param name="Urls">Source pages.</param>
/// <param name="Licence">Licence of the source.</param>
/// <param name="LicenceUrl">Licence text location.</param>
/// <param name="Attribution">The credit line.</param>
public sealed record VocabularySource(string Name, IReadOnlyList<string> Urls, string Licence, string LicenceUrl, string Attribution);

/// <summary>
/// The closed English vocabulary of display names (characters, maps, realms, modes) loaded from the committed JSON resource
/// <c>Vocabulary/vocabulary.en.json</c>. The data is dated (<see cref="RetrievedOn"/>) and must be refreshed when the game adds
/// content. Names are facts; the wiki they were read from is credited in the file and in THIRD-PARTY-NOTICES.md.
/// It also resolves canonical map ids to display names for the presence composer.
/// </summary>
public sealed class Vocabulary : IMapNameResolver
{
    private const string ResourceName = "DbdPresence.Vision.Vocabulary.vocabulary.en.json";

    private readonly Dictionary<VocabularyKind, IReadOnlyList<VocabularyEntry>> _entries;
    private readonly Dictionary<string, MapNames> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<VocabularyKind, VocabularyMatcher> _matchers = [];

    private Vocabulary(DateOnly retrievedOn, VocabularySource source, Dictionary<VocabularyKind, IReadOnlyList<VocabularyEntry>> entries)
    {
        RetrievedOn = retrievedOn;
        Source = source;
        _entries = entries;
        foreach (var (kind, list) in entries)
        {
            ValidateDistinct(kind, list);
        }

        ValidateCharacters(entries);

        var realms = entries[VocabularyKind.Realm].ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
        foreach (var map in entries[VocabularyKind.Map])
        {
            _maps[map.Id] = new MapNames(map.Name, realms[map.Group!]);
        }
    }

    /// <summary>Gets the date the names were read from the source.</summary>
    public DateOnly RetrievedOn { get; }

    /// <summary>Gets the credit for the source.</summary>
    public VocabularySource Source { get; }

    /// <summary>Loads the vocabulary embedded in this assembly.</summary>
    public static Vocabulary LoadEmbedded()
    {
        using var stream = typeof(Vocabulary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses and validates vocabulary JSON (schema version 1).</summary>
    /// <exception cref="FormatException">The document is malformed or inconsistent.</exception>
    public static Vocabulary Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using (var document = JsonFields.Parse(json, "The vocabulary"))
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "schemaVersion") != 1 || Str(root, "language") != "en")
            {
                throw new FormatException("Unsupported vocabulary: expected schemaVersion 1 and language 'en'.");
            }

            if (!DateOnly.TryParseExact(Str(root, "retrievedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var retrieved))
            {
                throw new FormatException("'retrievedOn' must be a yyyy-MM-dd date.");
            }

            var src = Prop(root, "source");
            var source = new VocabularySource(
                Str(src, "name"),
                [.. Prop(src, "urls").EnumerateArray().Select(u => u.GetString() ?? throw new FormatException("A source url is null."))],
                Str(src, "licence"),
                Str(src, "licenceUrl"),
                Str(src, "attribution"));
            if (source.Urls.Count == 0)
            {
                throw new FormatException("The source must list at least one url.");
            }

            var entries = new Dictionary<VocabularyKind, IReadOnlyList<VocabularyEntry>>
            {
                [VocabularyKind.Killer] = Read(root, "killers", withGroup: false, withCode: true),
                [VocabularyKind.Survivor] = Read(root, "survivors", withGroup: false, withCode: true),
                [VocabularyKind.Realm] = Read(root, "realms", withGroup: false),
                [VocabularyKind.Map] = Read(root, "maps", withGroup: true),
                [VocabularyKind.Mode] = Read(root, "modes", withGroup: false),
                [VocabularyKind.EventMode] = Read(root, "eventModes", withGroup: false),
                [VocabularyKind.ResultOutcome] = Read(root, "resultOutcomes", withGroup: false),
            };

            var counts = Prop(root, "counts");
            foreach (var (key, kind) in new[] { ("killers", VocabularyKind.Killer), ("survivors", VocabularyKind.Survivor), ("realms", VocabularyKind.Realm), ("maps", VocabularyKind.Map) })
            {
                if (Int(counts, key) != entries[kind].Count)
                {
                    throw new FormatException($"'counts.{key}' does not match the list.");
                }
            }

            var realmIds = entries[VocabularyKind.Realm].Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            if (entries[VocabularyKind.Map].Any(m => m.Group is null || !realmIds.Contains(m.Group)))
            {
                throw new FormatException("Every map must name an existing realm.");
            }

            return new Vocabulary(retrieved, source, entries);
        }
    }

    /// <summary>
    /// Returns a copy of this vocabulary whose event-mode list also contains <paramref name="extra"/>. This is the seam for a future signed remote
    /// list: the embedded list stays the baseline and extra names can only be added, never replace or rename an existing entry.
    /// </summary>
    /// <exception cref="FormatException">An extra id, name or alias collides with an existing one (after OCR folding), or an alias lacks provenance.</exception>
    public Vocabulary WithEventModes(IEnumerable<VocabularyEntry> extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var merged = new Dictionary<VocabularyKind, IReadOnlyList<VocabularyEntry>>(_entries)
        {
            [VocabularyKind.EventMode] = [.. _entries[VocabularyKind.EventMode], .. extra],
        };
        return new Vocabulary(RetrievedOn, Source, merged);
    }

    /// <summary>
    /// Returns a copy of this vocabulary whose killer and survivor lists also contain the given new characters (FR-ROS-04, FR-DET-18). This is the
    /// seam for the signed roster: the embedded list stays the baseline, characters can only be added, and an existing entry is never replaced or
    /// renamed. Detection is unchanged: a new character is matched only by name, like every other entry. The caller passes only characters whose
    /// codes are not yet known (<see cref="FindByCode"/>).
    /// </summary>
    /// <param name="killers">New killers, each with a <c>K</c> code.</param>
    /// <param name="survivors">New survivors, each with an <c>S</c> code.</param>
    /// <exception cref="FormatException">
    /// A code is missing, malformed, of the wrong role or already known; an id is not a valid slug or is already used; a name or alias collides with an
    /// existing one after OCR folding; or an alias lacks provenance.
    /// </exception>
    public Vocabulary WithCharacters(IEnumerable<VocabularyEntry> killers, IEnumerable<VocabularyEntry> survivors)
    {
        ArgumentNullException.ThrowIfNull(killers);
        ArgumentNullException.ThrowIfNull(survivors);
        List<VocabularyEntry> newKillers = [.. killers];
        List<VocabularyEntry> newSurvivors = [.. survivors];
        foreach (var entry in newKillers.Concat(newSurvivors))
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Code is not null && FindByCode(entry.Code) is not null)
            {
                throw new FormatException($"Character code '{entry.Code}' is already in the vocabulary; only new characters can be added.");
            }
        }

        var merged = new Dictionary<VocabularyKind, IReadOnlyList<VocabularyEntry>>(_entries)
        {
            [VocabularyKind.Killer] = [.. _entries[VocabularyKind.Killer], .. newKillers],
            [VocabularyKind.Survivor] = [.. _entries[VocabularyKind.Survivor], .. newSurvivors],
        };
        return new Vocabulary(RetrievedOn, Source, merged);
    }

    /// <summary>Finds the killer or survivor with a character code such as <c>K27</c>, or null.</summary>
    public VocabularyEntry? FindByCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _entries[VocabularyKind.Killer].Concat(_entries[VocabularyKind.Survivor]).FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.Ordinal));
    }

    /// <summary>True for a character code: <c>K</c> or <c>S</c> followed by two digits.</summary>
    public static bool IsCharacterCode(string? code) =>
        code is { Length: 3 } && code[0] is 'K' or 'S' && char.IsAsciiDigit(code[1]) && char.IsAsciiDigit(code[2]);

    /// <summary>Gets the entries of a kind.</summary>
    public IReadOnlyList<VocabularyEntry> Entries(VocabularyKind kind) => _entries[kind];

    /// <summary>Gets a matcher for one kind. Matchers are created once per kind with the given thresholds.</summary>
    public VocabularyMatcher MatcherFor(VocabularyKind kind, VocabularyMatcherOptions? options = null)
    {
        if (options is not null)
        {
            return new VocabularyMatcher(_entries[kind], options);
        }

        lock (_matchers)
        {
            if (!_matchers.TryGetValue(kind, out var matcher))
            {
                matcher = new VocabularyMatcher(_entries[kind]);
                _matchers[kind] = matcher;
            }

            return matcher;
        }
    }

    /// <inheritdoc />
    public MapNames? Resolve(string mapId) => _maps.GetValueOrDefault(mapId);

    /// <summary>Every id, name and alias of a kind must be distinct, also after OCR folding, or the fold would hide a real difference between two entries.</summary>
    private static void ValidateDistinct(VocabularyKind kind, IReadOnlyList<VocabularyEntry> list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var skeletons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            if (!ids.Add(entry.Id))
            {
                throw new FormatException($"{kind} has a duplicate id '{entry.Id}'.");
            }

            foreach (var alias in entry.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias.SeenOnScreen))
                {
                    throw new FormatException($"{kind} '{entry.Id}': alias '{alias.Name}' has no 'seenOnScreen' provenance.");
                }
            }

            foreach (var spelling in entry.Aliases.Select(a => a.Name).Prepend(entry.Name))
            {
                var skeleton = TextNormalizer.Skeleton(spelling);
                if (skeleton.Length == 0)
                {
                    throw new FormatException($"{kind} '{entry.Id}': '{spelling}' has no usable characters.");
                }

                if (!skeletons.TryAdd(skeleton, entry.Id))
                {
                    throw new FormatException($"{kind}: '{spelling}' of '{entry.Id}' collides with a name or alias of '{skeletons[skeleton]}'.");
                }
            }
        }
    }

    /// <summary>
    /// Killers and survivors share the pack keys <c>character:&lt;id&gt;</c> and the code space, so across both lists: every entry has a code of its
    /// role (<c>K</c> or <c>S</c>), codes and ids are unique, ids are pack slugs, and no two names or aliases fold to the same text. No other kind has
    /// a code.
    /// </summary>
    private static void ValidateCharacters(Dictionary<VocabularyKind, IReadOnlyList<VocabularyEntry>> entries)
    {
        foreach (var (kind, list) in entries)
        {
            if (kind is VocabularyKind.Killer or VocabularyKind.Survivor)
            {
                continue;
            }

            var coded = list.FirstOrDefault(e => e.Code is not null);
            if (coded is not null)
            {
                throw new FormatException($"{kind} '{coded.Id}' has a character code; only killers and survivors do.");
            }
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var skeletons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, letter) in new[] { (VocabularyKind.Killer, 'K'), (VocabularyKind.Survivor, 'S') })
        {
            foreach (var entry in entries[kind])
            {
                if (!IsCharacterCode(entry.Code) || entry.Code![0] != letter)
                {
                    throw new FormatException($"{kind} '{entry.Id}' needs a character code '{letter}' followed by two digits.");
                }

                if (!codes.Add(entry.Code))
                {
                    throw new FormatException($"Character code '{entry.Code}' is used twice.");
                }

                if (!IsSlug(entry.Id) || !ids.Add(entry.Id))
                {
                    throw new FormatException($"Character id '{entry.Id}' is not a unique slug of 1 to 64 characters [a-z0-9-].");
                }

                foreach (var spelling in entry.Aliases.Select(a => a.Name).Prepend(entry.Name))
                {
                    var skeleton = TextNormalizer.Skeleton(spelling);
                    if (skeletons.TryGetValue(skeleton, out var other) && other != entry.Id)
                    {
                        throw new FormatException($"'{spelling}' of '{entry.Id}' collides with a name or alias of '{other}'.");
                    }

                    skeletons[skeleton] = entry.Id;
                }
            }
        }
    }

    private static bool IsSlug(string id) =>
        id.Length is >= 1 and <= 64 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static List<VocabularyEntry> Read(JsonElement root, string name, bool withGroup, bool withCode = false)
    {
        var list = new List<VocabularyEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Prop(root, name).EnumerateArray())
        {
            var id = Str(item, "id");
            var display = Str(item, "name");
            if (id.Length == 0 || display.Length == 0 || !seen.Add(id))
            {
                throw new FormatException($"'{name}' has an empty or duplicate id or name near '{id}'.");
            }

            var aliases = new List<VocabularyAlias>();
            if (item.TryGetProperty("aliases", out var aliasArray))
            {
                if (aliasArray.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"'{name}' entry '{id}': 'aliases' must be an array.");
                }

                aliases.AddRange(aliasArray.EnumerateArray().Select(a => new VocabularyAlias(Str(a, "name"), Str(a, "seenOnScreen"))));
            }

            var seenOnScreen = OptionalStr(item, "seenOnScreen");
            var code = OptionalStr(item, "code");
            if (code is not null && !withCode)
            {
                throw new FormatException($"'{name}' entry '{id}': only killers and survivors have a 'code'.");
            }

            list.Add(new VocabularyEntry(id, display, withGroup ? Str(item, "realm") : null, aliases, seenOnScreen, code));
        }

        if (list.Count == 0)
        {
            throw new FormatException($"'{name}' is empty.");
        }

        return list;
    }
}
