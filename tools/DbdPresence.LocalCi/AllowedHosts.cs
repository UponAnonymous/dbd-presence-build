namespace DbdPresence.LocalCi;

/// <summary>The only hosts this tool talks to (FR-CI-05): HTTPS, the default port, nothing else.</summary>
public static class AllowedHosts
{
    public const string Api = "api.github.com";

    /// <summary>Where downloads may come from, including the hosts GitHub's release addresses redirect to (checked against the live redirect, docs/owner/free-ci.md).</summary>
    public static IReadOnlySet<string> Downloads { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "releases.ubuntu.com",
    };

    /// <summary>Every host literal the program contains (the architecture test compares the built program with this list).</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(Downloads, StringComparer.OrdinalIgnoreCase) { Api };

    public static bool IsAllowedDownload(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return IsHttps(url) && Downloads.Contains(url.Host);
    }

    /// <summary>
    /// True for an address a download may <em>start</em> at: a file of the official runner releases on github.com, or a file in Canonical's Ubuntu 24.04 folder.
    /// Redirects may then lead to any host of <see cref="Downloads"/>, but nothing may start anywhere else (security review 2026-10-30, M6).
    /// </summary>
    public static bool IsAllowedDownloadStart(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsAllowedDownload(url) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            return false;
        }

        var path = url.AbsolutePath;
        return (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/actions/runner/releases/download/v", StringComparison.Ordinal))
               || (url.Host.Equals("releases.ubuntu.com", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/noble/", StringComparison.Ordinal));
    }

    public static bool IsAllowedApi(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return IsHttps(url) && string.Equals(url.Host, Api, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttps(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo);
}
