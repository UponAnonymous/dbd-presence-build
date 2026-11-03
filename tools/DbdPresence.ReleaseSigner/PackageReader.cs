using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using DbdPresence.Packs.Releases;

namespace DbdPresence.ReleaseSigner;

/// <summary>
/// Reads a full update package that Velopack's packer wrote (<c>DbdPresence-&lt;version&gt;-&lt;channel&gt;-full.nupkg</c>): its id and version from the
/// <c>.nuspec</c> inside, its size and its SHA-256. The file name, the id and the version must agree, or the package is refused.
/// </summary>
internal static class PackageReader
{
    private const long MaxNuspecBytes = 256 * 1024;

    /// <summary>Reads one package.</summary>
    /// <param name="path">The <c>.nupkg</c> file.</param>
    /// <param name="channel">The channel the package was packed for.</param>
    /// <param name="notes">Release notes to attach, or null.</param>
    /// <returns>The feed entry.</returns>
    /// <exception cref="InvalidDataException">The file is not a full package of this app, or its name, id or version disagree.</exception>
    public static ReleasePackage Read(string path, string channel, string? notes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new InvalidDataException($"'{path}' does not exist.");
        }

        if (info.Length is < 1 or > ReleaseFeed.MaxPackageBytes)
        {
            throw new InvalidDataException($"'{info.Name}' is {info.Length} bytes; a package must be 1 to {ReleaseFeed.MaxPackageBytes} bytes.");
        }

        var (id, versionText) = ReadNuspec(path);
        if (id != ReleaseFeed.ExpectedPackageId)
        {
            throw new InvalidDataException($"'{info.Name}' is package '{id}', not {ReleaseFeed.ExpectedPackageId}.");
        }

        var version = ReleaseVersion.TryParse(versionText)
            ?? throw new InvalidDataException($"'{info.Name}' has version '{versionText}', which is not major.minor.patch with an optional lower-case pre-release label.");
        if (info.Name != ReleaseFeed.PackageFileName(version, channel))
        {
            throw new InvalidDataException($"'{info.Name}' should be named {ReleaseFeed.PackageFileName(version, channel)} (the full package of version {version} on the {channel} channel).");
        }

        if (notes is { Length: > ReleaseFeed.MaxNotesLength })
        {
            throw new InvalidDataException($"The release notes have {notes.Length} characters; at most {ReleaseFeed.MaxNotesLength} are allowed.");
        }

        string sha;
        using (var stream = File.OpenRead(path))
        {
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        return new ReleasePackage(version, info.Name, info.Length, sha, notes);
    }

    /// <summary>Finds the full packages in a folder (Velopack's output folder), newest first.</summary>
    /// <param name="directory">The folder.</param>
    /// <param name="channel">The channel.</param>
    /// <returns>Their paths.</returns>
    public static IReadOnlyList<string> FindFullPackages(string directory, string channel) =>
        Directory.Exists(directory)
            ? [.. Directory.GetFiles(directory, $"{ReleaseFeed.ExpectedPackageId}-*-{channel}-full.nupkg", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");

    private static (string Id, string Version) ReadNuspec(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.SingleOrDefault(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' has no .nuspec at its root; it is not a Velopack package.");
            if (entry.Length > MaxNuspecBytes)
            {
                throw new InvalidDataException("The .nuspec is unexpectedly large.");
            }

            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var id = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim();
            var version = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
            {
                throw new InvalidDataException("The .nuspec has no id or version.");
            }

            return (id, version);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The .nuspec of '{Path.GetFileName(path)}' is not valid XML.", ex);
        }
    }
}
