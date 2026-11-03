using System.Diagnostics.CodeAnalysis;

namespace DbdPresence.TestSupport;

/// <summary>
/// Makes the in-memory file stores of the tests independent of the host's path separator. The code under test builds
/// paths with <c>Path.Combine</c> (a backslash on Windows) while test data is easiest to write with a forward slash, so a
/// store keyed by raw strings finds nothing on Windows. Every in-memory store in the tests keys its files with
/// <see cref="Comparer"/>, which treats <c>/</c> and <c>\</c> as the same character, and lists directories with
/// <see cref="IsUnder"/>. Test code can therefore spell a path either way, and a regression like "works on Linux only"
/// cannot come back by writing a literal path.
/// </summary>
internal static class VirtualPaths
{
    /// <summary>Gets the key comparer for in-memory file dictionaries.</summary>
    public static IEqualityComparer<string> Comparer { get; } = new SeparatorInsensitiveComparer();

    /// <summary>Returns the path with every separator written as <c>/</c>.</summary>
    public static string Normalise(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/');
    }

    /// <summary>Creates an empty dictionary keyed by virtual path.</summary>
    public static Dictionary<string, T> NewFiles<T>() => new(Comparer);

    /// <summary>
    /// True when <paramref name="path"/> lies below <paramref name="directory"/>. <paramref name="rest"/> is the part after
    /// the directory, normalised, without a leading separator.
    /// </summary>
    public static bool IsUnder(string path, string directory, out string rest)
    {
        var prefix = Normalise(directory).TrimEnd('/') + "/";
        var normal = Normalise(path);
        if (normal.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = normal[prefix.Length..];
            return true;
        }

        rest = string.Empty;
        return false;
    }

    private sealed class SeparatorInsensitiveComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            x is null || y is null ? x is null && y is null : string.Equals(Normalise(x), Normalise(y), StringComparison.Ordinal);

        public int GetHashCode([DisallowNull] string obj) => Normalise(obj).GetHashCode(StringComparison.Ordinal);
    }
}
