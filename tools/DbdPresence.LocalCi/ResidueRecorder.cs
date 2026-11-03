namespace DbdPresence.LocalCi;

/// <summary>What kind of thing a clean-up could not remove. Together with its subject it says exactly which leftover an entry is about (local CI round 4, red-team F7).</summary>
public static class ResidueKinds
{
    /// <summary>A Linux machine (a WSL distribution) that would not unregister; the subject is its exact name.</summary>
    public const string LinuxMachine = "linux-machine";

    /// <summary>A folder that is not empty or could not be removed; the subject is its exact path.</summary>
    public const string Folder = "folder";

    /// <summary>The marker file of an isolation probe in a Public folder on Windows; the subject is the exact file name (see <see cref="ProbeMarkerNames"/>).</summary>
    public const string MarkerFile = "marker-file";

    /// <summary>A check for leftovers that could not be made; the subject names the check.</summary>
    public const string Check = "check";

    /// <summary>The last use of the Linux machine did not end cleanly (the machine could not be shut down, or the account's programs could not be ended); the subject names the use (round 4, F14).</summary>
    public const string Session = "session";
}

/// <summary>One thing that could not be removed: what kind it is, exactly which one, and the plain words the status and the daily notice show.</summary>
public sealed class ResidueItem
{
    /// <summary>One of <see cref="ResidueKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The exact name or path the entry is about (like every text in the state file it passes the redactor, which changes nothing in a name or path the program made). Entries are found and taken off the list by it, never by a piece of their text.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The entry in plain words.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Keeps the list of what a clean-up could not remove (a Linux machine that would not unregister, a folder that is not empty, the probe's marker file on Windows) in the state file, so the status text shows
/// it and the owner gets a daily notice (local CI round 2, red-team L2). Every entry is about one exact subject; an entry is taken off the list only for that subject and kind, so the removal of one thing
/// can never erase the entry of another that merely has a similar name or path (round 4, F7). The sweep at each start of the loop replaces the entries about Linux machines and folders with what it finds
/// and looks again at the files on Windows instead of forgetting them.
/// </summary>
public sealed class ResidueRecorder
{
    private readonly StateStore _state;
    private readonly IProbeMarker? _marker;
    private readonly ICiLog? _log;

    /// <param name="state">The state file.</param>
    /// <param name="marker">Looks for and removes the probe's marker file on Windows; without it the entries about marker files are kept as they are.</param>
    /// <param name="log">The log.</param>
    public ResidueRecorder(StateStore state, IProbeMarker? marker = null, ICiLog? log = null)
    {
        _state = state;
        _marker = marker;
        _log = log;
    }

    /// <summary>
    /// What the sweep at the start of the loop found of Linux machines and folders (and whether it could do its check at all): it replaces every entry of those kinds, so something that is gone stops being
    /// named. Entries about files on Windows are not the sweep's: they stay until <see cref="RecheckMarkerFiles"/> has looked at the files. Nor are the entries about how a use of the Linux machine ended
    /// (the sweep is itself a use, and the end of it is recorded before the sweep writes its findings): a use that ends cleanly takes the entry off.
    /// </summary>
    public void Sweep(IEnumerable<ResidueItem> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        var items = found.ToList();
        _state.Update(s => s.ResidueItems = [.. s.ResidueItems.Where(i => i.Kind is ResidueKinds.MarkerFile or ResidueKinds.Session), .. items]);
    }

    /// <summary>
    /// Takes off the list the entry about exactly this subject of this kind: it was removed after all, so the status and the daily notice must stop naming it (local CI round 3, L5). Nothing else is touched,
    /// not even an entry whose text contains the subject; a subject that is on no entry changes nothing.
    /// </summary>
    public void Resolve(string kind, string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        _state.Update(s => s.ResidueItems = [.. s.ResidueItems.Where(i => !Is(i, kind, subject))]);
    }

    /// <summary>Adds one thing that could not be removed. The state file keeps one entry for each subject of each kind: an entry added again for the same one replaces the old one (<see cref="StateStore.Update"/>).</summary>
    public void Add(string kind, string subject, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(text);
        _state.Update(s => s.ResidueItems.Add(new ResidueItem { Kind = kind, Subject = subject, Text = text }));
    }

    /// <summary>
    /// Looks again, at the start of the loop, for every marker file of an isolation probe that was left on Windows: a file that is gone (the owner deleted it, or it can be removed now) is taken off the list,
    /// one that is still there stays on it. A file that cannot be looked at stays on the list too: it is not "gone" just because it could not be seen.
    /// </summary>
    public void RecheckMarkerFiles()
    {
        if (_marker is null)
        {
            return;
        }

        var names = _state.Load().ResidueItems.Where(i => string.Equals(i.Kind, ResidueKinds.MarkerFile, StringComparison.Ordinal)).Select(i => i.Subject).ToList();
        foreach (var name in names)
        {
            ProbeMarkerFinding finding;
            try
            {
                finding = _marker.FindAndRemove(name);
            }
#pragma warning disable CA1031 // The sweep never stops the loop; a marker file that cannot be looked at stays on the list and the reason is logged.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                _log?.Warn($"Could not look again for the isolation probe's marker file {name} on Windows: {ex.Message}");
                continue;
            }

            if (finding == ProbeMarkerFinding.FoundButNotRemoved)
            {
                continue;
            }

            if (finding == ProbeMarkerFinding.FoundAndRemoved)
            {
                _log?.Info($"Removed the marker file {name} an earlier isolation probe left on Windows.");
            }

            Resolve(ResidueKinds.MarkerFile, name);
        }
    }

    private static bool Is(ResidueItem item, string kind, string subject) =>
        string.Equals(item.Kind, kind, StringComparison.Ordinal) && string.Equals(item.Subject, subject, StringComparison.OrdinalIgnoreCase);
}
