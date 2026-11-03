using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>The exact shape of the marker file the isolation probe tries to write into the Windows Public folder (local CI round 3, red-team H1).</summary>
public static partial class ProbeMarkerNames
{
    public const string Prefix = "dbd-ci-probe-";

    /// <summary>A new, unguessable marker file name: <c>dbd-ci-probe-</c>, twelve random hex digits and <c>.txt</c>. A job cannot know it before the probe runs.</summary>
    public static string New() => Prefix + RandomNumberGenerator.GetHexString(12, lowercase: true) + ".txt";

    /// <summary>True for exactly a name <see cref="New"/> makes. Only such a name is ever put into the probe's script or looked for and deleted on Windows.</summary>
    public static bool IsMarker(string? name) => !string.IsNullOrEmpty(name) && MarkerName().IsMatch(name);

    [GeneratedRegex(@"^dbd-ci-probe-[0-9a-f]{12}\.txt\z", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerName();
}

/// <summary>What was found of the probe's marker file on the Windows side.</summary>
public enum ProbeMarkerFinding
{
    /// <summary>No such file: the write did not reach Windows (or was not made).</summary>
    NotThere,

    /// <summary>A plain file with the marker's name was there; it has been removed.</summary>
    FoundAndRemoved,

    /// <summary>Something with the marker's name was there and could not be removed (or was not a plain file and was left alone).</summary>
    FoundButNotRemoved,
}

/// <summary>The marker file of the isolation probe on the Windows side: made up, looked for after the probe and removed by its exact path.</summary>
public interface IProbeMarker
{
    /// <summary>A new marker file name (see <see cref="ProbeMarkerNames.New"/>).</summary>
    string NewName();

    /// <summary>
    /// Looks for the marker file in the Windows Public folder and removes it. Only a plain file with exactly this name in exactly that folder is removed; a folder, a link or anything else with that name is left
    /// alone and reported as not removed.
    /// </summary>
    ProbeMarkerFinding FindAndRemove(string name);
}

/// <summary>
/// Which text from the user's environment may name the Public folder (local CI round 4, red-team F13). The variable <c>%PUBLIC%</c> belongs to the owner's account, and any program the owner runs can change it:
/// the elevated program looks for (and removes) the probe's marker file in the folder it names and lists it in the audit, so it must be an ordinary folder on a local fixed disk, never a network share
/// (<c>\\server\share</c>), a device path (<c>\\?\</c>), a relative path or one with <c>..</c> in it.
/// </summary>
public static partial class LocalPublicFolder
{
    /// <summary>Where Windows puts the Public folder; the probe's script always writes there.</summary>
    public const string Usual = @"C:\Users\Public";

    /// <summary>True only for a full path that starts with a drive letter, has no <c>.</c> or <c>..</c> part, no empty part and no character Windows refuses in a name, on a drive that <paramref name="driveIsFixed"/> says is a local fixed disk.</summary>
    public static bool IsAcceptable(string? path, Func<char, bool> driveIsFixed)
    {
        ArgumentNullException.ThrowIfNull(driveIsFixed);
        if (string.IsNullOrEmpty(path) || path.Length > 240 || !DriveLetterPath().IsMatch(path))
        {
            return false;
        }

        return path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).All(part => part is not ("." or "..") && !part.EndsWith(' ') && !part.EndsWith('.')) && driveIsFixed(path[0]);
    }

    /// <summary>The folders to look in: the usual one, and the one the environment names when it is acceptable and another one.</summary>
    public static IReadOnlyList<string> Folders(string? fromEnvironment, Func<char, bool> driveIsFixed) =>
        IsAcceptable(fromEnvironment, driveIsFixed) && !string.Equals(fromEnvironment!.TrimEnd('\\'), Usual, StringComparison.OrdinalIgnoreCase) ? [Usual, fromEnvironment!.TrimEnd('\\')] : [Usual];

    // One part is one or more allowed characters; parts are separated by exactly one backslash. Each backslash is spent in exactly one place, so the match is linear (no backtracking blow-up on a long bad name).
    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^\\/:*?""<>|\x00-\x1F]+(?:\\[^\\/:*?""<>|\x00-\x1F]+)*\\?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DriveLetterPath();
}

/// <summary>
/// The marker file in the folders where the probe's script writes it (<c>C:\Users\Public</c>, and the Public folder the system names when that is another one). Removal is by the exact path
/// <c>folder\name</c> with a name that has the shape of a marker, and only for a plain file: a link or a folder with that name is never followed or deleted.
/// </summary>
public sealed class ProbeMarkerFile : IProbeMarker
{
    private readonly IReadOnlyList<string> _folders;
    private readonly ICiLog _log;

