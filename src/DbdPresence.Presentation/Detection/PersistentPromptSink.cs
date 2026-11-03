using System.Security;
using System.Text;
using DbdPresence.Core.Detection;
using DbdPresence.Packs.Local;
using DbdPresence.Vision.Prompts;

namespace DbdPresence.Presentation.Detection;

/// <summary>
/// Keeps what the power-prompt learning path observed on this PC only (FR-DET-22 follow-up, D31). It wraps a <see cref="LearnedPromptStore"/>, loads it
/// from one JSON file in the app's data folder and saves it after every match that taught it something. The file holds only trusted prompts (tied to one
/// killer in at least two matches, and absent from a match as another killer: text seen once is held in memory only and is lost when the app closes), killer ids and
/// counts, never a player name (the pipeline filters candidates before they reach the sink, and a name does not repeat as a prompt of one killer). A missing file starts an empty store; a corrupt or
/// unreadable file is logged in one plain sentence (no file content) and replaced by an empty store, so a bad file never stops detection. Nothing
/// is sent anywhere: the online report of names the list does not know belongs to the roster workstream and plugs in at <see cref="Reported"/>.
/// </summary>
public sealed class PersistentPromptSink : IPromptObservationSink
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly LearnedPromptStore _store;

    private PersistentPromptSink(string path, Action<string> warn, LearnedPromptStore store)
    {
        _path = path;
        _warn = warn;
        _store = store;
    }

    /// <summary>
    /// Raised after a match whose killer the list does not name, with that match's prompts. This is the seam for the roster workstream: its subscriber
    /// decides whether and how the owner's service learns that a name is missing. Nothing subscribes today, so nothing leaves this PC.
    /// </summary>
    public event EventHandler<UnmatchedKillerPrompts>? Reported;

    /// <summary>Gets the store the observations are added to.</summary>
    public LearnedPromptStore Store => _store;

    /// <summary>Opens the file, or starts an empty store when it is missing or cannot be used.</summary>
    /// <param name="path">The JSON file, in the app's data folder.</param>
    /// <param name="isKillerId">Whether an id is a killer of the closed vocabulary.</param>
    /// <param name="warn">Receives one plain sentence when the file could not be used (no file content).</param>
    public static PersistentPromptSink Open(string path, Func<string, bool> isKillerId, Action<string> warn)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(isKillerId);
        ArgumentNullException.ThrowIfNull(warn);
        if (!File.Exists(path))
        {
            return new PersistentPromptSink(path, warn, new LearnedPromptStore());
        }

        try
        {
            if (BoundedFile.ReadAllText(path, BoundedFile.TextLimitBytes) is not { } text)
            {
                warn("The learned killer prompts file is larger than 1 MB and was not read; starting with an empty list.");
                return new PersistentPromptSink(path, warn, new LearnedPromptStore());
            }

            var store = LearnedPromptStore.FromJson(text, isKillerId);
            return new PersistentPromptSink(path, warn, store);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or SecurityException or System.Text.Json.JsonException)
        {
            warn($"The learned killer prompts could not be read ({ex.GetType().Name}); starting with an empty list.");
            return new PersistentPromptSink(path, warn, new LearnedPromptStore());
        }
    }

    /// <inheritdoc />
    public void PromptsAssociated(PromptAssociation association)
    {
        ArgumentNullException.ThrowIfNull(association);
        _store.PromptsAssociated(association);
        Save();
    }

    /// <inheritdoc />
    public void KillerNotRecognised(UnmatchedKillerPrompts report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _store.KillerNotRecognised(report);
        Save();
        Reported?.Invoke(this, report);
    }

    private void Save()
    {
        // Write next to the file and move it over, so a crash never leaves half a file. A failure is logged; the store stays in memory.
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    _ = Directory.CreateDirectory(directory);
                }

                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, _store.ToPersistedJson(), new UTF8Encoding(false));
                File.Move(temporary, _path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _warn($"The learned killer prompts could not be saved ({ex.GetType().Name}); they are kept until the app closes.");
        }
    }
}
