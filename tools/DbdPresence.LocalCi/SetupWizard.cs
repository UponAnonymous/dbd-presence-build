namespace DbdPresence.LocalCi;

public enum SetupOutcome
{
    Done,
    RestartNeeded,
    Stopped,
}

/// <summary>The words that tell the owner how to make the GitHub token.</summary>
public static class TokenGuide
{
    public static IReadOnlyList<string> Steps { get; } =
    [
        "Make a GitHub token. It lets this program register runners and set one variable (LINUX_RUNNER) for ONE repository, nothing else. Treat it as the key to that repository:",
        "with Administration access it can change that repository's settings, so it stays on this PC (saved so that only Administrators can use it).",
        "  1. Open https://github.com/settings/personal-access-tokens/new in your browser (sign in to GitHub if asked).",
        "  2. Token name: DBD Local CI.",
        "  3. Expiration: Custom, 30 days from today (recommended: the program warns you 7 days before it ends; never choose No expiration).",
        "  4. Resource owner: " + LocalCiDefaults.RepositoryOwner + ".",
        "  5. Repository access: Only select repositories, then pick " + LocalCiDefaults.RepositoryOwner + "/" + LocalCiDefaults.RepositoryName + ".",
        "  6. Permissions, Repository permissions: set  Administration = Read and write,  Variables = Read and write,  Actions = Read and write.",
        "     (Metadata = Read-only is added for you.) Leave every other permission on No access.",
        "  7. Click Generate token, then Copy (it starts with github_pat_). GitHub shows it only once.",
        "Paste it below. Nothing appears while you paste; press Enter.",
    ];
}

/// <summary>
/// Setup (FR-CI-01): checks, the installed copy of this program, the closed data folder, WSL and the dedicated Windows account that owns the Linux machine, its settings, the Linux base image, the
/// runner package, the token, the sign-in task, and last of all the one repository variable that sends checks to this PC, followed by the test check. Every step can be run again and skips what is
/// done. The variable is set only after the loop runs and is taken away again unless the test check proves that a job ran on this PC and passed (local CI round 2, QA M3 and B4).
/// </summary>
public sealed class SetupWizard
{
    private const int Steps = 12;

    private readonly IConsoleUi _ui;
    private readonly IMachine _machine;
    private readonly IWsl _wsl;
    private readonly IWslInstaller _wslInstaller;
    private readonly IWslAccount _account;
    private readonly WslSandbox _sandbox;
    private readonly IProtectedStorage _storage;
    private readonly IProgramInstaller _installer;
    private readonly BaseImageBuilder _image;
    private readonly RunnerCache _runners;
    private readonly ISecretStore _secrets;
    private readonly SecretSource _token;
    private readonly IGitHubApi _api;
    private readonly IScheduler _scheduler;
    private readonly SelfTest _selfTest;
    private readonly ProbeReview _probe;
    private readonly StateStore _state;
    private readonly LocalCiPaths _paths;
    private readonly TimeProvider _time;
    private readonly Redactor _redactor;
    private readonly ICiLog _log;
    private readonly string _runningProgram;
    private bool? _repositoryPrivate;

    // What set-up has done to the background loop and to the checks' routing, so that however it ends it leaves neither a stopped loop behind a variable that still points at it, nor half a set-up
    // (local CI round 3, QA M-1).
    private bool _loopStopped;
    private bool _loopStarted;
    private bool _routingMustGo;

    public SetupWizard(
        IConsoleUi ui,
        IMachine machine,
        IWsl wsl,
        IWslInstaller wslInstaller,
        IWslAccount account,
        WslSandbox sandbox,
        IProtectedStorage storage,
        IProgramInstaller installer,
        BaseImageBuilder image,
        RunnerCache runners,
        ISecretStore secrets,
        SecretSource token,
        IGitHubApi api,
        IScheduler scheduler,
        SelfTest selfTest,
        ProbeReview probe,
        StateStore state,
        LocalCiPaths paths,
        TimeProvider time,
        Redactor redactor,
        ICiLog log,
        string runningProgram)
    {
        _ui = ui;
        _machine = machine;
        _wsl = wsl;
        _wslInstaller = wslInstaller;
        _account = account;
        _sandbox = sandbox;
        _storage = storage;
        _installer = installer;
        _image = image;
        _runners = runners;
        _secrets = secrets;
        _token = token;
        _api = api;
        _scheduler = scheduler;
        _selfTest = selfTest;
        _probe = probe;
        _state = state;
        _paths = paths;
        _time = time;
        _redactor = redactor;
        _log = log;
        _runningProgram = runningProgram;
    }

