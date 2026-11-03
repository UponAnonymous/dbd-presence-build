using System.Globalization;

namespace DbdPresence.LocalCi;

/// <summary>How the owner's review of an isolation probe ended.</summary>
public enum ProbeOutcome
{
    /// <summary>The owner typed "yes" to what the probe found: checks may be sent to this PC.</summary>
    Accepted,

    /// <summary>The owner did not type "yes": nothing is to be sent to this PC.</summary>
    Declined,

    /// <summary>The probe could not run, so nothing is shown to be closed; it cannot be accepted.</summary>
    CouldNotRun,

    /// <summary>
    /// The owner typed "yes", but the result they looked at is no longer the last one saved (another window of this program ran the probe in the meantime), so nothing was accepted: a "yes" is for one result.
    /// </summary>
    Superseded,
}

/// <summary>Only a typed "yes" accepts what the isolation probe found: not "y", not Enter, not a click.</summary>
public static class ProbeAcceptance
{
    public static bool IsYes(string? typed) => string.Equals(typed?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The isolation probe's findings in plain words: what each attempt was, and what it means when it worked.</summary>
public static class ProbeText
{
    public static string What(string id) => id switch
    {
        "mount-windows-drive" => "Mount a Windows drive as root and list C:\\Users",
        "write-windows-public" => "Write a file into C:\\Users\\Public as root",
        "windows-programs" => "Start a Windows program (cmd.exe /c whoami) as root",
        "gpu-device" => "Use the GPU device (/dev/dxg)",
        "virtualization-device" => "Use the virtualization device (/dev/kvm)",
        "shared-mounts" => "Find a file system shared from outside the machine",
        "host-network" => "Reach this PC over the network (common ports)",
        _ => id,
    };

    public static string Word(ProbeVerdict verdict) => verdict switch
    {
        ProbeVerdict.Blocked => "BLOCKED ",
        ProbeVerdict.Possible => "POSSIBLE",
        _ => "NOT TRIED",
    };

    /// <summary>What it means that this attempt worked or could not be made; empty for an attempt that was stopped.</summary>
    public static string Meaning(ProbeAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Verdict == ProbeVerdict.Blocked)
        {
            return string.Empty;
        }

        if (attempt.Verdict == ProbeVerdict.NotTried)
        {
            return "It could not be tried, so this way out is not shown to be closed.";
        }

        return attempt.Id switch
        {
            "mount-windows-drive" => $"A job that gains root inside its Linux machine could mount your Windows drive and read and change everything the Windows account {LocalCiDefaults.WslAccountName} can (the places listed above). I recommend NOT turning the Linux checks on for this PC. Update WSL (wsl --update, in an administrator prompt) and run \"DbdLocalCi probe\" again: the setting that closes this, hostFileSystemAccess=false in that account's .wslconfig, is ignored by a WSL that is too old.",
            "write-windows-public" => "Files can be written to Windows from inside the Linux machine.",
            "windows-programs" => $"A job that gains root inside its Linux machine could start Windows programs as the account {LocalCiDefaults.WslAccountName}.",
            "gpu-device" => "The Linux machine has a GPU device; a flaw in the graphics driver is a way out of a virtual machine.",
            "virtualization-device" => "Nested virtualization is on inside the Linux machine.",
            "shared-mounts" => "Files of this PC are shared into the Linux machine.",
            "host-network" => "Programs in the Linux machine can talk to services on this PC (such as the Windows file sharing or remote desktop ports), the way any program on a network could. The settings of this program do not stop that. Do not run services on this PC that trust every program on the local network.",
            _ => "This way out is open.",
        };
    }

    /// <summary>The report as lines for the console: the places the account may write, then every attempt with its evidence and, where it worked, what that means.</summary>
    public static IReadOnlyList<string> Report(ProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>();
        lines.AddRange(AuditLines(report.Audit));
        lines.Add(string.Empty);
        lines.Add("Attempts to get out of the Linux machine, as root inside a throwaway copy of the job image:");
        foreach (var attempt in report.Attempts)
        {
            lines.Add($"  {Word(attempt.Verdict)}  {What(attempt.Id)}");
            if (attempt.Evidence.Length > 0)
            {
                lines.Add($"            evidence: {attempt.Evidence}");
            }

            var meaning = Meaning(attempt);
            if (meaning.Length > 0)
            {
                lines.Add($"            what it means: {meaning}");
            }
        }

        return lines;
    }

    /// <summary>
    /// What the audit of the places the account may write to found, and what it could not look at (round 4, F1). A place that could not be checked is never reported as "none": the text says it is UNKNOWN.
    /// What the audit looks at is said too, so a place outside that list is not taken for a place that was checked.
    /// </summary>
    public static IReadOnlyList<string> AuditLines(WritableAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var lines = new List<string>();
        if (audit.Places.Count > 0)
        {
            lines.Add($"Places on this PC the Windows account {LocalCiDefaults.WslAccountName} may write to (a job that got out of its Linux machine as root could change these):");
            lines.AddRange(audit.Places.Select(l => "  " + l));
        }
        else if (audit.Complete)
        {
            lines.Add($"The audit looked at {audit.PlacesLooked} places on this PC and found none the Windows account {LocalCiDefaults.WslAccountName} may write to besides its own folders.");
        }
        else if (audit.PlacesLooked == 0)
        {
            lines.Add($"The audit could not look at any place, so it is UNKNOWN which places the Windows account {LocalCiDefaults.WslAccountName} may write to.");
        }
        else
        {
            lines.Add($"The audit found no place the Windows account {LocalCiDefaults.WslAccountName} may write to among the {audit.PlacesLooked} places it could look at. That is NOT \"none\": see below.");
        }

        if (!audit.Complete)
        {
            lines.Add($"The audit is INCOMPLETE: {audit.GapCount} {(audit.GapCount == 1 ? "place" : "places")} could not be checked, so it is UNKNOWN whether the account may write there:");
            lines.AddRange(audit.Gaps.Select(g => "  " + g));
        }

        lines.Add("What the audit looks at: the roots of the drives and their top-level folders, the Public folder, the Windows temp folder, the folders directly in Program Files and ProgramData, and the folders of the system PATH. It does not look deeper, and it does not follow links.");
        return lines;
    }

    /// <summary>One sentence for the result as a whole.</summary>
    public static string Summary(ProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Failure is not null)
        {
            return "The probe could not run, so nothing is shown to be closed.";
        }

        var possible = report.Attempts.Count(a => a.Verdict == ProbeVerdict.Possible);
        var notTried = report.Attempts.Count(a => a.Verdict == ProbeVerdict.NotTried);
        if (possible == 0 && notTried == 0)
        {
            return report.Audit.Complete
                ? "Every attempt was stopped."
                : string.Create(CultureInfo.InvariantCulture, $"Every attempt was stopped, but the audit of the places the account may write to is INCOMPLETE ({report.Audit.GapCount} could not be checked).");
        }

        var parts = new List<string>();
        if (possible > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{possible} of {report.Attempts.Count} attempts worked"));
        }

        if (notTried > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{notTried} could not be tried"));
        }

        return string.Join(" and ", parts) + ".";
    }
}

