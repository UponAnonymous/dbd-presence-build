// Covers: FR-CI-09 (the native structures the isolation passes to Windows have the sizes Windows expects on x64; the permissions of a private window station and desktop give access to the
// system, Administrators and that account only; the program paths the elevated loop starts are approved one by one and only by full path), security review 2026-10-30 H2 M3. These are
// managed or plain-data checks, so they run on any system; whether Windows accepts the calls is PENDING-ENV. docs/owner/free-ci.md.
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using DbdPresence.LocalCi.Windows;

namespace DbdPresence.LocalCi.Tests;

#pragma warning disable CA1416 // The checks below are about data layouts and managed security descriptors, which exist on every system.
public sealed class WindowsLayoutTests
{
    private const string AccountSid = "S-1-5-21-1111-2222-3333-1099";

    [Fact]
    public void TheNativeStructures_HaveTheSizesWindowsExpectsOnX64()
    {
        Assert.Equal(104, Unsafe.SizeOf<StartupInfo>());
        Assert.Equal(24, Unsafe.SizeOf<ProcessInformation>());
        Assert.Equal(24, Unsafe.SizeOf<SecurityAttributes>());
        Assert.Equal(64, Unsafe.SizeOf<JobObjectBasicLimitInformation>());
        Assert.Equal(48, Unsafe.SizeOf<IoCounters>());
        Assert.Equal(144, Unsafe.SizeOf<JobObjectExtendedLimitInformation>());
        Assert.Equal(4, Unsafe.SizeOf<JobObjectBasicUiRestrictions>());
        Assert.Equal(48, Unsafe.SizeOf<JobObjectBasicAccountingInformation>());
        Assert.Equal(52, Unsafe.SizeOf<ByHandleFileInformation>());
        Assert.Equal(56, Unsafe.SizeOf<UserInfo1>());
        Assert.Equal(8, Unsafe.SizeOf<UserInfo1003>());
        Assert.Equal(4, Unsafe.SizeOf<UserInfo1008>());
        Assert.Equal(8, Unsafe.SizeOf<LocalGroupUsersInfo0>());
        Assert.Equal(16, Unsafe.SizeOf<DataBlob>());
        Assert.Equal(8, Unsafe.SizeOf<Luid>());
        Assert.Equal(16, Unsafe.SizeOf<TokenPrivileges>());
    }

    [Fact]
    public void TheFlagsOfTheLinkFreeRead_AreTheDocumentedOnes()
    {
        Assert.Equal(0x80000000u, NativeMethods.GenericRead);
        Assert.Equal(0x1u, NativeMethods.ShareRead); // Only reading is shared: no FILE_SHARE_WRITE (0x2) and no FILE_SHARE_DELETE (0x4), so a file that is held cannot be written to, renamed or replaced.
        Assert.Equal(3u, NativeMethods.OpenExisting);
        Assert.Equal(0x00200000u, NativeMethods.FlagOpenReparsePoint);
        Assert.Equal(0x10u, NativeMethods.AttributeDirectory);
        Assert.Equal(0x400u, NativeMethods.AttributeReparsePoint);
        Assert.Equal(-1, (int)NativeMethods.InvalidHandleValue);
    }

    [Fact]
    public void TheBackupRightFlags_AreTheDocumentedOnes()
    {
        Assert.Equal(0x20u, NativeMethods.TokenAdjustPrivileges);
        Assert.Equal(0x8u, NativeMethods.TokenQuery);
        Assert.Equal(0x2u, NativeMethods.PrivilegeEnabled);
        Assert.Equal(1300, NativeMethods.ErrorNotAllAssigned);
    }

    [Fact]
    public void TheJobObjectLimits_AreExactlyTheDocumentedFlags()
    {
        Assert.Equal(0x2000u, AccountJobLimits.LimitKillOnJobClose);
        Assert.Equal(0x8u, AccountJobLimits.LimitActiveProcess);
        Assert.Equal(0x200u, AccountJobLimits.LimitJobMemory);
        Assert.Equal(0x2208u, AccountJobLimits.LimitFlags);
        Assert.Equal(0xFFu, AccountJobLimits.UiLimitAll);
        Assert.Equal(1, NativeMethods.JobObjectBasicAccountingInfo);
        Assert.Equal(4, NativeMethods.JobObjectBasicUiRestrictionsInfo);
        Assert.Equal(9, NativeMethods.JobObjectExtendedLimitInfo);
        Assert.Equal(0x1u, NativeMethods.LogonWithProfile);
        Assert.Equal(0x4u, NativeMethods.CreateSuspended);
        Assert.Equal(0x400u, NativeMethods.CreateUnicodeEnvironment);
        Assert.Equal(0x1u, NativeMethods.CreateOnly);
        Assert.Equal(0x000F037Fu, NativeMethods.WindowStationAllAccess);
        Assert.Equal(0x000F01FFu, NativeMethods.DesktopAllAccess);
    }

