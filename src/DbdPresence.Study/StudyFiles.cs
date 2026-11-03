using DbdPresence.Vision.Evaluation;

namespace DbdPresence.Study;

/// <summary>Small file rules shared by the labelling code: links are never followed, missing pictures are told plainly, and a saved answer file is found by name.</summary>
internal static class StudyFiles
{
    /// <summary>Tells whether a file or folder is a symbolic link, junction or other reparse point. Such an entry is never followed.</summary>
    public static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (((int)info.Attributes) != -1 && (info.Attributes & FileAttributes.ReparsePoint) != 0);

    /// <summary>Gets how many of a session's pictures are no longer on the disk.</summary>
    public static int MissingFrames(DatasetSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Frames.Count(f => !File.Exists(Path.Combine(session.Directory, f.File)));
    }

    /// <summary>Refuses a run on recordings whose pictures are gone, in plain words, instead of failing on the first missing file.</summary>
    /// <exception cref="InvalidOperationException">Some pictures are missing.</exception>
    public static void RequireFrames(IEnumerable<DatasetSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var gone = sessions.Where(s => MissingFrames(s) > 0).Select(s => s.Metadata.SessionId).ToList();
        if (gone.Count > 0)
        {
            throw new InvalidOperationException($"Pictures of these recordings are gone (deleted to free up space after the check, or removed by hand), so they cannot be read again: {string.Join(", ", gone)}. Nothing was run. A report that was already written stays the record of that check; measuring again needs new recordings (DETECTION_VALIDATION section 5).");
        }
    }

    /// <summary>Finds the newest saved answer file (the browser may add " (1)" to the name) written after the page was made.</summary>
    public static string? FindSaved(string answerFile, IEnumerable<string?> folders, DateTime notBeforeUtc)
    {
        // Browsers and chat apps add to a downloaded file's name ("name (1).json", "1a2b3c4d-name.json") and some save it as .txt, so any file whose name
        // holds the expected name is taken; what it says is checked when it is read.
        var stem = Path.GetFileNameWithoutExtension(answerFile);
        return folders
            .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f))
            .SelectMany(f => Directory.GetFiles(f!, "*" + stem + "*"))
            .Where(p => Path.GetExtension(p).ToUpperInvariant() is ".JSON" or ".TXT")
            .Where(p => File.GetLastWriteTimeUtc(p) >= notBeforeUtc)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
