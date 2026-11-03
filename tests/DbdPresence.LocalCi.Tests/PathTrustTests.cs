// Covers: FR-CI-09 (every component of a path is checked from the drive down: no link, a trusted owner, nobody without rights able to change, replace or remove it), security review
// 2026-10-30 C1 H1: the data folder at every start of the loop, entries inside it, and the place the elevated program runs from. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class PathTrustTests
{
    private static readonly string s_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusttest"));
    private static readonly string s_data = Path.Combine(s_root, "ProgramData", "DbdLocalCi");

    /// <summary>Describes every folder from the drive down to <paramref name="path"/> (inclusive) as an ordinary, safe folder above.</summary>
    private static FakeProbe WithFoldersDownTo(string path, FakeProbe? probe = null)
    {
        probe ??= new FakeProbe();
        for (var folder = path; !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder)!)
        {
            probe.Set(Nodes.Ancestor(folder));
        }

        return probe;
    }

    private static FakeProbe GoodDataFolder() => WithFoldersDownTo(Path.Combine(s_root, "ProgramData")).Set(Nodes.ClosedFolder(s_data));

    [Fact]
    public void AGoodDataFolder_AndAnEntryDeepInsideIt_AreTrusted()
    {
        var probe = GoodDataFolder();
        probe.Set(Nodes.ClosedFolder(Path.Combine(s_data, "runner"), protectedAcl: false));
        probe.Set(Nodes.ClosedFolder(Path.Combine(s_data, "runner", "2.338.0"), protectedAcl: false));
        probe.Set(Nodes.ClosedFile(Path.Combine(s_data, "runner", "2.338.0", "x.zip")));
        var trust = new PathTrust(probe);

        Assert.Empty(trust.CheckDataFolder(s_data));
        Assert.Empty(trust.CheckInsideDataFolder(s_data, Path.Combine(s_data, "runner", "2.338.0", "x.zip")));
    }

    [Fact]
    public void AJunctionAnywhereInThePath_IsFound()
    {
        var probe = GoodDataFolder();
        probe.Set(Nodes.ClosedFolder(Path.Combine(s_data, "runner"), protectedAcl: false));
        probe.Set(Nodes.Link(Path.Combine(s_data, "runner", "2.338.0")));
        probe.Set(Nodes.ClosedFile(Path.Combine(s_data, "runner", "2.338.0", "x.zip")));

        var problems = new PathTrust(probe).CheckInsideDataFolder(s_data, Path.Combine(s_data, "runner", "2.338.0", "x.zip"));

        Assert.Contains(problems, p => p.Contains("link", StringComparison.Ordinal) && p.Contains("2.338.0", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEntryOwnedByAnOrdinaryUser_IsFound_EvenWhenEverythingAboveIsFine()
    {
        var probe = GoodDataFolder();
        probe.Set(Nodes.ClosedFolder(Path.Combine(s_data, "runner"), protectedAcl: false));
        probe.Set(Nodes.ClosedFolder(Path.Combine(s_data, "runner", "99.0.0"), owner: "S-1-5-21-1-2-3-1001", protectedAcl: false));

        var problems = new PathTrust(probe).CheckInsideDataFolder(s_data, Path.Combine(s_data, "runner", "99.0.0"));

        Assert.Contains(problems, p => p.Contains("owned by", StringComparison.Ordinal));
    }

    [Fact]
    public void ADataFolderWithAnEntryForUsers_IsFound()
    {
        var probe = GoodDataFolder();
        probe.Set(Nodes.ClosedFolder(s_data, extra: [new AceRecord(WellKnownSids.Users, true, FileRights.ReadAndExecute)]));

        Assert.NotEmpty(new PathTrust(probe).CheckDataFolder(s_data));
    }

    [Fact]
    public void AMissingDataFolder_OrAMissingEntry_IsAProblem()
    {
        var probe = WithFoldersDownTo(Path.Combine(s_root, "ProgramData"));
        var trust = new PathTrust(probe);

        Assert.Contains(trust.CheckDataFolder(s_data), p => p.Contains("does not exist", StringComparison.Ordinal));
        probe.Set(Nodes.ClosedFolder(s_data));
        Assert.Contains(trust.CheckInsideDataFolder(s_data, Path.Combine(s_data, "nothing")), p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderAboveTheDataFolder_ThatAnUserCouldReplace_IsFound()
    {
        var probe = GoodDataFolder();
        probe.Set(Nodes.Ancestor(Path.Combine(s_root, "ProgramData"), extra: [new AceRecord(WellKnownSids.Users, true, FileRights.DeleteChild)]));

        var problems = new PathTrust(probe).CheckDataFolder(s_data);

        Assert.Contains(problems, p => p.Contains("remove, rename or take over", StringComparison.Ordinal));
    }

    [Fact]
    public void APathOutsideTheDataFolder_IsNeverTrusted_AndSiblingNamesWithTheSamePrefixAreOutside()
    {
        var probe = GoodDataFolder();
        var trust = new PathTrust(probe);

        Assert.Contains(trust.CheckInsideDataFolder(s_data, Path.Combine(s_root, "ProgramData", "Other", "x")), p => p.Contains("not inside", StringComparison.Ordinal));
        Assert.Contains(trust.CheckInsideDataFolder(s_data, s_data + "-evil"), p => p.Contains("not inside", StringComparison.Ordinal));
        Assert.Contains(trust.CheckInsideDataFolder(s_data, Path.Combine(s_data, "..", "x")), p => p.Contains("not inside", StringComparison.Ordinal));
    }

    [Fact]
    public void PathComparison_IgnoresCase_LikeWindows()
    {
        Assert.True(PathTrust.IsSameOrInside(s_data, s_data.ToUpperInvariant() + Path.DirectorySeparatorChar + "X"));
        Assert.True(PathTrust.IsSameOrInside(s_data, s_data.ToUpperInvariant()));
        Assert.False(PathTrust.IsSameOrInside(s_data, s_data + "x"));
    }

    // ---- the place the elevated program runs from

    private static FakeProbe ProgramLocation(out string program)
    {
        var programFiles = Path.Combine(s_root, "Program Files");
        var folder = Path.Combine(programFiles, "DbdLocalCi");
        program = Path.Combine(folder, "DbdLocalCi.exe");
        var probe = WithFoldersDownTo(s_root);
        return probe
            .Set(Nodes.Ancestor(programFiles, owner: WellKnownSids.TrustedInstaller))
            .Set(Nodes.ProgramFolder(folder))
            .Set(Nodes.ProgramFile(program));
    }

    [Fact]
    public void TheInstalledProgram_InProgramFiles_IsTrusted()
    {
        var probe = ProgramLocation(out var program);

        Assert.Empty(new PathTrust(probe).CheckProgramLocation(program));
    }

    [Fact]
    public void AProgramInAFolderOrdinaryUsersCanWriteTo_IsRefused_LikeTheC_Tools_FolderTheEarlierGuideSuggested()
    {
        var tools = Path.Combine(s_root, "Tools", "DbdLocalCi");
        var probe = WithFoldersDownTo(Path.Combine(s_root, "Tools"));

        // A folder made by an ordinary user inherits "Authenticated Users: Modify" from the drive, and is not closed.
        probe.Set(new NodeSecurity(tools, true, true, false, "S-1-5-21-1-2-3-1001", false, [new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.FullControl & ~FileRights.WriteOwner)]));
        probe.Set(new NodeSecurity(Path.Combine(tools, "DbdLocalCi.exe"), true, false, false, "S-1-5-21-1-2-3-1001", false, [new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.FullControl & ~FileRights.WriteOwner)]));

        var problems = new PathTrust(probe).CheckProgramLocation(Path.Combine(tools, "DbdLocalCi.exe"));

        Assert.Contains(problems, p => p.Contains("gives access to", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("can be changed by", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("owned by", StringComparison.Ordinal));
    }

    [Fact]
    public void AProgramWhoseFolderAboveCouldBeRenamed_IsRefused()
    {
        var probe = ProgramLocation(out var program);
        probe.Set(Nodes.Ancestor(Path.Combine(s_root, "Program Files"), owner: WellKnownSids.TrustedInstaller, extra: [new AceRecord(WellKnownSids.Users, true, FileRights.DeleteChild)]));

        Assert.NotEmpty(new PathTrust(probe).CheckProgramLocation(program));
    }

    [Fact]
    public void AProgramThatIsALink_OrWhoseFolderIsALink_IsRefused()
    {
        var probe = ProgramLocation(out var program);
        probe.Set(Nodes.Link(program, directory: false));
        Assert.NotEmpty(new PathTrust(probe).CheckProgramLocation(program));

        var other = ProgramLocation(out var program2);
        other.Set(Nodes.Link(Path.GetDirectoryName(program2)!));
        Assert.NotEmpty(new PathTrust(other).CheckProgramLocation(program2));
    }
}
