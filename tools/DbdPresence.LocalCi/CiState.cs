using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbdPresence.LocalCi;

public sealed class JobRecord
{
    public DateTimeOffset At { get; set; }

    public string Os { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;
}

public sealed class SelfTestRecord
{
    public DateTimeOffset At { get; set; }

    public string Outcome { get; set; } = string.Empty;
}

/// <summary>One attempt of the isolation probe as kept in the state file.</summary>
public sealed class ProbeAttemptRecord
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Blocked, Possible or NotTried.</summary>
    public string Verdict { get; set; } = string.Empty;

    public string Evidence { get; set; } = string.Empty;
}

/// <summary>What the isolation probe found the last time it ran, and whether the owner accepted it (local CI round 3, red-team H1). Never a secret.</summary>
public sealed class ProbeRecord
{
    /// <summary>
    /// Names this result and no other. The owner's "yes" is given to one result; it is written onto the saved result only while that result is still the last one saved (round 4, F8), so a result that
    /// another window saved in the meantime can never be accepted by an answer given for the one before.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When the probe ran.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>AllBlocked, AuditIncomplete (every attempt stopped, but some places could not be checked), SomePossible or Incomplete.</summary>
    public string Outcome { get; set; } = string.Empty;

    public List<ProbeAttemptRecord> Attempts { get; set; } = [];

    /// <summary>The places on this PC the dedicated account may write to, in plain words (as found when the probe ran).</summary>
    public List<string> WritableLocations { get; set; } = [];

    /// <summary>How many places the audit of writable places read the permissions of (round 4, F1).</summary>
    public int AuditPlacesLooked { get; set; }

    /// <summary>How many places the audit could not check. When this is not 0 the list above is incomplete and it is UNKNOWN whether the account may write to those places.</summary>
    public int AuditGapCount { get; set; }

    /// <summary>The first of those places by name, each with the reason (and a last line that says how many more).</summary>
    public List<string> AuditGaps { get; set; } = [];

    /// <summary>Names the Linux machine settings and the base image recipe the probe was run for; a result for another setup says nothing about this one.</summary>
    public string Recipe { get; set; } = string.Empty;

    /// <summary>The text of <c>wsl --version</c> when the probe ran, if it was known.</summary>
    public string? WslVersion { get; set; }

    /// <summary>True when the owner typed "yes" to what the probe found.</summary>
    public bool Accepted { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>How many times the program repeated the attempts by itself after its own WSL update and got a result no worse than the one the owner accepted (round 4, F5). 0 for a result the owner looked at.</summary>
    public int Renewals { get; set; }

    /// <summary>When the program last did that.</summary>
    public DateTimeOffset? LastRenewedAt { get; set; }
}

/// <summary>What the program remembers between runs (never a secret).</summary>
public sealed class CiState
{
    /// <summary>The last isolation probe (local CI round 3, red-team H1); null when none has run.</summary>
    public ProbeRecord? Probe { get; set; }

    public DateTimeOffset? LastRunnerCheck { get; set; }

    public DateTimeOffset? LastRunnerAttempt { get; set; }

    public DateTimeOffset? LastBaseImageAttempt { get; set; }

    public DateTimeOffset? LastTokenCheck { get; set; }

