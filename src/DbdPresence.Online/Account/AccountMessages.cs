using DbdPresence.Core.Online;
using DbdPresence.Online.Api;

namespace DbdPresence.Online.Account;

/// <summary>User-facing messages for account actions (plain language; never contain tokens, codes or keys).</summary>
public static class AccountMessages
{
    public const string SignedIn = "Signed in.";
    public const string SignedOut = "Signed out on this PC. DBD Presence is locked until you sign in again.";
    public const string Deleted = "Your account was deleted and its data on this PC was removed.";
    public const string DeletedFilesPending = "Your account was deleted and its data on this PC was removed. Your uploaded images no longer show at their addresses; the service deletes the last stored copies later.";
    public const string InviteAccepted = "Invite accepted. Online features are now available.";
    public const string Offline = "Could not reach the DBD Presence service. Your Discord status keeps working; try again later.";
    public const string OfflineLocked = "Could not reach the DBD Presence service. Check your internet connection and try again.";
    public const string BrowserFailed = "Could not open your browser. Open Discord's sign-in page yourself or check your default browser setting.";
    public const string SignInTimedOut = "Sign-in was not finished in time. Try again when you are ready.";
    public const string SignInDenied = "Sign-in was cancelled in Discord.";
    public const string SignInInvalid = "The browser's answer was not what DBD Presence expected, so it was ignored. Try signing in again.";
    public const string StateMismatch = "The sign-in answer did not match this attempt, so it was ignored. Try signing in again.";
    public const string SignInAgain = AccountSnapshot.SessionEndedNotice;
    public const string NeedSignIn = "Sign in with Discord first.";
    public const string Revoked = "Access for this account was withdrawn by the service owner. Your Discord status is off until that changes.";
    public const string BadInviteFormat = "That does not look like an invite code. Codes have 16 letters and digits, like XXXX-XXXX-XXXX-XXXX.";
    public const string InviteInvalid = "That code did not work. Check it and try again, or ask for a new one.";
    public const string DeviceLimit = "This account is already signed in on the maximum number of PCs. Sign out on another PC first.";
    public const string DeviceRevoked = "This PC was removed from the account. Ask the service owner for help.";
    public const string LicenceUnverified = "The service's licence could not be verified. Check that your PC's date and time are correct, then try again.";
    public const string ClockBehind = "Your PC's clock is earlier than the service's. Online features are paused until it is corrected; your Discord status is not affected.";
    public const string ClockProof = "The service did not accept the request. Check that your PC's date and time are correct, then try again.";
    public const string Rejected = "The service did not accept the request. Try again later.";
    public const string LocalStorage = "DBD Presence could not use Windows' protected storage for your sign-in, so it cannot keep you signed in on this PC.";
    public const string ServiceNotReady = "The service is not set up for sign-in yet. Try again later.";

    public const string PublicClosed = "DBD Presence is invite-only again. Try again in a moment, then enter your invite code.";
    public const string RedeemConflict = "Your code was being applied at the same moment as another change. Nothing was used up; enter it again in a moment.";
    public const string AccountChanged = "Your account changed while this was happening, so nothing was changed. Check the Account page and try again if you still want to.";
    public const string RefreshReplayed = "The service saw that request twice and asked the app to try again. Your sign-in is kept; it will retry on its own.";

    /// <summary>The message for a rate-limited call.</summary>
    /// <param name="retryAfter">The delay the service asked for, or null.</param>
    /// <returns>The message.</returns>
    public static string RateLimited(TimeSpan? retryAfter) =>
        retryAfter switch
        {
            { } delay when delay > TimeSpan.Zero && delay < TimeSpan.FromMinutes(1) => $"Too many attempts. Wait about {Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))} second(s) and try again.",
            { } delay when delay > TimeSpan.Zero => $"Too many attempts. Wait about {(int)Math.Ceiling(delay.TotalMinutes)} minute(s) and try again.",
            _ => "Too many attempts. Wait a few minutes and try again.",
        };

    /// <summary>The failure result of a rate-limited call, carrying the wait.</summary>
    /// <param name="retryAfter">The delay the service asked for, or null.</param>
    /// <returns>The result.</returns>
    public static AccountResult RateLimitedResult(TimeSpan? retryAfter) =>
        AccountResult.RateLimited(RateLimited(retryAfter), retryAfter is { } d && d > TimeSpan.Zero ? d : ServiceClient.DefaultRetryAfter);

    /// <summary>Whether a service error code is one of the 409 answers about a change that happened at the same time.</summary>
    /// <param name="code">The error code.</param>
    /// <returns>True for <c>user_changed</c> and <c>account_changed</c>.</returns>
    public static bool IsChangedConflict(string? code) => code is "user_changed" or "account_changed";
}
