using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.Core.Online;

/// <summary>
/// The public half of the device key (ECDSA P-256), as the service expects it (ADR-0011): a JWK with only
/// <c>kty</c>, <c>crv</c>, <c>x</c> and <c>y</c>, where <c>x</c> and <c>y</c> are unpadded base64url of 32 bytes.
/// </summary>
public sealed record DevicePublicKey
{
    private const int CoordinateLength = 32;

    /// <summary>Initializes a new instance of the <see cref="DevicePublicKey"/> class from the two 32-byte coordinates.</summary>
    /// <param name="x">The X coordinate, 32 bytes.</param>
    /// <param name="y">The Y coordinate, 32 bytes.</param>
    public DevicePublicKey(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        if (x.Length != CoordinateLength || y.Length != CoordinateLength)
        {
            throw new ArgumentException("A P-256 coordinate is exactly 32 bytes.");
        }

        X = Base64Url.EncodeToString(x);
        Y = Base64Url.EncodeToString(y);
    }

    /// <summary>Gets the X coordinate, unpadded base64url.</summary>
    public string X { get; }

    /// <summary>Gets the Y coordinate, unpadded base64url.</summary>
    public string Y { get; }

    /// <summary>
    /// Gets the RFC 7638 JWK thumbprint input: required members only, in lexicographic order, no whitespace. The service stores this
    /// exact text and hashes it for <c>cnf</c>.
    /// </summary>
    public string CanonicalJwk => $$"""{"crv":"P-256","kty":"EC","x":"{{X}}","y":"{{Y}}"}""";

    /// <summary>Gets the thumbprint: base64url of SHA-256 over <see cref="CanonicalJwk"/> (the licence's <c>cnf</c>).</summary>
    public string Thumbprint => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJwk)));
}

/// <summary>The device key could not be created, read or used.</summary>
public sealed class DeviceKeyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DeviceKeyException"/> class.</summary>
    public DeviceKeyException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DeviceKeyException"/> class.</summary>
    /// <param name="message">What failed (never key material).</param>
    public DeviceKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DeviceKeyException"/> class.</summary>
    /// <param name="message">What failed (never key material).</param>
    /// <param name="innerException">The underlying failure.</param>
    public DeviceKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The per-user, non-exportable device key (FR-ONL-03, ADR-0011). The Windows implementation keeps it in a CNG key container; the
/// private half never leaves the store, so a copied token is useless on another PC.
/// </summary>
public interface IDeviceKeyStore
{
    /// <summary>Returns the public key, creating the key pair first when there is none.</summary>
    /// <returns>The public key.</returns>
    /// <exception cref="DeviceKeyException">The key store is unavailable.</exception>
    DevicePublicKey GetOrCreatePublicKey();

    /// <summary>Signs <paramref name="data"/> with ECDSA P-256 / SHA-256 and returns the 64-byte IEEE P1363 (r||s) signature.</summary>
    /// <param name="data">The bytes to sign.</param>
    /// <returns>The 64-byte signature.</returns>
    /// <exception cref="DeviceKeyException">There is no key or signing failed.</exception>
    byte[] Sign(ReadOnlySpan<byte> data);

    /// <summary>Deletes the key pair (after account deletion). Succeeds when there is none.</summary>
    /// <exception cref="DeviceKeyException">The key exists but could not be deleted.</exception>
    void Delete();
}
