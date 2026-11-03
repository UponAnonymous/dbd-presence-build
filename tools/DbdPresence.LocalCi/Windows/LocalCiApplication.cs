using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DbdPresence.LocalCi.Windows;

/// <summary>Wires the real Windows pieces to the platform-neutral core and runs one command. PENDING-ENV: not run on a Windows PC yet.</summary>
[SupportedOSPlatform("windows")]
internal static class LocalCiApplication
{
    private const int Ok = ExitCodes.Ok;
    private const int Failed = ExitCodes.Failed;

    /// <summary>The answer of GitHub is small; this bounds what the API client buffers.</summary>
    private const long ApiBufferBytes = GitHubRestClient.MaximumAnswerBytes;

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = ParsedCommand.Parse(args);
        var ui = new ConsoleUi();

        // A command that runs with administrator rights does nothing while the environment asks the runtime to load other code (round 4, F13). First of all, so that the copy that would ask for the
        // administrator prompt refuses before it asks.
        var environmentProblems = ElevatedStartCheck.Problems(parsed.Command, Environment.GetEnvironmentVariable);
        if (environmentProblems.Count > 0)
        {
            foreach (var problem in environmentProblems)
            {
                ui.WriteLine(problem);
            }

            PauseIfElevated(ui, parsed);
            return Failed;
        }

