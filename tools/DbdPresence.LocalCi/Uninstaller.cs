namespace DbdPresence.LocalCi;

/// <summary>
/// Turns the program off and removes it (FR-CI-08): the repository variable first (checks go back to GitHub's machines), then the task, the runners, the leftover Linux machines, every Linux
/// machine of the dedicated account, the account itself (its programs, profile, sign-in entry, password record and work folder), the caches, the token, and the installed copy of the program.
/// </summary>
/// <remarks>
/// If GitHub does not let the program delete the variable (an expired or refused token, say) nothing else is removed: the token and the program stay so that the uninstall can be run again, and
/// the owner is told exactly what to delete by hand (local CI round 2, QA B3). After the owner confirms that it is done by hand the rest goes. Only exact names are ever touched: this program's
/// runner names, the variable it set, the Linux machines of the dedicated account (all of them: nobody else can have any in that account), and the account <see cref="LocalCiDefaults.WslAccountName"/>
/// itself, which is removed here deliberately and by no sweep.
/// </remarks>
public sealed class Uninstaller
{
    private static readonly string[] s_keptInDataFolder = ["logs"];
    private static readonly string[] s_tokenFiles = ["token.bin", "token.entropy"];

    private readonly IConsoleUi _ui;
    private readonly IGitHubApi _api;
    private readonly IScheduler _scheduler;
    private readonly IJobHost _host;
    private readonly IWsl _wsl;
    private readonly IWslAccount _account;
    private readonly WslSandbox _sandbox;
    private readonly ISecretStore _secrets;
    private readonly IProgramInstaller _installer;
    private readonly LocalCiPaths _paths;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly List<string> _failed = [];

    public Uninstaller(
        IConsoleUi ui,
        IGitHubApi api,
        IScheduler scheduler,
        IJobHost host,
        IWsl wsl,
        IWslAccount account,
        WslSandbox sandbox,
        ISecretStore secrets,
        IProgramInstaller installer,
        LocalCiPaths paths,
        ICiLog log,
        Redactor redactor)
    {
        _ui = ui;
        _api = api;
        _scheduler = scheduler;
        _host = host;
        _wsl = wsl;
        _account = account;
        _sandbox = sandbox;
        _secrets = secrets;
        _installer = installer;
        _paths = paths;
        _log = log;
        _redactor = redactor;
    }

    /// <summary>True only when everything was removed and nothing is left for the owner to do by hand. The program exits with an error code otherwise.</summary>
    public bool Completed { get; private set; }

    /// <summary>
    /// Runs the uninstall after a yes/no question. Returns false when the owner said no (nothing was changed); true when the question was answered yes and the steps were run, whether or not
    /// they all worked (<see cref="Completed"/> says that).
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        Completed = false;
        _failed.Clear();
        if (!_ui.Confirm("Turn off DBD Local CI and remove it? Checks will run on GitHub's machines again. (yes/no)"))
        {
            _ui.WriteLine("Nothing was changed.");
            return false;
        }

        var manual = new List<string>();
        if (_secrets.Exists())
        {
            var undone = await RemoveVariablesAsync(cancellationToken).ConfigureAwait(false);
            if (undone.Count > 0)
            {
                var list = string.Join(", ", undone);
                _ui.WriteLine($"NOT DONE: the repository variable{(undone.Count == 1 ? string.Empty : "s")} {list} could not be deleted from here, so checks are still sent to this PC. Nothing else was removed, so you can run uninstall again.");
                _ui.WriteLine($"Delete {(undone.Count == 1 ? "it" : "them")} on GitHub by hand now: Settings, Secrets and variables, Actions, Variables, then the bin icon next to {list}. (A token that ended is the usual cause: make a new one with set-up and run uninstall again, or delete by hand.)");
                if (!_ui.Confirm("Have you deleted them by hand, so the rest can be removed from this PC now? (yes/no)"))
                {
                    _ui.WriteLine("The token and the program were kept. Nothing else was changed.");
                    return true;
                }

                // The owner's word is not proof, and the token this program would ask with is the one GitHub refused (the usual reason the variable could not be deleted): the rest is removed, but the end
                // says honestly that the variable was not checked, and the program exits with an error (local CI round 3, QA M-2).
                manual.Add($"check on GitHub that {list} {(undone.Count == 1 ? "is" : "are")} deleted (you said you deleted {(undone.Count == 1 ? "it" : "them")} by hand; this program could not delete {(undone.Count == 1 ? "it" : "them")} itself, so it cannot confirm that {(undone.Count == 1 ? "it is" : "they are")} gone)");
            }
        }
        else
        {
            var names = string.Join(", ", LocalCiDefaults.Variables.Keys);
            _ui.WriteLine("No GitHub token is saved, so the repository variables cannot be removed from here. Delete " + names + " on GitHub by hand (Settings, Secrets and variables, Actions, Variables).");
            manual.Add($"delete {names} on GitHub by hand");
        }

