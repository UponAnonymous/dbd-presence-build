using System.Security;
using DbdPresence.Packs.Local;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Presentation.Detection;

/// <summary>
/// Loads the own-row state pictures from the app's data folder (FR-AUTO-03). The file is read only when it is at most 1 MB (checked before reading,
/// security review L3); a missing, oversized, unreadable or malformed file means no pictures, and the own-status detector then reads nothing.
/// </summary>
public static class StatusTemplateFile
{
    /// <summary>Loads the pictures, or returns null with one plain sentence passed to <paramref name="warn"/> (never the file's content).</summary>
    /// <param name="path">The file.</param>
    /// <param name="warn">Receives the sentence when a file exists but could not be used.</param>
    /// <returns>The pictures, or null.</returns>
    public static OwnStatusTemplates? Load(string path, Action<string> warn)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(warn);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (BoundedFile.ReadAllText(path, BoundedFile.TextLimitBytes) is not { } text)
            {
                warn("Status icon pictures were not loaded: the file is larger than 1 MB.");
                return null;
            }

            var templates = OwnStatusTemplates.TryParse(text, out var problem);
            if (templates is null)
            {
                warn("Status icon pictures were not loaded: " + problem);
            }

            return templates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            warn("Status icon pictures could not be read (" + ex.GetType().Name + ").");
            return null;
        }
    }
}
