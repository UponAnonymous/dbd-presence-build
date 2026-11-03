using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbdPresence.Vision.Evaluation;

namespace DbdPresence.Eval;

/// <summary>
/// The held-out manifest file (DETECTION_VALIDATION §5 and §12.7): the held-out sessions with the hash of each one's labels, and one hash over all of
/// them. The owner commits it, with the hash, <b>before</b> the first evaluation of a frozen detector version; a held-out run refuses any set that
/// differs from it. The file holds session ids and hashes only: no names, no pictures.
/// </summary>
internal sealed record ManifestFile(string Hash, IReadOnlyList<string> Entries)
{
    private const string HashPrefix = "manifest-sha256:";

    /// <summary>Computes the manifest hash of entries (<c>sessionId:labelsSha256</c>): the same formula as <see cref="DatasetLoader.ManifestHash"/>.</summary>
    public static string HashOf(IEnumerable<string> entries) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries.Order(StringComparer.Ordinal)))));

    /// <summary>Builds a manifest for sessions.</summary>
    public static ManifestFile For(IEnumerable<DatasetSession> sessions)
    {
        var entries = sessions.Select(s => $"{s.Metadata.SessionId}:{s.LabelsSha256}").Order(StringComparer.Ordinal).ToList();
        return new ManifestFile(HashOf(entries), entries);
    }

    /// <summary>Renders the file text.</summary>
    public string ToText() => string.Concat(
        "# DBD Presence held-out manifest (schema 1)\n",
        "# Commit this file and its hash BEFORE the first evaluation of a frozen detector version (DETECTION_VALIDATION section 5).\n",
        "# Any later change to a held-out session's labels changes the hash, and the held-out run then refuses to start.\n",
        HashPrefix, " ", Hash, "\n",
        string.Concat(Entries.Select(e => e + "\n")));

    /// <summary>Reads a manifest and checks that its hash line matches its entries.</summary>
    /// <exception cref="FormatException">The file is not a manifest, or its hash does not match its entries.</exception>
    public static ManifestFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? hash = null;
        var entries = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith(HashPrefix, StringComparison.Ordinal))
            {
                hash = line[HashPrefix.Length..].Trim().ToLower(CultureInfo.InvariantCulture);
            }
            else if (line.Contains(':', StringComparison.Ordinal))
            {
                entries.Add(line);
            }
            else
            {
                throw new FormatException("The manifest has a line that is neither an entry nor the hash.");
            }
        }

        if (hash is null || entries.Count == 0)
        {
            throw new FormatException("The manifest has no hash line or no sessions.");
        }

        if (entries.Distinct(StringComparer.Ordinal).Count() != entries.Count || entries.Select(e => e[..e.IndexOf(':', StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).Count() != entries.Count)
        {
            throw new FormatException("The manifest lists a session twice.");
        }

        if (!string.Equals(HashOf(entries), hash, StringComparison.Ordinal))
        {
            throw new FormatException("The manifest's hash line does not match its sessions: the file was edited.");
        }

        return new ManifestFile(hash, [.. entries.Order(StringComparer.Ordinal)]);
    }

    /// <summary>Gets the session ids the manifest lists.</summary>
    public IReadOnlyList<string> SessionIds => [.. Entries.Select(e => e[..e.IndexOf(':', StringComparison.Ordinal)])];
}
