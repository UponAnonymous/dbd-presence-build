using System.Security.Cryptography;
using DbdPresence.Core.Online;

namespace DbdPresence.Platform.Windows.Accounts;

/// <summary>
/// The device key (FR-ONL-03, ADR-0011): an ECDSA P-256 key in a named per-user CNG key container (Microsoft Software Key Storage
/// Provider) created with <see cref="CngExportPolicies.None"/>, so the private half cannot be exported or copied to another PC.
/// Limit, stated plainly: malware running as the same Windows user can still ask the key to sign.
/// Windows behaviour is covered by tests that run only on Windows CI.
/// </summary>
public sealed class CngDeviceKeyStore : IDeviceKeyStore
{
    /// <summary>The default key container name.</summary>
    public const string DefaultKeyName = "DBDPresence.DeviceKey.v1";

    private readonly string _name;

    /// <summary>Initializes a new instance of the <see cref="CngDeviceKeyStore"/> class.</summary>
    /// <param name="keyName">The container name; tests use their own so they never touch the real key.</param>
    public CngDeviceKeyStore(string keyName = DefaultKeyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyName);
        _name = keyName;
    }

    /// <inheritdoc />
    public DevicePublicKey GetOrCreatePublicKey()
    {
        try
        {
            using var key = OpenOrCreate();
            using var ecdsa = new ECDsaCng(key);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            return new DevicePublicKey(parameters.Q.X!, parameters.Q.Y!);
        }
        catch (CryptographicException ex)
        {
            throw new DeviceKeyException("The device key could not be created or read.", ex);
        }
    }

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        try
        {
            if (!CngKey.Exists(_name, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                throw new DeviceKeyException("There is no device key.");
            }

            using var key = CngKey.Open(_name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            using var ecdsa = new ECDsaCng(key);
            return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException ex)
        {
            throw new DeviceKeyException("The device key could not sign.", ex);
        }
    }

    /// <inheritdoc />
    public void Delete()
    {
        try
        {
            if (CngKey.Exists(_name, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                using var key = CngKey.Open(_name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                key.Delete();
            }
        }
        catch (CryptographicException ex)
        {
            throw new DeviceKeyException("The device key could not be deleted.", ex);
        }
    }

    private CngKey OpenOrCreate()
    {
        if (CngKey.Exists(_name, CngProvider.MicrosoftSoftwareKeyStorageProvider))
        {
            return CngKey.Open(_name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        }

        return CngKey.Create(
            CngAlgorithm.ECDsaP256,
            _name,
            new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                KeyCreationOptions = CngKeyCreationOptions.None,
            });
    }
}
