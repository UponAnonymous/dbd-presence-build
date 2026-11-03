using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.Online.SignIn;

/// <summary>PKCE (RFC 7636, S256) values and the Discord authorize URL (FR-ONL-01).</summary>
public sealed record PkceChallenge(string Verifier, string Challenge, string State)
{
    /// <summary>Discord's OAuth authorize page; the only page the app opens (ADR-0016).</summary>
    public const string AuthorizeUrl = "https://discord.com/oauth2/authorize";

    /// <summary>The OAuth scope the app requests.</summary>
    public const string Scope = "identify";

    /// <summary>Creates a fresh verifier (43 characters from 32 random bytes), its S256 challenge and a random state.</summary>
    /// <returns>The values.</returns>
    public static PkceChallenge Create()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return new PkceChallenge(verifier, ChallengeFor(verifier), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24)));
    }

    /// <summary>Computes the S256 challenge: base64url (no padding) of SHA-256 over the ASCII verifier.</summary>
    /// <param name="verifier">The verifier.</param>
    /// <returns>The challenge.</returns>
    public static string ChallengeFor(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>The loopback redirect for a listener port (ADR-0011).</summary>
    /// <param name="port">The port.</param>
    /// <returns>The redirect URI.</returns>
    public static string RedirectUri(int port) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/callback");

    /// <summary>Builds the authorize URL; it always carries <c>code_challenge</c> and <c>code_challenge_method=S256</c>.</summary>
    /// <param name="clientId">The Discord application (client) id.</param>
    /// <param name="redirectUri">The loopback redirect.</param>
    /// <returns>The URL.</returns>
    public string BuildAuthorizeUrl(string clientId, string redirectUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(redirectUri);
        return AuthorizeUrl
            + "?client_id=" + Uri.EscapeDataString(clientId)
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&scope=" + Scope
            + "&state=" + Uri.EscapeDataString(State)
            + "&code_challenge=" + Uri.EscapeDataString(Challenge)
            + "&code_challenge_method=S256";
    }
}
