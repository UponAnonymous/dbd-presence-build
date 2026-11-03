namespace DbdPresence.LocalCi;

/// <summary>
/// The rules for the dedicated Windows account that owns the Linux machine (local CI round 2, red-team F2), as pure functions: what the account must look like, what is wrong when it does not, and
/// the numbers Windows uses for it (netapi32 and the registry). The Windows class applies them; every rule is tested without Windows.
/// </summary>
public static class WslAccountPolicy
{
    /// <summary>The account's comment. An account of the right name without it was not made by this program and is never adopted, reset or removed.</summary>
    public const string Comment = "DBD Local CI: owns the Linux machine for the checks. Made and removed by DbdLocalCi.";

    // USER_INFO_1 flags (lmaccess.h): UF_SCRIPT must always be set, UF_ACCOUNTDISABLE, UF_LOCKOUT, UF_PASSWD_CANT_CHANGE, UF_DONT_EXPIRE_PASSWD.
    public const uint FlagScript = 0x1;
    public const uint FlagAccountDisable = 0x2;
    public const uint FlagLockout = 0x10;
    public const uint FlagPasswordCannotChange = 0x40;
    public const uint FlagDontExpirePassword = 0x10000;

    /// <summary>The flags the account has: a normal account that nobody can change the password of, whose password does not expire, which is neither switched off nor locked.</summary>
    public const uint DesiredFlags = FlagScript | FlagPasswordCannotChange | FlagDontExpirePassword;

    /// <summary>The well-known SID of the built-in group Users, the only group the account may be in.</summary>
    public const string UsersGroupSid = "S-1-5-32-545";

    /// <summary>The registry key whose DWORD values hide accounts from the sign-in screen (value 0 = hidden).</summary>
    public const string HiddenAccountsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    /// <summary>The registry key that holds the start type of the Secondary Logon service (4 = disabled).</summary>
    public const string SecondaryLogonKey = @"SYSTEM\CurrentControlSet\Services\seclogon";

    public const int ServiceDisabled = 4;

    public const string SecondaryLogonDisabledProblem = "the Windows service 'Secondary Logon' is switched off; it is needed to start the Linux machine as its own account (set it to Manual in Services)";

    /// <summary>True when the account's comment says that this program made it.</summary>
    public static bool IsOurs(string? comment) => string.Equals(comment, Comment, StringComparison.Ordinal);

    /// <summary>The flags to set to put an account right: the wanted ones, and neither switched off nor locked.</summary>
    public static uint RepairedFlags(uint current) => (current & ~(FlagAccountDisable | FlagLockout)) | DesiredFlags;

    /// <summary>True when the flags say the account is locked out.</summary>
    public static bool IsLockedOut(uint flags) => (flags & FlagLockout) != 0;

    /// <summary>The flags with the lock-out cleared and every other flag exactly as it was (the loop repairs the lock-out only; a switched-off account stays off).</summary>
    public static uint WithoutLockout(uint flags) => flags & ~FlagLockout;

    /// <summary>What is wrong with an existing account. Empty when it is as it must be.</summary>
    /// <param name="userName">The account name (for the messages).</param>
    /// <param name="flags">Its flags.</param>
    /// <param name="comment">Its comment.</param>
    /// <param name="groupSids">The SIDs of every group it is in (nested groups included); an empty string stands for a group that could not be identified.</param>
    public static IReadOnlyList<string> AccountProblems(string userName, uint flags, string? comment, IReadOnlyList<string> groupSids) =>
        [.. IdentityProblems(userName, comment, groupSids), .. StateProblems(userName, flags)];

    /// <summary>
    /// What makes an existing account of this name not this program's: it was not made by this program, or it is in a group other than Users. Such an account is never adopted, reset or removed;
    /// set-up stops and says so.
    /// </summary>
    public static IReadOnlyList<string> IdentityProblems(string userName, string? comment, IReadOnlyList<string> groupSids)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(groupSids);
        var problems = new List<string>();
        if (!IsOurs(comment))
        {
            problems.Add($"the account {userName} was not made by this program (its comment is not the one set-up writes), so it is not used");
        }

        foreach (var sid in groupSids)
        {
            if (sid.Length == 0)
            {
                problems.Add($"the account {userName} is in a group that cannot be identified");
            }
            else if (!string.Equals(sid, UsersGroupSid, StringComparison.Ordinal))
            {
                problems.Add($"the account {userName} is in the group {sid}; it must be in no group but Users");
            }
        }

        if (!groupSids.Contains(UsersGroupSid, StringComparer.Ordinal) && groupSids.All(s => s.Length > 0))
        {
            problems.Add($"the account {userName} is not in the group Users");
        }

        return problems;
    }

    /// <summary>What set-up repairs by itself: the account is switched off or locked out.</summary>
    public static IReadOnlyList<string> StateProblems(string userName, uint flags)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        var problems = new List<string>();
        if ((flags & FlagAccountDisable) != 0)
        {
            problems.Add($"the account {userName} is switched off");
        }

        if ((flags & FlagLockout) != 0)
        {
            problems.Add($"the account {userName} is locked out");
        }

        return problems;
    }
}