    public DateTimeOffset? LastBaseImageBuild { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>When the owner last saw a notice about the token (at most one a day).</summary>
    public DateTimeOffset? LastTokenNotice { get; set; }

    /// <summary>The recipe (pinned image and setup scripts) the current base image was built from; a different recipe means the image is out of date.</summary>
    public string? BaseImageRecipe { get; set; }

    /// <summary>When the owner last saw a notice about leftovers that could not be removed (at most one a day).</summary>
    public DateTimeOffset? LastResidueNotice { get; set; }

    /// <summary>What GitHub last said about the repository: true = private, false = not private, null = never asked, or its answer did not say (local CI round 3, red-team M5).</summary>
    public bool? RepositoryPrivate { get; set; }

    /// <summary>When <see cref="RepositoryPrivate"/> was last read from GitHub.</summary>
    public DateTimeOffset? RepositoryCheckedAt { get; set; }

    /// <summary>When the owner last saw a notice that the repository is not private (at most one a day).</summary>
    public DateTimeOffset? LastPublicNotice { get; set; }

    /// <summary>When the owner last saw a notice that a job was stopped because the disk was almost full (at most one a day).</summary>
    public DateTimeOffset? LastDiskNotice { get; set; }

    /// <summary>What <c>wsl --version</c> printed for the dedicated account the last time it was asked (WSL version, kernel version, Windows version), one item per line. Status shows it (round 3, H1/L12).</summary>
    public string? WslVersionText { get; set; }

    /// <summary>When <see cref="WslVersionText"/> was read.</summary>
    public DateTimeOffset? WslVersionAt { get; set; }

    /// <summary>The kernel boot id (<c>/proc/sys/kernel/random/boot_id</c>) of the last job's Linux machine. The next job must see another one, or its machine was not started fresh (round 3, M3).</summary>
    public string? LastBootId { get; set; }

    /// <summary>The kernel release (<c>uname -r</c>) the last job's Linux machine ran.</summary>
    public string? KernelRelease { get; set; }

    /// <summary>When that kernel was built (read from <c>uname -v</c>); null when the text had no date.</summary>
    public DateTimeOffset? KernelBuilt { get; set; }

    /// <summary>When the kernel and boot id were last read from a job's Linux machine.</summary>
    public DateTimeOffset? KernelSeenAt { get; set; }

    /// <summary>When the weekly WSL update was last tried (or found switched off); null when never (round 3, H1).</summary>
    public DateTimeOffset? LastWslUpdateAttempt { get; set; }

    /// <summary>What came of the last WSL update, in plain words (updated, failed, switched off). Status shows it.</summary>
    public string? LastWslUpdateNote { get; set; }

    /// <summary>
    /// The owner's answer, given at set-up, to "may this program update WSL itself once a week?" (round 4, F5): true = yes, false = no, null = never asked, which counts as no. WSL is shared with the owner's own
    /// Linux, so the program never changes it without the owner saying yes.
    /// </summary>
    public bool? WslUpdateAllowed { get; set; }

    /// <summary>When the owner last saw a notice that the isolation probe no longer allows Linux checks on this PC (at most one a day).</summary>
    public DateTimeOffset? LastProbeNotice { get; set; }

    /// <summary>
    /// What the last clean-up could not remove (a Linux machine, a folder, the probe's marker file on Windows), each entry with the exact thing it is about; empty when nothing is left. Status shows it and the
    /// owner gets a daily notice. (Round 4, F7: the entries are found by their subject, not by a piece of their text.)
    /// </summary>
    public List<ResidueItem> ResidueItems { get; set; } = [];

    /// <summary>The words of the entries of <see cref="ResidueItems"/>. Not kept in the file.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Residue => [.. ResidueItems.Select(i => i.Text)];

    public List<JobRecord> Jobs { get; set; } = [];

    public SelfTestRecord? SelfTest { get; set; }
}

/// <summary>
/// Reads and writes <see cref="CiState"/> as one small JSON file, replacing it atomically. Every text that goes into the file passes the redactor first when one is given (local CI round 2,
/// QA M1: a failure text that quotes a secret must not reach the file even if a caller forgot to clean it). A change is read, made and written while this program holds a lock file that no other
/// program (the loop, the status, the probe, set-up all run at times side by side) can hold at the same time, so one program's change never overwrites another's (red-team round 3, L6).
/// </summary>
public sealed class StateStore
{
    private const int JobsKept = 10;
    private const int ResidueKept = 20;

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Redactor? _redactor;
    private readonly object _gate = new();
    private bool _updating;
    private readonly TimeSpan _lockPatience;
    private readonly TimeSpan _lockStep;
    private readonly Action<TimeSpan> _wait;

    /// <param name="path">The state file.</param>
    /// <param name="redactor">Cleans every text before it is written.</param>
    /// <param name="lockPatience">How long a change waits for another program's lock (default 15 seconds); counted as the sum of the waits made, not read from a clock.</param>
    /// <param name="lockStep">One wait between two tries (default 25 milliseconds).</param>
    /// <param name="wait">How a wait is made (default: the thread sleeps); tests make it release the other holder instead.</param>
    public StateStore(string path, Redactor? redactor = null, TimeSpan? lockPatience = null, TimeSpan? lockStep = null, Action<TimeSpan>? wait = null)
    {
        _path = path;
        _redactor = redactor;
        _lockPatience = lockPatience ?? TimeSpan.FromSeconds(15);
        _lockStep = lockStep ?? TimeSpan.FromMilliseconds(25);
        _wait = wait ?? Thread.Sleep;
    }

    public CiState Load()
    {
        lock (_gate)
        {
            return ReadLocked();
        }
    }

