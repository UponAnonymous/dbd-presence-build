using System.Text.Json;
using DbdPresence.Core.Game;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Packs.Tests;

/// <summary>The shipped roster for tests: the killers and survivors of the embedded vocabulary, read as data (Packs never references Vision).</summary>
internal static class TestRoster
{
    private static readonly Lazy<CharacterRoster> s_embedded = new(Load);

    public static CharacterRoster Embedded => s_embedded.Value;

    public static string FixturePath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private static CharacterRoster Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("vocabulary.en.json")));
        var root = document.RootElement;
        return CharacterRoster.Create(Read(root, "killers", Role.Killer).Concat(Read(root, "survivors", Role.Survivor)).ToList());
    }

    private static IEnumerable<RosterCharacter> Read(JsonElement root, string list, Role role) =>
        root.GetProperty(list).EnumerateArray().Select(e => new RosterCharacter(
            e.GetProperty("code").GetString()!,
            e.GetProperty("id").GetString()!,
            role,
            e.GetProperty("name").GetString()!,
            e.TryGetProperty("aliases", out var aliases) ? [.. aliases.EnumerateArray().Select(a => a.GetProperty("name").GetString()!)] : []));
}
