// Covers: FR-PROC-01/05 on real Windows (snapshot without handles; least-privilege start time and path query).
using System.Diagnostics;
using DbdPresence.Platform.Windows.Processes;

namespace DbdPresence.Platform.Windows.Tests;

public sealed class ProcessSnapshotTests
{
    [Fact]
    public void Snapshot_ContainsCurrentProcessByName()
    {
        using var self = Process.GetCurrentProcess();

        var snapshot = new WindowsProcessSnapshotSource().Snapshot();

        Assert.Contains(snapshot, p => p.Id == self.Id && p.Name == self.ProcessName);
    }

    [Fact]
    public void Details_ForOwnProcess_ReturnsStartTimeAndImagePath()
    {
        using var self = Process.GetCurrentProcess();

        var details = new WindowsProcessSnapshotSource().Details(self.Id);

        Assert.NotNull(details);
        Assert.NotNull(details.StartedUtc);
        Assert.InRange(details.StartedUtc.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(Environment.ProcessPath, details.ImagePath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Details_ForProtectedSystemProcess_DoesNotThrow()
    {
        // PID 4 (System) generally refuses most access; the query must degrade to null or partial data.
        var details = new WindowsProcessSnapshotSource().Details(4);

        Assert.True(details is null || details.ImagePath is null || details.ImagePath.Length > 0);
    }

    [Fact]
    public void Details_ForMissingProcess_ReturnsNull()
    {
        Assert.Null(new WindowsProcessSnapshotSource().Details(int.MaxValue - 1));
    }
}
