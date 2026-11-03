using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using DbdPresence.Packs.Signing;

namespace DbdPresence.ReleaseSigner;

/// <summary>Which offline key to make (PLAN_V2 §6.2).</summary>
internal enum OfflineKeyKind
{
    /// <summary>Signs release feeds (purpose <c>releases</c>). Can push code to every user: the most valuable key.</summary>
    Release,

    /// <summary>Signs key replacements (purpose <c>key-replacement</c>): the only way to revoke a compromised key. Two offline copies.</summary>
    Recovery,
}

/// <summary>The public half of an offline key, in the form of an entry of <c>src/DbdPresence.Online/TrustedServiceKeys.json</c>.</summary>
/// <param name="Kid">The key id.</param>
/// <param name="Spki">The P-256 SubjectPublicKeyInfo bytes.</param>
/// <param name="Purposes">What it may sign.</param>
internal sealed record PublicKeyEntry(string Kid, byte[] Spki, IReadOnlyList<string> Purposes)
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    /// <summary>Gets the SPKI as unpadded base64url, as the key lists hold it.</summary>
    public string SpkiText => Base64Url.EncodeToString(Spki);

    /// <summary>Gets the fingerprint to publish on a second channel and to read back: SHA-256 of the SPKI, in groups of four hex digits.</summary>
    public string Fingerprint => ReleaseKeys.Fingerprint(Spki);

    /// <summary>The entry as JSON (one object, indented).</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(
        new Dictionary<string, object> { ["kid"] = Kid, ["purposes"] = Purposes, ["spki"] = SpkiText },
        s_indented);

    /// <summary>Reads an entry written by <see cref="ToJson"/>.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="FormatException">The text is not such an entry.</exception>
    public static PublicKeyEntry Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var kid = root.GetProperty("kid").GetString() ?? throw new FormatException("The key entry has no kid.");
            var spki = Base64Url.DecodeFromChars(root.GetProperty("spki").GetString() ?? throw new FormatException("The key entry has no spki."));
            var purposes = root.GetProperty("purposes").EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToArray();
            _ = new TrustedKeySet(new Dictionary<string, TrustedKey> { [kid] = new TrustedKey(spki, purposes) });
            return new PublicKeyEntry(kid, spki, purposes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("The file is not a public key entry made by this tool.", ex);
        }
    }
}

/// <summary>Making, storing and loading the offline keys. Private keys are only ever written encrypted with the owner's passphrase.</summary>
internal static class ReleaseKeys
{
    /// <summary>The shortest passphrase accepted.</summary>
    public const int MinPassphraseLength = 12;

    /// <summary>PBKDF2 iterations for the encrypted private key file (OWASP 2023 guidance for PBKDF2-HMAC-SHA256).</summary>
    public const int Pbkdf2Iterations = 600_000;

    /// <summary>The purposes a key of <paramref name="kind"/> may sign.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The purposes.</returns>
    public static IReadOnlyList<string> PurposesOf(OfflineKeyKind kind) => kind == OfflineKeyKind.Release ? [SignedPurposes.Releases] : [SignedPurposes.KeyReplacement];

    /// <summary>Makes a new P-256 key pair.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="kid">The key id, for example <c>release-prod-1</c>.</param>
    /// <returns>The private key (the caller disposes it) and its public entry.</returns>
    public static (ECDsa Key, PublicKeyEntry Public) Generate(OfflineKeyKind kind, string kid)
    {
        RequireKid(kid);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, new PublicKeyEntry(kid, key.ExportSubjectPublicKeyInfo(), PurposesOf(kind)));
    }

    /// <summary>The encrypted PKCS#8 PEM text of a private key (AES-256-CBC, PBKDF2-HMAC-SHA256).</summary>
    /// <param name="key">The key.</param>
    /// <param name="passphrase">The owner's passphrase.</param>
    /// <returns>The PEM text.</returns>
    public static string ExportEncrypted(ECDsa key, ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequirePassphrase(passphrase);
        return key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations));
    }

    /// <summary>Loads an encrypted private key file.</summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="passphrase">The passphrase.</param>
    /// <returns>The key (the caller disposes it).</returns>
    /// <exception cref="CryptographicException">Wrong passphrase, or not an encrypted P-256 key.</exception>
    public static ECDsa ImportEncrypted(string pem, ReadOnlySpan<char> passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(pem);
        if (!pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new CryptographicException("The key file is not an encrypted private key made by this tool.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(pem, passphrase);
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new CryptographicException("The key is not an ECDSA P-256 key.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>SHA-256 of the SPKI, lower-case hex in groups of four.</summary>
    /// <param name="spki">The public key bytes.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(ReadOnlySpan<byte> spki)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(spki));
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Checks a key id: letters, digits and hyphens, 3 to 64 characters.</summary>
    /// <param name="kid">The key id.</param>
    /// <exception cref="ArgumentException">Not a valid key id.</exception>
    public static void RequireKid(string kid)
    {
        if (string.IsNullOrEmpty(kid) || kid.Length is < 3 or > 64 || !kid.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("A key name is 3 to 64 lower-case letters, digits and hyphens, for example release-prod-1.", nameof(kid));
        }
    }

    /// <summary>Checks a passphrase length.</summary>
    /// <param name="passphrase">The passphrase.</param>
    /// <exception cref="ArgumentException">Too short.</exception>
    public static void RequirePassphrase(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.Trim().Length < MinPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {MinPassphraseLength} characters.", nameof(passphrase));
        }
    }

    /// <summary>
    /// Refuses a folder inside a source repository (a <c>.git</c> folder or file in it or above it): a private key must never be committed,
    /// not even encrypted.
    /// </summary>
    /// <param name="directory">The folder a private key would be written to.</param>
    /// <exception cref="InvalidOperationException">The folder is inside a repository.</exception>
    public static void RefuseRepositoryFolder(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                throw new InvalidOperationException($"'{directory}' is inside a source repository ({current.FullName}). Keep private keys on the offline drive, never in a repository.");
            }
        }
    }
}
