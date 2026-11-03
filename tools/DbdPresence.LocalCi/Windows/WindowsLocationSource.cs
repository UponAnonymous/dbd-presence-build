using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The places on this PC worth asking whether the dedicated account may write to them (local CI round 3, red-team H1; round 4, F1): the roots of the fixed and removable drives (and of network drives) and their
/// top-level folders, the Public folder, the folders directly in Program Files (both) and in ProgramData, the Windows temp folder, and the folders of the system PATH. It only lists names;
/// <see cref="AccountWritableLocations"/> asks what the account may do in them. Everything that could not be listed is returned as a gap, never dropped: a drive that is not ready, a folder that cannot be
/// opened, a PATH that cannot be read. PENDING-ENV: not run on a Windows PC yet (the Windows CI runs it over its own C: drive).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsLocationSource : ILocationSource
{
    /// <summary>The most folders of one folder that are listed; a folder with more is listed up to this number and the rest is named as a gap.</summary>
    internal const int MostPerFolder = 400;

    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public LocationList List()
    {
        var all = new List<LocationCandidate>();
        var notListed = new List<string>();
        AddDrives(all, notListed);

        foreach (var publicFolder in PublicFolders.Folders())
        {
            all.Add(new LocationCandidate(publicFolder, LocationKinds.PublicFolder));
        }

        all.Add(new LocationCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), LocationKinds.WindowsTemp));
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            all.AddRange(Children(programFiles, LocationKinds.ProgramFiles, notListed));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (programData.Length > 0)
        {
            all.AddRange(Children(programData, LocationKinds.ProgramData, notListed));
        }

        foreach (var folder in MachinePathFolders(notListed))
        {
            all.Add(new LocationCandidate(folder, LocationKinds.SystemPath));
        }

        return new LocationList([.. all.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)], notListed);
    }

    private static void AddDrives(List<LocationCandidate> all, List<string> notListed)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException ex)
        {
            notListed.Add($"the list of drives could not be read: {ex.Message}");
            return;
        }

        foreach (var drive in drives)
        {
            // A CD-ROM cannot be written to; "no root directory" and "unknown" are not drives to look at.
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.Ram))
            {
                continue;
            }

            var root = drive.Name;
            var ready = false;
            try
            {
                ready = drive.IsReady;
            }
            catch (IOException ex)
            {
                notListed.Add($"{root} ({Describe(drive.DriveType)}) could not be asked whether it is ready: {ex.Message}");
                continue;
            }

            if (!ready)
            {
                // An empty card reader or a network drive that is not connected has nothing anybody could write to; a fixed disk that is not ready (a locked volume) is a gap.
                if (drive.DriveType is DriveType.Fixed or DriveType.Ram)
                {
                    notListed.Add($"{root} ({Describe(drive.DriveType)}) is not ready, so it was not looked at");
                }

                continue;
            }

            AddReadyDrive(drive, all, notListed);
        }
    }

    private static void AddReadyDrive(DriveInfo drive, List<LocationCandidate> all, List<string> notListed)
    {
        var root = drive.Name;
        var kind = drive.DriveType switch
        {
            DriveType.Removable => LocationKinds.RemovableDriveRoot,
            DriveType.Network => LocationKinds.NetworkDriveRoot,
            _ => LocationKinds.DriveRoot,
        };
        string? withoutPermissions = null;
        try
        {
            var format = drive.DriveFormat;
            if (format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "exFAT", StringComparison.OrdinalIgnoreCase))
            {
                withoutPermissions = format;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notListed.Add($"{root} ({Describe(drive.DriveType)}): the file system could not be read: {ex.Message}");
        }

        all.Add(new LocationCandidate(root, kind, withoutPermissions));

        // A network drive is only asked about at its root (its folders are another computer's); a drive without permissions is the same everywhere.
        if (drive.DriveType != DriveType.Network && withoutPermissions is null)
        {
            all.AddRange(Children(root, LocationKinds.TopLevelFolder, notListed));
        }
    }

    private static string Describe(DriveType type) => type switch
    {
        DriveType.Removable => "removable drive",
        DriveType.Network => "network drive",
        _ => "fixed drive",
    };

    private static IEnumerable<LocationCandidate> Children(string folder, string kind, List<string> notListed)
    {
        try
        {
            var names = Directory.EnumerateDirectories(folder).Take(MostPerFolder + 1).ToList();
            if (names.Count > MostPerFolder)
            {
                notListed.Add($"{folder} has more than {MostPerFolder} folders; only the first {MostPerFolder} were looked at");
            }

            return [.. names.Take(MostPerFolder).Select(f => new LocationCandidate(f, kind))];
        }
        catch (UnauthorizedAccessException ex)
        {
            notListed.Add($"{folder} could not be listed: {ex.Message}");
            return [];
        }
        catch (IOException ex)
        {
            notListed.Add($"{folder} could not be listed: {ex.Message}");
            return [];
        }
    }

    private static List<string> MachinePathFolders(List<string> notListed)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EnvironmentKey);
            var raw = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrEmpty(raw))
            {
                notListed.Add("the machine's search path (PATH) could not be read, so its folders were not looked at");
                return [];
            }

            return [.. raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Environment.ExpandEnvironmentVariables).Select(p => p.Trim('"')).Where(p => p.Length > 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && Path.IsPathFullyQualified(p) && Directory.Exists(p)).Take(MostPerFolder)];
        }
        catch (UnauthorizedAccessException ex)
        {
            notListed.Add($"the machine's search path (PATH) could not be read: {ex.Message}");
            return [];
        }
        catch (System.Security.SecurityException ex)
        {
            notListed.Add($"the machine's search path (PATH) could not be read: {ex.Message}");
            return [];
        }
    }
}
