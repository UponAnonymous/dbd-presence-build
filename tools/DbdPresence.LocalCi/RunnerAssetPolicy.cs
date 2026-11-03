using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>
/// What a runner download must look like before it is fetched (security review 2026-10-30, M6): the file names GitHub gives the packages, an address under the official
/// <c>actions/runner</c> release of exactly that version, and a checksum from the release's own digest that agrees with the one in the release notes.
/// </summary>
public static partial class RunnerAssetPolicy
{
    public static string AddressPrefix(string version) => $"https://github.com/actions/runner/releases/download/v{version}/";

    public static string LinuxFileName(string version) => $"actions-runner-linux-x64-{version}.tar.gz";

    /// <summary>True for a version such as 2.338.0 (three numbers).</summary>
    public static bool IsVersion(string? text) => !string.IsNullOrEmpty(text) && VersionText().IsMatch(text);

    /// <summary>Parses a version such as 2.338.0 for comparison; null when it is not one.</summary>
    public static Version? ParseVersion(string? text) => IsVersion(text) ? Version.Parse(text!) : null;

    /// <summary>True when <paramref name="url"/> is exactly the address of <paramref name="fileName"/> in the official release of <paramref name="version"/>.</summary>
    public static bool IsExpectedAddress(Uri url, string version, string fileName)
    {
        ArgumentNullException.ThrowIfNull(url);
        return IsVersion(version)
               && url.IsAbsoluteUri
               && string.Equals(url.AbsoluteUri, AddressPrefix(version) + fileName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the <c>digest</c> GitHub keeps for a release file (<c>sha256:</c> and 64 hexadecimal digits). Null for no digest; throws for one in another form, because a
    /// checksum that cannot be read must not be skipped quietly.
    /// </summary>
    /// <exception cref="DownloadVerificationException">The digest is present but not a SHA-256.</exception>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var match = DigestText().Match(digest.Trim());
        if (!match.Success)
        {
            throw new DownloadVerificationException("GitHub gave a checksum for a runner file in a form this program does not know; refusing to use the download.");
        }

        return match.Groups["hash"].Value.ToLowerInvariant();
    }

    /// <summary>The problems that stop a release from being used (empty when it can be downloaded).</summary>
    public static IReadOnlyList<string> Problems(RunnerRelease release, string linuxShaFromNotes)
    {
        ArgumentNullException.ThrowIfNull(release);
        var problems = new List<string>();
        if (!IsVersion(release.Version))
        {
            problems.Add("the runner release has a version that is not three numbers");
            return problems;
        }

        if (!IsExpectedAddress(release.LinuxPackage, release.Version, LinuxFileName(release.Version)))
        {
            problems.Add($"the linux-x64 download address is not {AddressPrefix(release.Version)}{LinuxFileName(release.Version)}");
        }

        if (release.LinuxDigest is not null && !Hashing.SameHash(release.LinuxDigest, linuxShaFromNotes))
        {
            problems.Add("GitHub's checksum for the linux-x64 file differs from the one in the release notes");
        }

        return problems;
    }

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionText();

    [GeneratedRegex(@"^sha256:(?<hash>[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestText();
}