    /// <summary>Windows refuses to replace a file while another program reads it, even one that opened it allowing deletion (seen on GitHub's Windows
    /// machines, 2026-11-01); Linux does not. Readers hold the file only for a moment, so the replace is tried again for up to about two seconds.</summary>
    private static void ReplaceWithRetry(string temporary, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(s_replaceWait);
            }
        }
    }

    private const int ReplaceAttempts = 40;

    private static readonly TimeSpan s_replaceWait = TimeSpan.FromMilliseconds(50);

    public void Update(Action<CiState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            // The same thread may enter this lock again; a change made inside a change would be written first and then overwritten by the outer one, so it is refused, not allowed to lose data quietly.
            if (_updating)
            {
                throw new InvalidOperationException("A change of the state file was started inside another change of it; the inner one would be lost. This is a mistake in the program.");
            }

            _updating = true;
            try
            {
                DataFolder.RequireExists(Path.GetDirectoryName(_path)!);
                using var interprocess = TakeTheFileLock();
                var state = ReadLocked();
                change(state);
                state.Jobs = [.. state.Jobs.OrderByDescending(j => j.At).Take(JobsKept)];
                state.ResidueItems = [.. state.ResidueItems.GroupBy(i => (i.Kind, Subject: i.Subject.ToUpperInvariant())).Select(g => g.Last()).Take(ResidueKept)];
                Scrub(state);
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, s_options));
                ReplaceWithRetry(temporary, _path);
            }
            finally
            {
                _updating = false;
            }
        }
    }

    /// <summary>
    /// Holds <c>state.json.lock</c> open with no sharing until the returned object is disposed. Another program that has it is waited for, up to the patience; then the change is not made and the reason is
    /// said in words (a lock that nobody lets go would otherwise stop the loop without a message).
    /// </summary>
    private FileStream TakeTheFileLock()
    {
        var lockPath = _path + ".lock";
        var waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (waited < _lockPatience)
            {
                _wait(_lockStep);
                waited += _lockStep;
            }
            catch (IOException ex)
            {
                throw new IOException($"The program's state file {_path} is in use by another DbdLocalCi program that did not let go within {_lockPatience.TotalSeconds:0} seconds, so this change was not made.", ex);
            }
        }
    }

    private void Scrub(CiState state)
    {
        if (_redactor is null)
        {
            return;
        }

        foreach (var job in state.Jobs)
        {
            job.Os = _redactor.Redact(job.Os);
            job.Result = _redactor.Redact(job.Result);
        }

        foreach (var item in state.ResidueItems)
        {
            item.Subject = _redactor.Redact(item.Subject);
            item.Text = _redactor.Redact(item.Text);
        }

        if (state.SelfTest is { } test)
        {
            test.Outcome = _redactor.Redact(test.Outcome);
        }

        state.BaseImageRecipe = state.BaseImageRecipe is null ? null : _redactor.Redact(state.BaseImageRecipe);
        state.WslVersionText = state.WslVersionText is null ? null : _redactor.Redact(state.WslVersionText);
        state.LastWslUpdateNote = state.LastWslUpdateNote is null ? null : _redactor.Redact(state.LastWslUpdateNote);
        if (state.Probe is { } probe)
        {
            foreach (var attempt in probe.Attempts)
            {
                attempt.Evidence = _redactor.Redact(attempt.Evidence);
            }

            probe.WritableLocations = [.. probe.WritableLocations.Select(_redactor.Redact)];
            probe.AuditGaps = [.. probe.AuditGaps.Select(_redactor.Redact)];
            probe.WslVersion = probe.WslVersion is null ? null : _redactor.Redact(probe.WslVersion);
        }
        state.KernelRelease = state.KernelRelease is null ? null : _redactor.Redact(state.KernelRelease);
    }

    private CiState ReadLocked()
    {
        if (!File.Exists(_path))
        {
            return new CiState();
        }

        try
        {
            // Read so that a program that replaces the file at the same moment is not stopped by this reader (Windows refuses to replace a file that is open without delete sharing).
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<CiState>(reader.ReadToEnd()) ?? new CiState();
        }
        catch (JsonException)
        {
            // A damaged state file only forgets the history; the next write replaces it.
            return new CiState();
        }
        catch (FileNotFoundException)
        {
            // hygiene: allow-swallow the file was replaced between the check and the open; there is no history to read, which is what a missing file means
            return new CiState();
        }
    }
}
