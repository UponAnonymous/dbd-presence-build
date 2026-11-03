using System.Security.Cryptography;

namespace DbdPresence.Packs.Signing;

/// <summary>A trusted public key (SPKI DER) and the purposes it may sign (ADR-0012: a separate key per purpose).</summary>
/// <param name="Spki">The P-256 SubjectPublicKeyInfo bytes.</param>
/// <param name="Purposes">The purposes this key is allowed to sign; must not be empty.</param>
public sealed record TrustedKey(byte[] Spki, IReadOnlyCollection<string> Purposes);

/// <summary>Trusted ECDSA P-256 public keys by <c>kid</c>, each scoped to purposes (ADR-0012). Keys are stored as SPKI (DER) bytes.</summary>
public sealed class TrustedKeySet
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly Dictionary<string, byte[]> _keys;
    private readonly Dictionary<string, HashSet<string>> _purposes;

    /// <summary>Creates the set; every key must be a valid P-256 SubjectPublicKeyInfo with at least one purpose.</summary>
    /// <exception cref="ArgumentException">A key is not a P-256 public key or has no purposes (a configuration error, not remote input).</exception>
    public TrustedKeySet(IReadOnlyDictionary<string, TrustedKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = new Dictionary<string, byte[]>(keys.Count, StringComparer.Ordinal);
        _purposes = new Dictionary<string, HashSet<string>>(keys.Count, StringComparer.Ordinal);
        foreach (var (kid, key) in keys)
        {
            if (string.IsNullOrEmpty(kid))
            {
                throw new ArgumentException("A trusted key needs a non-empty kid.", nameof(keys));
            }

            ArgumentNullException.ThrowIfNull(key);
            if (key.Purposes is null || key.Purposes.Count == 0 || key.Purposes.Any(string.IsNullOrEmpty))
            {
                throw new ArgumentException($"Key '{kid}' needs at least one non-empty purpose.", nameof(keys));
            }

            Validate(kid, key.Spki);
            _keys[kid] = [.. key.Spki];
            _purposes[kid] = new HashSet<string>(key.Purposes, StringComparer.Ordinal);
        }
    }

    /// <summary>The number of trusted keys.</summary>
    public int Count => _keys.Count;

    /// <summary>The ids of the trusted keys.</summary>
    public IReadOnlyCollection<string> Kids => _keys.Keys;

    /// <summary>Whether <paramref name="kid"/> is trusted.</summary>
    public bool Contains(string kid) => _keys.ContainsKey(kid);

    /// <summary>Whether <paramref name="kid"/> is trusted to sign documents of <paramref name="purpose"/>.</summary>
    public bool IsAllowedFor(string kid, string purpose) => _purposes.TryGetValue(kid, out var allowed) && allowed.Contains(purpose);

    /// <summary>Verifies an IEEE P1363 (r||s) SHA-256 signature over <paramref name="data"/> with the named key.</summary>
    /// <returns>False when the key is unknown, the signature is malformed, or it does not match. Never throws on hostile input.</returns>
    public bool VerifySignature(string kid, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (!_keys.TryGetValue(kid, out var spki))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // The platform crypto provider rejected the signature encoding: that is a bad signature, not a crash.
            return false;
        }
    }

    private static void Validate(string kid, byte[] spki)
    {
        ArgumentNullException.ThrowIfNull(spki);
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            var oid = ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
            if (oid != P256Oid)
            {
                throw new ArgumentException($"Key '{kid}' is not an ECDSA P-256 key.", nameof(spki));
            }
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"Key '{kid}' is not a valid SubjectPublicKeyInfo.", nameof(spki), ex);
        }
    }
}
