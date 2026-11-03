namespace DbdPresence.Configuration.Tests;

/// <summary>In-memory <see cref="ISettingsFileSystem"/> with fault injection.</summary>
internal sealed class InMemoryFileSystem : ISettingsFileSystem
{
    public Dictionary<string, string> Files { get; } = VirtualPaths.NewFiles<string>();

    public int Writes { get; private set; }

    /// <summary>When set, the next write throws this exception (once).</summary>
    public Exception? FailNextWrite { get; set; }

    /// <summary>Called during each write, before the content is stored (simulates work happening mid-save).</summary>
    public Action? DuringWrite { get; set; }

    public bool Exists(string path) => Files.ContainsKey(path);

    public string ReadAllText(string path) => Files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);

    public void WriteAllTextDurable(string path, string text)
    {
        if (FailNextWrite is { } failure)
        {
            FailNextWrite = null;
            throw failure;
        }

        DuringWrite?.Invoke();
        Writes++;
        Files[path] = text;
    }

    public void Move(string source, string destination)
    {
        Files[destination] = ReadAllText(source);
        Files.Remove(source);
    }

    public void Copy(string source, string destination) => Files[destination] = ReadAllText(source);

    public void Delete(string path) => Files.Remove(path);

    public void CreateDirectory(string path)
    {
    }

    public long Length(string path) => System.Text.Encoding.UTF8.GetByteCount(ReadAllText(path));
}
