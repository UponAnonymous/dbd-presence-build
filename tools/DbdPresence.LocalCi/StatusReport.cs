using System.Globalization;
using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>What the program knows about the dedicated account's WSL, from its last use (round 3, H1, L12): the text of <c>wsl --version</c> and the kernel a job's Linux machine ran.</summary>
/// <param name="VersionText"><c>wsl --version</c>, one item per line.</param>
/// <param name="VersionAt">When it was read.</param>
/// <param name="KernelRelease">The kernel release of the last job's machine.</param>
/// <param name="KernelBuilt">When that kernel was built, if its text said.</param>
/// <param name="KernelSeenAt">When it was seen.</param>
public sealed record WslFacts(string? VersionText, DateTimeOffset? VersionAt, string? KernelRelease, DateTimeOffset? KernelBuilt, DateTimeOffset? KernelSeenAt);

/// <summary>What this program is built from (round 3, L12): the .NET runtime inside the single file and the day it was built.</summary>
/// <param name="RuntimeVersion">The .NET runtime in the file, such as ".NET 10.0.0".</param>
/// <param name="BuildDate">The day the program was built, or null when the file does not say.</param>
public sealed record BuildFacts(string RuntimeVersion, DateTimeOffset? BuildDate);

/// <summary>Everything the status text needs. No field can hold a secret.</summary>
/// <param name="Now">The time of the report.</param>
/// <param name="TokenSaved">Whether a GitHub token is saved.</param>
/// <param name="TaskExists">Whether the sign-in task exists.</param>
/// <param name="LoopRunning">Whether a loop is running.</param>
/// <param name="Runners">The runners GitHub lists, or null when GitHub could not be asked.</param>
/// <param name="RunnerProblem">Why GitHub could not be asked.</param>
/// <param name="Jobs">The last jobs.</param>
/// <param name="TokenExpiresAt">When the token ends, if GitHub said.</param>
/// <param name="DiskBytes">What the program's data folder uses.</param>
/// <param name="SelfTest">The last test check.</param>
/// <param name="BaseImageBuilt">Whether the Linux base image exists.</param>
/// <param name="ProgramPath">Where the installed program is (null: not asked).</param>
/// <param name="ProgramInstalled">Whether it is installed.</param>
/// <param name="DataFolderProblems">What is wrong with the data folder (null: not asked).</param>
/// <param name="Variables">The values of the repository variables the status looks at (a missing one is null); null when GitHub could not be asked.</param>
/// <param name="AccountProblems">What is wrong with the dedicated Windows account of the Linux machine (null: not asked).</param>
/// <param name="Residue">What the last clean-up could not remove.</param>
/// <param name="TokenRefused">True when GitHub answered that it does not accept the saved token.</param>
/// <param name="RepositoryPrivate">What GitHub last said about the repository: true = private, false = not private, null = not known.</param>
/// <param name="RepositoryCheckedAt">When that was read.</param>
/// <param name="TokenIsClassic">True when GitHub's answers show that the saved token is a classic token, which this program refuses.</param>
/// <param name="Probe">The last isolation probe and whether the owner accepted it (null: none has run).</param>
/// <param name="CurrentProbeRecipe">Names the setup a probe result would be about today; a result with another name is out of date.</param>
/// <param name="Wsl">What is known about the account's WSL.</param>
/// <param name="WslUpdateNote">What came of the last weekly WSL update, in words (null: never tried).</param>
/// <param name="Build">What this program is built from.</param>
public sealed record StatusFacts(
    DateTimeOffset Now,
    bool TokenSaved,
    bool TaskExists,
    bool LoopRunning,
    IReadOnlyList<RunnerInfo>? Runners,
    string? RunnerProblem,
    IReadOnlyList<JobRecord> Jobs,
    DateTimeOffset? TokenExpiresAt,
    long DiskBytes,
    SelfTestRecord? SelfTest,
    bool BaseImageBuilt,
    string? ProgramPath = null,
    bool ProgramInstalled = true,
    IReadOnlyList<string>? DataFolderProblems = null,
    IReadOnlyDictionary<string, string?>? Variables = null,
    IReadOnlyList<string>? AccountProblems = null,
    IReadOnlyList<string>? Residue = null,
    bool TokenRefused = false,
    bool? RepositoryPrivate = null,
    DateTimeOffset? RepositoryCheckedAt = null,
    bool TokenIsClassic = false,
    ProbeRecord? Probe = null,
    string? CurrentProbeRecipe = null,
    WslFacts? Wsl = null,
    BuildFacts? Build = null,
    string? WslUpdateNote = null);

