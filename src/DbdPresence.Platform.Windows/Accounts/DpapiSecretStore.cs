using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DbdPresence.Core.Online;

namespace DbdPresence.Platform.Windows.Accounts;

/// <summary>
/// <see cref="ISecretStore"/> over DPAPI (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>) with fixed
/// application entropy (ADR-0011). Each secret is one file, written to a temporary file and moved over the old one. Values are never
/// logged and never appear in exception messages. Windows behaviour is covered by tests that run only on Windows CI.
/// </summary>
public sealed partial class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("DBD Presence secrets v1");
    private readonly string _directory;

    /// <summary>Initializes a new instance of the <see cref="DpapiSecretStore"/> class.</summary>
    /// <param name="directory">The folder for the protected files; created on first write.</param>
    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    /// <inheritdoc />
    public string? Read(string name)
    {
        var path = PathFor(name);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), s_entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException("A protected value could not be decrypted.", ex);
        }
        catch (IOException ex)
        {
            throw new SecretStoreException("A protected value could not be read.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SecretStoreException("A protected value could not be read.", ex);
        }
    }

    /// <inheritdoc />
    public void Write(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathFor(name);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(temp, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), s_entropy, DataProtectionScope.CurrentUser));
            File.Move(temp, path, overwrite: true);
        }
        catch (CryptographicException ex)
        {
            throw new SecretStoreException("A value could not be protected.", ex);
        }
        catch (IOException ex)
        {
            throw new SecretStoreException("A protected value could not be written.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SecretStoreException("A protected value could not be written.", ex);
        }
    }

    /// <inheritdoc />
    public void Delete(string name)
    {
        var path = PathFor(name);
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            throw new SecretStoreException("A protected value could not be removed.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SecretStoreException("A protected value could not be removed.", ex);
        }
    }

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex NamePattern();

    private string PathFor(string name)
    {
        if (name is null || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException("Secret names are lower-case letters, digits and hyphens.", nameof(name));
        }

        return Path.Combine(_directory, name + ".bin");
    }
}