/// <summary>
/// Runs the isolation probe with the owner watching, keeps what it found and asks for the owner's acceptance (local CI round 3, red-team H1). Used by set-up (before anything is sent to this PC) and by the
/// <c>probe</c> command (to look again after an update). The acceptance is a typed "yes": a probe that worked anywhere recommends against it and says so; a probe that could not run cannot be accepted.
/// </summary>
public sealed class ProbeReview
{
    private readonly IsolationProbe _probe;
    private readonly StateStore _state;
    private readonly IConsoleUi _ui;
    private readonly Func<string> _currentRecipe;
    private readonly TimeProvider _time;
    private readonly ICiLog _log;

    /// <param name="probe">The probe.</param>
    /// <param name="state">Where the result is kept.</param>
    /// <param name="ui">The owner's console.</param>
    /// <param name="currentRecipe">Names the setup (Linux machine settings and base image recipe) a result is about.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The log.</param>
    public ProbeReview(IsolationProbe probe, StateStore state, IConsoleUi ui, Func<string> currentRecipe, TimeProvider time, ICiLog log)
    {
        _probe = probe;
        _state = state;
        _ui = ui;
        _currentRecipe = currentRecipe;
        _time = time;
        _log = log;
    }

    public async Task<ProbeOutcome> RunAsync(CancellationToken cancellationToken)
    {
        _ui.WriteLine("The isolation probe tries, as root inside a throwaway copy of the job image, what a job that broke into its Linux machine would try. It changes nothing on this PC; it removes the machine and its test file afterwards.");
        _ui.WriteLine("This takes a few minutes. Please wait.");
        var report = await _probe.RunAsync(cancellationToken).ConfigureAwait(false);
        var savedId = Save(report);
        foreach (var line in ProbeText.Report(report))
        {
            _ui.WriteLine(line);
        }

        _ui.WriteLine(string.Empty);
        _ui.WriteLine(ProbeText.Summary(report));
        if (report.Failure is not null)
        {
            _ui.WriteLine(report.Failure);
            _ui.WriteLine("Nothing can be accepted without a result. Fix what the log says and run \"DbdLocalCi probe\" again.");
            return ProbeOutcome.CouldNotRun;
        }

        _ui.WriteLine("This is evidence about the WSL on this PC as it is today, not a guarantee: an update of WSL, Windows or Linux can change it, so run \"DbdLocalCi probe\" again after updates (PENDING-ENV: no probe has been verified on a real PC by the people who wrote this program).");
        _ui.WriteLine("Checks of this repository also run on this PC without you: the weekly schedule of CI (service) and the pull requests of any bot you switched on (Dependabot, for example). They install packages and run third-party code.");
        _ui.WriteLine(report.AllBlocked
            ? "Type yes to send the repository's Linux checks to this PC. Anything else (or just Enter) stops here and sends nothing."
            : report.AttemptsAllBlocked
                ? "Every attempt was stopped, but the list of places the account may write to is INCOMPLETE (see above), so it is UNKNOWN what a job that got out could change there. I recommend you do NOT accept this until you have looked at those places. Type yes only if you accept that. Anything else (or just Enter) stops here and sends nothing to this PC."
                : "I recommend you do NOT accept this. Type yes only if you understand what is open and accept it. Anything else (or just Enter) stops here and sends nothing to this PC.");
        var typed = _ui.ReadLine();
        if (!ProbeAcceptance.IsYes(typed))
        {
            _ui.WriteLine("Not accepted. Nothing will be sent to this PC.");
            return ProbeOutcome.Declined;
        }

        var now = _time.GetUtcNow();
        var written = false;
        _state.Update(s =>
        {
            if (s.Probe is { } probe && string.Equals(probe.Id, savedId, StringComparison.Ordinal))
            {
                probe.Accepted = true;
                probe.AcceptedAt = now;
                written = true;
            }
        });
        if (!written)
        {
            _log.Warn("The owner typed yes, but the isolation probe result they looked at is no longer the last one saved (another DBD Local CI window ran the probe in the meantime), so nothing was accepted.");
            _ui.WriteLine("Another DBD Local CI window ran the probe while you were reading this, so the result you looked at is no longer the last one. Nothing was accepted. Run the probe again and decide on that result.");
            return ProbeOutcome.Superseded;
        }

        _log.Info($"The owner accepted the result of the isolation probe ({report.Outcome}).");
        _ui.WriteLine("Accepted.");
        return ProbeOutcome.Accepted;
    }

    /// <summary>Keeps the result as the last probe and returns its id, which the owner's "yes" must still find when it is written.</summary>
    private string Save(ProbeReport report)
    {
        var now = _time.GetUtcNow();
        var recipe = _currentRecipe();
        var version = _state.Load().WslVersionText;
        var id = Guid.NewGuid().ToString("N");
        _state.Update(s => s.Probe = new ProbeRecord
        {
            Id = id,
            At = now,
            Outcome = report.Outcome,
            Attempts = [.. report.Attempts.Select(a => new ProbeAttemptRecord { Id = a.Id, Verdict = a.Verdict.ToString(), Evidence = a.Evidence })],
            WritableLocations = [.. report.WritableLocations],
            AuditPlacesLooked = report.Audit.PlacesLooked,
            AuditGapCount = report.Audit.GapCount,
            AuditGaps = [.. report.Audit.Gaps],
            Recipe = recipe,
            WslVersion = version,
            Accepted = false,
        });
        return id;
    }
}
