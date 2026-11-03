using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The Public folders the elevated program looks in (the probe's marker file, the audit of writable places). The only place the program reads <c>%PUBLIC%</c>: the answer goes through
/// <see cref="LocalPublicFolder"/>, which refuses a network share, a device path and anything that is not an ordinary folder on a local fixed disk (local CI round 4, red-team F13). PENDING-ENV: not run on a
/// Windows PC yet.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PublicFolders
{
    /// <summary>The usual Public folder, and the one <c>%PUBLIC%</c> names when it is an ordinary folder on a local fixed disk.</summary>
    public static IReadOnlyList<string> Folders() => LocalPublicFolder.Folders(Environment.GetEnvironmentVariable("PUBLIC"), IsOnFixedDrive);

    /// <summary>True when <paramref name="letter"/> is a drive that is a local fixed disk.</summary>
    public static bool IsOnFixedDrive(char letter)
    {
        try
        {
            return new DriveInfo(letter.ToString()).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            // hygiene: allow-swallow the letter is not a drive this PC has, which is the answer "not a fixed disk"
            return false;
        }
    }
}
