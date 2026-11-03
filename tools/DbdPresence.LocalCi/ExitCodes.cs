namespace DbdPresence.LocalCi;

/// <summary>
/// The exit code each command ends with, kept apart from the Windows start-up code so that it is tested (local CI round 3, QA M-4). A command succeeds with 0 only when it did what it is for and
/// proved it: set-up only after a test job ran on this PC and passed, uninstall only when nothing is left for the owner to do, the test check only when its job ran on this PC and passed.
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The command did not do what it is for (or could not prove that it did), or the owner stopped it.</summary>
    public const int Failed = 1;

    /// <summary>The words typed were not a command of this program.</summary>
    public const int Usage = 2;

    /// <summary>Set-up: success only when it ended with a test job that ran on this PC and passed. A stop, a restart request and an unproven end all exit with an error.</summary>
    public static int ForSetup(SetupOutcome outcome, bool provenOnThisPc) => outcome == SetupOutcome.Done && provenOnThisPc ? Ok : Failed;

    /// <summary>Uninstall: success only when everything was removed and nothing is left to do by hand. The owner answering "no" is not a success either.</summary>
    public static int ForUninstall(bool completed) => completed ? Ok : Failed;

    /// <summary>The test check: success only when the job ran on this PC and passed. No result at all (GitHub refused the start, a failure nobody expected) is an error.</summary>
    public static int ForSelfTest(SelfTestOutcome? outcome) => outcome is SelfTestOutcome.PassedOnThisPc ? Ok : Failed;

    /// <summary>The isolation probe: success only when the owner accepted what it found, so that checks may be sent to this PC.</summary>
    public static int ForProbe(bool accepted) => accepted ? Ok : Failed;

    /// <summary>A command line that is not a command: the help text exits 0, anything else the program does not know exits with <see cref="Usage"/>.</summary>
    public static int ForUnstarted(LocalCiCommand command) => command == LocalCiCommand.Help ? Ok : Usage;
}
