using System.Security.Cryptography;

namespace DbdPresence.LocalCi;

public static class Hashing
{
    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            return await Sha256OfStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The SHA-256 of everything from the stream's current position to its end, as lower-case hexadecimal.</summary>
    public static async Task<string> Sha256OfStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
