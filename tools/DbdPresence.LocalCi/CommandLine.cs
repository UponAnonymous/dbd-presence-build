namespace DbdPresence.LocalCi;

public enum LocalCiCommand
{
    Menu,
    Setup,
    Serve,
    Status,
    SelfTest,
    Probe,
    Uninstall,
    Help,
    Unknown,
}

/// <summary>What was typed: the command, whether this copy was started by the administrator prompt of another copy, and what that other copy told it.</summary>
/// <param name="Command">The command.</param>
/// <param name="Elevated">True when another copy started this one through the administrator prompt (<c>--elevated</c>).</param>
/// <param name="OwnerSid">The security identifier of the account that started the administrator prompt (<c>--owner</c>); null when not given.</param>
public sealed record ParsedCommand(LocalCiCommand Command, bool Elevated, string? OwnerSid = null)
{
    public const string Usage = """
        DBD Local CI: runs this repository's Linux GitHub checks on your own PC, each in a throwaway environment.

          DbdLocalCi              a small menu (1 Set up, 2 Status, 3 Test check, 4 Turn off and remove, 5 Isolation probe)
          DbdLocalCi setup        set everything up (asks for the administrator prompt; installs itself in Program Files)
          DbdLocalCi status       say what is running and what happened (asks for the administrator prompt)
          DbdLocalCi selftest     run one test check and say where it ran and whether it passed
          DbdLocalCi probe        try to get out of the Linux machine the way a hostile job would, show what worked, ask whether you accept it
          DbdLocalCi uninstall    turn it off and remove it (asks first)
          DbdLocalCi serve        the background loop (the scheduled task runs this; you do not need to)
        """;

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new ParsedCommand(LocalCiCommand.Menu, false);
        }

        var rest = args.Skip(1).ToList();
        var elevated = rest.Any(a => string.Equals(a, "--elevated", StringComparison.Ordinal));
        var ownerIndex = rest.FindIndex(a => string.Equals(a, "--owner", StringComparison.Ordinal));
        var owner = ownerIndex >= 0 && ownerIndex + 1 < rest.Count ? rest[ownerIndex + 1] : null;
        var command = args[0].ToLowerInvariant() switch
        {
            "setup" => LocalCiCommand.Setup,
            "serve" => LocalCiCommand.Serve,
            "status" => LocalCiCommand.Status,
            "selftest" => LocalCiCommand.SelfTest,
            "probe" => LocalCiCommand.Probe,
            "uninstall" => LocalCiCommand.Uninstall,
            "help" or "--help" or "-h" or "/?" => LocalCiCommand.Help,
            _ => LocalCiCommand.Unknown,
        };
        return new ParsedCommand(command, elevated, owner);
    }

    /// <summary>The words for starting this command again, elevated: the command, <c>--elevated</c>, and (when known) <c>--owner</c>.</summary>
    public string ElevatedArguments(string? ownerSid)
    {
        var name = Command switch
        {
            LocalCiCommand.Setup => "setup",
            LocalCiCommand.Serve => "serve",
            LocalCiCommand.Status => "status",
            LocalCiCommand.SelfTest => "selftest",
            LocalCiCommand.Probe => "probe",
            LocalCiCommand.Uninstall => "uninstall",
            _ => throw new InvalidOperationException("This command is not started again with the administrator prompt."),
        };
        var text = name + " --elevated";
        if (!string.IsNullOrEmpty(ownerSid))
        {
            text += " --owner " + ownerSid;
        }

        return text;
    }
}

public enum RoutingAction
{
    /// <summary>Do the command in this copy.</summary>
    RunHere,

    /// <summary>Start it again with the administrator prompt.</summary>
    RelaunchElevated,

    /// <summary>Start the installed copy with the same words and show its output.</summary>
    HandOverToInstalled,

    /// <summary>Do not do it; say why.</summary>
    Refuse,
}

