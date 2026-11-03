using DbdPresence.Packs.Local;

namespace DbdPresence.TestSupport;

/// <summary>
/// In-memory <see cref="ILocalPackStorage"/>, shared by the test projects that exercise the pack library. Keys ignore the
/// separator style (see <see cref="VirtualPaths"/>), so tests run the same on Linux and Windows.
/// </summary>
internal sealed class InMemoryLocalStorage : ILocalPackStorage
{
    public Dictionary<string, byte[]> Files { get; } = VirtualPaths.NewFiles<byte[]>();

    public bool Exists(string path) => Files.ContainsKey(path);

    public long Length(string path) => Files[path].Length;

    public byte[] ReadAllBytes(string path) => Files.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path);

    public void WriteAllBytesDurable(string path, ReadOnlySpan<byte> bytes) => Files[path] = bytes.ToArray();

    public void Move(string source, string destination)
    {
        Files[destination] = Files[source];
        Files.Remove(source);
    }

    public void Delete(string path) => Files.Remove(path);

    public void CreateDirectory(string path)
    {
    }

    public IReadOnlyList<string> ListFiles(string directory) =>
        [.. Files.Keys.Where(k => VirtualPaths.IsUnder(k, directory, out var rest) && !rest.Contains('/', StringComparison.Ordinal))];

    public IReadOnlyList<string> ListDirectories(string directory)
    {
        var found = new Dictionary<string, string>(VirtualPaths.Comparer);
        foreach (var key in Files.Keys)
        {
            if (VirtualPaths.IsUnder(key, directory, out var rest) && rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0)
            {
                var child = Path.Combine(directory, rest[..slash]);
                found[child] = child;
            }
        }

        return [.. found.Values];
    }

    public void DeleteDirectory(string directory)
    {
        foreach (var key in Files.Keys.Where(k => VirtualPaths.IsUnder(k, directory, out _)).ToArray())
        {
            Files.Remove(key);
        }
    }
}
