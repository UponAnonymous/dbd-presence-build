using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

/// <summary>The account's <c>.wslconfig</c>, in the account's own profile folder (<c>%UserProfile%\.wslconfig</c> of that account; local CI round 2, red-team F2, F8). The owner's own file is never touched.</summary>
/// <remarks>
/// WSL reads the file from the profile of the user the virtual machine is started for, when it starts the machine (microsoft/WSL source, read 2026-10-30). The account's profile is a folder that
/// account's own programs can write to, so the elevated program reads it through a handle that follows no link, refuses a link or a very large file, writes by renaming a new file with an unguessable name over
/// it, and holds the file open for reading with nobody else allowed to write, rename or delete it while a session lasts (same confused-deputy class as the clean-up of a job's folder).
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WslSettingsFile : IWslSettingsFile
{
    /// <summary>The program's own file is a few lines; anything larger is not read by an elevated program.</summary>
    private const long MaximumBytes = 64 * 1024;

    private readonly WindowsWslAccount _account;

    public WslSettingsFile(WindowsWslAccount account)
    {
        _account = account;
    }

    public string Location => Path.Combine(_account.ProfilePath(), ".wslconfig");

    public byte[]? Read()
    {
        using var stream = VerifiedUserFile.OpenRead(Location);
        return stream is null ? null : ReadAll(stream);
    }

    public void Write(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        SafeFileReplace.Write(Location, content);
    }

    public IDisposable OpenHeld(out byte[] content)
    {
        var stream = VerifiedUserFile.OpenRead(Location) ?? throw new IOException($"{Location} is not there.");
        try
        {
            content = ReadAll(stream);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads the file to its end but never more than <see cref="MaximumBytes"/>: the size is not trusted, the bytes that come are counted.</summary>
    private byte[] ReadAll(FileStream stream)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (copy.Length + read > MaximumBytes)
            {
                throw new IOException($"{Location} is larger than {MaximumBytes / 1024} KB, which this program's settings file never is; it was not read.");
            }

            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }
}