/// <summary>What to do with a command (security review 2026-10-30, C1 H3): which copy of the program does it, and whether it needs the administrator prompt first.</summary>
/// <param name="Action">What to do.</param>
/// <param name="UseInstalledCopy">For <see cref="RoutingAction.RelaunchElevated"/>: start the installed copy (true) or this one (false).</param>
/// <param name="Message">For <see cref="RoutingAction.Refuse"/>: the reason, in plain words.</param>
public sealed record RoutingDecision(RoutingAction Action, bool UseInstalledCopy = false, string Message = "")
{
    /// <summary>
    /// Everything but the help and the menu needs administrator rights, because the token and the data folder are closed to everyone else. Setup runs in the copy that was started (it installs
    /// itself); the loop runs only from the installed copy; the other commands go to the installed copy when there is one.
    /// </summary>
    public static RoutingDecision For(LocalCiCommand command, bool isAdministrator, bool isInstalledCopy, bool isInstalled)
    {
        switch (command)
        {
            case LocalCiCommand.Help or LocalCiCommand.Unknown or LocalCiCommand.Menu:
                return new RoutingDecision(RoutingAction.RunHere);
            case LocalCiCommand.Serve:
                if (!isAdministrator)
                {
                    return new RoutingDecision(RoutingAction.Refuse, Message: "The loop needs administrator rights (it checks the protected folders and starts the Linux machines of the dedicated account). The scheduled task starts it that way.");
                }

                if (isInstalledCopy)
                {
                    return new RoutingDecision(RoutingAction.RunHere);
                }

                return isInstalled
                    ? new RoutingDecision(RoutingAction.HandOverToInstalled)
                    : new RoutingDecision(RoutingAction.Refuse, Message: "The loop only runs from the installed copy in Program Files. Run setup first.");
            case LocalCiCommand.Setup:
                return isAdministrator ? new RoutingDecision(RoutingAction.RunHere) : new RoutingDecision(RoutingAction.RelaunchElevated, UseInstalledCopy: false);
            default:
                if (!isAdministrator)
                {
                    return new RoutingDecision(RoutingAction.RelaunchElevated, UseInstalledCopy: isInstalled);
                }

                return isInstalled && !isInstalledCopy ? new RoutingDecision(RoutingAction.HandOverToInstalled) : new RoutingDecision(RoutingAction.RunHere);
        }
    }
}

/// <summary>
/// The refusal of the variables that make the .NET runtime load other code, for every command that runs with administrator rights besides the loop: set-up, status, the test check, the probe and
/// uninstall (local CI round 4, red-team F13; before, only the loop refused them). The variables come from the owner's own account, which any program the owner runs can change, and these commands
/// inherit them through the administrator prompt. The check runs first of all in <c>LocalCiApplication</c>, so the copy that would ask for the prompt refuses before it asks, and the elevated copy refuses again.
/// Whether the prompt passes the variables on to the elevated copy is PENDING-ENV; a profiler is loaded before any check of the program can run, so the check tells the owner to remove the variable and does
/// not pretend to stop a profiler that is already in.
/// </summary>
public static class ElevatedStartCheck
{
    /// <summary>True for the commands that run with administrator rights: everything but the help, the menu and an unknown word.</summary>
    public static bool Applies(LocalCiCommand command) => command is not (LocalCiCommand.Help or LocalCiCommand.Menu or LocalCiCommand.Unknown);

    /// <summary>One problem for each variable that is set (not empty), in plain words; none when the command does not run elevated.</summary>
    public static IReadOnlyList<string> Problems(LocalCiCommand command, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!Applies(command))
        {
            return [];
        }

        return [.. ServeStartCheck.CodeLoadingVariables
            .Where(name => !string.IsNullOrEmpty(environment(name)))
            .Select(name => $"The environment variable {name} is set. It makes the .NET runtime load code that is not part of this program, and this command runs with administrator rights, so it will not run. Delete the variable (Settings, System, About, Advanced system settings, Environment Variables), sign in again and start the program again.")];
    }
}

/// <summary>
/// The checks the loop makes before it starts (security review 2026-10-30, C1 H1; local CI round 2, QA M8): it runs from the protected installed copy, its data folder is closed to ordinary users, and
/// no environment variable asks the .NET runtime to load somebody's code into it (the loop runs with administrator rights, and the variables come from the owner's own account, which any program the
/// owner runs can change). Anything else and it refuses to run.
/// </summary>
public static class ServeStartCheck
{
    /// <summary>Variables with which the .NET runtime loads code that is not in the program: startup hooks and profilers (both the old and the new spelling).</summary>
    public static IReadOnlyList<string> CodeLoadingVariables { get; } =
    [
        "DOTNET_STARTUP_HOOKS",
        "CORECLR_ENABLE_PROFILING",
        "CORECLR_PROFILER",
        "CORECLR_PROFILER_PATH",
        "CORECLR_PROFILER_PATH_32",
        "CORECLR_PROFILER_PATH_64",
        "COR_ENABLE_PROFILING",
        "COR_PROFILER",
        "COR_PROFILER_PATH",
        "COR_PROFILER_PATH_32",
        "COR_PROFILER_PATH_64",
    ];

    public static IReadOnlyList<string> Problems(IProgramInstaller installer, string runningProgram, IProtectedStorage storage, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(storage);
        var problems = new List<string>();
        if (!installer.IsInstalledCopy(runningProgram))
        {
            problems.Add($"The loop runs only from {installer.InstalledPath}, not from {runningProgram}.");
        }

        problems.AddRange(installer.CheckInstalledLocation());
        problems.AddRange(storage.CheckDataFolder());
        if (environment is not null)
        {
            foreach (var name in CodeLoadingVariables.Where(n => !string.IsNullOrEmpty(environment(n))))
            {
                problems.Add($"The environment variable {name} is set. It makes the .NET runtime load code that is not part of this program, and the loop runs with administrator rights, so it will not start. Delete the variable (Settings, System, About, Advanced system settings, Environment Variables) and sign in again.");
            }
        }

        return problems;
    }
}
