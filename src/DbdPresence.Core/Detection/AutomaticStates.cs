using System.Collections.Frozen;

namespace DbdPresence.Core.Detection;

/// <summary>
/// The values of an <see cref="ObservationKind.OwnStatus"/> observation: what the icon left of the owner's own name in the trial HUD shows (FR-AUTO-03).
/// <see cref="Dead"/> is read only so that it never scores as <see cref="Hooked"/>; it is never published.
/// </summary>
public static class OwnStatuses
{
    /// <summary>The ordinary character portrait: nothing to show.</summary>
    public const string Portrait = "Portrait";

    /// <summary>The hooked icon.</summary>
    public const string Hooked = "Hooked";

    /// <summary>The dying (downed) icon.</summary>
    public const string Dying = "Dying";

    /// <summary>The dead icon. Read only to keep it apart from the others; never published.</summary>
    public const string Dead = "Dead";

    private static readonly FrozenSet<string> s_all = new[] { Portrait, Hooked, Dying, Dead }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns whether the text is a value of an own-status observation.</summary>
    /// <param name="value">The value.</param>
    public static bool IsValid(string value) => s_all.Contains(value);

    /// <summary>Returns whether the status can ever reach Discord (hooked and dying only).</summary>
    /// <param name="value">The value.</param>
    public static bool IsPublishable(string value) => value is Hooked or Dying;
}

/// <summary>
/// The canonical ids of the match outcomes printed on the result screen, split by the role that sees them (FR-AUTO-04). The ids are those of the
/// closed vocabulary. A survivor outcome while the role is Killer, or the reverse, is a contradiction and is dropped.
/// </summary>
public static class ResultOutcomes
{
    /// <summary>The survivor escaped.</summary>
    public const string Escaped = "outcome-escaped";

    /// <summary>The survivor was sacrificed.</summary>
    public const string Sacrificed = "outcome-sacrificed";

    /// <summary>The survivor died.</summary>
    public const string Dead = "outcome-dead";

    /// <summary>The killer's lowest result.</summary>
    public const string EntityHungers = "outcome-entity-hungers";

    /// <summary>The killer's second result.</summary>
    public const string BrutalKiller = "outcome-brutal-killer";

    /// <summary>The killer's third result.</summary>
    public const string RuthlessKiller = "outcome-ruthless-killer";

    /// <summary>The killer's best result.</summary>
    public const string MercilessKiller = "outcome-merciless-killer";

    private static readonly FrozenSet<string> s_survivor = new[] { Escaped, Sacrificed, Dead }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> s_killer = new[] { EntityHungers, BrutalKiller, RuthlessKiller, MercilessKiller }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Returns whether the id is an outcome a survivor sees.</summary>
    /// <param name="id">The outcome id.</param>
    public static bool IsSurvivorOutcome(string id) => s_survivor.Contains(id);

    /// <summary>Returns whether the id is an outcome a killer sees.</summary>
    /// <param name="id">The outcome id.</param>
    public static bool IsKillerOutcome(string id) => s_killer.Contains(id);

    /// <summary>Returns the observation kind an outcome id belongs to, or null when the id is not an outcome.</summary>
    /// <param name="id">The outcome id.</param>
    public static ObservationKind? KindOf(string id) =>
        IsSurvivorOutcome(id) ? ObservationKind.SurvivorResult : IsKillerOutcome(id) ? ObservationKind.KillerResult : null;
}
