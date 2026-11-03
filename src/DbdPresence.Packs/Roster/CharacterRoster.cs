using DbdPresence.Core.Game;
using DbdPresence.Packs.Index;

namespace DbdPresence.Packs.Roster;

/// <summary>One killer or survivor of the roster (PLAN_V2_S5B §2.1).</summary>
/// <param name="Code">The game's character code, <c>K</c> or <c>S</c> plus two digits (<c>K27</c>): the prefix of portrait file names. Never changes.</param>
/// <param name="Id">The pack slug of the character (<c>the-onryo</c>), the key in packs and settings (<c>character:the-onryo</c>). Never changes.</param>
/// <param name="Role">Killer for a <c>K</c> code, survivor for an <c>S</c> code.</param>
/// <param name="Name">The display name.</param>
/// <param name="OtherNames">Other spellings of the same character (aliases), used only to suggest a match for an imported file.</param>
public sealed record RosterCharacter(string Code, string Id, Role Role, string Name, IReadOnlyList<string> OtherNames);

/// <summary>
/// The killers and survivors the app knows, keyed by code. Import proposals use it to assign a picture to its character by file name
/// (FR-PACK-07). It is built from the embedded vocabulary at the composition root, because Packs never references Vision.
/// </summary>
public sealed class CharacterRoster
{
    private readonly Dictionary<string, RosterCharacter> _byCode;

    private CharacterRoster(List<RosterCharacter> characters)
    {
        Characters = characters;
        _byCode = characters.ToDictionary(c => c.Code, StringComparer.Ordinal);
    }

    /// <summary>Gets a roster without characters: imports then propose only roles and maps.</summary>
    public static CharacterRoster Empty { get; } = new([]);

    /// <summary>Gets the characters in the order given.</summary>
    public IReadOnlyList<RosterCharacter> Characters { get; }

    /// <summary>Builds a roster and checks it.</summary>
    /// <exception cref="FormatException">
    /// A code is malformed or does not fit its role, a code or id is used twice, an id is not a pack slug, or a name is empty.
    /// </exception>
    public static CharacterRoster Create(IEnumerable<RosterCharacter> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        var list = new List<RosterCharacter>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in characters)
        {
            ArgumentNullException.ThrowIfNull(character);
            if (!IsCode(character.Code) || character.Role != RoleOf(character.Code))
            {
                throw new FormatException($"Character code '{character.Code}' is malformed or does not fit the role {character.Role}.");
            }

            if (!PackKey.IsSlug(character.Id) || !codes.Add(character.Code) || !ids.Add(character.Id))
            {
                throw new FormatException($"Character '{character.Code}' needs a unique code and a unique pack slug id ('{character.Id}').");
            }

            if (string.IsNullOrWhiteSpace(character.Name) || character.OtherNames is null || character.OtherNames.Any(string.IsNullOrWhiteSpace))
            {
                throw new FormatException($"Character '{character.Code}' needs a name, and every other name must have text.");
            }

            list.Add(character);
        }

        return new CharacterRoster(list);
    }

    /// <summary>True for <c>K</c> or <c>S</c> followed by two digits.</summary>
    public static bool IsCode(string? code) =>
        code is { Length: 3 } && code[0] is 'K' or 'S' && char.IsAsciiDigit(code[1]) && char.IsAsciiDigit(code[2]);

    /// <summary>Finds a character by code, or null.</summary>
    public RosterCharacter? FindByCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _byCode.GetValueOrDefault(code);
    }

    private static Role RoleOf(string code) => code[0] == 'K' ? Role.Killer : Role.Survivor;
}
