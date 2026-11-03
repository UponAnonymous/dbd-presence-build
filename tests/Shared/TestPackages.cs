using System.IO;
using System.IO.Compression;
using System.Text;

namespace DbdPresence.TestSupport;

/// <summary>Small stand-ins for Velopack full packages: a zip with a <c>.nuspec</c> naming the id and version, plus some content.</summary>
internal static class TestPackages
{
    /// <summary>
    /// Writes <c>&lt;id&gt;-&lt;version&gt;-&lt;channel&gt;-full.nupkg</c> into <paramref name="directory"/> and returns its path. With
    /// <paramref name="onlineAssembly"/> the package also holds <c>lib/app/DbdPresence.Online.dll</c> with those bytes, as a real app package does.
    /// </summary>
    public static string Write(string directory, string version, string channel = "stable", string id = "DbdPresence", string? fileName = null, int contentBytes = 4096, byte[]? onlineAssembly = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName ?? $"{id}-{version}-{channel}-full.nupkg");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var nuspec = zip.CreateEntry(id + ".nuspec");
            using (var writer = new StreamWriter(nuspec.Open(), new UTF8Encoding(false)))
            {
                writer.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
                      <metadata>
                        <id>{id}</id>
                        <version>{version}</version>
                        <title>DBD Presence</title>
                        <authors>DBD Presence contributors</authors>
                        <description>Test package</description>
                        <channel>{channel}</channel>
                      </metadata>
                    </package>
                    """);
            }

            if (onlineAssembly is not null)
            {
                using var online = zip.CreateEntry("lib/app/DbdPresence.Online.dll").Open();
                online.Write(onlineAssembly);
            }

            var content = zip.CreateEntry("lib/app/DBD-Presence.dll");
            using var stream = content.Open();
            var bytes = new byte[contentBytes];
            new Random(version.Aggregate(17, (h, c) => unchecked((h * 31) + c))).NextBytes(bytes);
            stream.Write(bytes);
        }

        return path;
    }
}
