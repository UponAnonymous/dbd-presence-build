// Covers: red-team F2 (the dedicated account is a persistent standard user in no group but Users, with this program's marker comment and a random 32-character password; an account of the same name
// that is not this program's, or is in another group, is never adopted, reset or removed; set-up repairs a switched-off or locked-out account), F6 (the one filter taskkill is ever given names the
// dedicated account and no other, so it can never reach the owner's own programs or the game), and the plain words for the logon errors the first run can meet. docs/owner/free-ci.md.
namespace DbdPresence.LocalCi.Tests;

public sealed class WslAccountPolicyTests
{
    private const string Users = "S-1-5-32-545";
    private const string Administrators = "S-1-5-32-544";

    // ---- the account

    [Fact]
    public void AnAccountThatThisProgramMade_InTheGroupUsersOnly_IsAsItMustBe()
    {
        Assert.Empty(WslAccountPolicy.AccountProblems("dbd-ci-wsl", WslAccountPolicy.DesiredFlags | 0x200, WslAccountPolicy.Comment, [Users]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A user somebody made")]
    [InlineData("DBD Local CI: owns the Linux machine for the checks. Made and removed by DbdLocalCi")]
    [InlineData("dbd local ci: owns the linux machine for the checks. made and removed by dbdlocalci.")]
    public void AnAccountWithoutThisProgramsComment_IsNotThisProgramsAndIsNeverAdopted(string? comment)
    {
        Assert.False(WslAccountPolicy.IsOurs(comment));
        Assert.Contains(WslAccountPolicy.IdentityProblems("dbd-ci-wsl", comment, [Users]), p => p.Contains("was not made by this program", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMarkerComment_IsExactlyWhatSetupWrites()
    {
        Assert.True(WslAccountPolicy.IsOurs(WslAccountPolicy.Comment));
        Assert.DoesNotContain("Claude", WslAccountPolicy.Comment, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new[] { Users, Administrators }, "S-1-5-32-544")]
    [InlineData(new[] { Users, "S-1-5-32-555" }, "S-1-5-32-555")]
    [InlineData(new[] { Administrators }, "S-1-5-32-544")]
    public void AnyGroupButUsers_IsAProblem_AndNamesTheGroup(string[] groups, string named)
    {
        var problems = WslAccountPolicy.IdentityProblems("dbd-ci-wsl", WslAccountPolicy.Comment, groups);

        Assert.Contains(problems, p => p.Contains(named, StringComparison.Ordinal) && p.Contains("no group but Users", StringComparison.Ordinal));
    }

    [Fact]
    public void AGroupThatCannotBeIdentified_FailsClosed()
    {
        var problems = WslAccountPolicy.IdentityProblems("dbd-ci-wsl", WslAccountPolicy.Comment, [Users, string.Empty]);

        Assert.Contains(problems, p => p.Contains("cannot be identified", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains("not in the group Users", StringComparison.Ordinal));
    }

    [Fact]
    public void NotBeingInUsers_IsAProblem()
    {
        Assert.Contains(WslAccountPolicy.IdentityProblems("dbd-ci-wsl", WslAccountPolicy.Comment, []), p => p.Contains("not in the group Users", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x2u, "switched off")]
    [InlineData(0x10u, "locked out")]
    [InlineData(0x12u, "switched off")]
    public void ASwitchedOffOrLockedOutAccount_IsAProblem(uint flags, string expected)
    {
        Assert.Contains(WslAccountPolicy.StateProblems("dbd-ci-wsl", flags), p => p.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(WslAccountPolicy.AccountProblems("dbd-ci-wsl", flags, WslAccountPolicy.Comment, [Users]), p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AGoodState_HasNoProblem()
    {
        Assert.Empty(WslAccountPolicy.StateProblems("dbd-ci-wsl", WslAccountPolicy.DesiredFlags));
    }

    [Theory]
    [InlineData(0x201u)]
    [InlineData(0x203u)]
    [InlineData(0x211u)]
    [InlineData(0x0u)]
    public void RepairedFlags_AreSwitchedOnAndUnlocked_KeepEveryOtherBit_AndHaveTheWantedOnes(uint current)
    {
        var repaired = WslAccountPolicy.RepairedFlags(current);

        Assert.Equal(0u, repaired & WslAccountPolicy.FlagAccountDisable);
        Assert.Equal(0u, repaired & WslAccountPolicy.FlagLockout);
        Assert.Equal(WslAccountPolicy.DesiredFlags, repaired & WslAccountPolicy.DesiredFlags);
        Assert.Equal(current & 0x200u, repaired & 0x200u);
    }

    [Fact]
    public void TheWantedFlags_AreTheDocumentedOnes()
    {
        Assert.Equal(0x1u, WslAccountPolicy.FlagScript);
        Assert.Equal(0x2u, WslAccountPolicy.FlagAccountDisable);
        Assert.Equal(0x10u, WslAccountPolicy.FlagLockout);
        Assert.Equal(0x40u, WslAccountPolicy.FlagPasswordCannotChange);
        Assert.Equal(0x10000u, WslAccountPolicy.FlagDontExpirePassword);
        Assert.Equal(0x10041u, WslAccountPolicy.DesiredFlags);
        Assert.Equal("S-1-5-32-545", WslAccountPolicy.UsersGroupSid);
        Assert.Equal(4, WslAccountPolicy.ServiceDisabled);
        Assert.EndsWith(@"Winlogon\SpecialAccounts\UserList", WslAccountPolicy.HiddenAccountsKey, StringComparison.Ordinal);
        Assert.EndsWith(@"Services\seclogon", WslAccountPolicy.SecondaryLogonKey, StringComparison.Ordinal);
    }

    [Fact]
    public void BadArguments_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => WslAccountPolicy.AccountProblems(string.Empty, 0, null, []));
        Assert.Throws<ArgumentNullException>(() => WslAccountPolicy.IdentityProblems("x", null, null!));
        Assert.Throws<ArgumentException>(() => WslAccountPolicy.StateProblems(string.Empty, 0));
    }

    // ---- the password

    [Fact]
    public void ThePassword_Is32Characters_WithEveryKindOfCharacter_AndNeverTheSameTwice()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var password = WslAccountPassword.New();
            seen.Add(password);

            Assert.Equal(32, password.Length);
            Assert.Contains(password, char.IsAsciiLetterLower);
            Assert.Contains(password, char.IsAsciiLetterUpper);
            Assert.Contains(password, char.IsAsciiDigit);
            Assert.Contains(password, c => "!#%+-=?@^_".Contains(c, StringComparison.Ordinal));
            Assert.All(password, c => Assert.True(char.IsAsciiLetterOrDigit(c) || "!#%+-=?@^_".Contains(c, StringComparison.Ordinal), $"'{c}' is not an allowed character"));
        }

        Assert.Equal(200, seen.Count);
    }

    [Fact]
    public void ThePassword_LeavesOutTheLettersThatLookAlike()
    {
        var all = string.Concat(Enumerable.Range(0, 300).Select(_ => WslAccountPassword.New()));

        Assert.DoesNotContain('l', all);
        Assert.DoesNotContain('I', all);
        Assert.DoesNotContain('O', all);
        Assert.DoesNotContain('0', all);
        Assert.DoesNotContain('1', all);
    }

    // ---- taskkill

    [Fact]
    public void TaskkillIsGivenOneFilter_ForTheAccountOnly()
    {
        var arguments = TaskkillArguments.ForTheAccount("OWNERS-PC", "dbd-ci-wsl");

        Assert.Equal(["/F", "/T", "/FI", @"USERNAME eq OWNERS-PC\dbd-ci-wsl"], arguments);
        Assert.Equal(128, TaskkillArguments.NothingFound);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Administrator")]
    [InlineData("SYSTEM")]
    [InlineData("dbd-ci-wsl2")]
    [InlineData("dbd-ci-wsl ")]
    [InlineData(" dbd-ci-wsl")]
    [InlineData("dbd-ci-20261029120000")]
    [InlineData("dbd-ci-job-20261029120000")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("dbd-ci-wsl\" /FI \"USERNAME ne x")]
    public void TaskkillIsNeverGivenAnyOtherUser_NotTheOwnerNotAnAlmostMatch(string user)
    {
        Assert.Throws<ArgumentException>(() => TaskkillArguments.ForTheAccount("OWNERS-PC", user));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PC name")]
    [InlineData("PC\"")]
    [InlineData("PC\\x")]
    [InlineData("-PC")]
    [InlineData("PC;calc")]
    [InlineData("PC\n")]
    public void ANameOfTheComputerThatCouldChangeTheFilter_IsRefused(string machine)
    {
        Assert.Throws<ArgumentException>(() => TaskkillArguments.ForTheAccount(machine, "dbd-ci-wsl"));
    }

    [Fact]
    public void TheAccountNameIsAcceptedInAnyCase_ButTheFilterAlwaysHasTheRealName()
    {
        var arguments = TaskkillArguments.ForTheAccount("PC", "DBD-CI-WSL");

        Assert.Equal(@"USERNAME eq PC\dbd-ci-wsl", arguments[3]);
    }

    // ---- the logon errors

    [Theory]
    [InlineData(1326, "password of dbd-ci-wsl is wrong")]
    [InlineData(1331, "switched off")]
    [InlineData(1909, "locked out")]
    [InlineData(1327, "will not let dbd-ci-wsl sign in")]
    [InlineData(1328, "will not let dbd-ci-wsl sign in")]
    [InlineData(1329, "will not let dbd-ci-wsl sign in")]
    [InlineData(1330, "has run out")]
    [InlineData(1907, "has run out")]
    [InlineData(1385, "Allow log on locally")]
    [InlineData(1386, "Allow log on locally")]
    [InlineData(1058, "Secondary Logon")]
    [InlineData(1060, "Secondary Logon")]
    [InlineData(1053, "did not answer in time")]
    [InlineData(2, "cannot find the program")]
    [InlineData(5, "access denied")]
    [InlineData(267, "folder")]
    [InlineData(1314, "lacks a right")]
    [InlineData(4711, "Windows error 4711")]
    public void ALogonErrorIsSaidInPlainWords_WithWhatToDo(int error, string expected)
    {
        var text = LogonErrors.Describe(error, "dbd-ci-wsl");

        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Theory]
    [InlineData(1326)]
    [InlineData(1331)]
    [InlineData(1909)]
    [InlineData(1330)]
    public void TheErrorsSetUpCanRepair_SaySo(int error)
    {
        Assert.Contains("Run set-up again", LogonErrors.Describe(error, "dbd-ci-wsl"), StringComparison.Ordinal);
    }
}
