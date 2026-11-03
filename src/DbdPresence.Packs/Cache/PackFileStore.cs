namespace DbdPresence.Packs.Cache;

/// <summary>The few file operations the pack cache needs; faked in tests (ARCHITECTURE §10).</summary>
public interface IPackFileStore
{
    /// <summary>Returns whether a file exists.</summary>
    bool Exists(string path);

    /// <summary>Returns the size of a file in bytes.</summary>
    long Length(string path);

    /// <summary>Reads a whole file.</summary>
    byte[] ReadAllBytes(string path);

    /// <summary>Writes a whole file and flushes it to disk.</summary>
    void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes);

    /// <summary>Atomically replaces <paramref name="destination"/> with <paramref name="source"/> (same volume).</summary>
    void Move(string source, string destination);

    /// <summary>Deletes a file if it exists.</summary>
    void Delete(string path);

    /// <summary>Creates a directory and its parents if missing.</summary>
    void CreateDirectory(string path);

    /// <summary>Lists the files (full paths) directly inside a directory; empty if the directory does not exist.</summary>
    IReadOnlyList<string> ListFiles(string directory);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalPackFileStore : IPackFileStore
{
    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public long Length(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void Move(string source, string destination) => File.Move(source, destination, overwrite: true);

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
}