        await TryAsync("stop the background task", () => _scheduler.StopAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync("remove the background task", () => _scheduler.RemoveAsync(cancellationToken)).ConfigureAwait(false);

        if (_secrets.Exists())
        {
            await TryAsync("remove the runners from GitHub", () => RemoveRunnersAsync(cancellationToken)).ConfigureAwait(false);
        }

        await TryAsync("remove leftover Linux environments", () => _host.CleanupLeftoversAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync("remove every Linux machine of the account " + LocalCiDefaults.WslAccountName, () => RemoveAllDistributionsAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync("remove the Windows account " + LocalCiDefaults.WslAccountName + " with its programs, profile and work folder", () => _account.RemoveAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync("delete the downloads and caches", () =>
        {
            DeleteDataFolderContents(keepToken: true);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await TryAsync("delete the saved token", () =>
        {
            _secrets.Delete();
            DeleteDataFolderContents(keepToken: false);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        var note = string.Empty;
        await TryAsync("remove the installed program", () =>
        {
            note = _installer.Remove();
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        var aside = FoldersMovedAside();
        if (_failed.Count == 0 && manual.Count == 0)
        {
            Completed = true;
            _ui.WriteLine("DBD Local CI is turned off and removed. " + note + " The log files stay in " + _paths.LogDirectory + " (open that folder as an administrator).");
        }
        else
        {
            _ui.WriteLine("NOT FULLY DONE. " + note + " Left for you: " + string.Join("; ", [.. _failed.Select(f => "could not " + f), .. manual]) + ". The log files stay in " + _paths.LogDirectory + " (open that folder as an administrator).");
        }

        if (aside.Count > 0)
        {
            _ui.WriteLine("Folders the program moved aside earlier because they were not closed to ordinary users (look at them, then delete them yourself): " + string.Join(", ", aside));
        }

        return true;
    }

    /// <summary>
    /// Deletes the repository variable this program set, but only when it still holds this program's label (a variable somebody pointed elsewhere is not this program's to delete; an absent one is
    /// already gone). Variables this program never set are not touched. Returns the names that could not be deleted.
    /// </summary>
    private async Task<List<string>> RemoveVariablesAsync(CancellationToken cancellationToken)
    {
        var undone = new List<string>();
        foreach (var (name, label) in LocalCiDefaults.Variables)
        {
            try
            {
                var value = await _api.GetVariableAsync(name, cancellationToken).ConfigureAwait(false);
                if (value is not null && !string.Equals(value, label, StringComparison.Ordinal))
                {
                    _ui.WriteLine($"{name} is set to '{value}', not to this program's '{label}', so it was left alone.");
                    continue;
                }

                await _api.DeleteVariableAsync(name, cancellationToken).ConfigureAwait(false);
                _log.Info($"Uninstall: the repository variable {name}: deleted.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Whatever stopped GitHub's answer (a refusal, a network failure, a damaged answer) is said aloud and leaves the variable on the list of things to delete by hand.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Failure($"Uninstall could not delete the repository variable {name}: {_redactor.Describe(ex)}");
                _ui.WriteLine($"Could not delete the repository variable {name}: {_redactor.Redact(ex.Message)}");
                undone.Add(name);
            }
        }

        return undone;
    }

    /// <summary>
    /// Every distribution of the dedicated account, whatever its name: the account is this program's alone, so nothing in it is anybody else's. They are stopped and unregistered one by one (WSL
    /// deletes each disk file), then the list is read again and must be empty, and the account's machine is shut down.
    /// </summary>
    private async Task RemoveAllDistributionsAsync(CancellationToken cancellationToken)
    {
        var session = await _sandbox.BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var present = await _wsl.ListDistributionsAsync(cancellationToken).ConfigureAwait(false);
            var stuck = new List<string>();
            foreach (var name in present)
            {
                if (!await _sandbox.RemoveDistributionAsync(name).ConfigureAwait(false))
                {
                    stuck.Add(name);
                }
            }

            if (stuck.Count > 0)
            {
                throw new InvalidOperationException("these Linux machines of the account could not be removed: " + string.Join(", ", stuck));
            }
        }
    }

    /// <summary>
    /// Deletes what is in the data folder except the log folder (and, when asked, the token files); the folder itself goes when nothing is left in it.
    /// Nothing is followed through a link.
    /// </summary>
    private void DeleteDataFolderContents(bool keepToken)
    {
        if (!Directory.Exists(_paths.Machine))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.Machine))
        {
            var name = Path.GetFileName(entry);
            if (s_keptInDataFolder.Contains(name, StringComparer.OrdinalIgnoreCase) || (keepToken && s_tokenFiles.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            SafeTree.Delete(entry);
        }

        if (!Directory.EnumerateFileSystemEntries(_paths.Machine).Any())
        {
            Directory.Delete(_paths.Machine, recursive: false);
        }
    }

    /// <summary>Removes the runners this program registered (exact names only, so an owner's own runner is never removed).</summary>
    private async Task RemoveRunnersAsync(CancellationToken cancellationToken)
    {
        var runners = await _api.ListRunnersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var runner in runners.Where(r => LocalCiDefaults.IsRunnerName(r.Name)))
        {
            await _api.DeleteRunnerAsync(runner.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<string> FoldersMovedAside()
    {
        try
        {
            return Directory.Exists(_paths.ProgramData)
                ? [.. Directory.EnumerateDirectories(_paths.ProgramData, "DbdLocalCi*.untrusted-*").Select(Path.GetFileName).OfType<string>()]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Uninstall could not look for folders that were moved aside: " + _redactor.Describe(ex));
            return [];
        }
    }

    /// <summary>One uninstall step. A failure of any kind is said out loud and kept for the end, and the next step still runs, so a half-working PC does not block the rest.</summary>
    private async Task TryAsync(string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            _log.Info("Uninstall: " + what + ": done.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure of one step (a program that cannot be started, a damaged answer, a timeout) must not stop the steps after it; it is logged, said aloud and listed at the end.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Failure($"Uninstall could not {what}: {_redactor.Describe(ex)}");
            _ui.WriteLine($"Could not {what}: {_redactor.Redact(ex.Message)}");
            _failed.Add(what);
        }
    }
}
