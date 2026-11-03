namespace DbdPresence.LocalCi;

/// <summary>
/// Deletes a file or folder and what is inside it without ever following a link (security review 2026-10-30, H1 M1). A job may leave a junction or symbolic link
/// inside its own folder that points at a place it cannot touch itself (the Windows folder, the owner's files). Deleting through it as an administrator would delete the
/// target; this removes the link and leaves the target alone.
/// </summary>
/// <remarks>
/// For places only Administrators and the system can write to (the data folder, the program's folder, the token files). It clears a read-only attribute before deleting, and the attributes of a
/// file are shared by all its hard links, so it must never be used on anything a job's user could have filled: a job's folder is removed with the handle based clean-up
/// (<c>WindowsTreeRemoval</c>), which writes no attribute and no permission at all (security review 2026-10-30, confused deputy via hard links). The boundary tests check that.
/// </remarks>
public static class SafeTree
{
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var attributes = AttributesOf(path);
        if (attributes is null)
        {
            return;
        }

        var kind = attributes.Value;
        if (kind.HasFlag(FileAttributes.ReparsePoint))
        {
            RemoveLink(path, kind);
            return;
        }

        if (!kind.HasFlag(FileAttributes.Directory))
        {
            ClearReadOnly(path, kind);
            File.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            Delete(entry);
        }

        ClearReadOnly(path, kind);
        Directory.Delete(path, recursive: false);
    }

    /// <summary>True when the path is a link (junction, symbolic link or another reparse point) itself, whatever it points at.</summary>
    public static bool IsLink(string path) => AttributesOf(path) is { } attributes && attributes.HasFlag(FileAttributes.ReparsePoint);

    private static FileAttributes? AttributesOf(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void RemoveLink(string path, FileAttributes kind)
    {
        // RemoveDirectory on a directory link removes the link, never its target; the same holds for a file link with DeleteFile.
        if (kind.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void ClearReadOnly(string path, FileAttributes kind)
    {
        if (kind.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, kind & ~FileAttributes.ReadOnly);
        }
    }
}