/// <summary>The status as plain words (FR-CI-07).</summary>
public static class StatusReport
{
    public static string Build(StatusFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var text = new StringBuilder();
        text.AppendLine("DBD Local CI: status");
        text.AppendLine();
        text.AppendLine(facts.TokenSaved && facts.TaskExists
            ? "Set up: yes."
            : "Set up: NO. " + (facts.TokenSaved ? "The sign-in task is missing." : "No GitHub token is saved.") + " Choose 1 (Set up).");
        text.AppendLine("Background task \"" + LocalCiDefaults.TaskName + "\": " + (facts.LoopRunning ? "running." : facts.TaskExists ? "installed but not running right now (it starts when you sign in)." : "not installed."));
        if (facts.ProgramPath is not null)
        {
            text.AppendLine(facts.ProgramInstalled
                ? "Program: installed in " + facts.ProgramPath + " (the background task runs that copy)."
                : "Program: NOT installed in Program Files. Choose 1 (Set up).");
        }

        if (facts.DataFolderProblems is { Count: > 0 } problems)
        {
            text.AppendLine("Data folder: PROBLEM. The background task will not run until this is repaired (choose 1, Set up): " + problems[0]);
        }
        else if (facts.DataFolderProblems is not null)
        {
            text.AppendLine("Data folder: closed to everyone but Administrators and the system.");
        }

        if (facts.AccountProblems is { Count: > 0 } accountProblems)
        {
            text.AppendLine($"Windows account {LocalCiDefaults.WslAccountName} (owns the Linux machine): PROBLEM. No job starts until this is repaired (choose 1, Set up): " + accountProblems[0]);
        }
        else if (facts.AccountProblems is not null)
        {
            text.AppendLine($"Windows account {LocalCiDefaults.WslAccountName} (owns the Linux machine): as it must be (standard user, hidden, closed work folder).");
        }

        text.AppendLine("Linux base image: " + (facts.BaseImageBuilt ? "built." : "not built yet."));
        foreach (var line in WslLines(facts))
        {
            text.AppendLine(line);
        }

        foreach (var line in ProbeLines(facts))
        {
            text.AppendLine(line);
        }

        if (RepositoryLine(facts) is { } repository)
        {
            text.AppendLine(repository);
        }

        text.AppendLine("Runners on GitHub: " + Runners(facts));
        foreach (var line in VariableLines(facts))
        {
            text.AppendLine(line);
        }

        if (facts.Residue is { Count: > 0 } residue)
        {
            text.AppendLine("Leftovers: PROBLEM. The last clean-up could not remove: " + string.Join("; ", residue) + ". The loop tries again at its next start; if it keeps coming back, delete them by hand (see docs/owner/free-ci.md, \"If something is left behind\").");
        }
        else if (facts.Residue is not null)
        {
            text.AppendLine("Leftovers: none.");
        }

        text.AppendLine();
        text.AppendLine("Last jobs:");
        if (facts.Jobs.Count == 0)
        {
            text.AppendLine("  none yet.");
        }

        foreach (var job in facts.Jobs.OrderByDescending(j => j.At).Take(10))
        {
            text.AppendLine($"  {Local(job.At)}  {job.Os,-7}  {job.Result}");
        }

        text.AppendLine();
        text.AppendLine("GitHub token: " + Token(facts));
        text.AppendLine("Disk used by this program: " + Gigabytes(facts.DiskBytes));
        if (BuildLine(facts) is { } build)
        {
            text.AppendLine(build);
        }

        text.AppendLine("Does GitHub refuse jobs right now? " + Refusal(facts.SelfTest));
        return text.ToString();
    }

