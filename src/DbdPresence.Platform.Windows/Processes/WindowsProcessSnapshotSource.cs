using System.Diagnostics;
using DbdPresence.Core.Game;

namespace DbdPresence.Platform.Windows.Processes;

/// <summary>Process enumeration via <see cref="Process.GetProcesses()"/> (no handles) plus the allow-listed detail query.</summary>
public sealed class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
    /// <inheritdoc />
    public IReadOnlyList<RunningProcess> Snapshot()
    {
        var processes = Process.GetProcesses();
        try
        {
            return [.. processes.Select(p => new RunningProcess(p.Id, p.ProcessName))];
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public ProcessDetails? Details(int processId) => NativeProcessQuery.Query(processId);
}