    [Fact]
    public void ThePrivateWindowStationAndDesktop_AreOpenToTheSystemAdministratorsAndTheAccountOnly()
    {
        // The managed security-descriptor classes throw on other systems ("ACL APIs ... are not supported on this platform"), so this check runs in the Windows CI.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PENDING-ENV: the security-descriptor classes exist only on Windows; this runs in the Windows CI.");
        foreach (var mask in new[] { NativeMethods.WindowStationAllAccess, NativeMethods.DesktopAllAccess })
        {
            var bytes = PrivateDesktopSecurity.Descriptor(AccountSid, mask);
            var descriptor = new RawSecurityDescriptor(bytes, 0);

            Assert.Equal(WellKnownSids.Administrators, descriptor.Owner!.Value);
            Assert.NotNull(descriptor.DiscretionaryAcl);
            var entries = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToList();
            Assert.Equal([WellKnownSids.LocalSystem, WellKnownSids.Administrators, AccountSid], entries.Select(e => e.SecurityIdentifier.Value).ToArray());
            Assert.All(entries, e =>
            {
                Assert.Equal(AceQualifier.AccessAllowed, e.AceQualifier);
                Assert.Equal(unchecked((int)mask), e.AccessMask);
            });
            Assert.DoesNotContain(entries, e => e.SecurityIdentifier.Value is WellKnownSids.Everyone or WellKnownSids.Users or WellKnownSids.AuthenticatedUsers);
        }
    }

    [Fact]
    public void ThePrivateDesktopGrantees_AreFixed()
    {
        Assert.Equal([WellKnownSids.LocalSystem, WellKnownSids.Administrators, AccountSid], PrivateDesktopSecurity.Grantees(AccountSid));
        Assert.Throws<ArgumentException>(() => PrivateDesktopSecurity.Grantees(string.Empty));
    }

    // ---- the programs the elevated loop may start

    private static readonly string s_system = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "win", "System32"));

    [Fact]
    public void OnlyApprovedProgramsByFullPath_MayBeStarted()
    {
        var programs = new ApprovedPrograms(s_system);

        Assert.Equal(programs.Wsl, programs.Require(programs.Wsl));
        Assert.Equal(programs.Schtasks, programs.Require(programs.Schtasks));
        Assert.Equal(programs.Taskkill, programs.Require(programs.Taskkill));
        Assert.Throws<InvalidOperationException>(() => programs.Require("wsl.exe"));
        Assert.Throws<InvalidOperationException>(() => programs.Require("taskkill.exe"));
        Assert.Throws<InvalidOperationException>(() => programs.Require(string.Empty));
        Assert.Throws<InvalidOperationException>(() => programs.Require(Path.Combine(Path.GetTempPath(), "evil", "wsl.exe")));
        Assert.Throws<InvalidOperationException>(() => programs.Approve("relative.exe"));
    }

    [Fact]
    public void AnApprovedFullPath_CanBeStarted_AndNothingNextToIt()
    {
        var programs = new ApprovedPrograms(s_system);
        var approved = Path.Combine(Path.GetTempPath(), "approved", "tool.exe");
        Assert.Throws<InvalidOperationException>(() => programs.Require(approved));

        programs.Approve(approved);

        Assert.Equal(approved, programs.Require(approved));
        var withDot = Path.Combine(Path.GetDirectoryName(approved)!, ".", "tool.exe");
        Assert.Equal(withDot, programs.Require(withDot));
        Assert.Throws<InvalidOperationException>(() => programs.Require(Path.Combine(Path.GetDirectoryName(approved)!, "other.exe")));
        Assert.Throws<InvalidOperationException>(() => programs.Require(Path.Combine(Path.GetDirectoryName(approved)!, "..", "approved2", "tool.exe")));
    }

    [Fact]
    public void TheWindowsPrograms_AreBuiltFromTheSystemFolder_AndComparedWithoutRegardToCase()
    {
        var programs = new ApprovedPrograms(s_system);

        foreach (var program in new[] { programs.Wsl, programs.Schtasks, programs.Taskkill })
        {
            Assert.True(Path.IsPathFullyQualified(program));
            Assert.StartsWith(s_system, program, StringComparison.Ordinal);
        }

        var upper = programs.Wsl.ToUpperInvariant();
        Assert.Equal(upper, programs.Require(upper));
    }
}
