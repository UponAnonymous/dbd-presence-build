using System.Collections.Frozen;

namespace DbdPresence.Core.Templates;

/// <summary>The documented template tokens (FR-TPL-02).</summary>
public static class TemplateTokens
{
    /// <summary>Survivor or Killer.</summary>
    public const string Role = "role";

    /// <summary>Confirmed character.</summary>
    public const string Character = "character";

    /// <summary>Confirmed map name.</summary>
    public const string Map = "map";

    /// <summary>Confirmed realm name.</summary>
    public const string Realm = "realm";

    /// <summary>Confirmed generators remaining.</summary>
    public const string Generators = "generators";

    /// <summary>Confirmed game mode.</summary>
    public const string Mode = "mode";

    /// <summary>Confirmed result.</summary>
    public const string Result = "result";

    /// <summary>Elapsed time at minute resolution.</summary>
    public const string Duration = "duration";

    /// <summary>Survivors currently shown dead or sacrificed.</summary>
    public const string StatusDead = "status_dead";

    /// <summary>Survivors currently shown hooked.</summary>
    public const string StatusHooked = "status_hooked";

    /// <summary>The hook counter of the killer's own HUD (killer only, FR-AUTO-07).</summary>
    public const string HookCount = "hook_count";

    /// <summary>Alias of <see cref="StatusDead"/>.</summary>
    public const string Eliminations = "eliminations";

    /// <summary>Canonical token names, each with a one-line description for the Studio's token palette.</summary>
    public static readonly FrozenDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Role] = "Survivor or Killer. Left out until the app is sure of it.",
        [Character] = "The character you're playing. Left out until the app is sure of it.",
        [Map] = "The map. Left out until the app is sure of it.",
        [Realm] = "The realm. Left out until the app is sure of it.",
        [Generators] = "Generators left. Left out until the app is sure of it, and unless live match details are on.",
        [Mode] = "The game mode. Left out until the app is sure of it.",
        [Result] = "The match result. Left out until the results screen is confirmed.",
        [Duration] = "How long you've played, in minutes. It updates at most once a minute.",
        [StatusDead] = "Survivors shown as dead or sacrificed. Left out unless live match details are on.",
        [StatusHooked] = "Survivors shown as hooked. Left out unless live match details are on.",
        [HookCount] = "The hook counter on your screen while you play the killer. Left out until the app is sure of it, and unless live match details are on.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Accepted alternative spellings and the canonical token they map to.</summary>
    public static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Eliminations] = StatusDead,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Tokens that are deliberately not supported, with the explanation shown to the user.</summary>
    public static readonly FrozenDictionary<string, string> Rejected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hooks"] = "Global hook counts are not observable; use {hook_count} for the counter on the killer's own screen, or {status_hooked} for survivors currently shown hooked.",
        ["killer"] = "The killer's identity is not shown to Discord (it is opponent information).",
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
