using System.Collections.Frozen;
using DbdPresence.Core.Detection;

namespace DbdPresence.Core.Composition;

/// <summary>
/// The nine reserved pack slugs of the automatic small image (FR-AUTO-02, ADR-0013 addendum). They are slugs of the existing <c>app</c> kind
/// (<c>app:status-hooked</c> and so on), so an older app still loads an index that carries them. They are owner-pack-only: a user override never applies.
/// </summary>
public static class AutomaticIcons
{
    /// <summary>The owner is on a hook.</summary>
    public const string StatusHooked = "status-hooked";

    /// <summary>The owner is down.</summary>
    public const string StatusDying = "status-dying";

    /// <summary>A survivor escaped.</summary>
    public const string ResultEscaped = "result-escaped";

    /// <summary>A survivor was sacrificed.</summary>
    public const string ResultSacrificed = "result-sacrificed";

    /// <summary>A survivor died (shown to the user as "Killed").</summary>
    public const string ResultDead = "result-dead";

    /// <summary>A killer's lowest result.</summary>
    public const string ResultEntityHungers = "result-entity-hungers";

    /// <summary>A killer's second result.</summary>
    public const string ResultBrutal = "result-brutal";

    /// <summary>A killer's third result.</summary>
    public const string ResultRuthless = "result-ruthless";

    /// <summary>A killer's best result.</summary>
    public const string ResultMerciless = "result-merciless";

    private static readonly FrozenDictionary<string, string> s_byOutcome = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ResultOutcomes.Escaped] = ResultEscaped,
        [ResultOutcomes.Sacrificed] = ResultSacrificed,
        [ResultOutcomes.Dead] = ResultDead,
        [ResultOutcomes.EntityHungers] = ResultEntityHungers,
        [ResultOutcomes.BrutalKiller] = ResultBrutal,
        [ResultOutcomes.RuthlessKiller] = ResultRuthless,
        [ResultOutcomes.MercilessKiller] = ResultMerciless,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets all nine slugs.</summary>
    public static IReadOnlyList<string> AllSlugs { get; } =
        [StatusHooked, StatusDying, ResultEscaped, ResultSacrificed, ResultDead, ResultEntityHungers, ResultBrutal, ResultRuthless, ResultMerciless];

    /// <summary>Returns whether the slug is one of the nine reserved ones.</summary>
    /// <param name="slug">The slug.</param>
    public static bool IsReserved(string? slug) => slug is not null && AllSlugs.Contains(slug, StringComparer.Ordinal);

    /// <summary>Returns the slug of an own status, or null for <see cref="OwnStatus.None"/>.</summary>
    /// <param name="status">The status.</param>
    public static string? SlugOf(OwnStatus status) => status switch
    {
        OwnStatus.Hooked => StatusHooked,
        OwnStatus.Dying => StatusDying,
        _ => null,
    };

    /// <summary>Returns the slug of an outcome id, or null when the id is not an outcome.</summary>
    /// <param name="outcomeId">The outcome id (<see cref="ResultOutcomes"/>).</param>
    public static string? SlugOfOutcome(string? outcomeId) => outcomeId is not null && s_byOutcome.TryGetValue(outcomeId, out var slug) ? slug : null;
}
