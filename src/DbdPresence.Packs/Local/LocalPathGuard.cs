using DbdPresence.Core.Images;

namespace DbdPresence.Packs.Local;

/// <summary>
/// Decides whether a path the user chose may be opened by the app process at all (ADR-0013, ADR-0016). Opening <c>\\server\share\file</c> makes Windows
/// authenticate to another computer, which the app never does, so such a path is refused before any file is read, hashed or handed to the image helper.
/// </summary>
public static class LocalPathGuard
{
    /// <summary>The sentence shown when a path is refused.</summary>
    public const string RefusalMessage = "That file is not on this PC. Copy it to a folder on this PC first, then choose it again.";

    /// <summary>
    /// True when <paramref name="path"/> is a local file path. UNC and device paths, anything with <c>://</c> and, on Windows, any drive that is not a
    /// fixed or removable disk are refused. On other systems (the Linux test host) a path is local when it is not UNC-shaped.
    /// </summary>
    /// <param name="path">The path as the user gave it.</param>
    public static bool IsAcceptable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }

        return ImagePathPolicy.IsLocalPath(full, root => new DriveInfo(root).DriveType);
    }
}
