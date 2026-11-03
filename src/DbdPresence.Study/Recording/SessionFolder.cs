namespace DbdPresence.StudyCapture;

/// <summary>The folder of one study session: Documents\DBD Presence Study\&lt;date-time&gt;\.</summary>
internal sealed class SessionFolder
{
    private SessionFolder(string path) => Path = path;

    /// <summary>Gets the parent folder for all sessions (the Documents folder of the current user, wherever Windows places it).</summary>
    public static string Root { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "DBD Presence Study");

    public string Path { get; }

    public static SessionFolder Create(DateTimeOffset localNow) => Create(Root, localNow);

    /// <summary>Creates the folder of a session under <paramref name="root"/> (a test or the app passes its own root; the default is <see cref="Root"/>).</summary>
    public static SessionFolder Create(string root, DateTimeOffset localNow)
    {
        // A name is the second the recording started. When another recorder (the console tool, or an earlier recording in the same second) already holds it, the
        // next free second is used, so two recordings never share a folder; the name stays in the format the labelling flow reads.
        var path = System.IO.Path.Combine(root, localNow.ToString(SessionRetention.NameFormat, System.Globalization.CultureInfo.InvariantCulture));
        for (var step = 1; Directory.Exists(path) && step <= 3600; step++)
        {
            path = System.IO.Path.Combine(root, localNow.AddSeconds(step).ToString(SessionRetention.NameFormat, System.Globalization.CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(path);
        return new SessionFolder(path);
    }

    public void WriteFile(string name, byte[] bytes) => File.WriteAllBytes(System.IO.Path.Combine(Path, name), bytes);

    /// <summary>Writes metadata.json; returns false and the reason when the disk refuses (the caller tells the owner).</summary>
    public bool TryWriteMetadata(SessionMetadata metadata, out string? problem)
    {
        try
        {
            // Written aside and moved into place, so a power cut or a kill never leaves a half-written marker file (the 30-day clean-up reads it).
            var temp = System.IO.Path.Combine(Path, SessionRetention.MetadataTempName);
            File.WriteAllText(temp, metadata.ToJson());
            File.Move(temp, System.IO.Path.Combine(Path, "metadata.json"), overwrite: true);
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
    }
}
