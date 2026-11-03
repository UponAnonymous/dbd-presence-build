namespace DbdPresence.LocalCi;

/// <summary>
/// Writes files into a place an ordinary user controls (the owner's profile folder, where <c>.wslconfig</c> lives) from the elevated program, without being sent anywhere by a link the user
/// may have put there. A new file is never created at a name the user could have guessed (a link at a guessed name would make a create follow it and make the file somewhere else), a new content
/// replaces an existing file by renaming (a rename replaces the name, a link or a hard link included, and never changes what it leads to), and nothing is ever written through an existing name.
/// </summary>
public static class SafeFileReplace
{
    /// <summary>A name next to <paramref name="path"/> that nobody can have guessed: <c>path.label-</c> and 32 random hexadecimal digits.</summary>
    public static string UnguessableSibling(string path, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(label);
        return $"{path}.{label}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a new file with an unguessable name next to <paramref name="path"/> and renames it over <paramref name="path"/>. If anything fails the new file is removed
    /// and <paramref name="path"/> is as it was.
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> content)
    {
        var temporary = UnguessableSibling(path, "new");
        var replaced = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
            }

            File.Move(temporary, path, overwrite: true);
            replaced = true;
        }
        finally
        {
            if (!replaced)
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Copies the rest of <paramref name="source"/> into a new file at <paramref name="destination"/>, which must not exist. Pass a name from <see cref="UnguessableSibling"/> for a place an ordinary
    /// user controls. The source is read from an open stream, never from a path.
    /// </summary>
    public static void CopyToNewFile(Stream source, string destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }
}