    /// <summary>
    /// True only after a run that ended with a test check that ran on this PC and passed. Anything else (a stop, a restart, a test that failed or could not be started) leaves it false, and the
    /// program then exits with an error code and has taken the variable away again.
    /// </summary>
    public bool ProvenOnThisPc { get; private set; }

    /// <summary>
    /// Runs set-up. However it ends (done, stopped, a restart wanted, the owner pressing Ctrl+C), a background loop that set-up stopped is started again, or, when it cannot be, or when set-up refused
    /// the repository or the isolation, the variable that sends checks here is deleted: nothing is left pointing at a PC that does not serve it.
    /// </summary>
    public async Task<SetupOutcome> RunAsync(CancellationToken cancellationToken)
    {
        ProvenOnThisPc = false;
        _loopStopped = false;
        _loopStarted = false;
        _routingMustGo = false;
        try
        {
            return await RunStepsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await LeaveNothingHalfDoneAsync().ConfigureAwait(false);
        }
    }

    /// <summary>After any end of set-up: take the routing away when set-up refused, otherwise start the loop again if set-up stopped it and nothing started it since.</summary>
    private async Task LeaveNothingHalfDoneAsync()
    {
        if (_routingMustGo)
        {
            await RemoveRoutingAsync("set-up refused to send checks to this PC").ConfigureAwait(false);
            return;
        }

        // A loop that set-up itself started in step 10 needs a moment to show as running; it is not started a second time.
        if (!_loopStopped || _loopStarted || _scheduler.LoopIsRunning())
        {
            return;
        }

        var started = false;
        try
        {
            await _scheduler.StartAsync(CancellationToken.None).ConfigureAwait(false);
            started = true;
        }
#pragma warning disable CA1031 // Whatever stops the restart is logged and answered by taking the routing away: set-up never leaves a variable pointing at a loop that is not running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"Setup could not start the background loop again: {_redactor.Describe(ex)}");
        }

        if (started)
        {
            _ui.WriteLine("The background loop that set-up stopped was started again, so checks already sent to this PC are served as before.");
            return;
        }

