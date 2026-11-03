namespace DbdPresence.Configuration;

/// <summary>The few file operations the settings store needs; faked in tests (ARCHITECTURE §10).</summary>
public interface ISettingsFileSystem
{
    /// <summary>Returns whether a file exists.</summary>
    bool Exists(string path);

    /// <summary>Reads a whole file as UTF-8 text.</summary>
    string ReadAllText(string path);

    /// <summary>Writes a whole file as UTF-8 text and flushes it to disk.</summary>
    void WriteAllTextDurable(string path, string text);

    /// <summary>Atomically replaces <paramref name="destination"/> with <paramref name="source"/> (same volume).</summary>
    void Move(string source, string destination);

    /// <summary>Copies a file, overwriting the destination.</summary>
    void Copy(string source, string destination);

    /// <summary>Deletes a file if it exists.</summary>
    void Delete(string path);

    /// <summary>Creates a directory and its parents if missing.</summary>
    void CreateDirectory(string path);

    /// <summary>Returns the size of a file in bytes.</summary>
    long Length(string path);
}

/// <summary>The real file system.</summary>
public sealed class PhysicalSettingsFileSystem : ISettingsFileSystem
{
    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void WriteAllTextDurable(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void Move(string source, string destination) => File.Move(source, destination, overwrite: true);

    /// <inheritdoc />
    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: true);

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public long Length(string path) => new FileInfo(path).Length;
}
