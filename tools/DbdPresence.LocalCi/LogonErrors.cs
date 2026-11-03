namespace DbdPresence.LocalCi;

/// <summary>
/// Plain words for the Windows error codes <c>CreateProcessWithLogonW</c> gives when it cannot start a program as the dedicated account. The numbers are Windows system error codes
/// (winerror.h; the page "System Error Codes" on Microsoft Learn). Every message says what to do, because the owner meets these at the first run and nowhere else.
/// </summary>
public static class LogonErrors
{
    public static string Describe(int error, string userName) => error switch
    {
        1326 => $"Windows says the password of {userName} is wrong. The saved password no longer matches the account (somebody changed it). Run set-up again: it sets a new password.",
        1331 => $"The account {userName} is switched off. Run set-up again: it switches it on.",
        1909 => $"The account {userName} is locked out. Run set-up again: it unlocks it.",
        1327 or 1328 or 1329 => $"Windows will not let {userName} sign in here (a limit on the account: hours, computers or other restrictions). Run set-up again; if it stays, look at the account in Computer Management.",
        1330 or 1907 => $"The password of {userName} has run out. Run set-up again: it sets a new one that does not run out.",
        1385 or 1386 => $"Windows will not let {userName} sign in on this PC (a security policy forbids that kind of sign-in). The policy 'Allow log on locally' must include the group Users.",
        1058 => "The Windows service 'Secondary Logon' is switched off, and this program needs it to start the Linux machine as its own account. Open Services, set 'Secondary Logon' to Manual, then run set-up again.",
        1060 => "The Windows service 'Secondary Logon' is missing on this Windows.",
        1053 or 1460 => "Windows did not answer in time when asked to start the program as the account. Try again; if it keeps happening, restart the PC.",
        2 or 3 => "Windows cannot find the program it was told to start. Check that WSL is installed.",
        5 => $"Windows refused to start the program as {userName} (access denied). Run set-up again.",
        267 => "Windows cannot use the folder the program was to start in.",
        1314 => "Windows says this program lacks a right it needs to start another account's program.",
        _ => $"Windows error {error}.",
    };
}
