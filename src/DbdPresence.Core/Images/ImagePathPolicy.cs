namespace DbdPresence.Core.Images;

/// <summary>
/// Decides whether a full file path may be handed to the image helper (ADR-0013, ADR-0016). Network locations are never opened:
/// opening <c>\\server\share</c> would make Windows authenticate to a remote host (NTLM) and reach the network, which the app never does.
/// </summary>
public static class ImagePathPolicy
{
    /// <summary>
    /// True when <paramref name="fullPath"/> (already passed through <c>Path.GetFullPath</c>) is a local path: it does not start with
    /// two backslashes (UNC, <c>\\?\UNC\</c>, device paths) and its drive is fixed or removable.
    /// </summary>
    /// <param name="fullPath">The absolute path.</param>
    /// <param name="driveTypeOf">Maps a drive root such as <c>C:\</c> to its type; may throw <see cref="ArgumentException"/> for an invalid root.</param>
    public static bool IsLocalPath(string fullPath, Func<string, DriveType> driveTypeOf)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(driveTypeOf);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        // Parsed here rather than with Path.GetPathRoot so the rule is the same on every platform: only "X:\" or "X:/" roots qualify.
        if (fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':' || fullPath[2] is not ('\\' or '/'))
        {
            return false;
        }

        var root = fullPath[..3];

        try
        {
            return driveTypeOf(root) is DriveType.Fixed or DriveType.Removable;
        }
        catch (ArgumentException)
        {
            // The root is not a drive the OS knows: not a local path we may open.
            return false;
        }
    }
}
