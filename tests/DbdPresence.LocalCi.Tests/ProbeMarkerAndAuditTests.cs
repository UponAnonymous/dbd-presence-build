// Covers: local CI round 3, red-team H1 (the probe's marker file on the Windows side is looked for and removed by its exact name and only as a plain file in the Public folder: a folder, a link or a
// wrongly named file is never followed or deleted; the audit of places the dedicated account may write to reads permissions only, ranks the places where a planted program would be run by others first,
// skips links and files, takes deny entries off and bounds what it prints). docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class ProbeMarkerAndAuditTests : IDisposable
{
    private const string Marker = "dbd-ci-probe-0123456789ab.txt";
    private const string WslUser = "S-1-5-21-1111-2222-3333-1099";

    private readonly TempFolder _folder = new();
    private readonly MemoryCiLog _log = new(new Redactor());

    public void Dispose() => _folder.Dispose();

    private string Public(string name = "Public")
    {
        var path = Path.Combine(_folder.Paths.Machine, "users", name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- the marker file

    [Fact]
    public void APlainFileWithTheMarkersName_IsFoundAndRemoved_ByItsExactPath()
    {
        var folder = Public();
        File.WriteAllText(Path.Combine(folder, Marker), "dbd-ci isolation probe\n");
        File.WriteAllText(Path.Combine(folder, "other.txt"), "mine");
        var marker = new ProbeMarkerFile([folder], _log);

        var finding = marker.FindAndRemove(Marker);

        Assert.Equal(ProbeMarkerFinding.FoundAndRemoved, finding);
        Assert.False(File.Exists(Path.Combine(folder, Marker)));
        Assert.True(File.Exists(Path.Combine(folder, "other.txt")));
    }

    [Fact]
    public void WhenTheFileIsNotThere_NothingIsFound_AndNothingIsTouched()
    {
        var folder = Public();
        File.WriteAllText(Path.Combine(folder, "dbd-ci-probe-ffffffffffff.txt"), "another probe's");
        var marker = new ProbeMarkerFile([folder, Path.Combine(folder, "does-not-exist")], _log);

        Assert.Equal(ProbeMarkerFinding.NotThere, marker.FindAndRemove(Marker));
        Assert.True(File.Exists(Path.Combine(folder, "dbd-ci-probe-ffffffffffff.txt")));
    }

    [Fact]
    public void AFolderWithTheMarkersName_IsNeverDeleted_AndIsReportedAsNotRemoved()
    {
        var folder = Public();
        var impostor = Path.Combine(folder, Marker);
        Directory.CreateDirectory(impostor);
        File.WriteAllText(Path.Combine(impostor, "keep.txt"), "mine");
        var marker = new ProbeMarkerFile([folder], _log);

        var finding = marker.FindAndRemove(Marker);

        Assert.Equal(ProbeMarkerFinding.FoundButNotRemoved, finding);
        Assert.True(File.Exists(Path.Combine(impostor, "keep.txt")));
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("not a plain file", StringComparison.Ordinal));
    }

    [Fact]
    public void ALinkWithTheMarkersName_IsNeverFollowedOrDeleted()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "symbolic links need a privilege on Windows; this runs on Linux.");
        var folder = Public();
        var target = Path.Combine(_folder.Paths.Machine, "precious.txt");
        File.WriteAllText(target, "precious");
        File.CreateSymbolicLink(Path.Combine(folder, Marker), target);
        var marker = new ProbeMarkerFile([folder], _log);

        var finding = marker.FindAndRemove(Marker);

        Assert.Equal(ProbeMarkerFinding.FoundButNotRemoved, finding);
        Assert.True(File.Exists(target));
        Assert.Equal("precious", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(Path.Combine(folder, Marker)).LinkTarget);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../precious.txt")]
    [InlineData("dbd-ci-probe-0123456789ab.txt/../x")]
    [InlineData("dbd-ci-probe-*.txt")]
    [InlineData("notes.txt")]
    public void ANameThatIsNotAMarkersName_IsRefused_BeforeAnyPathIsBuilt(string name)
    {
        var folder = Public();
        File.WriteAllText(Path.Combine(_folder.Paths.Machine, "precious.txt"), "precious");
        var marker = new ProbeMarkerFile([folder], _log);

        Assert.Throws<ArgumentException>(() => marker.FindAndRemove(name));
        Assert.True(File.Exists(Path.Combine(_folder.Paths.Machine, "precious.txt")));
    }

    [Fact]
    public void BothFolders_AreLookedIn_AndARelativeOrEmptyFolderIsIgnored()
    {
        var one = Public("Public");
        var two = Public("Other");
        File.WriteAllText(Path.Combine(one, Marker), "x");
        File.WriteAllText(Path.Combine(two, Marker), "x");
        var marker = new ProbeMarkerFile([one, two, "relative/folder", string.Empty, one], _log);

        Assert.Equal(ProbeMarkerFinding.FoundAndRemoved, marker.FindAndRemove(Marker));
        Assert.False(File.Exists(Path.Combine(one, Marker)));
        Assert.False(File.Exists(Path.Combine(two, Marker)));
    }

    /// <summary>Mutant 61 (the AND of the folder filter became an OR): a relative or empty folder was accepted. What a folder list holds is shown by the list itself, not by a lookup in the working folder.</summary>
    [Fact]
    public void OnlyFullPaths_AreLookedIn_OnceEach_AndARelativeOrEmptyOrBlankOneIsNot()
    {
        var one = Public("Public");
        var two = Public("Other");

        var marker = new ProbeMarkerFile([one, two, "relative/folder", string.Empty, "   ", one.ToUpperInvariant(), "./here", "..", one], _log);

        Assert.Equal([one, two], marker.Folders);
        Assert.Empty(new ProbeMarkerFile(["relative", string.Empty, "  "], _log).Folders);
        Assert.Empty(new ProbeMarkerFile([], _log).Folders);
    }

    [Fact]
    public void AFileThatCannotBeRemoved_IsReported_NotHidden()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PENDING-ENV: Windows refuses to delete a file that another program holds open (Linux allows it); this runs in the Windows CI.");
        var folder = Public();
        var path = Path.Combine(folder, Marker);
        using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var marker = new ProbeMarkerFile([folder], _log);

        var finding = marker.FindAndRemove(Marker);

        Assert.Equal(ProbeMarkerFinding.FoundButNotRemoved, finding);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal) && l.Contains("Could not remove the probe's marker file", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMarkerFileObject_MakesNamesOfTheRightShape()
    {
        var marker = new ProbeMarkerFile([], _log);

        Assert.True(ProbeMarkerNames.IsMarker(marker.NewName()));
        Assert.Equal(ProbeMarkerFinding.NotThere, marker.FindAndRemove(Marker));
    }

    // ---- what the account may write

    private static readonly HashSet<string> s_identities = [.. AccountWritableLocations.IdentitiesOf(WslUser)];

    private static NodeSecurity Folder(string path, params AceRecord[] aces) =>
        new(path, true, true, false, WellKnownSids.Administrators, true, [new AceRecord(WellKnownSids.Administrators, true, FileRights.FullControl), .. aces]);

    [Fact]
    public void TheIdentitiesOfTheAccount_AreItsOwnSidAndTheGroupsOfAnInteractiveLogon()
    {
        Assert.Contains(WslUser, s_identities);
        Assert.Contains(WellKnownSids.Users, s_identities);
        Assert.Contains(WellKnownSids.AuthenticatedUsers, s_identities);
        Assert.Contains(WellKnownSids.Everyone, s_identities);
        Assert.DoesNotContain(WellKnownSids.Administrators, s_identities);
        Assert.DoesNotContain(string.Empty, AccountWritableLocations.IdentitiesOf(WslUser));
        Assert.Contains(string.Empty, AccountWritableLocations.IdentitiesOf(null));
    }

    [Theory]
    [InlineData(WellKnownSids.Users, FileRights.ReadAndExecute, WriteAccess.None)]
    [InlineData(WellKnownSids.AuthenticatedUsers, FileRights.AppendData, WriteAccess.CreateFolders)]
    [InlineData(WellKnownSids.AuthenticatedUsers, FileRights.WriteData, WriteAccess.CreateFiles)]
    [InlineData(WellKnownSids.Everyone, FileRights.Modify, WriteAccess.CreateFiles | WriteAccess.CreateFolders | WriteAccess.ChangeEntries)]
    [InlineData(WellKnownSids.Users, FileRights.DeleteChild, WriteAccess.ChangeEntries)]
    [InlineData(WellKnownSids.Users, FileRights.WriteDac, WriteAccess.ChangePermissions)]
    [InlineData(WellKnownSids.Users, FileRights.WriteOwner, WriteAccess.ChangePermissions)]
    [InlineData(WslUser, FileRights.FullControl, WriteAccess.CreateFiles | WriteAccess.CreateFolders | WriteAccess.ChangeEntries | WriteAccess.ChangePermissions)]
    [InlineData(WellKnownSids.Administrators, FileRights.FullControl, WriteAccess.None)]
    [InlineData("S-1-5-21-1111-2222-3333-1001", FileRights.FullControl, WriteAccess.None)]
    public void WhatTheAccountMayDoInAFolder_FollowsTheEntriesThatApplyToIt(string sid, uint rights, WriteAccess expected)
    {
        var access = AccessRules.AccountMayWrite(Folder("/x", new AceRecord(sid, true, rights)), s_identities);

        Assert.Equal(expected, sid == WellKnownSids.Administrators ? WriteAccess.None : access);
    }

    [Fact]
    public void AnEntryForChildrenOnly_IsNotAWriteRightOnTheFolderItself()
    {
        var node = Folder("/x", new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.Modify, InheritOnly: true));

        Assert.Equal(WriteAccess.None, AccessRules.AccountMayWrite(node, s_identities));
    }

    [Fact]
    public void ADenyEntryForTheAccountsGroup_TakesTheDeniedRightsAway_AndWithWriteDeniedThereIsNoRightToChangeWhatIsInTheFolder()
    {
        var node = Folder("/x", new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.Modify), new AceRecord(WellKnownSids.Users, false, FileRights.WriteData | FileRights.AppendData));

        Assert.Equal(WriteAccess.None, AccessRules.AccountMayWrite(node, s_identities));

        var onlyAppendDenied = Folder("/x", new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.Modify), new AceRecord(WellKnownSids.Users, false, FileRights.AppendData));
        Assert.Equal(WriteAccess.CreateFiles | WriteAccess.ChangeEntries, AccessRules.AccountMayWrite(onlyAppendDenied, s_identities));
    }

    [Fact]
    public void AFolderThatIsNotThere_GivesNoAccess()
    {
        Assert.Equal(WriteAccess.None, AccessRules.AccountMayWrite(NodeSecurity.Missing("/x"), s_identities));
        Assert.Throws<ArgumentNullException>(() => AccessRules.AccountMayWrite(null!, s_identities));
        Assert.Throws<ArgumentNullException>(() => AccessRules.AccountMayWrite(Folder("/x"), null!));
    }

    private sealed class Source : ILocationSource
    {
        public List<LocationCandidate> Found { get; } = [];

        public List<string> NotListed { get; } = [];

        public LocationList List() => new([.. Found], [.. NotListed]);
    }

    private AccountWritableLocations Audit(Source source, FakeProbe probe, string? sid = WslUser) =>
        new(source, probe, new FakeWslAccount { Sid = sid }, _log);

    private static void Add(Source source, FakeProbe probe, string path, string kind, params AceRecord[] aces)
    {
        source.Found.Add(new LocationCandidate(path, kind));
        probe.Set(Folder(path, aces));
    }

    [Fact]
    public async Task TheAudit_ListsWhatTheAccountMayWrite_PlacesWhereAPlantedFileIsUsedFirst_AndNothingElse()
    {
        var source = new Source();
        var probe = new FakeProbe();
        Add(source, probe, "C:\\", "drive root", new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.AppendData));
        Add(source, probe, "C:\\Users\\Public", "public folder", new AceRecord(WellKnownSids.Everyone, true, FileRights.Modify));
        Add(source, probe, "C:\\Tools", "folder in the system PATH", new AceRecord(WellKnownSids.Users, true, FileRights.Modify));
        Add(source, probe, "C:\\ProgramData\\Vendor", "folder in ProgramData", new AceRecord(WellKnownSids.Users, true, FileRights.Modify));
        Add(source, probe, "C:\\Windows\\Temp", "Windows temp folder", new AceRecord(WellKnownSids.Users, true, FileRights.WriteData | FileRights.AppendData));
        Add(source, probe, "C:\\Program Files\\Vendor", "folder in Program Files", new AceRecord(WellKnownSids.Users, true, FileRights.ReadAndExecute));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Equal(5, audit.Places.Count);
        Assert.StartsWith("C:\\ProgramData\\Vendor (folder in ProgramData): the account may make files, make folders, change or delete what is in it. A file placed here can be used by the programs of this PC that keep their files here", audit.Places[0], StringComparison.Ordinal);
        Assert.StartsWith("C:\\Tools (folder in the system PATH): the account may make files, make folders, change or delete what is in it. A program placed here can be run by other programs of this PC", audit.Places[1], StringComparison.Ordinal);
        Assert.StartsWith("C:\\Users\\Public (public folder): the account may make files", audit.Places[2], StringComparison.Ordinal);
        Assert.Equal("C:\\Windows\\Temp (Windows temp folder): the account may make files, make folders", audit.Places[3]);
        Assert.Equal("C:\\ (drive root): the account may make folders", audit.Places[4]);
        Assert.DoesNotContain(audit.Places, f => f.Contains("Vendor (folder in Program Files)", StringComparison.Ordinal));
        Assert.True(audit.Complete);
        Assert.Equal(6, audit.PlacesLooked);
        Assert.Empty(audit.Gaps);
    }

    [Fact]
    public async Task TheAudit_SkipsLinksFilesAndFoldersThatAreNotThere_AndTheyAreNotGaps()
    {
        var source = new Source();
        var probe = new FakeProbe();
        var open = new AceRecord(WellKnownSids.Everyone, true, FileRights.Modify);
        source.Found.Add(new LocationCandidate("C:\\link", "top-level folder"));
        probe.Set(new NodeSecurity("C:\\link", true, true, true, WellKnownSids.Administrators, true, [open]));
        source.Found.Add(new LocationCandidate("C:\\file.txt", "top-level folder"));
        probe.Set(new NodeSecurity("C:\\file.txt", true, false, false, WellKnownSids.Administrators, true, [open]));
        source.Found.Add(new LocationCandidate("C:\\gone", "top-level folder"));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Empty(audit.Places);
        Assert.True(audit.Complete);
        Assert.Equal(3, audit.PlacesLooked);
    }

    [Fact]
    public async Task TheAudit_ShowsAtMostTwentyFivePlaces_AndSaysHowManyMoreThereAre()
    {
        var source = new Source();
        var probe = new FakeProbe();
        for (var i = 0; i < 40; i++)
        {
            Add(source, probe, $"C:\\folder{i:D2}", "top-level folder", new AceRecord(WellKnownSids.Users, true, FileRights.Modify));
        }

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Equal(26, audit.Places.Count);
        Assert.Equal("and 15 more places", audit.Places[^1]);
        Assert.Equal(40, audit.PlacesLooked);
    }

    [Fact]
    public async Task TheAudit_CanBeCancelled_AndWorksWithoutAnAccountSid()
    {
        var source = new Source();
        var probe = new FakeProbe();
        Add(source, probe, "C:\\x", "top-level folder", new AceRecord(WslUser, true, FileRights.Modify));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Audit(source, probe).FindAsync(cts.Token));
        Assert.Empty((await Audit(source, probe, sid: null).FindAsync(CancellationToken.None)).Places);
        Assert.Single((await Audit(source, probe).FindAsync(CancellationToken.None)).Places);
    }

    // ---- a place that cannot be read is a named gap, never "none" (round 4, F1)

    [Fact]
    public async Task APlaceWhosePermissionsAreRefused_IsCountedAndNamed_TheAuditGoesOnToTheNextPlace()
    {
        var source = new Source();
        var probe = new FakeProbe();
        Add(source, probe, "C:\\Tools", "folder in the system PATH", new AceRecord(WellKnownSids.Users, true, FileRights.Modify));
        source.Found.Insert(0, new LocationCandidate("C:\\System Volume Information", "top-level folder"));
        probe.Refuse("C:\\System Volume Information", new UnauthorizedAccessException("Attempted to perform an unauthorized operation."));
        probe.RefuseEvenWithBackupRights("C:\\System Volume Information", new UnauthorizedAccessException("the program does not hold the backup right"));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.False(audit.Complete);
        Assert.Equal(1, audit.GapCount);
        Assert.Equal(["C:\\System Volume Information (top-level folder): Attempted to perform an unauthorized operation.; with the backup right: the program does not hold the backup right"], audit.Gaps);
        Assert.Single(audit.Places);
        Assert.Equal(1, audit.PlacesLooked);
        Assert.Equal(["C:\\System Volume Information"], probe.BackupReads);
        Assert.Contains(_log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal) && l.Contains("could not check 1 place(s)", StringComparison.Ordinal) && l.Contains("System Volume Information", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedPlaceIsReadAgainWithTheBackupRight_AndWhatItShowsCountsLikeAnyOther()
    {
        var source = new Source();
        var probe = new FakeProbe();
        source.Found.Add(new LocationCandidate("C:\\System Volume Information", "top-level folder"));
        probe.Refuse("C:\\System Volume Information", new UnauthorizedAccessException("denied"));
        probe.ShowWithBackupRights(Folder("C:\\System Volume Information", new AceRecord(WellKnownSids.Users, true, FileRights.Modify)));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.True(audit.Complete);
        Assert.Equal(["C:\\System Volume Information (top-level folder): the account may make files, make folders, change or delete what is in it"], audit.Places);
        Assert.Equal(["C:\\System Volume Information"], probe.BackupReads);
    }

    [Fact]
    public async Task OnlyAReadThatWasRefused_IsRepeatedWithTheBackupRight_OtherFailuresAreGapsWithoutIt()
    {
        var source = new Source();
        var probe = new FakeProbe();
        source.Found.Add(new LocationCandidate("D:\\", "removable drive"));
        probe.Refuse("D:\\", new IOException("The device is not ready.\r\nsecond line is not shown"));
        source.Found.Add(new LocationCandidate("E:\\", "drive root"));
        probe.Refuse("E:\\", new InvalidOperationException(string.Empty));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Equal(2, audit.GapCount);
        Assert.Equal(["D:\\ (removable drive): The device is not ready.", "E:\\ (drive root): InvalidOperationException"], audit.Gaps);
        Assert.Empty(probe.BackupReads);
        Assert.Equal(0, audit.PlacesLooked);
    }

    [Fact]
    public async Task AReasonThatIsVeryLong_IsCutShort_InTheGap()
    {
        var source = new Source();
        var probe = new FakeProbe();
        source.Found.Add(new LocationCandidate("C:\\x", "top-level folder"));
        probe.Refuse("C:\\x", new IOException(new string('w', 500)));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Equal("C:\\x (top-level folder): " + new string('w', 120) + "...", Assert.Single(audit.Gaps));
    }

    [Fact]
    public async Task AnAuditThatCouldNotListSomething_CarriesThatAsAGapToo_BeforeTheFoldersThatCouldNotBeRead()
    {
        var source = new Source();
        var probe = new FakeProbe();
        Add(source, probe, "C:\\Tools", "folder in the system PATH", new AceRecord(WellKnownSids.Users, true, FileRights.Modify));
        source.NotListed.Add("C:\\Program Files could not be listed: Access to the path is denied.");
        source.Found.Add(new LocationCandidate("C:\\Locked", "top-level folder"));
        probe.Refuse("C:\\Locked", new UnauthorizedAccessException("denied"));

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Equal(2, audit.GapCount);
        Assert.Equal("C:\\Program Files could not be listed: Access to the path is denied.", audit.Gaps[0]);
        Assert.StartsWith("C:\\Locked (top-level folder): denied", audit.Gaps[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanTwentyFiveGaps_AreCountedExactly_AndTheFirstOnesAreNamed()
    {
        var source = new Source();
        var probe = new FakeProbe();
        for (var i = 0; i < 40; i++)
        {
            source.Found.Add(new LocationCandidate($"C:\\locked{i:D2}", "top-level folder"));
            probe.Refuse($"C:\\locked{i:D2}", new IOException("nope"));
        }

        var audit = await Audit(source, probe).FindAsync(CancellationToken.None);

        Assert.Equal(40, audit.GapCount);
        Assert.Equal(26, audit.Gaps.Count);
        Assert.StartsWith("C:\\locked00 ", audit.Gaps[0], StringComparison.Ordinal);
        Assert.StartsWith("C:\\locked24 ", audit.Gaps[24], StringComparison.Ordinal);
        Assert.Equal("and 15 more places could not be checked", audit.Gaps[25]);
        Assert.False(audit.Complete);
    }

    [Fact]
    public async Task AFileSystemWithoutPermissions_IsAPlaceTheAccountMayWrite_AsAnyProgramMay()
    {
        var source = new Source();
        source.Found.Add(new LocationCandidate("E:\\", "removable drive", NoPermissionsOn: "FAT32"));

        var audit = await Audit(source, new FakeProbe()).FindAsync(CancellationToken.None);

        Assert.Equal(["E:\\ (removable drive): the file system (FAT32) has no permissions, so the account may write there as any program may"], audit.Places);
        Assert.True(audit.Complete);
        Assert.Equal(1, audit.PlacesLooked);
    }

    [Fact]
    public void ABackupReadFailure_IsNeverTheEndOfTheAudit_ButACancelStillPassesThrough()
    {
        var source = new Source();
        var probe = new FakeProbe();
        source.Found.Add(new LocationCandidate("C:\\x", "top-level folder"));
        probe.Refuse("C:\\x", new UnauthorizedAccessException("denied"));
        probe.RefuseEvenWithBackupRights("C:\\x", new OperationCanceledException());

        Assert.ThrowsAny<OperationCanceledException>(() => Audit(source, probe).FindAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void TheEmptyAudit_IsCompleteAndTheFailedOneIsNot()
    {
        Assert.True(WritableAudit.Empty.Complete);
        Assert.False(WritableAudit.Failed("no drives").Complete);
        Assert.Equal(["the audit could not be made at all: no drives"], WritableAudit.Failed("no drives").Gaps);
        Assert.Equal(1, WritableAudit.Failed("no drives").GapCount);
        Assert.Equal(0, WritableAudit.Failed("no drives").PlacesLooked);
    }

    // ---- which folder may stand for the Public folder (round 4, F13)

    private static bool OnlyCIsFixed(char letter) => char.ToUpperInvariant(letter) == 'C';

    [Theory]
    [InlineData(@"C:\Users\Public", true)]
    [InlineData(@"c:\Users\Public\", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Users\Public Documents", true)]
    [InlineData(@"\\server\share\Public", false)]
    [InlineData(@"\\?\C:\Users\Public", false)]
    [InlineData(@"\\.\C:\Users\Public", false)]
    [InlineData(@"D:\Users\Public", false)]
    [InlineData(@"Users\Public", false)]
    [InlineData(@"\Users\Public", false)]
    [InlineData(@"C:Users\Public", false)]
    [InlineData(@"C:\Users\..\Windows", false)]
    [InlineData(@"C:\Users\.\Public", false)]
    [InlineData(@"C:\Users\\Public", false)]
    [InlineData(@"C:\Users\Pub*lic", false)]
    [InlineData(@"C:\Users\Public.", false)]
    [InlineData(@"C:\Users\Public ", false)]
    [InlineData(@"C:\Users\Public:stream", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAnOrdinaryFolderOnALocalFixedDisk_MayStandForThePublicFolder(string? path, bool expected)
    {
        Assert.Equal(expected, LocalPublicFolder.IsAcceptable(path, OnlyCIsFixed));
    }

    [Fact]
    public void ANameThatIsTooLong_OrAHugeBadOne_IsRefusedAtOnce()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(LocalPublicFolder.IsAcceptable(@"C:\" + new string('a', 300), OnlyCIsFixed));
        Assert.False(LocalPublicFolder.IsAcceptable(@"C:\" + new string('a', 230) + "<", OnlyCIsFixed));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "The check must not take long on a long bad name.");
    }

    [Fact]
    public void TheFoldersToLookIn_AreTheUsualOneAndAcceptableOthers_NeverANetworkShare()
    {
        Assert.Equal([@"C:\Users\Public"], LocalPublicFolder.Folders(null, OnlyCIsFixed));
        Assert.Equal([@"C:\Users\Public"], LocalPublicFolder.Folders(@"\\evil\share", OnlyCIsFixed));
        Assert.Equal([@"C:\Users\Public"], LocalPublicFolder.Folders(@"C:\Users\Public", OnlyCIsFixed));
        Assert.Equal([@"C:\Users\Public"], LocalPublicFolder.Folders(@"c:\users\public\", OnlyCIsFixed));
        Assert.Equal([@"C:\Users\Public", @"C:\Other\Public"], LocalPublicFolder.Folders(@"C:\Other\Public", OnlyCIsFixed));
        Assert.Equal([@"C:\Users\Public"], LocalPublicFolder.Folders(@"D:\Other\Public", OnlyCIsFixed));
        Assert.Throws<ArgumentNullException>(() => LocalPublicFolder.IsAcceptable(@"C:\x", null!));
    }
}