    /// <param name="folders">The folders to look in (full paths of the Public folder).</param>
    /// <param name="log">The log.</param>
    public ProbeMarkerFile(IEnumerable<string> folders, ICiLog log)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = [.. folders.Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f)).Distinct(StringComparer.OrdinalIgnoreCase)];
        _log = log;
    }

    /// <summary>The folders the marker file is looked for in: the full paths that were given, once each. A relative path or an empty text is not one of them.</summary>
    public IReadOnlyList<string> Folders => _folders;

    public string NewName() => ProbeMarkerNames.New();

    public ProbeMarkerFinding FindAndRemove(string name)
    {
        if (!ProbeMarkerNames.IsMarker(name))
        {
            throw new ArgumentException("That is not the name of a probe marker file, so nothing is looked for or removed.", nameof(name));
        }

        var result = ProbeMarkerFinding.NotThere;
        foreach (var folder in _folders)
        {
            var path = Path.Combine(folder, name);
            var info = new FileInfo(path);
            var isLink = info.LinkTarget is not null;
            var isDirectory = Directory.Exists(path);
            var isFile = info.Exists;
            if (!isLink && !isDirectory && !isFile)
            {
                continue;
            }

            if (isLink || isDirectory || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                _log.Warn($"Something that is not a plain file has the probe's marker name {path}; it was left alone.");
                result = ProbeMarkerFinding.FoundButNotRemoved;
                continue;
            }

            try
            {
                File.Delete(path);
                if (result == ProbeMarkerFinding.NotThere)
                {
                    result = ProbeMarkerFinding.FoundAndRemoved;
                }
            }
            catch (IOException ex)
            {
                _log.Failure($"Could not remove the probe's marker file {path}: {ex.Message}");
                result = ProbeMarkerFinding.FoundButNotRemoved;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Failure($"Could not remove the probe's marker file {path}: {ex.Message}");
                result = ProbeMarkerFinding.FoundButNotRemoved;
            }
        }

        return result;
    }
}

/// <summary>
/// What the audit of the places the dedicated account may write to found, and, just as important, what it could not look at (local CI round 4, red-team F1). A place that could not be checked is never
/// "a place the account may not write to": it is counted and named here, and the report, the status and the owner's decision all say that the audit is incomplete.
/// </summary>
/// <param name="Places">The places the account may write to, in plain words, the most serious first (at most 25 and a last line that says how many more).</param>
/// <param name="PlacesLooked">How many places had their permissions read.</param>
/// <param name="GapCount">How many places could not be checked (a folder that could not be listed counts as one).</param>
/// <param name="Gaps">The first of them by name, each with the reason, and a last line that says how many more when there are more than <see cref="WritableAudit.MostGapsNamed"/>; empty exactly when <paramref name="GapCount"/> is 0.</param>
public sealed record WritableAudit(IReadOnlyList<string> Places, int PlacesLooked, int GapCount, IReadOnlyList<string> Gaps)
{
    /// <summary>The most gaps that are named one by one; the rest are only counted.</summary>
    public const int MostGapsNamed = 25;

    /// <summary>An audit with nothing in it: nothing found, nothing missed (used where the audit does not matter).</summary>
    public static WritableAudit Empty { get; } = new([], 0, 0, []);

    /// <summary>True only when every place that was meant to be looked at was looked at.</summary>
    public bool Complete => GapCount == 0;

    /// <summary>An audit that could not be made at all.</summary>
    public static WritableAudit Failed(string reason) => new([], 0, 1, [$"the audit could not be made at all: {reason}"]);

    /// <summary>
    /// The audit of a probe the program repeated by itself in the loop (round 4, F5): the loop never reads Windows permissions or switches the backup right on, so there is no audit. It counts as incomplete,
    /// so such a report is never taken for a clean one; the program keeps the audit of the result the owner looked at.
    /// </summary>
    public static WritableAudit NotMade { get; } = new([], 0, 1, ["not looked at: the program repeated the attempts by itself and does not read Windows permissions in the background"]);
}

/// <summary>The places on this PC the dedicated account may write to, in plain words (what a job that gets out of its Linux machine could change).</summary>
public interface IWritableLocations
{
    Task<WritableAudit> FindAsync(CancellationToken cancellationToken);
}

/// <summary>The kinds of place the audit looks at. They are words the owner reads, so they are plain.</summary>
public static class LocationKinds
{
    public const string DriveRoot = "drive root";
    public const string TopLevelFolder = "top-level folder";
    public const string PublicFolder = "public folder";
    public const string ProgramFiles = "folder in Program Files";
    public const string ProgramData = "folder in ProgramData";
    public const string WindowsTemp = "Windows temp folder";
    public const string SystemPath = "folder in the system PATH";
    public const string RemovableDriveRoot = "removable drive";
    public const string NetworkDriveRoot = "network drive";
}

/// <summary>One folder worth asking about, and what kind of place it is.</summary>
/// <param name="Path">The full path.</param>
/// <param name="Kind">What the folder is (see <see cref="LocationKinds"/>).</param>
/// <param name="NoPermissionsOn">The name of the file system when it has no permissions at all (FAT, exFAT): its permissions cannot be read because there are none, and any program may write there.</param>
public sealed record LocationCandidate(string Path, string Kind, string? NoPermissionsOn = null);

/// <summary>The folders worth asking about, and the places the list itself could not be made for.</summary>
/// <param name="Candidates">The folders to ask about.</param>
/// <param name="NotListed">What could not be listed (a drive that is not ready, a folder that cannot be opened, the PATH that cannot be read), each with the reason; these are gaps of the audit.</param>
public sealed record LocationList(IReadOnlyList<LocationCandidate> Candidates, IReadOnlyList<string> NotListed);

/// <summary>
/// Where to look for places the account may write to: the Windows program lists the drive roots, their top-level folders, the Public folder, the folders directly in Program Files and in ProgramData,
/// the Windows temp folder and the folders of the system PATH. It looks one level down and no further; the report says so.
/// </summary>
public interface ILocationSource
{
    LocationList List();
}