        var running = Environment.ProcessPath ?? throw new InvalidOperationException("This program cannot find its own file.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var paths = new LocalCiPaths(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var redactor = new Redactor();
        var time = TimeProvider.System;
        // The commands that take minutes (set-up, the test check, the probe) show their log lines on the console as they happen, so the window is never silent (local CI round 4, QA M-1).
        var log = new FileCiLog(paths.LogDirectory, time, redactor, parsed.Command is LocalCiCommand.Setup or LocalCiCommand.SelfTest or LocalCiCommand.Probe ? Console.Out : null);
        var probe = new WindowsFileSecurityProbe();
        var installer = new WindowsProgramInstaller(programFiles, probe, log);
        var machine = new WindowsMachine(log);
        ProgramPaths.Approve(running);
        ProcessRunner.Configure(paths.TemporaryDirectory);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            var route = RoutingDecision.For(parsed.Command, machine.IsAdministrator(), installer.IsInstalledCopy(running), installer.IsInstalled);
            switch (route.Action)
            {
                case RoutingAction.Refuse:
                    ui.WriteLine(route.Message);
                    return Failed;
                case RoutingAction.RelaunchElevated:
                    return machine.RelaunchElevated(route.UseInstalledCopy ? installer.InstalledPath : running, parsed.ElevatedArguments(CurrentUserSid()));
                case RoutingAction.HandOverToInstalled:
                    return HandOver.Run(installer.InstalledPath, args);
            }

            if (parsed.Elevated && parsed.OwnerSid is { } owner && !string.Equals(owner, CurrentUserSid(), StringComparison.Ordinal))
            {
                ui.WriteLine("The administrator prompt was answered with another account's password. DBD Local CI belongs to your own Windows account: sign in with an administrator account and run it again.");
                PauseIfElevated(ui, parsed);
                return Failed;
            }

            using var apiClient = NewClient(TimeSpan.FromSeconds(60), ApiBufferBytes);
            using var downloadClient = NewClient(TimeSpan.FromHours(2), 2147483647L);
            var storage = new WindowsProtectedStorage(paths, probe, log, time);
            var store = new DpapiSecretStore(paths.TokenFile, paths.TokenEntropyFile, "DBD Local CI token", "The saved token", storage, log);
            var token = new SecretSource(store, redactor);
            var api = new GitHubRestClient(apiClient, token.Current, LocalCiDefaults.RepositoryOwner, LocalCiDefaults.RepositoryName);
            var downloads = new HttpDownloads(downloadClient);
            var passwordStore = new DpapiSecretStore(paths.WslAccountPasswordFile, paths.WslAccountEntropyFile, "DBD Local CI account password", "The saved password of the Windows account " + LocalCiDefaults.WslAccountName, storage, log);
            var account = new WindowsWslAccount(paths, passwordStore, probe, log, redactor, time);
            var wsl = new WslClient(new LogonAccountProcess(account, log, redactor), ProgramPaths.Wsl, time, log);
            var scheduler = new TaskSchedulerService(log, probe, paths);
            var state = new StateStore(paths.StateFile, redactor);
            var marker = new ProbeMarkerFile(PublicFolders.Folders(), log);
            var residue = new ResidueRecorder(state, marker, log);
            var sandbox = new WslSandbox(wsl, account, new WslSettingsFile(account), HostFacts(), new StateMachineRecord(state, time), log, redactor, time, new FileSessionLock(paths.SessionLockFile), residue);
            var retry = new Retry(time, redactor, log);
            var integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
            var disk = new SystemDiskSpace();
            var runners = new RunnerCache(api, downloads, paths, log, retry, integrity, storage, disk);
            var image = new BaseImageBuilder(downloads, wsl, sandbox, paths, log, retry, integrity, disk, residue, time);
            Func<string> currentRecipe = () => IsolationProbe.RecipeIdFor(sandbox.Facts, image.RecipeId);
            var probeGate = new ProbeGate(state, currentRecipe);
            var host = new LinuxJobHost(wsl, sandbox, paths, log, redactor, integrity, storage, disk, residue, time, probeGate);
            var selfTest = new SelfTest(api, state, time, log, redactor);
            var isolation = new IsolationProbe(wsl, sandbox, paths, integrity, marker, new AccountWritableLocations(new WindowsLocationSource(), probe, account, log), residue, log, redactor, time);
            var review = new ProbeReview(isolation, state, ui, () => IsolationProbe.RecipeIdFor(sandbox.Facts, image.RecipeId), time, log);

            switch (parsed.Command)
            {
                case LocalCiCommand.Menu:
                    return Menu(ui, machine, running, installer);
                case LocalCiCommand.Setup:
                    var setup = new SetupWizard(
                        ui, machine, wsl, new LimitedWslInstaller(new WslInstaller(log), WslLimits.Install, time), account, sandbox, storage, installer, image, runners, store, token, api, scheduler, selfTest, review, state, paths, time, redactor, log, running);
                    var outcome = await setup.RunAsync(cancel.Token).ConfigureAwait(false);
                    PauseIfElevated(ui, parsed);

                    // Only a run that ended with a test job that ran on this PC and passed is a success; every other end says what is not proven and exits with an error code.
                    return ExitCodes.ForSetup(outcome, setup.ProvenOnThisPc);
                case LocalCiCommand.Serve:
                    return await ServeAsync(api, host, runners, image, storage, installer, running, state, paths, redactor, log, time, retry, new ServeParts(isolation, wsl, store, probeGate, currentRecipe), cancel.Token).ConfigureAwait(false);
                case LocalCiCommand.Status:
                    return await StatusAsync(ui, store, scheduler, api, state, paths, time, redactor, installer, storage, account, () => IsolationProbe.RecipeIdFor(sandbox.Facts, image.RecipeId), parsed, cancel.Token).ConfigureAwait(false);
                case LocalCiCommand.SelfTest:
                    var selfTestExit = await SelfTestAsync(ui, selfTest, redactor, cancel.Token).ConfigureAwait(false);
                    PauseIfElevated(ui, parsed);
                    return selfTestExit;
                case LocalCiCommand.Probe:
                    var probeCommand = new ProbeCommand(review, new RoutingRemover(ui, api, store, log, redactor), ui, scheduler, api, log, redactor);
                    var probeExit = await probeCommand.RunAsync(cancel.Token).ConfigureAwait(false);
                    PauseIfElevated(ui, parsed);
                    return probeExit;
                case LocalCiCommand.Uninstall:
                    var uninstaller = new Uninstaller(ui, api, scheduler, host, wsl, account, sandbox, store, installer, paths, log, redactor);
                    _ = await uninstaller.RunAsync(cancel.Token).ConfigureAwait(false);
                    PauseIfElevated(ui, parsed);

                    // Everything removed is the only success: a "no" to the question, or any step left for the owner, exits with an error code (local CI round 2, QA B3).
                    return ExitCodes.ForUninstall(uninstaller.Completed);
                default:
                    ui.WriteLine(ParsedCommand.Usage);
                    return ExitCodes.ForUnstarted(parsed.Command);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // hygiene: allow-swallow the owner pressed Ctrl+C; saying so and exiting is the whole reaction
            ui.WriteLine("Stopped.");
            return Failed;
        }
#pragma warning disable CA1031 // The last line of defence: any failure becomes a plain message, a log line and exit code 1.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Failure("The program stopped: " + redactor.Describe(ex));
            ui.WriteLine("Something went wrong: " + redactor.Redact(ex.Message));
            ui.WriteLine("The log is in " + paths.LogDirectory + " (open that folder as an administrator).");
            PauseIfElevated(ui, parsed);
            return Failed;
        }
    }

    /// <summary>The parts of the isolation probe the loop needs besides the job host: the probe itself, WSL, the saved token, the gate and the name of the current setup.</summary>
    private sealed record ServeParts(IsolationProbe Isolation, IWsl Wsl, ISecretStore Store, ProbeGate Gate, Func<string> CurrentRecipe);

    /// <summary>What this PC has, for the memory and processor limits of the Linux machine.</summary>
    private static WslHostFacts HostFacts() => new(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount);

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? string.Empty;
    }

    private static HttpClient NewClient(TimeSpan timeout, long bufferBytes) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = timeout, MaxResponseContentBufferSize = bufferBytes };

    private static void PauseIfElevated(ConsoleUi ui, ParsedCommand parsed)
    {
        if (parsed.Elevated)
        {
            ui.WriteLine("Press Enter to close this window.");
            _ = ui.ReadLine();
        }
    }

    private static async Task<int> ServeAsync(
        GitHubRestClient api,
        IJobHost host,
        RunnerCache runners,
        BaseImageBuilder image,
        IProtectedStorage storage,
        IProgramInstaller installer,
        string running,
        StateStore state,
        LocalCiPaths paths,
        Redactor redactor,
        FileCiLog log,
        TimeProvider time,
        Retry retry,
        ServeParts parts,
        CancellationToken cancellationToken)
    {
        // The loop runs only from the protected installed copy and only when the data folder is closed to ordinary users; otherwise it says why and does nothing.
        var problems = ServeStartCheck.Problems(installer, running, storage, Environment.GetEnvironmentVariable);
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                log.Failure("The loop will not start: " + problem);
            }

            Console.Error.WriteLine("The loop will not start: " + problems[0]);
            return Failed;
        }

        // A copy that is just ending may still hold the lock for a moment, and so may a status check: try for a few seconds before concluding that another loop is running.
        IDisposable? held = null;
        for (var attempt = 1; attempt <= 10 && held is null; attempt++)
        {
            held = ServeLock.TryAcquire(paths.LockFile);
            if (held is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
        }

        if (held is null)
        {
            log.Info("Another copy of the loop is already running; this one exits.");
            return Ok;
        }

        using var lockHolder = held;

        var console = NativeMethods.GetConsoleWindow();
        if (console != nint.Zero)
        {
            _ = NativeMethods.ShowWindow(console, 0);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Info("The program is exiting.");
        var gate = new JobGate(new WindowsProcessList(), new GameNames(paths.GameNamesFile, log).Current);
        var notifier = new MessageBoxNotifier(log);
        var wslUpdate = new WeeklyWslUpdate(new LimitedWslInstaller(new WslInstaller(log), WslLimits.Install, time), new WindowsProcessList(), () => WslUpdatePolicy.OffBecause(state.Load().WslUpdateAllowed, File.Exists(paths.WslUpdateOffFile)), state, time, log, redactor);
        var notices = new OwnerNotices(notifier, state, time);

        // After its own WSL update the loop repeats the isolation probe by itself (no console: what it would say goes to the log) and keeps serving only if nothing got worse (round 4, F5).
        var renewal = new ProbeRenewal(parts.Isolation, parts.Wsl, state, parts.CurrentRecipe, new RoutingRemover(new LogConsoleUi(log), api, parts.Store, log, redactor), notices, gate, log, redactor, time);
        var maintenance = new Maintenance(runners, image, api, state, gate, notifier, time, log, redactor, wslUpdate: wslUpdate, renewal: renewal, probeGate: parts.Gate);
        var service = new LocalCiService(api, host, maintenance, gate, state, log, redactor, time, retry, new RepositoryGuard(api, state, time, log, redactor), notices);
        await service.RunAsync(cancellationToken).ConfigureAwait(false);
        return Ok;
    }

    private static async Task<int> StatusAsync(
        ConsoleUi ui,
        ISecretStore store,
        TaskSchedulerService scheduler,
        GitHubRestClient api,
        StateStore state,
        LocalCiPaths paths,
        TimeProvider time,
        Redactor redactor,
        IProgramInstaller installer,
        IProtectedStorage storage,
        IWslAccount account,
        Func<string> probeRecipe,
        ParsedCommand parsed,
        CancellationToken cancellationToken)
    {
        var facts = await new StatusCollector(store, scheduler, api, state, paths, time, redactor, installer, storage, account, probeRecipe, ThisBuild()).CollectAsync(cancellationToken).ConfigureAwait(false);
        ui.WriteLine(StatusReport.Build(facts));
        PauseIfElevated(ui, parsed);
        return Ok;
    }

    /// <summary>The .NET runtime inside the file and the day the program was built (round 3, L12).</summary>
    private static BuildFacts ThisBuild()
    {
        var recorded = typeof(LocalCiApplication).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => string.Equals(a.Key, "BuildDate", StringComparison.Ordinal))?.Value;
        DateTimeOffset? date = DateTimeOffset.TryParseExact(recorded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate) ? parsedDate : null;
        return new BuildFacts(RuntimeInformation.FrameworkDescription, date);
    }

    private static async Task<int> SelfTestAsync(ConsoleUi ui, SelfTest selfTest, Redactor redactor, CancellationToken cancellationToken)
    {
        ui.WriteLine("Starting one test check. If the pull request is merged (so the test workflow is on main) it is expected to take a few minutes (UNMEASURED) and has a limit of 30 minutes.");
        ui.WriteLine("If it is not merged, GitHub cannot start the test by hand: this then waits up to 30 minutes for a push of yours, and then for the whole CI (Linux) run (15 minutes plus 30 for each of its jobs). Merge the pull request first to avoid that. What happens is shown below as it happens.");
        try
        {
            var result = await selfTest.RunAsync(cancellationToken).ConfigureAwait(false);
            ui.WriteLine(result.Message);
            return ExitCodes.ForSelfTest(result.Outcome);
        }
        catch (GitHubApiException ex)
        {
            ui.WriteLine("The test could not be run: " + redactor.Redact(ex.Message));
            return ExitCodes.ForSelfTest(null);
        }
    }

    /// <summary>The menu runs without administrator rights and shows nothing from the data folder; each choice starts the right copy with the administrator prompt.</summary>
    private static int Menu(ConsoleUi ui, WindowsMachine machine, string running, WindowsProgramInstaller installer)
    {
        var owner = CurrentUserSid();
        var target = installer.IsInstalled ? installer.InstalledPath : running;
        while (true)
        {
            ui.WriteLine(string.Empty);
            ui.WriteLine("DBD Local CI");
            ui.WriteLine("  1  Set up");
            ui.WriteLine("  2  Status");
            ui.WriteLine("  3  Test check");
            ui.WriteLine("  4  Turn off and remove");
            ui.WriteLine("  5  Isolation probe (try to get out of the Linux machine; run it again after WSL is updated)");
            ui.WriteLine("  Enter  Close");
            Console.Write("Choose: ");
            var choice = ui.ReadLine()?.Trim();
            switch (choice)
            {
                case "1":
                    _ = machine.RelaunchElevated(running, new ParsedCommand(LocalCiCommand.Setup, true).ElevatedArguments(owner));
                    break;
                case "2":
                    _ = machine.RelaunchElevated(target, new ParsedCommand(LocalCiCommand.Status, true).ElevatedArguments(owner));
                    break;
                case "3":
                    _ = machine.RelaunchElevated(target, new ParsedCommand(LocalCiCommand.SelfTest, true).ElevatedArguments(owner));
                    break;
                case "4":
                    _ = machine.RelaunchElevated(target, new ParsedCommand(LocalCiCommand.Uninstall, true).ElevatedArguments(owner));
                    break;
                case "5":
                    _ = machine.RelaunchElevated(target, new ParsedCommand(LocalCiCommand.Probe, true).ElevatedArguments(owner));
                    break;
                case null or "":
                    return Ok;
                default:
                    ui.WriteLine("Please type 1, 2, 3, 4, 5 or press Enter.");
                    break;
            }
        }
    }
}
