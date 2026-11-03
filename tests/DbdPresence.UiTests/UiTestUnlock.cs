using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbdPresence.Platform.Windows.Accounts;

namespace DbdPresence.UiTests;

/// <summary>
/// Starts the UI-test build of the app unlocked through the normal path (D32). The fixture writes a licence signed with a throw-away test key into the
/// data folder's protected storage, bound to the test device key. The UI-test build (and only that build) trusts the matching public key
/// (<c>UiTestTrust</c> in the app); the app then verifies the licence with its real verifier. There is no switch in the app that skips verification.
/// </summary>
internal static class UiTestUnlock
{
    /// <summary>The key id the UI-test build trusts (must equal <c>UiTestTrust.Kid</c>).</summary>
    public const string Kid = "licence-uitest-1";

    private const string UserId = "uitest-user";

    // PKCS#8 private key of the throw-away test key. Its public half is trusted only by the UI-test build of the app; it unlocks nothing else.
    private const string PrivateKey = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgmA8lyIQy3Dn8TYBhsmsQv32R2pkFAvlOnS2ytwTCbyWhRANCAARwzcSpK7WPSkOS4+kJ/GAH3qY1PXqS1zHUlhq01yeSZi5wmWsOziMSSZaNUBZeosGZ02Br1IUElui9tt/FxSbj";

    /// <summary>Writes the profile and the signed licence for <paramref name="dataRoot"/>, as the app's own secret store would.</summary>
    public static void Write(string dataRoot)
    {
        var device = new CngDeviceKeyStore(CngDeviceKeyStore.DefaultKeyName + ".test").GetOrCreatePublicKey();
        var secrets = new DpapiSecretStore(Path.Combine(dataRoot, "data", "online", "secrets"));
        secrets.Write("profile", $$"""{"userId":"{{UserId}}","displayName":"UI Test","hasAccess":true,"revoked":false,"lastRefreshUtc":null}""");
        secrets.Write("licence", SignLicence(device.Thumbprint));
    }

    private static string SignLicence(string thumbprint)
    {
        var issued = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = issued.AddDays(7);
        var payload = $$"""{"schemaVersion":1,"sub":"{{UserId}}","cnf":"{{thumbprint}}","status":"active","features":["online"],"iat":"{{Iso(issued)}}","nbf":"{{Iso(issued)}}","exp":"{{Iso(expires)}}","serverTime":"{{Iso(issued)}}"}""";
        // A licence's version is its issue time in whole seconds (threat-model review TM-01); the verifier refuses any other value.
        var envelope = $$"""{"kid":"{{Kid}}","purpose":"licence","channel":"stable","version":{{issued.ToUnixTimeSeconds()}},"issuedAt":"{{Iso(issued)}}","expiresAt":"{{Iso(expires)}}","payload":{{payload}}}""";
        var bytes = Encoding.UTF8.GetBytes(envelope);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKey), out _);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $$"""{"kid":"{{Kid}}","signed":"{{Base64Url.EncodeToString(bytes)}}","sig":"{{Base64Url.EncodeToString(signature)}}"}""";
    }

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
