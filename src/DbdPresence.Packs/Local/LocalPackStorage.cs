using DbdPresence.Packs.Cache;

namespace DbdPresence.Packs.Local;

/// <summary>
/// The file operations the local pack library needs on top of <see cref="IPackFileStore"/>: folder listing and removal.
/// Faked in tests (ARCHITECTURE §10).
/// </summary>
public interface ILocalPackStorage : IPackFileStore
{
    /// <summary>Lists the sub-directories (full paths) directly inside a directory; empty if it does not exist.</summary>
    IReadOnlyList<string> ListDirectories(string directory);

    /// <summary>Deletes a directory and everything in it, if it exists.</summary>
    void DeleteDirectory(string directory);
}

/// <summary>The real file system for imported packs and overrides.</summary>
public sealed class PhysicalLocalPackStorage : ILocalPackStorage
{
    private readonly PhysicalPackFileStore _files = new();

    /// <inheritdoc />
    public bool Exists(string path) => _files.Exists(path);

    /// <inheritdoc />
    public long Length(string path) => _files.Length(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => _files.ReadAllBytes(path);

    /// <inheritdoc />
    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes) => _files.WriteAllBytesDurable(path, bytes);

    /// <inheritdoc />
    public void Move(string source, string destination) => _files.Move(source, destination);

    /// <inheritdoc />
    public void Delete(string path) => _files.Delete(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _files.CreateDirectory(path);

    /// <inheritdoc />
    public IReadOnlyList<string> ListFiles(string directory) => _files.ListFiles(directory);

    /// <inheritdoc />
    public IReadOnlyList<string> ListDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];

    /// <inheritdoc />
    public void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
