// Covers: FR-ONL-03 (device-signed refresh), ADR-0011 (refresh-proof contract), RFC 7638 thumbprint, cross-stack contract with
// service/src/auth/device.ts via tests/fixtures/auth/refresh-proof-vector.json (made by the TypeScript code).
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;

namespace DbdPresence.Online.Tests;

public sealed class RefreshProofTests
{
    private static readonly JsonElement s_vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auth", "refresh-proof-vector.json"))).RootElement;

    private static string Str(string name) => s_vector.GetProperty(name).GetString()!;

    [Fact]
    public void Message_IsByteForByteWhatTheTypeScriptServiceBuilds()
    {
        var message = ServiceClient.ProofMessage(Str("refreshToken"), s_vector.GetProperty("timestamp").GetInt64(), Str("nonce"));

        Assert.Equal(Str("message"), Encoding.UTF8.GetString(message));
        Assert.StartsWith("dbdp-refresh-proof-v1\n", Encoding.UTF8.GetString(message), StringComparison.Ordinal);
    }

    [Fact]
    public void TokenHash_IsBase64UrlSha256OfTheToken_WithoutPadding()
    {
        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(Str("refreshToken"))));

        Assert.Equal(Str("tokenHash"), expected);
        Assert.DoesNotContain('=', expected);
    }

    [Fact]
    public void Thumbprint_AndCanonicalJwk_MatchTheTypeScriptService()
    {
        var jwk = s_vector.GetProperty("privateJwk");
        var key = new DevicePublicKey(Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!), Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!));

        Assert.Equal(Str("publicJwkCanonical"), key.CanonicalJwk);
        Assert.Equal(Str("thumbprint"), key.Thumbprint);
    }

    [Fact]
    public void TypeScriptSignature_VerifiesOverTheMessageBuiltInCSharp()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jwk = s_vector.GetProperty("privateJwk");
        ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!), Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!) },
        });
        var message = ServiceClient.ProofMessage(Str("refreshToken"), s_vector.GetProperty("timestamp").GetInt64(), Str("nonce"));
        var signature = Base64Url.DecodeFromChars(Str("signature"));

        Assert.Equal(64, signature.Length);
        Assert.True(ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        message[^1] ^= 1;
        Assert.False(ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void CSharpSignature_HasTheSameShapeTheServiceRequires()
    {
        using var keys = new FakeDeviceKeys();
        var message = ServiceClient.ProofMessage(Str("refreshToken"), 1798000000, "nonce_0123456789abcdef");

        var signature = keys.Sign(message);
        var encoded = Base64Url.EncodeToString(signature);

        Assert.Equal(64, signature.Length);
        Assert.Matches("^[A-Za-z0-9_-]{86}$", encoded); // the service's signature pattern
        Assert.True(keys.Verify(message, signature));
    }

    [Fact]
    public void Thumbprint_UsesCanonicalBase64Url()
    {
        using var keys = new FakeDeviceKeys();
        var key = keys.Public;

        Assert.Matches("^[A-Za-z0-9_-]{43}$", key.X);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", key.Y);
        Assert.Equal(32, Base64Url.DecodeFromChars(key.X).Length);
        Assert.Equal(key.X, Base64Url.EncodeToString(Base64Url.DecodeFromChars(key.X))); // no spare bits, no padding
        Assert.Matches("^[A-Za-z0-9_-]{43}$", key.Thumbprint);
    }
}
