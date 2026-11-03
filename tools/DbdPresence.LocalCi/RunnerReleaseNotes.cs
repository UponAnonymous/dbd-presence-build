using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>Reads the SHA-256 that GitHub prints in an actions/runner release's notes between <c>&lt;!-- BEGIN SHA linux-x64 --&gt;</c> and <c>&lt;!-- END SHA linux-x64 --&gt;</c>.</summary>
public static partial class RunnerReleaseNotes
{
    public const string LinuxPlatform = "linux-x64";

    /// <exception cref="DownloadVerificationException">The marker is missing, appears twice with different values, or does not hold a 64-digit hexadecimal value.</exception>
    public static string ShaFor(string notes, string platform)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        var pattern = "<!--\\s*BEGIN SHA " + Regex.Escape(platform) + "\\s*-->(?<value>.*?)<!--\\s*END SHA " + Regex.Escape(platform) + "\\s*-->";
        var matches = Regex.Matches(notes, pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        if (matches.Count == 0)
        {
            throw new DownloadVerificationException($"The release notes have no checksum marker for {platform}; refusing to use the download.");
        }

        var values = matches.Select(m => m.Groups["value"].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Count != 1 || !Sha256Text().IsMatch(values[0]))
        {
            throw new DownloadVerificationException($"The checksum for {platform} in the release notes is not a SHA-256 value; refusing to use the download.");
        }

        return values[0].ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Text();
}
