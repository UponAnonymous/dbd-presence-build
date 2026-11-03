namespace DbdPresence.Core.Game;

/// <summary>A process seen in a system snapshot (name without ".exe", as the OS reports it).</summary>
/// <param name="Id">Process id.</param>
/// <param name="Name">Process name without extension.</param>
public sealed record RunningProcess(int Id, string Name);

/// <summary>Details read once per PID through the least-privilege query (SAFETY §2.1).</summary>
/// <param name="StartedUtc">Process start time, if available.</param>
/// <param name="ImagePath">Full executable path, if available.</param>
public sealed record ProcessDetails(DateTimeOffset? StartedUtc, string? ImagePath);

/// <summary>OS process access used by <see cref="GameMonitor"/>; implemented in Platform.Windows, faked in tests.</summary>
public interface IProcessSnapshotSource
{
    /// <summary>Returns the running processes (no process handles are opened).</summary>
    IReadOnlyList<RunningProcess> Snapshot();

    /// <summary>Queries start time and image path for one PID, or null if access is not possible.</summary>
    ProcessDetails? Details(int processId);
}
