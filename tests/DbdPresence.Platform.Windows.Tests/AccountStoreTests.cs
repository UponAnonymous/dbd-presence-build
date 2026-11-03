// Covers: FR-ONL-03 (non-exportable per-user CNG device key, ES256 signatures the service accepts), ADR-0011 (DPAPI-protected tokens),
// ADR-0016 (browser launch only for Discord's authorize URL). Runs only on Windows CI; the product code is PENDING-ENV until this job has run.
using System.Security.Cryptography;
using DbdPresence.Core.Online;
using DbdPresence.Platform.Windows.Accounts;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class AccountStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbdp-secrets", Guid.NewGuid().ToString("N"));
    private readonly string _keyName = "DBDPresence.Tests." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        new CngDeviceKeyStore(_keyName).Delete();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void DpapiStore_RoundTrips_ProtectsOnDisk_AndReplacesAtomically()
    {
        var store = new DpapiSecretStore(_dir);
        Assert.Null(store.Read("refresh-token"));

        store.Write("refresh-token", "rt_secret-value-1");
        store.Write("refresh-token", "rt_secret-value-2");

        Assert.Equal("rt_secret-value-2", store.Read("refresh-token"));
        var raw = File.ReadAllBytes(Path.Combine(_dir, "refresh-token.bin"));
        Assert.DoesNotContain("secret-value", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        store.Delete("refresh-token");
        Assert.Null(store.Read("refresh-token"));
        store.Delete("refresh-token"); // deleting nothing succeeds
    }

    [Fact]
    public void DpapiStore_CorruptValue_ThrowsATypedError_NeverReturnsGarbage()
    {
        var store = new DpapiSecretStore(_dir);
        store.Write("licence", "doc");
        File.WriteAllBytes(Path.Combine(_dir, "licence.bin"), [1, 2, 3, 4]);

        Assert.Throws<SecretStoreException>(() => store.Read("licence"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("UPPER")]
    [InlineData("")]
    public void DpapiStore_RejectsNamesThatCouldEscapeTheFolder(string name)
    {
        var store = new DpapiSecretStore(_dir);

        Assert.Throws<ArgumentException>(() => store.Write(name, "x"));
        Assert.Throws<ArgumentException>(() => store.Read(name));
    }

    [Fact]
    public void CngKey_IsCreatedOnce_SignsES256_AndIsNotExportable()
    {
        var store = new CngDeviceKeyStore(_keyName);

        var first = store.GetOrCreatePublicKey();
        var second = new CngDeviceKeyStore(_keyName).GetOrCreatePublicKey();
        Assert.Equal(first.Thumbprint, second.Thumbprint);

        var data = "dbdp-refresh-proof-v1\nhash\n1\nnonce"u8.ToArray();
        var signature = store.Sign(data);
        Assert.Equal(64, signature.Length);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = System.Buffers.Text.Base64Url.DecodeFromChars(first.X), Y = System.Buffers.Text.Base64Url.DecodeFromChars(first.Y) },
        });
        Assert.True(verifier.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        using var key = CngKey.Open(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        Assert.Equal(CngExportPolicies.None, key.ExportPolicy);
        Assert.ThrowsAny<CryptographicException>(() => key.Export(CngKeyBlobFormat.EccPrivateBlob));
    }

    [Fact]
    public void CngKey_Delete_RemovesIt_AndSigningThenFailsTyped()
    {
        var store = new CngDeviceKeyStore(_keyName);
        store.GetOrCreatePublicKey();

        store.Delete();
        store.Delete();

        Assert.Throws<DeviceKeyException>(() => store.Sign("x"u8));
    }

    [Theory]
    [InlineData("https://evil.example.com/oauth2/authorize?x=1")]
    [InlineData("http://discord.com/oauth2/authorize?x=1")]
    [InlineData("https://discord.com.evil.example/oauth2/authorize?x=1")]
    [InlineData("https://discord.com/other?x=1")]
    [InlineData("https://user@discord.com/oauth2/authorize?x=1")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("calc.exe")]
    public void BrowserLauncher_RefusesAnythingButDiscordsAuthorizePage_WithoutStartingAnything(string url)
    {
        Assert.False(new DiscordBrowserLauncher().TryOpen(url));
    }
}