        _ui.WriteLine("The background loop that set-up stopped could not be started again.");
        await RemoveRoutingAsync("the background loop is not running").ConfigureAwait(false);
    }

    private async Task<SetupOutcome> RunStepsAsync(CancellationToken cancellationToken)
    {
        if (!_machine.IsAdministrator())
        {
            _ui.WriteLine("Setup needs administrator rights. Right-click the program and choose Run as administrator.");
            return SetupOutcome.Stopped;
        }

        if (_scheduler.LoopIsRunning())
        {
            _ui.WriteLine("The background loop is running; stopping it for the time set-up takes (it starts again at step 10). A check that is running on this PC right now is ended with it.");
            _loopStopped = true;
            if (!await GuardAsync(() => _scheduler.StopAsync(cancellationToken), "stopping the background task").ConfigureAwait(false))
            {
                return SetupOutcome.Stopped;
            }

            if (_scheduler.LoopIsRunning())
            {
                _ui.WriteLine("The background loop is still running. Stop it (Task Scheduler, task \"" + LocalCiDefaults.TaskName + "\", End) and run setup again.");
                _loopStopped = false;
                return SetupOutcome.Stopped;
            }
        }

        _ui.WriteLine($"Step 1 of {Steps}: checking this PC.");
        if (!CheckMachine())
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 2 of {Steps}: putting the program in its protected place (Program Files).");
        if (!await GuardAsync(() => InstallProgramAsync(cancellationToken), "installing the program").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 3 of {Steps}: the data folder, closed to everyone but Administrators.");
        if (!await GuardAsync(
                () =>
                {
                    _storage.CreateOrRepairDataFolder();
                    var problems = _storage.CheckDataFolder();
                    if (problems.Count > 0)
                    {
                        throw new InvalidOperationException("The data folder could not be closed: " + problems[0]);
                    }

                    _ui.WriteLine($"  {_paths.Machine}: only Administrators and the system can use it.");
                    return Task.CompletedTask;
                },
                "creating the data folder").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 4 of {Steps}: the Windows account {LocalCiDefaults.WslAccountName} that owns the Linux machine, and Windows Subsystem for Linux.");
        var wslStep = await EnsureAccountAndWslAsync(cancellationToken).ConfigureAwait(false);
        if (wslStep is not null)
        {
            return wslStep.Value;
        }

        _ui.WriteLine($"Step 5 of {Steps}: the settings of that account's Linux machine (written, read back and held; a fresh machine starts and stops).");
        if (!await GuardAsync(
                async () =>
                {
                    var session = await _sandbox.BeginAsync(cancellationToken).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                    _ui.WriteLine($"  The settings are exactly: {string.Join(", ", WslSettingsPolicy.Expected(_sandbox.Facts).Select(s => $"{s.Key}={s.Value}"))}. Nothing of yours (your own Linux, your .wslconfig) was touched.");
                },
                "starting the account's Linux machine").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 6 of {Steps}: the Linux base image (a large download the first time).");
        if (!await GuardAsync(() => EnsureImageAsync(cancellationToken), "building the Linux base image").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 7 of {Steps}: the runner program from GitHub (checked against the checksums GitHub publishes).");
        if (!await GuardAsync(async () => await _runners.RefreshAsync(cancellationToken).ConfigureAwait(false), "downloading the runner program").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.LastRunnerCheck = now;
            s.LastRunnerAttempt = now;
        });

        _ui.WriteLine($"Step 8 of {Steps}: your GitHub token, and the repository variables that are already set.");
        var tokenStep = await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!tokenStep)
        {
            return SetupOutcome.Stopped;
        }

        // Only a private repository is served (local CI round 3, red-team M5): jobs of a public one can be started by anybody, so nothing is set up to send them to this PC.
        if (_repositoryPrivate != true)
        {
            _ui.WriteLine(RepositoryGuard.Judge(_repositoryPrivate).Reason);
            _ui.WriteLine("Nothing was set to send checks to this PC. Fix that, then run setup again.");
            _routingMustGo = true;
            return SetupOutcome.Stopped;
        }

        if (!await GuardAsync(() => OfferToRemoveVariablesThisProgramDoesNotServeAsync(cancellationToken), "looking at the repository variables").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 9 of {Steps}: the isolation probe: trying to get out of the Linux machine, with you watching.");
        var probeOutcome = ProbeOutcome.CouldNotRun;
        if (!await GuardAsync(async () => probeOutcome = await _probe.RunAsync(cancellationToken).ConfigureAwait(false), "running the isolation probe").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        if (probeOutcome != ProbeOutcome.Accepted)
        {
            _ui.WriteLine("Nothing was set to send checks to this PC" + probeOutcome switch
            {
                ProbeOutcome.Declined => ".",
                ProbeOutcome.Superseded => ", because another window of this program saved a newer probe result while you were reading this one.",
                _ => ", because the probe could not give a result.",
            });
            _routingMustGo = true;
            return SetupOutcome.Stopped;
        }

        // The probe was accepted for the WSL that is installed right now. Whether the program may update WSL itself is the owner's choice, asked here, and the first update (if any) is a week away, so it cannot
        // change the WSL the probe was accepted for on the first idle round of the loop (local CI round 4, red-team F2 and F5).
        var allowUpdates = AskAboutTheWeeklyUpdate();
        var acceptedAt = _time.GetUtcNow();
        _state.Update(s =>
        {
            s.WslUpdateAllowed = allowUpdates;
            s.LastWslUpdateAttempt ??= acceptedAt;
            if (!allowUpdates)
            {
                s.LastWslUpdateNote = "off: you did not allow the weekly update at set-up (run set-up again to change that), so WSL is not updated by this program";
            }
            else if (s.LastWslUpdateNote is null || s.LastWslUpdateNote.StartsWith("off: ", StringComparison.Ordinal))
            {
                s.LastWslUpdateNote = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"not tried yet: set-up looked at WSL on {acceptedAt:yyyy-MM-dd}, so the first update is due a week later");
            }
        });

        _ui.WriteLine($"Step 10 of {Steps}: starting automatically when you sign in.");
        var location = _installer.CheckInstalledLocation();
        if (location.Count > 0)
        {
            foreach (var problem in location)
            {
                _ui.WriteLine("  " + problem);
            }

            _ui.WriteLine("The sign-in task is only created for a program that no ordinary user can change. Nothing was registered.");
            return SetupOutcome.Stopped;
        }

        if (!await GuardAsync(
                async () =>
                {
                    await _scheduler.InstallAsync(_installer.InstalledPath, cancellationToken).ConfigureAwait(false);
                    await _scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
                    _loopStarted = true;
                },
                "creating the sign-in task").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        _ui.WriteLine($"Step 11 of {Steps}: sending the Linux checks to this PC ({LocalCiDefaults.Variables.Count} Actions variable). It is the last thing set, now that the loop runs.");

        // From the first request that may set the variable until a test check has shown that a job runs on this PC, the variable goes again if set-up ends in any other way: a Ctrl+C in the middle of
        // the request must not leave checks pointing at a PC nothing has proven to serve them (round 4, QA minor 4).
        _routingMustGo = true;
        foreach (var (name, value) in LocalCiDefaults.Variables)
        {
            if (!await GuardAsync(async () => await _api.SetVariableAsync(name, value, cancellationToken).ConfigureAwait(false), $"setting the variable {name}").ConfigureAwait(false))
            {
                return SetupOutcome.Stopped;
            }
        }

        _ui.WriteLine($"Step 12 of {Steps}: a test run. Expected to take a few minutes if the pull request is merged (the test workflow is then on main; UNMEASURED), limit 30 minutes. If it is not merged, GitHub cannot start the test by hand: set-up then waits up to 30 minutes for a push of yours and then for the whole CI (Linux) run, and the log lines below say what to do.");
        var proven = false;
        try
        {
            SelfTestResult? result = null;
            try
            {
                result = await _selfTest.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Whatever stops the test (GitHub refused it, a failure nobody expected) means "not proven": it is shown and the variable is taken away below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Failure($"The test check could not be run: {_redactor.Describe(ex)}");
                _ui.WriteLine("The test could not be run: " + _redactor.Redact(ex.Message));
            }

            if (result is not null)
            {
                _ui.WriteLine(result.Message);
            }

            proven = result?.Outcome == SelfTestOutcome.PassedOnThisPc;
        }
        finally
        {
            if (!proven)
            {
                await TakeTheVariablesAwayAsync().ConfigureAwait(false);
            }
        }

        ProvenOnThisPc = proven;
        if (proven)
        {
            _routingMustGo = false;
            _ui.WriteLine("Done. DBD Local CI now runs the checks of this repository on your PC (only the Linux ones: Windows jobs, the release build, deploys and backups stay on GitHub's machines).");
        }

        return SetupOutcome.Done;
    }

    /// <summary>
    /// Asks whether the program may update WSL itself, once a week, when nobody uses WSL (local CI round 4, red-team F5). WSL is shared with the owner's own Linux, so the default is no and nothing is changed
    /// without a typed yes. The explanation says what each answer costs.
    /// </summary>
    private bool AskAboutTheWeeklyUpdate()
    {
        _ui.WriteLine("One question: may DBD Local CI update WSL itself?");
        _ui.WriteLine("  WSL is shared by the whole PC, so an update changes the WSL you use yourself too, at a time you did not pick.");
        _ui.WriteLine("  If you say yes: once a week, only when no Linux of yours and none of this program is running, the program runs \"wsl --update\". A newer Linux kernel closes holes that a job that broke into its Linux machine would use.");
        _ui.WriteLine("  After each update the program repeats the isolation probe by itself. If the result is worse than the one you accepted, it turns LINUX_RUNNER off and tells you.");
        _ui.WriteLine("  If you say no (the default): the program never changes WSL. You update it yourself, and then run the probe again (choose 5), because the accepted probe holds only for the WSL it was run on.");
        var yes = _ui.Confirm("Update WSL once a week? (yes/no; Enter means no)");
        _ui.WriteLine(yes
            ? "  The weekly WSL update is on. You can change that by running set-up again; the file no-wsl-update in the data folder switches it off too."
            : "  The weekly WSL update is off. You can change that by running set-up again.");
        return yes;
    }

    /// <summary>Everything is set up but nothing proves that a job runs on this PC: the variable goes again, so no check waits for a PC that may not serve it, and the owner is told what is not proven.</summary>
    private async Task TakeTheVariablesAwayAsync()
    {
        var failed = await DeleteVariablesAsync().ConfigureAwait(false);

        // What was taken away does not need taking away again when set-up ends; what could not be is tried once more then.
        _routingMustGo = failed.Count > 0;

        _ui.WriteLine("NOT PROVEN: no test job has run on this PC and passed, so the program cannot say that it serves your checks.");
        if (failed.Count == 0)
        {
            _ui.WriteLine($"I turned {string.Join(", ", LocalCiDefaults.Variables.Keys)} off again: every check still runs on GitHub's machines and nothing waits for this PC. Everything else is set up. Fix the cause (see the message above), then run set-up again; it skips what is done.");
        }
        else
        {
            _ui.WriteLine($"I could not turn {string.Join(", ", failed)} off again. Delete it on GitHub by hand now (Settings, Secrets and variables, Actions, Variables), or checks wait for this PC. Then fix the cause (see the message above) and run set-up again.");
        }
    }

    /// <summary>The names that could not be deleted (empty when all are gone).</summary>
    private async Task<List<string>> DeleteVariablesAsync()
    {
        var failed = new List<string>();
        foreach (var name in LocalCiDefaults.Variables.Keys)
        {
            try
            {
                await _api.DeleteVariableAsync(name, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GitHubApiException ex)
            {
                _log.Failure($"Setup could not take the variable {name} away again: {_redactor.Describe(ex)}");
                failed.Add(name);
            }
        }

        return failed;
    }

    /// <summary>
    /// Set-up stopped for a reason that leaves nothing to serve checks with (it refused the repository or the isolation, or the loop it stopped will not start again): a variable that this program set and
    /// that still holds this program's label is deleted, so no check waits for a runner nobody serves. A variable with another value is the owner's and stays. Needs the saved token.
    /// </summary>
    private Task RemoveRoutingAsync(string why) => new RoutingRemover(_ui, _api, _secrets, _log, _redactor).RemoveAsync(why);

    private bool CheckMachine()
    {
        var build = _machine.WindowsBuild;
        if (build < LocalCiDefaults.MinimumWindowsBuild)
        {
            _ui.WriteLine($"This Windows is too old (build {build}); build {LocalCiDefaults.MinimumWindowsBuild} (version 2004) or newer is needed.");
            return false;
        }

        // A first set-up needs room for the image, its builds and the job machines. A re-run (the base image is already there) needs only what its next action needs, and the image build and
        // the downloads check that room themselves; the program's own files must not make a re-run for a new token impossible.
        var free = _machine.FreeDiskBytes(_paths.ProgramData);
        var needed = File.Exists(_paths.BaseArchive) ? 0 : LocalCiDefaults.MinimumFreeDiskBytes;
        if (free < needed)
        {
            _ui.WriteLine($"Only {StatusReport.Gigabytes(free)} are free on the drive that holds ProgramData; {StatusReport.Gigabytes(needed)} are needed for a first set-up. Free some space, then run setup again.");
            return false;
        }

        if (_machine.VirtualizationEnabled() == false
            && !_ui.Confirm("Virtualization looks switched off in your PC's firmware (BIOS), which WSL 2 needs. If WSL already works on this PC, answer yes to go on. Go on?"))
        {
            _ui.WriteLine("Switch on virtualization (Intel VT-x or AMD-V, often called SVM) in the BIOS, then run setup again.");
            return false;
        }

        _ui.WriteLine($"  Windows build {build}, {StatusReport.Gigabytes(free)} free: fine.");
        return true;
    }

    /// <summary>Copies this program into <c>Program Files\DbdLocalCi</c>, where only Administrators can change it (C1). The scheduled task will run that copy.</summary>
    private async Task InstallProgramAsync(CancellationToken cancellationToken)
    {
        // A different copy may replace the installed one only while the background task (which runs the installed copy) is stopped; step 10 starts it again.
        if (_installer.IsInstalled && !_installer.IsInstalledCopy(_runningProgram))
        {
            _loopStopped |= _scheduler.LoopIsRunning();
            await _scheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        var outcome = await _installer.InstallAsync(_runningProgram, cancellationToken).ConfigureAwait(false);
        var problems = _installer.CheckInstalledLocation();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The installed copy is not in a safe place: " + problems[0]);
        }

        _ui.WriteLine(outcome switch
        {
            InstallOutcome.Installed => $"  Installed {_installer.InstalledPath}. You can delete the copy you started from; run it from anywhere, it installs itself.",
            InstallOutcome.Updated => $"  Updated {_installer.InstalledPath} to this version.",
            _ => $"  {_installer.InstalledPath} is already this version.",
        });
    }

    /// <summary>
    /// The dedicated account, then WSL for that account (the first-run check: <c>wsl --version</c> as that user). A WSL that is not installed is installed from here (the one wsl.exe start under the
    /// administrator's own token), and if it still does not answer for the account the plain reason is printed. Null means "go on".
    /// </summary>
    private async Task<SetupOutcome?> EnsureAccountAndWslAsync(CancellationToken cancellationToken)
    {
        if (!await GuardAsync(
                async () =>
                {
                    await _account.EnsureAsync(cancellationToken).ConfigureAwait(false);
                    _ui.WriteLine($"  {LocalCiDefaults.WslAccountName}: a standard user in no group but Users, hidden from the sign-in screen, with a random password only this program can read.");
                },
                "creating the Windows account for the Linux machine").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        bool usable;
        try
        {
            usable = await _wsl.IsUsableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _log.Failure($"Setup could not ask WSL: {_redactor.Describe(ex)}");
            _ui.WriteLine("Windows would not run WSL for the account " + LocalCiDefaults.WslAccountName + ": " + _redactor.Redact(ex.Message));
            return SetupOutcome.Stopped;
        }

        if (usable)
        {
            _ui.WriteLine($"  WSL answers for the account {LocalCiDefaults.WslAccountName}.");
            return null;
        }

        _ui.WriteLine("WSL does not answer for that account yet. Installing WSL itself now (this can take a few minutes).");
        var restart = false;
        if (!await GuardAsync(async () => restart = await _wslInstaller.InstallWithoutDistributionAsync(cancellationToken).ConfigureAwait(false), "installing WSL").ConfigureAwait(false))
        {
            return SetupOutcome.Stopped;
        }

        if (await _wsl.IsUsableAsync(cancellationToken).ConfigureAwait(false))
        {
            _ui.WriteLine($"  WSL answers for the account {LocalCiDefaults.WslAccountName} now.");
            return null;
        }

        if (restart)
        {
            _ui.WriteLine("Restart your PC, then open DbdLocalCi again.");
            return SetupOutcome.RestartNeeded;
        }

        _ui.WriteLine($"WSL is installed but does not start for the account {LocalCiDefaults.WslAccountName}. This is the one thing that cannot be checked in advance (PENDING-ENV): Windows may need a restart before a new account can use it, or the account's first start failed.");
        _ui.WriteLine("Another possible cause: WSL can be installed for your own Windows account only (for example from the Microsoft Store), and the account " + LocalCiDefaults.WslAccountName + " is another Windows user, so it cannot use that. \"wsl --install --no-distribution\", which this program just ran as administrator, installs WSL for the whole PC; if WSL still answers only for you, run \"wsl --update\" in an administrator window, restart, and run set-up again (PENDING-ENV: not tried on a real PC).");
        _ui.WriteLine("Restart your PC, open DbdLocalCi again and choose 1; if it stops here again, the log (C:\\ProgramData\\DbdLocalCi\\logs, open as administrator) says what wsl.exe answered: send those lines to whoever looks after this program.");
        return SetupOutcome.Stopped;
    }

    /// <summary>Builds the base image unless one is already there that matches the recipe, still matches its checksum and is younger than a week (a re-run for a new token must not rebuild it).</summary>
    private async Task EnsureImageAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_image.IsCurrent(_state.Load(), now))
        {
            _ui.WriteLine("  The Linux base image is already built from this recipe and is less than a week old: keeping it.");
            return;
        }

        await _image.BuildAsync(cancellationToken).ConfigureAwait(false);
        _state.Update(s =>
        {
            s.LastBaseImageBuild = now;
            s.LastBaseImageAttempt = now;
            s.BaseImageRecipe = _image.RecipeId;
        });
    }

    /// <summary>
    /// Variables this program never sets (decision D41): if one is set, jobs that read it wait for a runner this PC does not serve. Says so plainly and offers to delete each. Needs the saved token.
    /// </summary>
    private async Task OfferToRemoveVariablesThisProgramDoesNotServeAsync(CancellationToken cancellationToken)
    {
        foreach (var name in LocalCiDefaults.NotServedVariables)
        {
            var value = await _api.GetVariableAsync(name, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                continue;
            }

            _ui.WriteLine($"  {name} is set on GitHub (to '{value}'). This PC serves only Linux checks (decision D41), so the jobs that read {name} would wait for a runner nobody serves.");
            if (_ui.Confirm($"Delete {name} on GitHub? (yes/no)"))
            {
                await _api.DeleteVariableAsync(name, cancellationToken).ConfigureAwait(false);
                _ui.WriteLine($"  Deleted {name}.");
            }
            else
            {
                _ui.WriteLine($"  Left {name} as it is; the status keeps saying it is a problem until it is deleted.");
            }
        }
    }

    private async Task<bool> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (_secrets.Exists())
        {
            try
            {
                var existing = await _api.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
                RememberExpiry(existing);
                if (existing.TokenExpiresAt is { } ends && ends - _time.GetUtcNow() <= LocalCiDefaults.TokenWarning)
                {
                    await OfferRenewalAsync(ends, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                _ui.WriteLine("  The saved token still works; keeping it.");
                return true;
            }
            catch (GitHubApiException ex) when (ex.IsTransient || ex.StatusCode == 0)
            {
                // No answer is not "the token is bad": the saved token is kept and nothing is asked for.
                _log.Failure($"Setup could not reach GitHub to check the saved token: {_redactor.Describe(ex)}");
                _ui.WriteLine("GitHub could not be reached to check the saved token (" + _redactor.Redact(ex.Message) + "). The saved token was kept. Check the internet connection and run setup again.");
                return false;
            }
            catch (GitHubApiException ex)
            {
                _ui.WriteLine("  The saved token no longer works (" + _redactor.Redact(ex.Message) + "). Please make a new one.");
            }
        }

        foreach (var line in TokenGuide.Steps)
        {
            _ui.WriteLine(line);
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var typed = _ui.ReadSecret("GitHub token: ")?.Trim();
            if (string.IsNullOrEmpty(typed))
            {
                _ui.WriteLine("Nothing was entered.");
                continue;
            }

            var candidate = new SecretText(typed);
            if (typed.Length >= 16)
            {
                _redactor.Register(candidate);
            }

            if (NotFineGrained(typed))
            {
                _ui.WriteLine(NotFineGrainedSentence);
                continue;
            }

            _redactor.Register(candidate);
            _token.TryCandidate(candidate);
            try
            {
                var repo = await _api.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
                _secrets.Save(candidate);
                RememberExpiry(repo);
                _log.Info("A GitHub token was saved (encrypted for this computer, readable by Administrators only).");
                return true;
            }
            catch (GitHubApiException ex)
            {
                _ui.WriteLine("GitHub did not accept that token: " + _redactor.Redact(ex.Message));
            }
            finally
            {
                _token.ClearCandidate();
            }
        }

        _ui.WriteLine("No working token was entered. Run setup again when you have one.");
        return false;
    }

    /// <summary>
    /// The saved token works but ends within the warning time: this is how it is renewed before it ends (making a new one and running set-up again). The owner pastes the new token, or presses Enter to
    /// keep the saved one. A new token is checked before it is saved; one that GitHub refuses is not saved and the saved one stays.
    /// </summary>
    private async Task OfferRenewalAsync(DateTimeOffset ends, CancellationToken cancellationToken)
    {
        _ui.WriteLine($"  The saved token still works but ends on {ends.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}. Make a new one now and paste it, or press Enter to keep the saved one.");
        foreach (var line in TokenGuide.Steps)
        {
            _ui.WriteLine(line);
        }

        var typed = _ui.ReadSecret("New GitHub token (Enter keeps the saved one): ")?.Trim();
        if (string.IsNullOrEmpty(typed))
        {
            _ui.WriteLine("  Kept the saved token. It ends on " + ends.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "; run set-up again with a new one before then.");
            return;
        }

        var candidate = new SecretText(typed);
        if (typed.Length >= 16)
        {
            _redactor.Register(candidate);
        }

        if (NotFineGrained(typed))
        {
            _ui.WriteLine("  " + NotFineGrainedSentence + " The saved token was kept.");
            return;
        }

        _redactor.Register(candidate);
        _token.TryCandidate(candidate);
        try
        {
            var repo = await _api.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
            _secrets.Save(candidate);
            RememberExpiry(repo);
            _log.Info("A new GitHub token was saved (encrypted for this computer, readable by Administrators only).");
        }
        catch (GitHubApiException ex)
        {
            _ui.WriteLine(ex.IsTransient || ex.StatusCode == 0
                ? "  GitHub could not be reached to check that token (" + _redactor.Redact(ex.Message) + "). The saved token was kept; run set-up again with the new one."
                : "  GitHub did not accept that token: " + _redactor.Redact(ex.Message) + " The saved token was kept.");
        }
        finally
        {
            _token.ClearCandidate();
        }
    }

    /// <summary>The only kind of token this program takes: a fine-grained token, which starts with <c>github_pat_</c>. A classic token (<c>ghp_</c>) reaches every repository of the account (red-team L9).</summary>
    public const string FineGrainedPrefix = "github_pat_";

    private const string NotFineGrainedSentence = "That is not a fine-grained token: it must start with github_pat_. A classic token (starting ghp_) reaches every repository of your account, so this program does not take it. Make the token as the steps above say.";

    /// <summary>True for text that is not shaped like a fine-grained token.</summary>
    public static bool NotFineGrained(string typed) => !typed.StartsWith(FineGrainedPrefix, StringComparison.Ordinal);

    private void RememberExpiry(RepoInfo repo)
    {
        _repositoryPrivate = repo.IsPrivate;
        _state.Update(s =>
        {
            s.TokenExpiresAt = repo.TokenExpiresAt;
            s.RepositoryPrivate = repo.IsPrivate;
            s.RepositoryCheckedAt = _time.GetUtcNow();
        });
        _ui.WriteLine(repo.TokenExpiresAt is { } expires
            ? "  Token accepted. It expires on " + expires.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "; you will be warned 7 days before."
            : "  Token accepted. GitHub did not say when it expires.");
    }

    /// <summary>Runs one step; any failure becomes a plain message and a stop, never a crash or a silent skip.</summary>
    private async Task<bool> GuardAsync(Func<Task> step, string what)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure of a step is logged and shown as a plain message; the owner decides what to do, set-up never half-continues.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"Setup stopped while {what}: {_redactor.Describe(ex)}");
            _ui.WriteLine($"Stopped while {what}: {_redactor.Redact(ex.Message)}");
            _ui.WriteLine("Nothing is half-done that matters: fix the cause (see the log) and run setup again.");
            return false;
        }
    }
}
