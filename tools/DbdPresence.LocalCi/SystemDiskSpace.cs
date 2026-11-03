namespace DbdPresence.LocalCi;

/// <summary>The free room of the drive that holds a path.</summary>
public sealed class SystemDiskSpace : IDiskSpace
{
    public long FreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