    public static string Gigabytes(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    private static string Local(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Runners(StatusFacts facts)
    {
        if (facts.Runners is null)
        {
            return "could not ask GitHub (" + (facts.RunnerProblem ?? "no answer") + ").";
        }

        var mine = facts.Runners.Where(r => LocalCiDefaults.IsRunnerName(r.Name)).ToList();
        if (mine.Count == 0)
        {
            return "Linux none.";
        }

        var online = mine.Where(r => !string.Equals(r.Status, "offline", StringComparison.OrdinalIgnoreCase)).ToList();
        var stale = mine.Count - online.Count;
        var describe = online.Count == 0 ? "none" : online.Any(r => r.Busy) ? "busy with a job" : "waiting for a job";
        return stale == 0 ? $"Linux {describe}." : $"Linux {describe} ({stale} offline registration{(stale == 1 ? string.Empty : "s")} left over from a crash; GitHub removes them by itself).";
    }

    /// <summary>Whether checks are sent to this PC, and a plain warning for every variable this program does not serve that is set (decision D41).</summary>
    private static IEnumerable<string> VariableLines(StatusFacts facts)
    {
        if (facts.Variables is null)
        {
            if (facts.TokenSaved)
            {
                yield return "Repository variables: could not ask GitHub, so it is not known whether checks are sent to this PC.";
            }

            yield break;
        }

        var routed = facts.Variables.TryGetValue("LINUX_RUNNER", out var linux) ? linux : null;
        if (string.Equals(routed, LocalCiDefaults.LinuxLabel, StringComparison.Ordinal))
        {
            // Checks sent to a PC whose loop is not running wait in GitHub's queue for a runner nobody serves (local CI round 3, QA M-1).
            yield return facts.LoopRunning
                ? "Checks on this PC: ON. LINUX_RUNNER is set, so the Linux checks of CI (Linux) and CI (service) run here; Windows jobs, the release build, deploys and backups run on GitHub's machines."
                : "Checks on this PC: PROBLEM. LINUX_RUNNER is set, so the Linux checks are sent to this PC, but the background loop is not running, so they wait for a runner nobody serves. "
                  + (facts.TaskExists
                      ? "It starts when you sign in; if it does not, choose 1 (Set up) or delete the variable LINUX_RUNNER on GitHub."
                      : "The sign-in task is missing: choose 1 (Set up), or delete the variable LINUX_RUNNER on GitHub.");
        }
        else
        {
            yield return routed is null
                ? "Checks on this PC: OFF. LINUX_RUNNER is not set, so the Linux checks run on GitHub's machines (choose 1, Set up, to turn it on)."
                : $"Checks on this PC: PROBLEM. LINUX_RUNNER is set to '{routed}', not to '{LocalCiDefaults.LinuxLabel}', so this PC is not the one that gets them.";
        }

        foreach (var name in LocalCiDefaults.NotServedVariables)
        {
            if (facts.Variables.TryGetValue(name, out var value) && value is not null)
            {
                yield return $"PROBLEM: {name} is set to '{value}'. This program does not serve that kind of job (decision D41: only Linux checks run on this PC), so jobs that read it wait for a runner nobody serves. "
                             + "Delete the variable on GitHub (Settings, Secrets and variables, Actions, Variables) or choose 1 (Set up), which offers to.";
            }
        }
    }

    /// <summary>The program's own build: the .NET runtime that is inside the file and the day it was built (round 3, L12). The runtime is bundled, so a new runtime fix needs a new DbdLocalCi.exe.</summary>
    private static string? BuildLine(StatusFacts facts)
    {
        if (facts.Build is not { } build)
        {
            return null;
        }

        var built = build.BuildDate is { } date ? $"built on {date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({AgeInDays(facts.Now, date)} days ago)" : "build date not known";
        return $"This program: {built}, with the {build.RuntimeVersion} runtime inside the file. Windows Update does not update that runtime; a new DbdLocalCi.exe does (docs/owner/free-ci.md, \"Getting and checking the program\").";
    }

    /// <summary>What is known about WSL on this PC: its version, and how old the Linux kernel of the last job's machine is.</summary>
    private static IEnumerable<string> WslLines(StatusFacts facts)
    {
        if (facts.Wsl is not { } wsl || (wsl.VersionText is null && wsl.KernelRelease is null))
        {
            yield return "Windows Subsystem for Linux: not seen yet (it is read when the first Linux machine starts).";
            if (facts.WslUpdateNote is { Length: > 0 } earlierNote)
            {
                yield return "  Weekly WSL update: " + earlierNote + ".";
            }

            yield break;
        }

        if (wsl.VersionText is { Length: > 0 } text)
        {
            var seen = wsl.VersionAt is { } at ? " (read at " + Local(at) + ")" : string.Empty;
            yield return "Windows Subsystem for Linux" + seen + ":";
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return "  " + line;
            }
        }

        if (facts.WslUpdateNote is { Length: > 0 } updateNote)
        {
            yield return "  Weekly WSL update: " + updateNote + ".";
        }

        if (wsl.KernelRelease is { Length: > 0 } release)
        {
            if (wsl.KernelBuilt is { } built)
            {
                var days = AgeInDays(facts.Now, built);
                var old = days > LocalCiDefaults.KernelOldAfter.TotalDays;
                yield return $"Linux kernel of the last job's machine: {release}, built {built.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({days} days old)."
                              + (old ? " WARNING: that is old. Run \"wsl --update\" in an administrator prompt when you are not using WSL yourself, then run the isolation probe again (choose 5)." : string.Empty);
            }
            else
            {
                yield return $"Linux kernel of the last job's machine: {release} (its build date is not known, so its age is not known).";
            }
        }
    }

    /// <summary>The isolation probe: what it found, whether the owner accepted it, and whether it still describes this setup.</summary>
    private static IEnumerable<string> ProbeLines(StatusFacts facts)
    {
        var routed = facts.Variables is not null && facts.Variables.TryGetValue("LINUX_RUNNER", out var linux) && string.Equals(linux, LocalCiDefaults.LinuxLabel, StringComparison.Ordinal);
        if (facts.Probe is not { } probe)
        {
            yield return "Isolation probe: " + (routed ? "PROBLEM. " : "NOT RUN. ")
                         + "Nobody has tried to get out of the Linux machine on this PC yet, so it is UNKNOWN how well it holds. Choose 5 (Isolation probe).";
            yield break;
        }

        var words = probe.Outcome switch
        {
            "AllBlocked" => "every attempt was stopped",
            "AuditIncomplete" => "every attempt was stopped, but the audit of the places the account may write to is INCOMPLETE",
            "SomePossible" => "at least one attempt WORKED",
            _ => "it could not be completed, so nothing is shown to be closed",
        };
        var accepted = probe.Accepted
            ? "you accepted it" + (probe.AcceptedAt is { } at ? " at " + Local(at) : string.Empty)
              + (probe.Renewals > 0 ? $" (after its own WSL update the program repeated the attempts by itself {probe.Renewals} {(probe.Renewals == 1 ? "time" : "times")}, last at {(probe.LastRenewedAt is { } renewed ? Local(renewed) : "an unknown time")}, with a result no worse)" : string.Empty)
            : "you have NOT accepted it";
        yield return $"Isolation probe: at {Local(probe.At)}: {words}; {accepted}.";
        foreach (var attempt in probe.Attempts.Where(a => !string.Equals(a.Verdict, nameof(ProbeVerdict.Blocked), StringComparison.Ordinal)))
        {
            var word = string.Equals(attempt.Verdict, nameof(ProbeVerdict.Possible), StringComparison.Ordinal) ? "POSSIBLE" : "NOT TRIED";
            yield return $"  {word}: {ProbeText.What(attempt.Id)}" + (attempt.Evidence.Length > 0 ? $" ({attempt.Evidence})" : string.Empty);
        }

        if (probe.WritableLocations.Count > 0)
        {
            yield return $"  Places the Windows account {LocalCiDefaults.WslAccountName} may write to: {probe.WritableLocations.Count}" + (probe.WritableLocations.Count > 0 ? $", for example {probe.WritableLocations[0]}." : ".");
        }

        if (probe.AuditGapCount > 0)
        {
            // Round 4, F1: a place that could not be checked is UNKNOWN, never "none".
            yield return $"  UNKNOWN: the audit of the places the account may write to is INCOMPLETE: {probe.AuditGapCount} {(probe.AuditGapCount == 1 ? "place" : "places")} could not be checked"
                         + (probe.AuditGaps.Count > 0 ? $", for example {probe.AuditGaps[0]}." : ".") + " Look at the list the probe printed (choose 5 to run it again).";
        }

        if (facts.CurrentProbeRecipe is { } current && !string.Equals(current, probe.Recipe, StringComparison.Ordinal))
        {
            yield return "  PROBLEM: the probe was run for an older setup of the Linux machine (its settings or the base image recipe changed since). Run it again (choose 5). No Linux check starts on this PC until then.";
        }

        if (probe.WslVersion is { Length: > 0 } then && facts.Wsl?.VersionText is { Length: > 0 } now && !string.Equals(then, now, StringComparison.Ordinal))
        {
            yield return "  PROBLEM: WSL was updated or changed since the probe was run. Run it again (choose 5). No Linux check starts on this PC until then.";
        }

        if (!probe.Accepted && routed)
        {
            yield return "  PROBLEM: checks are sent to this PC although you have not accepted the result of the probe, so no Linux check starts here and they wait in GitHub's queue. Choose 5 (Isolation probe) and decide, or delete the variable LINUX_RUNNER on GitHub.";
        }
    }

    private static int AgeInDays(DateTimeOffset now, DateTimeOffset then) => Math.Max(0, (int)Math.Floor((now - then).TotalDays));

    /// <summary>Whether the repository is private: only a private repository is served (local CI round 3, red-team M5).</summary>
    private static string? RepositoryLine(StatusFacts facts)
    {
        if (!facts.TokenSaved)
        {
            return null;
        }

        var name = LocalCiDefaults.RepositoryOwner + "/" + LocalCiDefaults.RepositoryName;
        var when = facts.RepositoryCheckedAt is { } checkedAt ? " (GitHub said so at " + Local(checkedAt) + ")" : string.Empty;
        return facts.RepositoryPrivate switch
        {
            true => $"Repository {name}: private{when}.",
            false => $"Repository {name}: PROBLEM{when}. " + RepositoryGuard.NotPrivateSentence,
            null => $"Repository {name}: PROBLEM. " + RepositoryGuard.UnknownSentence,
        };
    }

    private static string Token(StatusFacts facts)
    {
        if (!facts.TokenSaved)
        {
            return "not saved.";
        }

        if (facts.TokenIsClassic)
        {
            return "PROBLEM. GitHub's answers show that this is a CLASSIC token, which reaches every repository of your account. This program refuses it and starts no runner. Make a fine-grained token (it starts with github_pat_, limited to this one repository) and run setup again.";
        }

        if (facts.TokenRefused)
        {
            return "GitHub REFUSES it (it ended, was revoked or lacks a permission). Checks sent to this PC wait for a runner nobody serves. Make a new token and run setup again.";
        }

        if (facts.TokenExpiresAt is not { } expires)
        {
            return "saved (GitHub did not say when it expires).";
        }

        var days = (int)Math.Floor((expires - facts.Now).TotalDays);
        var date = expires.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (days < 0)
        {
            return $"EXPIRED on {date}. Make a new token and run setup again.";
        }

        return days <= LocalCiDefaults.TokenWarning.TotalDays
            ? $"WARNING: expires on {date} (in {days} days). Make a new token and run setup again before then."
            : $"saved, expires on {date} (in {days} days).";
    }

    private static string Refusal(SelfTestRecord? test)
    {
        if (test is null)
        {
            return "UNKNOWN. Nobody has measured it yet: choose 3 (Test check) or run selftest.";
        }

        var when = Local(test.At);
        return test.Outcome switch
        {
            nameof(SelfTestOutcome.PassedOnThisPc) => $"No. At {when} the test job ran on this PC and passed.",
            nameof(SelfTestOutcome.Failed) => $"No refusal seen. At {when} the test job ran and failed (see the Actions page).",
            nameof(SelfTestOutcome.FailedToStart) => $"UNKNOWN, and possible. At {when} the test job did not start (no runner took it and no step ran); a payment or spending-limit block looks like this. Open that run on the Actions page and read GitHub's message.",
            nameof(SelfTestOutcome.PassedOnGitHub) => $"UNKNOWN. At {when} the test passed on GitHub's machine, not on this PC.",
            nameof(SelfTestOutcome.Cancelled) => $"UNKNOWN. The last test ({when}) was cancelled before it finished (a newer push cancels the older run of CI (Linux)), so nothing was measured. Choose 3 again.",
            _ => $"UNKNOWN. The last test ({when}) did not finish.",
        };
    }
}

/// <summary>Collects <see cref="StatusFacts"/> from the machine and GitHub.</summary>
public sealed class StatusCollector
{
    private readonly ISecretStore _secrets;
    private readonly IScheduler _scheduler;
    private readonly IGitHubApi _api;
    private readonly StateStore _state;
    private readonly LocalCiPaths _paths;
    private readonly TimeProvider _time;
    private readonly Redactor _redactor;
    private readonly IProgramInstaller? _installer;
    private readonly IProtectedStorage? _storage;
    private readonly IWslAccount? _account;
    private readonly Func<string>? _currentProbeRecipe;
    private readonly BuildFacts? _build;

