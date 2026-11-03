using DbdPresence.Core.Game;
using DbdPresence.Packs.Roster;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Presence;

/// <summary>
/// Builds the character roster that imports match against from the detection vocabulary (PLAN_V2_S5B §2.1, §2.4): the same codes, ids, names and
/// aliases, so a picture is assigned to exactly the character detection would report. Packs never references Vision; this is where they meet.
/// </summary>
public static class VocabularyRoster
{
    /// <summary>Returns every killer and survivor of <paramref name="vocabulary"/> as a roster.</summary>
    /// <exception cref="FormatException">The vocabulary's characters do not form a valid roster.</exception>
    public static CharacterRoster From(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        return CharacterRoster.Create(
            vocabulary.Entries(VocabularyKind.Killer).Select(e => Character(e, Role.Killer))
                .Concat(vocabulary.Entries(VocabularyKind.Survivor).Select(e => Character(e, Role.Survivor))));
    }

    private static RosterCharacter Character(VocabularyEntry entry, Role role) =>
        new(entry.Code ?? throw new FormatException($"Character '{entry.Id}' has no code."), entry.Id, role, entry.Name, [.. entry.Aliases.Select(a => a.Name)]);
}
