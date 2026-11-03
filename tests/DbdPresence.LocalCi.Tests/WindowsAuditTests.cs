// Covers: local CI round 4, red-team F1 (the audit of the places the dedicated account may write to, run over the real drives of the machine the tests run on: every place the real source lists is
// either read or named as a gap, nothing is dropped, nothing throws, and the backup right can be switched on and off again). These tests read the real drives and run only on Windows, in the Windows CI
// (ci-windows.yml, job windows); on any other system they are skipped. What they print as diagnostics (the places that could not be checked, whether the backup right was held) is the first measurement of
// the PENDING-ENV items "ACL read error on System Volume Information" and "the backup right". docs/owner/free-ci.md.
using DbdPresence.LocalCi.Windows;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // The checks below run only on Windows.
public sealed class WindowsAuditTests
{
    private const string AccountSid = "S-1-5-21-1111-2222-3333-1099";

    private static void NeedsWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PENDING-ENV: this reads the real drives of a Windows PC; it runs in the Windows CI (ci-windows.yml, job windows).");

    [Fact]
    public async Task TheRealSource_OverTheRealDrives_ReadsEveryPlaceOrNamesItAsAGap_AndNothingIsDropped()
    {
        NeedsWindows();
        var log = new MemoryCiLog(new Redactor());
        var source = new WindowsLocationSource();
        var list = source.List();
        var audit = await new AccountWritableLocations(source, new WindowsFileSecurityProbe(), new FakeWslAccount { Sid = AccountSid }, log).FindAsync(CancellationToken.None);

        TestContext.Current.SendDiagnosticMessage($"Audit over this machine: {list.Candidates.Count} places listed, {audit.PlacesLooked} read, {audit.GapCount} gaps, {audit.Places.Count} places the account may write to.");
        foreach (var gap in audit.Gaps)
        {
            TestContext.Current.SendDiagnosticMessage("  gap: " + gap);
        }

        foreach (var place in audit.Places)
        {
            TestContext.Current.SendDiagnosticMessage("  writable: " + place);
        }

        // Every listed place is read or is a named gap; what the list itself could not make is a gap too.
        Assert.Equal(list.Candidates.Count, audit.PlacesLooked + (audit.GapCount - list.NotListed.Count));
        Assert.True(audit.Complete == (audit.GapCount == 0));
        Assert.Equal(audit.GapCount > WritableAudit.MostGapsNamed ? WritableAudit.MostGapsNamed + 1 : audit.GapCount, audit.Gaps.Count);
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.DriveRoot && c.Path.Length == 3 && c.Path[1] == ':');
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.TopLevelFolder);
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.WindowsTemp && c.Path.EndsWith(@"\Windows\Temp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.PublicFolder && c.Path.Equals(LocalPublicFolder.Usual, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.ProgramFiles);
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.ProgramData);
        Assert.Contains(list.Candidates, c => c.Kind == LocationKinds.SystemPath);
        Assert.Equal(list.Candidates.Count, list.Candidates.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ThePlacesThatCannotBeRead_AreNamedByTheRealProbe_NotLost()
    {
        NeedsWindows();
        var probe = new WindowsFileSecurityProbe();

        // A folder that is not there is not a gap: it is a place that does not exist.
        Assert.False(probe.Describe(@"C:\this-folder-does-not-exist-dbd-ci").Exists);

        // C:\System Volume Information refuses even administrators. Read it with the ordinary read and then with the backup right, and print what happened: the audit names the place as a gap when both fail.
        var guarded = @"C:\System Volume Information";
        string ordinary;
        try
        {
            ordinary = probe.Describe(guarded).Exists ? "read" : "not there";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ordinary = "refused: " + ex.Message;
        }

        string withRight;
        try
        {
            withRight = probe.DescribeWithBackupRights(guarded).Exists ? "read" : "not there";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            withRight = "refused: " + ex.Message;
        }

        TestContext.Current.SendDiagnosticMessage($"{guarded}: ordinary read {ordinary}; with the backup right {withRight}.");
    }

    [Fact]
    public void TheBackupRight_CanBeSwitchedOnAndOffAgain_WhenTheTokenHoldsIt()
    {
        NeedsWindows();

        var right = BackupPrivilege.TryEnable();

        TestContext.Current.SendDiagnosticMessage(right is null ? "The token does not hold the backup right (or Windows would not switch it on)." : "The backup right was switched on.");
        if (right is not null)
        {
            Assert.True(right.SwitchOff(), "Windows did not confirm that the backup right is off again.");
            Assert.True(right.SwitchOff(), "A second switch-off must give the same answer and change nothing.");
        }
    }

    [Fact]
    public void ThePublicFolderTheEnvironmentNames_IsOnlyUsedWhenItIsAnOrdinaryFolderOnALocalFixedDisk()
    {
        NeedsWindows();

        var folders = PublicFolders.Folders();

        Assert.Equal(LocalPublicFolder.Usual, folders[0]);
        Assert.All(folders, f => Assert.True(LocalPublicFolder.IsAcceptable(f, PublicFolders.IsOnFixedDrive), f));
        Assert.False(PublicFolders.IsOnFixedDrive('?'));
    }
}
