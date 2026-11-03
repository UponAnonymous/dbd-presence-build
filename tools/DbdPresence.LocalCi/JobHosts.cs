namespace DbdPresence.LocalCi;

/// <summary>One runner to start: its name, its (secret) just-in-time configuration and the cached runner package.</summary>
public sealed class RunnerLaunch
{
    public RunnerLaunch(string name, SecretText jitConfig, string packagePath)
    {
        Name = name;
        JitConfig = jitConfig;
        PackagePath = packagePath;
    }

    public string Name { get; }

    public SecretText JitConfig { get; }

    public string PackagePath { get; }

    public override string ToString() => Name;
}

/// <summary>A job's environment is not as isolated as it must be, so the runner was not started.</summary>
public sealed class JobEnvironmentException : InvalidOperationException
{
    public JobEnvironmentException(string message)
        : base(message)
    {
    }

    public JobEnvironmentException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A job was stopped because the drive that holds the Linux machines almost ran out of room (local CI round 3, red-team M1): its machine and disk were removed before the job could fill the drive. The message
/// is plain words for the log and the notice.
/// </summary>
public sealed class LowDiskException : InvalidOperationException
{
    public LowDiskException(string message)
        : base(message)
    {
    }
}

/// <summary>A throwaway environment for one job. <see cref="RunAsync"/> builds it, runs the runner until it exits, and removes it again whatever happens.</summary>
public interface IJobHost
{
    /// <summary>Null when a runner can start, otherwise the plain-words reason it cannot (for example the base image is not built yet).</summary>
    string? NotReadyReason();

    /// <summary>Returns the runner's exit code. Cancelling the token ends the runner; clean-up still runs to the end.</summary>
    Task<int> RunAsync(RunnerLaunch launch, CancellationToken cancellationToken);

    /// <summary>Removes anything a crashed run left behind (distributions and their folders).</summary>
    Task CleanupLeftoversAsync(CancellationToken cancellationToken);
}

/// <summary>Passes a limited number of runner output lines to the log (a job cannot fill the disk through it) and notes once when it stops.</summary>
internal sealed class RunnerOutput
{
    private readonly ICiLog _log;
    private readonly LineBudget _budget = new(LocalCiDefaults.RunnerOutputLinesKept);
    private int _saidStopped;

    public RunnerOutput(ICiLog log)
    {
        _log = log;
    }

    public void Line(string line)
    {
        if (_budget.Allow())
        {
            _log.Info("runner: " + line);
        }
        else if (Interlocked.Exchange(ref _saidStopped, 1) == 0)
        {
            _log.Warn($"The runner printed more than {LocalCiDefaults.RunnerOutputLinesKept} lines; the rest is not logged.");
        }
    }
}
