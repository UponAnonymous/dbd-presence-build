namespace DbdPresence.Packs.Import;

/// <summary>
/// Role and map slugs used to propose mappings from file names. Proposals are never applied without the user (FR-PACK-07). Characters are not listed
/// here: they come from the roster (<see cref="Roster.CharacterRoster"/>), so the ids always equal the vocabulary's (PLAN_V2_S5B §2.1).
/// </summary>
internal static class KnownSlugs
{
    public static readonly IReadOnlyList<string> Roles = ["killer", "survivor"];

    public static readonly IReadOnlyList<string> Maps =
    [
        "hawkins", "the-game", "autohaven-wreckers", "coldwind-farm", "crotus-prenn-asylum", "haddonfield",
        "red-forest", "springwood", "yamaoka-estate",
    ];
}