    public StatusCollector(
        ISecretStore secrets,
        IScheduler scheduler,
        IGitHubApi api,
        StateStore state,
        LocalCiPaths paths,
        TimeProvider time,
        Redactor redactor,
        IProgramInstaller? installer = null,
        IProtectedStorage? storage = null,
        IWslAccount? account = null,
        Func<string>? currentProbeRecipe = null,
        BuildFacts? build = null)
    {
        _currentProbeRecipe = currentProbeRecipe;
        _build = build;
        _installer = installer;
        _storage = storage;
        _account = account;
        _secrets = secrets;
        _scheduler = scheduler;
        _api = api;
        _state = state;
        _paths = paths;
        _time = time;
        _redactor = redactor;
    }

    public async Task<StatusFacts> CollectAsync(CancellationToken cancellationToken)
    {
        var state = _state.Load();
        IReadOnlyList<RunnerInfo>? runners = null;
        Dictionary<string, string?>? variables = null;
        string? problem = null;
        var refused = false;
        var classic = false;
        var saved = _secrets.Exists();
        if (saved)
        {
            try
            {
                runners = await _api.ListRunnersAsync(cancellationToken).ConfigureAwait(false);
                variables = [];
                foreach (var name in LocalCiDefaults.Variables.Keys.Concat(LocalCiDefaults.NotServedVariables))
                {
                    variables[name] = await _api.GetVariableAsync(name, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (GitHubApiException ex)
            {
                problem = _redactor.Redact(ex.Message);
                refused = ex.IsAuthenticationFailure;
                classic = ex.IsClassicToken;
                runners = null;
                variables = null;
            }
        }
        else
        {
            problem = "no token saved";
        }

        return new StatusFacts(
            _time.GetUtcNow(),
            saved,
            await _scheduler.ExistsAsync(cancellationToken).ConfigureAwait(false),
            _scheduler.LoopIsRunning(),
            runners,
            problem,
            state.Jobs,
            state.TokenExpiresAt,
            DirectorySize(_paths.Machine),
            state.SelfTest,
            File.Exists(_paths.BaseArchive),
            _installer?.InstalledPath,
            _installer?.IsInstalled ?? true,
            _storage is null || !Directory.Exists(_paths.Machine) ? null : _storage.CheckDataFolder(),
            variables,
            _account is null || !Directory.Exists(_paths.Machine) ? null : _account.Check(),
            Directory.Exists(_paths.Machine) ? state.Residue : null,
            refused,
            state.RepositoryPrivate,
            state.RepositoryCheckedAt,
            classic,
            state.Probe,
            _currentProbeRecipe?.Invoke(),
            new WslFacts(state.WslVersionText, state.WslVersionAt, state.KernelRelease, state.KernelBuilt, state.KernelSeenAt),
            _build,
            state.LastWslUpdateNote);
    }

    public static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        {
            total += file.Length;
        }

        return total;
    }
}
