using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// Keeps one secret in the data folder (the GitHub token in <c>token.bin</c>, or the password of the dedicated Windows account in <c>wsl-account.bin</c>), closed to everyone but Administrators and
/// the system, protected with Windows data protection in machine scope (<c>CRYPTPROTECT_LOCAL_MACHINE</c>) and with a random per-install value (the <c>.entropy</c> file next to it, also in the data
/// folder) as extra entropy (security review 2026-10-30, H3). The folder's permissions are what keep other users away: machine scope lets any user of this computer decrypt a copy of the file, which
/// is why both files are checked again before every use and created with closed permissions in the first place. PENDING-ENV: not run on a Windows PC yet.
/// </summary>
/// <remarks>
/// CryptProtectData: "CRYPTPROTECT_LOCAL_MACHINE ... associates the data encrypted with the current computer instead of with an individual user. Any user on the computer on
/// which CryptProtectData is called can use CryptUnprotectData to decrypt the data"; "pOptionalEntropy ... The DATA_BLOB structure used in the encryption phase must also be used in the
/// decryption phase" (learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata, read 2026-10-29). Flag values from dpapi.h: UI_FORBIDDEN 0x1, LOCAL_MACHINE 0x4.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore : ISecretStore
{
    private const uint UiForbidden = 0x1;
    private const uint LocalMachine = 0x4;
    private const int EntropyBytes = 32;

    private readonly string _file;
    private readonly string _entropyFile;
    private readonly string _description;
    private readonly string _what;
    private readonly IProtectedStorage _storage;
    private readonly ICiLog _log;

    /// <param name="file">The protected secret.</param>
    /// <param name="entropyFile">The random value that goes with it.</param>
    /// <param name="description">The label stored inside the protected data.</param>
    /// <param name="what">How the secret is named in messages, for example "The saved token".</param>
    /// <param name="storage">Checks the closed folder before the files are used.</param>
    /// <param name="log">The log.</param>
    public DpapiSecretStore(string file, string entropyFile, string description, string what, IProtectedStorage storage, ICiLog log)
    {
        _file = file;
        _entropyFile = entropyFile;
        _description = description;
        _what = what;
        _storage = storage;
        _log = log;
    }

    public bool Exists() => File.Exists(_file) && File.Exists(_entropyFile);

    public SecretText? Load()
    {
        if (!Exists())
        {
            return null;
        }

        foreach (var file in new[] { _file, _entropyFile })
        {
            var problems = _storage.CheckInsideDataFolder(file);
            if (problems.Count > 0)
            {
                _log.Failure($"{_what} is not used because {problems[0]} Run setup again to save a new one.");
                return null;
            }
        }

        var plain = Array.Empty<byte>();
        try
        {
            plain = Unprotect(File.ReadAllBytes(_file), File.ReadAllBytes(_entropyFile));
            return new SecretText(Encoding.UTF8.GetString(plain));
        }
        catch (Win32Exception ex)
        {
            _log.Failure($"{_what} cannot be read (error {ex.NativeErrorCode}). Run setup again to save a new one.");
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(SecretText secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var plain = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            if (!File.Exists(_entropyFile))
            {
                ProtectedCreation.WriteNewFile(_entropyFile, RandomNumberGenerator.GetBytes(EntropyBytes));
            }

            var sealedBytes = Protect(plain, File.ReadAllBytes(_entropyFile));
            var temporary = _file + ".tmp";
            SafeTree.Delete(temporary);
            ProtectedCreation.WriteNewFile(temporary, sealedBytes);
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        SafeTree.Delete(_file);
        SafeTree.Delete(_entropyFile);
    }

    private byte[] Protect(byte[] plain, byte[] entropy) => Transform(plain, entropy, protect: true);

    private byte[] Unprotect(byte[] sealedBytes, byte[] entropy) => Transform(sealedBytes, entropy, protect: false);

    private byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var data = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var extra = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? NativeMethods.CryptProtectData(ref data, _description, ref extra, nint.Zero, nint.Zero, UiForbidden | LocalMachine, out var output)
                : NativeMethods.CryptUnprotectData(ref data, nint.Zero, ref extra, nint.Zero, nint.Zero, UiForbidden, out output);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return Take(output);
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    private static byte[] Take(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.Size];
            Marshal.Copy(blob.Data, bytes, 0, blob.Size);
            return bytes;
        }
        finally
        {
            _ = NativeMethods.LocalFree(blob.Data);
        }
    }
}
