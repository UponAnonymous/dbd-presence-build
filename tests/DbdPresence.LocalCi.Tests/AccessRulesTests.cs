// Covers: FR-CI-09 (the access rules of the data folder, its entries, the folders above the program, the program's own folder and file, and a job's folder, as pure functions over a
// description of the permissions: owner must be Administrators or the system, no links, no access for anyone else), security review 2026-10-30 C1 H1 H3 M2. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class AccessRulesTests
{
    private const string Folder = "/pd/DbdLocalCi";
    private const string OwnerUser = "S-1-5-21-1111-2222-3333-1001";
    private const string WslUser = "S-1-5-21-1111-2222-3333-1099";

    // ---- the data folder

    [Fact]
    public void ADataFolder_ClosedToEveryoneButAdministratorsAndTheSystem_IsAccepted()
    {
        Assert.Empty(AccessRules.AdminOnlyFolderProblems(Nodes.ClosedFolder(Folder)));
        Assert.Empty(AccessRules.AdminOnlyFolderProblems(Nodes.ClosedFolder(Folder, owner: WellKnownSids.LocalSystem)));
    }

    [Theory]
    [InlineData(WellKnownSids.Users)]
    [InlineData(WellKnownSids.Everyone)]
    [InlineData(WellKnownSids.AuthenticatedUsers)]
    [InlineData(OwnerUser)]
    public void ADataFolder_ThatGivesAnyoneElseAnyAccess_IsRefused(string sid)
    {
        var node = Nodes.ClosedFolder(Folder, extra: [new AceRecord(sid, true, FileRights.ReadAndExecute)]);

        var problems = AccessRules.AdminOnlyFolderProblems(node);

        Assert.Contains(problems, p => p.Contains("gives access to", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEntryThatOnlyApplyToChildren_StillCounts_BecauseTheChildrenInheritIt()
    {
        var node = Nodes.ClosedFolder(Folder, extra: [new AceRecord(WellKnownSids.Users, true, FileRights.ReadAndExecute, InheritOnly: true)]);

        Assert.NotEmpty(AccessRules.AdminOnlyFolderProblems(node));
    }

    [Fact]
    public void ACreatorOwnerEntry_GivesNothingByItself_AndIsIgnored()
    {
        var node = Nodes.ClosedFolder(Folder, extra: [new AceRecord(WellKnownSids.CreatorOwner, true, FileRights.FullControl, InheritOnly: true)]);

        Assert.Empty(AccessRules.AdminOnlyFolderProblems(node));
    }

    [Fact]
    public void AnOwnerThatIsAnOrdinaryUser_IsRefused_BecauseAnOwnerCanAlwaysChangeThePermissions()
    {
        var problems = AccessRules.AdminOnlyFolderProblems(Nodes.ClosedFolder(Folder, owner: OwnerUser));

        Assert.Contains(problems, p => p.Contains("owned by", StringComparison.Ordinal) && p.Contains("owner can always change", StringComparison.Ordinal));
    }

    [Fact]
    public void ADataFolderThatInheritsItsPermissions_IsRefused()
    {
        var problems = AccessRules.AdminOnlyFolderProblems(Nodes.ClosedFolder(Folder, protectedAcl: false));

        Assert.Contains(problems, p => p.Contains("takes its permissions from the folder above", StringComparison.Ordinal));
    }

    [Fact]
    public void ADataFolderThatIsALinkOrMissingOrAFile_IsRefused()
    {
        Assert.Contains(AccessRules.AdminOnlyFolderProblems(Nodes.Link(Folder)), p => p.Contains("link", StringComparison.Ordinal));
        Assert.Contains(AccessRules.AdminOnlyFolderProblems(NodeSecurity.Missing(Folder)), p => p.Contains("does not exist", StringComparison.Ordinal));
        Assert.Contains(AccessRules.AdminOnlyFolderProblems(Nodes.ClosedFile(Folder)), p => p.Contains("not a folder", StringComparison.Ordinal));
    }

    [Fact]
    public void ADataFolderWithoutFullControlForBothAdministratorsAndTheSystem_IsRefused()
    {
        var onlyAdmins = new NodeSecurity(Folder, true, true, false, WellKnownSids.Administrators, true, [new AceRecord(WellKnownSids.Administrators, true, FileRights.FullControl)]);
        var readOnly = new NodeSecurity(
            Folder,
            true,
            true,
            false,
            WellKnownSids.Administrators,
            true,
            [new AceRecord(WellKnownSids.Administrators, true, FileRights.FullControl), new AceRecord(WellKnownSids.LocalSystem, true, FileRights.ReadAndExecute)]);

        Assert.Contains(AccessRules.AdminOnlyFolderProblems(onlyAdmins), p => p.Contains("the system full control", StringComparison.Ordinal));
        Assert.Contains(AccessRules.AdminOnlyFolderProblems(readOnly), p => p.Contains("the system full control", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOwnerThatCannotBeRead_IsRefused()
    {
        var node = Nodes.ClosedFolder(Folder) with { OwnerSid = null };

        Assert.Contains(AccessRules.AdminOnlyFolderProblems(node), p => p.Contains("owner that could not be read", StringComparison.Ordinal));
    }

    // ---- an entry inside the data folder

    [Fact]
    public void AnEntryInsideTheDataFolder_OwnedByAdministrators_WithoutLinksOrStrangers_IsAccepted()
    {
        Assert.Empty(AccessRules.AdminOnlyEntryProblems(Nodes.ClosedFile(Folder + "/runner/2.338.0/x.zip")));
        Assert.Empty(AccessRules.AdminOnlyEntryProblems(Nodes.ClosedFolder(Folder + "/runner", protectedAcl: false)));
    }

    [Fact]
    public void AnEntryInsideTheDataFolder_ThatIsALink_OwnedByAnOrdinaryUser_OrOpenToUsers_IsRefused()
    {
        Assert.NotEmpty(AccessRules.AdminOnlyEntryProblems(Nodes.Link(Folder + "/runner/99.0.0")));
        Assert.NotEmpty(AccessRules.AdminOnlyEntryProblems(Nodes.ClosedFile(Folder + "/runner/99.0.0/x.zip", owner: OwnerUser)));
        Assert.NotEmpty(AccessRules.AdminOnlyEntryProblems(Nodes.ClosedFile(Folder + "/token.bin", extra: [new AceRecord(WellKnownSids.Users, true, FileRights.ReadData)])));
        Assert.NotEmpty(AccessRules.AdminOnlyEntryProblems(NodeSecurity.Missing(Folder + "/gone")));
    }

    /// <summary>Round 4, QA minor 9: an entry for a stranger that applies to children only is still a way in, because everything made inside the entry later inherits it.</summary>
    [Theory]
    [InlineData(WellKnownSids.Users)]
    [InlineData(WellKnownSids.Everyone)]
    [InlineData(OwnerUser)]
    public void AnEntryInsideTheDataFolder_WithAnEntryForSomebodyElseThatOnlyAppliesToChildren_IsRefused(string sid)
    {
        var folder = Nodes.ClosedFolder(Folder + "/runner", protectedAcl: false, extra: [new AceRecord(sid, true, FileRights.ReadAndExecute, InheritOnly: true)]);

        var problems = AccessRules.AdminOnlyEntryProblems(folder);

        Assert.Contains(problems, p => p.Contains("gives access to", StringComparison.Ordinal) && p.Contains("Only the system and Administrators may have any", StringComparison.Ordinal));
        Assert.Empty(AccessRules.AdminOnlyEntryProblems(Nodes.ClosedFolder(Folder + "/runner", protectedAcl: false)));
    }

    // ---- folders above the data folder and the program

    [Fact]
    public void TheDefaultShapeOfProgramDataAndProgramFiles_IsAccepted_AsAFolderAbove()
    {
        Assert.Empty(AccessRules.AncestorFolderProblems(Nodes.Ancestor("/pd")));
        Assert.Empty(AccessRules.AncestorFolderProblems(Nodes.Ancestor("/pf", owner: WellKnownSids.TrustedInstaller)));
    }

    [Fact]
    public void AFolderAbove_WhereOrdinaryUsersCanRemoveRenameOrTakeOver_IsRefused()
    {
        foreach (var right in new[] { FileRights.DeleteChild, FileRights.Delete, FileRights.WriteDac, FileRights.WriteOwner, FileRights.GenericAll })
        {
            var node = Nodes.Ancestor("/tools", extra: [new AceRecord(WellKnownSids.AuthenticatedUsers, true, right)]);

            Assert.NotEmpty(AccessRules.AncestorFolderProblems(node));
        }
    }

    [Fact]
    public void AFolderAbove_WhereUsersMayOnlyAddNewEntries_OrWhereTheRightAppliesToChildrenOnly_IsAccepted()
    {
        var addOnly = Nodes.Ancestor("/pd", extra: [new AceRecord(WellKnownSids.Users, true, FileRights.WriteData | FileRights.AppendData)]);
        var childrenOnly = Nodes.Ancestor("/c", extra: [new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.FullControl & ~FileRights.WriteOwner, InheritOnly: true)]);

        Assert.Empty(AccessRules.AncestorFolderProblems(addOnly));
        Assert.Empty(AccessRules.AncestorFolderProblems(childrenOnly));
    }

    [Fact]
    public void AFolderAbove_ThatIsALinkOrOwnedByAnOrdinaryUser_IsRefused()
    {
        Assert.NotEmpty(AccessRules.AncestorFolderProblems(Nodes.Link("/pd")));
        Assert.NotEmpty(AccessRules.AncestorFolderProblems(Nodes.Ancestor("/pd", owner: OwnerUser)));
    }

    // ---- the program's folder and file

    [Fact]
    public void TheProgramFolder_WithFullControlForAdministratorsAndTheSystem_AndReadAndRunForUsers_IsAccepted()
    {
        Assert.Empty(AccessRules.ProgramFolderProblems(Nodes.ProgramFolder("/pf/DbdLocalCi")));
        Assert.Empty(AccessRules.ProgramFolderProblems(Nodes.ProgramFolder("/pf/DbdLocalCi", owner: WellKnownSids.TrustedInstaller)));
    }

    [Theory]
    [InlineData(FileRights.WriteData)]
    [InlineData(FileRights.AppendData)]
    [InlineData(FileRights.Delete)]
    [InlineData(FileRights.DeleteChild)]
    [InlineData(FileRights.WriteDac)]
    [InlineData(FileRights.GenericAll)]
    [InlineData(FileRights.GenericWrite)]
    public void TheProgramFolder_WhereUsersCanChangeAnything_IsRefused(uint right)
    {
        var node = Nodes.ProgramFolder("/pf/DbdLocalCi", extra: [new AceRecord(WellKnownSids.Users, true, right)]);

        Assert.Contains(AccessRules.ProgramFolderProblems(node), p => p.Contains("lets every user change the program", StringComparison.Ordinal));
    }

    /// <summary>Round 4, QA minor 9: the program's folder needs full control for Administrators as well as for the system; either one missing is a problem on its own.</summary>
    [Theory]
    [InlineData(WellKnownSids.Administrators, "Administrators")]
    [InlineData(WellKnownSids.LocalSystem, "the system")]
    public void TheProgramFolder_WithoutFullControlForOneOfTheTwoTrustedAccounts_IsRefused_ByName(string missing, string name)
    {
        var good = Nodes.ProgramFolder("/pf/DbdLocalCi");
        var node = good with { Aces = [.. good.Aces.Where(a => a.Sid != missing || a.Mask != FileRights.FullControl), new AceRecord(missing, true, FileRights.ReadAndExecute)] };

        var problems = AccessRules.ProgramFolderProblems(node);

        Assert.Contains(problems, p => p.Contains(name, StringComparison.Ordinal) && p.Contains("full control", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(AccessRules.ProgramFolderProblems(good));
    }

    [Fact]
    public void TheProgramFolder_ThatAnotherAccountOrAuthenticatedUsersCanUse_IsRefused()
    {
        Assert.NotEmpty(AccessRules.ProgramFolderProblems(Nodes.ProgramFolder("/pf/DbdLocalCi", extra: [new AceRecord(OwnerUser, true, FileRights.FullControl)])));
        Assert.NotEmpty(AccessRules.ProgramFolderProblems(Nodes.ProgramFolder("/pf/DbdLocalCi", extra: [new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.ReadAndExecute)])));
    }

    [Fact]
    public void TheProgramFolder_ThatInheritsItsPermissions_OrIsOwnedByAUser_OrIsALink_IsRefused()
    {
        var inheriting = Nodes.ProgramFolder("/pf/DbdLocalCi") with { DaclProtected = false };

        Assert.NotEmpty(AccessRules.ProgramFolderProblems(inheriting));
        Assert.NotEmpty(AccessRules.ProgramFolderProblems(Nodes.ProgramFolder("/pf/DbdLocalCi", owner: OwnerUser)));
        Assert.NotEmpty(AccessRules.ProgramFolderProblems(Nodes.Link("/pf/DbdLocalCi")));
    }

    [Fact]
    public void TheProgramFile_IsAcceptedWhenOnlyAdministratorsAndTheSystemCanChangeIt()
    {
        Assert.Empty(AccessRules.ProgramFileProblems(Nodes.ProgramFile("/pf/DbdLocalCi/DbdLocalCi.exe")));
    }

    [Fact]
    public void TheProgramFile_IsRefusedWhenAnyoneElseCanChangeIt_OrItIsALinkOrAFolderOrMissing()
    {
        Assert.NotEmpty(AccessRules.ProgramFileProblems(Nodes.ProgramFile("/pf/x.exe", extra: [new AceRecord(WellKnownSids.AuthenticatedUsers, true, FileRights.FullControl & ~FileRights.WriteOwner)])));
        Assert.NotEmpty(AccessRules.ProgramFileProblems(Nodes.ProgramFile("/pf/x.exe", owner: OwnerUser)));
        Assert.NotEmpty(AccessRules.ProgramFileProblems(Nodes.Link("/pf/x.exe", directory: false)));
        Assert.NotEmpty(AccessRules.ProgramFileProblems(Nodes.ClosedFolder("/pf/x.exe")));
        Assert.NotEmpty(AccessRules.ProgramFileProblems(NodeSecurity.Missing("/pf/x.exe")));
    }

    // ---- the work folder of the dedicated WSL account

    [Fact]
    public void TheWorkFolder_ForTheWslAccountOnly_WithModifyForTheAccount_IsAccepted()
    {
        var node = Nodes.ClosedFolder("/pd/DbdLocalCi-wsl", extra: [new AceRecord(WslUser, true, FileRights.Modify)]);

        Assert.Empty(AccessRules.WslWorkFolderProblems(node, WslUser));
    }

    /// <summary>Round 3, L4: with full control the account could change the permissions of the folder itself (open it to others, or make itself its owner).</summary>
    [Theory]
    [InlineData(FileRights.FullControl)]
    [InlineData(FileRights.Modify | FileRights.WriteDac)]
    [InlineData(FileRights.Modify | FileRights.WriteOwner)]
    public void TheWorkFolder_ThatLetsTheAccountChangeItsOwnPermissionsOrOwner_IsRefused(uint rights)
    {
        var node = Nodes.ClosedFolder("/pd/DbdLocalCi-wsl", extra: [new AceRecord(WslUser, true, rights)]);

        var problems = AccessRules.WslWorkFolderProblems(node, WslUser);

        Assert.Contains(problems, p => p.Contains("change its own permissions or its owner", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Modify, not full control", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWorkFolder_WhereTheDangerousRightComesFromAnotherEntryOfTheAccount_IsRefusedToo()
    {
        var node = Nodes.ClosedFolder("/pd/DbdLocalCi-wsl", extra: [new AceRecord(WslUser, true, FileRights.Modify), new AceRecord(WslUser, true, FileRights.WriteDac)]);

        Assert.NotEmpty(AccessRules.WslWorkFolderProblems(node, WslUser));
    }

    [Fact]
    public void TheWorkFolder_WhereTheDangerousRightIsOnlyForChildren_IsAccepted()
    {
        var node = Nodes.ClosedFolder("/pd/DbdLocalCi-wsl", extra: [new AceRecord(WslUser, true, FileRights.Modify), new AceRecord(WslUser, true, FileRights.FullControl, InheritOnly: true)]);

        Assert.Empty(AccessRules.WslWorkFolderProblems(node, WslUser));
    }

    [Theory]
    [InlineData(FileRights.ReadAndExecute)]
    [InlineData(FileRights.ReadData | FileRights.WriteData)]
    [InlineData(FileRights.Modify & ~FileRights.Delete)]
    [InlineData(FileRights.Modify & ~FileRights.AppendData)]
    [InlineData(FileRights.Modify & ~FileRights.Synchronize)]
    public void TheWorkFolder_WhereTheAccountCannotChangeWhatIsInIt_IsRefused(uint rights)
    {
        var node = Nodes.ClosedFolder("/pd/DbdLocalCi-wsl", extra: [new AceRecord(WslUser, true, rights)]);

        Assert.Contains(AccessRules.WslWorkFolderProblems(node, WslUser), p => p.Contains("right to change what is in it (Modify)", StringComparison.Ordinal));
    }

    [Fact]
    public void Modify_IsWhatExplorerStores_AndHasNeitherOfTheTwoRightsToChangePermissions()
    {
        Assert.Equal(0x1301BFu, FileRights.Modify);
        Assert.Equal(0u, FileRights.Modify & (FileRights.WriteDac | FileRights.WriteOwner));
        Assert.Equal(FileRights.Modify, FileRights.FullControl & FileRights.Modify);
    }

    [Fact]
    public void TheWorkFolder_ThatAnotherUserOrAllUsersCanUse_OrThatLacksTheAccount_IsRefused()
    {
        var withOwner = Nodes.ClosedFolder("/pd/j", extra: [new AceRecord(WslUser, true, FileRights.Modify), new AceRecord(OwnerUser, true, FileRights.ReadData)]);
        var withUsers = Nodes.ClosedFolder("/pd/j", extra: [new AceRecord(WslUser, true, FileRights.Modify), new AceRecord(WellKnownSids.Users, true, FileRights.ReadData)]);
        var withoutJob = Nodes.ClosedFolder("/pd/j");

        Assert.NotEmpty(AccessRules.WslWorkFolderProblems(withOwner, WslUser));
        Assert.NotEmpty(AccessRules.WslWorkFolderProblems(withUsers, WslUser));
        Assert.Contains(AccessRules.WslWorkFolderProblems(withoutJob, WslUser), p => p.Contains("account for the Linux machine the right to change what is in it", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWorkFolder_OwnedByTheAccount_OrInheritingItsPermissions_IsRefused()
    {
        var owned = Nodes.ClosedFolder("/pd/j", owner: WslUser, extra: [new AceRecord(WslUser, true, FileRights.Modify)]);
        var inheriting = Nodes.ClosedFolder("/pd/j", protectedAcl: false, extra: [new AceRecord(WslUser, true, FileRights.Modify)]);

        Assert.Contains(AccessRules.WslWorkFolderProblems(owned, WslUser), p => p.Contains("owned by", StringComparison.Ordinal));
        Assert.Contains(AccessRules.WslWorkFolderProblems(inheriting, WslUser), p => p.Contains("own permissions", StringComparison.Ordinal));
    }

    // ---- the rights

    [Theory]
    [InlineData(FileRights.WriteData, true)]
    [InlineData(FileRights.AppendData, true)]
    [InlineData(FileRights.Delete, true)]
    [InlineData(FileRights.WriteOwner, true)]
    [InlineData(FileRights.GenericWrite, true)]
    [InlineData(FileRights.MaximumAllowed, true)]
    [InlineData(FileRights.ReadAndExecute, false)]
    [InlineData(FileRights.ReadData, false)]
    public void WhichRightsChangeContent(uint mask, bool expected) => Assert.Equal(expected, AccessRules.MayChangeContent(mask));

    [Theory]
    [InlineData(FileRights.DeleteChild, true)]
    [InlineData(FileRights.WriteDac, true)]
    [InlineData(FileRights.WriteData, false)]
    [InlineData(FileRights.AppendData, false)]
    [InlineData(FileRights.ReadAndExecute, false)]
    public void WhichRightsLetSomeoneReplaceWhatIsInsideAFolder(uint mask, bool expected) => Assert.Equal(expected, AccessRules.MayReplaceChildren(mask));

    [Fact]
    public void OnlyAdministratorsTheSystemAndTheWindowsInstaller_AreTrustedOwners()
    {
        Assert.True(AccessRules.IsTrustedOwner(WellKnownSids.Administrators));
        Assert.True(AccessRules.IsTrustedOwner(WellKnownSids.LocalSystem));
        Assert.True(AccessRules.IsTrustedOwner(WellKnownSids.TrustedInstaller));
        Assert.False(AccessRules.IsTrustedOwner(OwnerUser));
        Assert.False(AccessRules.IsTrustedOwner(WellKnownSids.Users));
        Assert.False(AccessRules.IsTrustedOwner(null));
    }

    // ---- the edges the mutation run of round 3 found open: an entry for somebody else that only applies to what is created inside is still a way in

    [Fact]
    public void TheProgramFolder_WithAnEntryForSomebodyElseThatOnlyAppliesToChildren_IsRefused()
    {
        var node = Nodes.ProgramFolder("/pf/DbdLocalCi", extra: [new AceRecord(OwnerUser, true, FileRights.ReadAndExecute, InheritOnly: true)]);

        Assert.Contains(AccessRules.ProgramFolderProblems(node), p => p.Contains("gives access to", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWorkFolder_WithAnEntryForSomebodyElseThatOnlyAppliesToChildren_IsRefused()
    {
        var node = Nodes.ClosedFolder("/pd/DbdLocalCi-wsl", extra: [new AceRecord(WslUser, true, FileRights.Modify), new AceRecord(OwnerUser, true, FileRights.ReadAndExecute, InheritOnly: true)]);

        Assert.Contains(AccessRules.WslWorkFolderProblems(node, WslUser), p => p.Contains("gives access to", StringComparison.Ordinal));
    }
}
