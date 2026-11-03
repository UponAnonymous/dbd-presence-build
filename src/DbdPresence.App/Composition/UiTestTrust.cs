#if DBDP_UITEST
using System.Buffers.Text;
using DbdPresence.Packs.Signing;

namespace DbdPresence.App.Composition;

/// <summary>
/// The licence key that only the UI-test build trusts (D32). The matching private key lives in the UI test project and nowhere else. This file
/// compiles to nothing unless <c>DBDP_UITEST</c> is defined, which only the UI test project's build of the app does; a test in the architecture
/// suite checks that the Release output carries neither this code nor this key id.
/// </summary>
internal static class UiTestTrust
{
    /// <summary>The key id (contains "uitest"; the release checks look for it).</summary>
    public const string Kid = "licence-uitest-1";

    private const string Spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcM3EqSu1j0pDkuPpCfxgB96mNT16ktcx1JYatNcnkmYucJlrDs4jEkmWjVAWXqLBmdNga9SFBJbovbbfxcUm4w";

    public static IReadOnlyDictionary<string, TrustedKey> Keys { get; } = new Dictionary<string, TrustedKey>
    {
        [Kid] = new TrustedKey(Base64Url.DecodeFromChars(Spki), [SignedPurposes.Licence]),
    };
}
#endif
