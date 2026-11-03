using DbdPresence.StudyCapture;

namespace DbdPresence.Study;

/// <summary>
/// The words about what the study recorder saves, shared by the Study page's one-time confirmation and the console recorder's start-up text so the two never
/// say different things (SAFETY_AND_PRIVACY section 3, FR-LAB-06).
/// </summary>
public static class StudyRecorderText
{
    /// <summary>Gets the days a recording is kept before it deletes itself.</summary>
    public static int RetentionDays => SessionRetention.Days;

    /// <summary>Gets the local date (yyyy-MM-dd) on which a recording started at <paramref name="startedUtc"/> deletes itself.</summary>
    /// <param name="startedUtc">When the recording starts.</param>
    /// <returns>The date.</returns>
    public static string ExpiryDate(DateTimeOffset startedUtc) => SessionRetention.ExpiryDate(startedUtc);

    /// <summary>The lines that say what recording does.</summary>
    /// <param name="stopInstruction">How to stop, in the host's words (for example <c>press Q</c> or <c>press Stop recording</c>).</param>
    /// <returns>The lines, without bullets.</returns>
    public static IReadOnlyList<string> WhatItDoes(string stopInstruction)
    {
        ArgumentException.ThrowIfNullOrEmpty(stopInstruction);
        return
        [
            "It waits for Dead by Daylight to start.",
            "It then saves a picture of the game window (and only that window) every 2 seconds, and one extra picture each time you resize the window.",
            "The recorder itself sends nothing anywhere and has no network code. The pictures stay in the folder named below. If you turn on automatic labelling in the DBD Presence app, it later sends review pictures made from your recordings, with other players' names in them, to your own labelling service as described there.",
            "It never touches the game itself: it only asks Windows for a picture of the window.",
            "For a windowed game it keeps only the game area, without the title bar and border, and metadata.json says so.",
            $"It stops when you close the game or {stopInstruction}, when you sign out or the app no longer recognises your account as the owner's (in the app), or when a recording reaches 2 GB. There is no limit on the number of recordings except the {SessionRetention.Days} days below.",
            $"Each time it starts, it deletes its own recordings that are more than {SessionRetention.Days} days old.",
        ];
    }

    /// <summary>The line that names the folder and says whether it looks like a synced folder.</summary>
    /// <param name="root">The folder that holds the recordings.</param>
    /// <param name="environment">Reads an environment variable (null when unset); injected so tests do not depend on the machine.</param>
    /// <returns>The lines, without bullets.</returns>
    public static IReadOnlyList<string> Where(string root, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(environment);
        return [$"Pictures are saved in: {root}", SyncNote(root, environment)];
    }

    /// <summary>The privacy lines.</summary>
    /// <param name="expiryDate">The date a recording made today deletes itself (yyyy-MM-dd).</param>
    /// <returns>The lines, without bullets.</returns>
    public static IReadOnlyList<string> Privacy(string expiryDate)
    {
        ArgumentException.ThrowIfNullOrEmpty(expiryDate);
        return
        [
            "The pictures can show other players' names, and anything else that is on the game screen.",
            "Look through the folder before you send it to anyone. Send it only to the project owner, privately.",
            "Never post these pictures in public.",
            $"Recordings are deleted automatically after {SessionRetention.Days} days, on {expiryDate} for a recording made today.",
            "To delete a recording earlier, delete its folder (nothing is kept anywhere else).",
        ];
    }

    private static string SyncNote(string root, Func<string, string?> environment)
    {
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (environment(name) is { Length: > 0 } sync && IsInside(root, sync))
            {
                return $"WARNING: this folder is inside OneDrive ({name}). OneDrive uploads the pictures to your Microsoft account, and a synced file can stop the {SessionRetention.Days}-day clean-up from deleting it (it will say so). Move your recordings out of OneDrive if you do not want that.";
            }
        }

        try
        {
            var info = new DirectoryInfo(root);
            if (info.Exists && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00400000)) != 0)
            {
                return $"WARNING: this folder looks like a synced or linked folder. If a sync service uploads it, the pictures leave this PC through that service, and the {SessionRetention.Days}-day clean-up may refuse to delete them (it will say so).";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var notChecked = $"Could not check whether this folder is synced ({ex.GetType().Name}). If Windows keeps your Documents in OneDrive or another sync service, that service uploads the pictures too.";
            return notChecked;
        }

        return "This could not be seen to be a synced folder, but if Windows keeps your Documents in OneDrive or another sync service, that service uploads the pictures too.";
    }

    private static bool IsInside(string path, string folder)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var parent = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
