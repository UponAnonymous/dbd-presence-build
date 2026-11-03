using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The Scheduled Task "DBD Local CI": at the owner's sign-in, as the owner, with highest privileges, hidden, restarted on failure. It is registered only for a program that no ordinary user can change
/// (security review 2026-10-30, C1): the program's folder and every folder above it are checked first. The definition file is written in the data folder's protected temporary folder, not in the
/// owner's, so nothing can change it between writing and registering. PENDING-ENV: not run on a Windows PC yet.
/// </summary>
/// <remarks>
/// Task Scheduler principal (learn.microsoft.com/windows/win32/taskschd/taskschedulerschema-logontype-principaltype-element and ...-runlevel-principaltype-element, read 2026-10-29): LogonType
/// InteractiveToken means the user "must already be logged on. The task will be run only in an existing interactive session", and RunLevel HighestAvailable runs the task elevated for an administrator
/// (the value is ignored for the built-in Administrator account and when UAC is off, where tasks already run in full).
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TaskSchedulerService : IScheduler
{
    private static string Schtasks => ProgramPaths.Schtasks;

    private readonly ICiLog _log;
    private readonly IFileSecurityProbe _probe;
    private readonly LocalCiPaths _paths;

    public TaskSchedulerService(ICiLog log, IFileSecurityProbe probe, LocalCiPaths paths)
    {
        _log = log;
        _probe = probe;
        _paths = paths;
    }

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Schtasks, ["/Query", "/TN", LocalCiDefaults.TaskName], null, null, null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public bool LoopIsRunning() => ServeLock.IsHeld(_paths.LockFile);

    public async Task InstallAsync(string programPath, CancellationToken cancellationToken)
    {
        var problems = new PathTrust(_probe).CheckProgramLocation(programPath);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Refusing to register {programPath} to run with administrator rights: {problems[0]}");
        }

        using var identity = WindowsIdentity.GetCurrent();
        DataFolder.RequireExists(_paths.TemporaryDirectory);
        var definition = Path.Combine(_paths.TemporaryDirectory, $"task-{Guid.NewGuid():N}.xml");
        ProtectedCreation.WriteNewFile(definition, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(TaskDefinition.Build(programPath, identity.Name))).ToArray());
        try
        {
            var result = await ProcessRunner.RunAsync(Schtasks, ["/Create", "/TN", LocalCiDefaults.TaskName, "/XML", definition, "/F"], null, null, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Windows would not create the scheduled task (exit code {result.ExitCode}): {result.Output}");
            }
        }
        finally
        {
            SafeTree.Delete(definition);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Schtasks, ["/Run", "/TN", LocalCiDefaults.TaskName], null, null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows would not start the scheduled task (exit code {result.ExitCode}): {result.Output}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Schtasks, ["/End", "/TN", LocalCiDefaults.TaskName], null, null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            _log.Info("The scheduled task was not running (or does not exist).");
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var result = await ProcessRunner.RunAsync(Schtasks, ["/Delete", "/TN", LocalCiDefaults.TaskName, "/F"], null, null, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Windows would not delete the scheduled task (exit code {result.ExitCode}): {result.Output}");
        }
    }
}
