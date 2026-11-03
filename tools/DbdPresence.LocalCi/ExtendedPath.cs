namespace DbdPresence.LocalCi;

/// <summary>The form of a full path that Windows takes without the 260-character limit.</summary>
public static class ExtendedPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// <c>\\?\C:\folder</c> for <c>C:\folder</c> and <c>\\?\UNC\server\share</c> for <c>\\server\share</c>. The prefix turns Windows' path clean-up off, so the path must be a normalised full path
    /// already (the callers pass the full path of a file they were given).
    /// </summary>
    public static string For(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        if (fullPath.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{fullPath} is a device path, not a file or folder.", nameof(fullPath));
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return UncPrefix + fullPath[2..];
        }

        if (fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\')
        {
            return Prefix + fullPath;
        }

        throw new ArgumentException($"{fullPath} is not a full Windows path.", nameof(fullPath));
    }
}
