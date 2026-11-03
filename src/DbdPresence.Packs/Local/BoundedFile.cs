namespace DbdPresence.Packs.Local;

/// <summary>
/// Reads a local file only when its size is within a limit that is checked before any byte is read (NFR-SEC, security review L3). A file in the app's data
/// folder that is far larger than anything the app writes is damaged or planted, and reading it whole would only waste memory.
/// </summary>
public static class BoundedFile
{
    /// <summary>The limit for the small text files the app writes itself (status pictures, learned prompts): 1 MB.</summary>
    public const long TextLimitBytes = 1024 * 1024;

    /// <summary>Reads the file when it is at most <paramref name="maxBytes"/> long; null when it is longer.</summary>
    /// <param name="path">The file.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <returns>The bytes, or null when the file is too large.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static byte[]? ReadAllBytes(string path, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        return new FileInfo(path).Length > maxBytes ? null : File.ReadAllBytes(path);
    }

    /// <summary>Reads the file as UTF-8 text when it is at most <paramref name="maxBytes"/> long; null when it is longer.</summary>
    /// <param name="path">The file.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <returns>The text, or null when the file is too large.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static string? ReadAllText(string path, long maxBytes) =>
        ReadAllBytes(path, maxBytes) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
}
