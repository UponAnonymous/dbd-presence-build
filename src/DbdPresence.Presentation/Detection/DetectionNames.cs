using DbdPresence.Core.Detection;
using DbdPresence.Vision.Text;
using GameRole = DbdPresence.Core.Game.Role;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Presentation.Detection;

/// <summary>Plain-language names for what the engine confirmed, from the closed vocabulary (ids in, display words out). Used only on the app's own screen.</summary>
public sealed class DetectionNames
{
    private Lists _lists;

    /// <summary>Initializes a new instance of the <see cref="DetectionNames"/> class.</summary>
    public DetectionNames(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _lists = Build(vocabulary);
    }

    /// <summary>Replaces the names (the signed roster added characters), so a new character confirmed by the loop shows its name at once. Safe to call from any thread.</summary>
    /// <param name="vocabulary">The new vocabulary.</param>
    public void Update(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        Volatile.Write(ref _lists, Build(vocabulary));
    }

    private static Lists Build(Vocabulary vocabulary)
    {
        var characters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in vocabulary.Entries(VocabularyKind.Survivor).Concat(vocabulary.Entries(VocabularyKind.Killer)))
        {
            characters[entry.Id] = entry.Name;
        }

        var maps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in vocabulary.Entries(VocabularyKind.Map))
        {
            maps[entry.Id] = entry.Name;
        }

        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in vocabulary.Entries(VocabularyKind.EventMode))
        {
            modes[entry.Id] = entry.Name;
        }

        return new Lists(characters, maps, modes);
    }

    private sealed record Lists(Dictionary<string, string> Characters, Dictionary<string, string> Maps, Dictionary<string, string> Modes);

    /// <summary>Gets the words for a scene.</summary>
    public static string SceneName(SceneKind scene) => scene switch
    {
        SceneKind.MainMenu => "Main menu",
        SceneKind.Other => "Another menu",
        SceneKind.Lobby => "Lobby",
        SceneKind.Matchmaking => "Searching for a match",
        SceneKind.Loading => "Loading",
        SceneKind.InTrial => "In the trial",
        SceneKind.Spectating => "Spectating",
        SceneKind.Results => "Results screen",
        SceneKind.InMatchMenu => "TAB or ESC menu in a trial",
        _ => "Not sure yet",
    };

    /// <summary>Gets the words for a role value ("Survivor" or "Killer").</summary>
    public static string Role(string value) => Enum.TryParse<GameRole>(value, out var role) && role != GameRole.None ? value : "Not sure yet";

    /// <summary>Gets a character's display name, or the id's words when it is not in the list.</summary>
    public string Character(string id) => Volatile.Read(ref _lists).Characters.GetValueOrDefault(id, id);

    /// <summary>Gets a map's display name, or the id when it is not in the list.</summary>
    public string Map(string id) => Volatile.Read(ref _lists).Maps.GetValueOrDefault(id, id);

    /// <summary>Gets the queue's words ("Normal" or the event's name).</summary>
    public string Mode(string id) => id == QueueModes.Normal ? "Normal queue" : Volatile.Read(ref _lists).Modes.GetValueOrDefault(id, id);
}
