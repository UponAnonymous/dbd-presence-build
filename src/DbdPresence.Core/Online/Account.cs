namespace DbdPresence.Core.Online;

/// <summary>
/// Where the account stands (PLAN_V2 §3.3, D8, D32, FR-ONL-04/05/13). The app is locked (no presence) until the device holds a verified licence
/// (<see cref="Active"/> or <see cref="OfflineWithGrace"/>); once unlocked only a received, signed revocation locks it again (D8).
/// </summary>
public enum OnlineState
{
    /// <summary>Not signed in. The app is locked (D32): nothing is sent to Discord.</summary>
    SignedOut,

    /// <summary>Signed in, but the service wants an invite code first. The app is locked (D32).</summary>
    NeedsInvite,

    /// <summary>Signed in with a verified licence that has not lapsed. The app is unlocked.</summary>
    Active,

    /// <summary>A signed revocation was received. The app is locked and presence stops.</summary>
    Revoked,

    /// <summary>
    /// Unlocked by a stored, verified licence, but the service is unreachable, the licence has lapsed or the session ended. Presence keeps
    /// working (D8); online features wait.
    /// </summary>
    OfflineWithGrace,

    /// <summary>Signed in and the service granted access, but this PC holds no verified licence yet (it could not be checked). The app is locked until one is received.</summary>
    LicenceNeeded,
}

/// <summary>An immutable view of the account for the UI and the presence gate.</summary>
/// <param name="State">Where the account stands.</param>
/// <param name="DisplayName">The Discord display name the service returned (text only; the avatar is never shown), or null.</param>
/// <param name="OnlineFeaturesAllowed">True only with a current licence, a sane system clock and the service's emergency switch off.</param>
/// <param name="LicenceExpiresAt">When the current licence lapses, or null.</param>
/// <param name="Notice">A short explanation of anything unusual (clock behind, service unreachable), or null.</param>
/// <param name="PublicAccess">True only while a signed, unexpired configuration says the access mode is public: no invite is needed (sign-in still is).</param>
/// <param name="NeedsSignIn">True when the device is unlocked by a stored licence but its session ended, so only online features need a new sign-in.</param>
public sealed record AccountSnapshot(OnlineState State, string? DisplayName, bool OnlineFeaturesAllowed, DateTimeOffset? LicenceExpiresAt, string? Notice, bool PublicAccess = false, bool NeedsSignIn = false)
{
    /// <summary>The notice shown when the session ended; the Account page already says so in its status line and hides this repeat.</summary>
    public const string SessionEndedNotice = "Your session ended. Sign in with Discord again.";

    /// <summary>Gets the state for a user who has not signed in.</summary>
    public static AccountSnapshot SignedOut { get; } = new(OnlineState.SignedOut, null, false, null, null);

    /// <summary>
    /// Gets a value indicating whether local presence may run (D32, D8): true only while the device holds a verified licence. It is false before
    /// sign-in and invite, and after a received, signed revocation; it never turns false because a licence lapsed, the service is unreachable or
    /// the system clock changed.
    /// </summary>
    public bool PresenceAllowed => State is OnlineState.Active or OnlineState.OfflineWithGrace;

    /// <summary>Gets a value indicating whether the app is locked (the opposite of <see cref="PresenceAllowed"/>).</summary>
    public bool IsLocked => !PresenceAllowed;

    /// <summary>Gets a value indicating whether the user is signed in (any state except signed out).</summary>
    public bool IsSignedIn => State != OnlineState.SignedOut;
}

/// <summary>Why an account action did not succeed.</summary>
public enum AccountFailure
{
    /// <summary>It did succeed.</summary>
    None,

    /// <summary>The service could not be reached (offline, timeout, refused host or redirect).</summary>
    Offline,

    /// <summary>The user did not finish in the browser, or it timed out.</summary>
    NotCompleted,

    /// <summary>The service or the sign-in answer was refused (bad state, bad invite, rate limit, device limit, invalid document).</summary>
    Rejected,

    /// <summary>The device key or secret store failed.</summary>
    LocalStorage,

    /// <summary>The user must sign in again.</summary>
    SignInRequired,

    /// <summary>The service asked the app to wait (429 with <c>Retry-After</c>); see <see cref="AccountResult.RetryAfter"/>.</summary>
    RateLimited,
}

/// <summary>The result of an account action, with a message safe to show.</summary>
/// <param name="Failure">What went wrong, or <see cref="AccountFailure.None"/>.</param>
/// <param name="Message">A plain-language message for the user.</param>
/// <param name="RetryAfter">How long to wait before the next attempt when <paramref name="Failure"/> is <see cref="AccountFailure.RateLimited"/>; otherwise null.</param>
public sealed record AccountResult(AccountFailure Failure, string Message, TimeSpan? RetryAfter = null)
{
    /// <summary>Gets a value indicating whether the action succeeded.</summary>
    public bool Succeeded => Failure == AccountFailure.None;

    /// <summary>Creates a success result.</summary>
    /// <param name="message">What happened.</param>
    /// <returns>The result.</returns>
    public static AccountResult Ok(string message) => new(AccountFailure.None, message);

    /// <summary>Creates a failure result.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="message">What to tell the user.</param>
    /// <returns>The result.</returns>
    public static AccountResult Fail(AccountFailure failure, string message) => new(failure, message);

    /// <summary>Creates the result of a rate-limited call: the app must not try again before <paramref name="retryAfter"/> has passed.</summary>
    /// <param name="message">What to tell the user.</param>
    /// <param name="retryAfter">The wait.</param>
    /// <returns>The result.</returns>
    public static AccountResult RateLimited(string message, TimeSpan retryAfter) => new(AccountFailure.RateLimited, message, retryAfter);
}

/// <summary>The account as the UI uses it (implemented by <c>DbdPresence.Online</c>; Presentation never references that assembly).</summary>
public interface IAccountService
{
    /// <summary>Raised (on any thread) after <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current account view.</summary>
    AccountSnapshot Current { get; }

    /// <summary>Signs in with Discord in the system browser.</summary>
    /// <param name="cancellationToken">Cancels waiting for the browser.</param>
    /// <returns>The outcome.</returns>
    Task<AccountResult> SignInAsync(CancellationToken cancellationToken);

    /// <summary>Redeems an invite code.</summary>
    /// <param name="code">The code as typed.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<AccountResult> RedeemInviteAsync(string code, CancellationToken cancellationToken);

    /// <summary>Signs out on this PC. Local presence keeps working.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<AccountResult> SignOutAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the account on the service and then clears everything local.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<AccountResult> DeleteAccountAsync(CancellationToken cancellationToken);

    /// <summary>Refreshes the session, licence and configuration now when signed in or when they are due.</summary>
    /// <param name="force">Refresh even if the last refresh was recent.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    Task<AccountResult> RefreshAsync(bool force, CancellationToken cancellationToken);
}

/// <summary>One Discord application from the signed remote configuration.</summary>
/// <param name="Key">Stable key stored in settings.</param>
/// <param name="ApplicationId">Discord Application ID.</param>
/// <param name="Name">Name shown in Discord.</param>
/// <param name="Description">The application's unofficial-fan-app labelling.</param>
public sealed record RemoteApplication(string Key, string ApplicationId, string Name, string Description);

/// <summary>
/// The merged result of the signed configuration (FR-ONL-08, SAFETY_AND_PRIVACY §2.3). Every flag only <b>removes</b> capability:
/// there is no member that can switch capture, detection or any opt-in on.
/// </summary>
/// <param name="CaptureForcedOff">The service forces screen capture off.</param>
/// <param name="DetectionForcedOff">The service forces detection off.</param>
/// <param name="OnlineFeaturesEnabled">False while the emergency switch is on, or when the configuration is missing or expired (safe default).</param>
/// <param name="Applications">The Discord applications; empty means "use the built-in list". Kept from the last good signed copy after expiry (D8).</param>
/// <param name="DefaultApplicationKey">The key of the default application, or null.</param>
/// <param name="IsFresh">True while the signed configuration has not expired.</param>
/// <param name="Banner">Plain-text announcement from the owner (at most 280 characters, no control characters), or null. Never present once the configuration expired.</param>
/// <param name="MinimumVersion">The oldest app version the owner still supports (<c>major.minor.patch</c>), or null. Only a calm "please update" note; presence is never blocked (D8).</param>
/// <param name="AccessPublic">True only while the configuration is signed, verified and unexpired and says the access mode is public (D32): the invite step is skipped. It never unlocks by itself; a licence is still needed.</param>
public sealed record RemoteConfigSnapshot(
    bool CaptureForcedOff,
    bool DetectionForcedOff,
    bool OnlineFeaturesEnabled,
    IReadOnlyList<RemoteApplication> Applications,
    string? DefaultApplicationKey,
    bool IsFresh,
    string? Banner = null,
    string? MinimumVersion = null,
    bool AccessPublic = false)
{
    /// <summary>Gets the id of the announcement: 16 lower-case hex characters of the text's SHA-256, or null without a banner. A new text has a new id, so a dismissed banner shows again only when it changes.</summary>
    public string? AnnouncementId => Banner is null
        ? null
        : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Banner)))[..16];

    /// <summary>Whether <paramref name="appVersion"/> (for example <c>0.1.0+abc1234</c>) is below <see cref="MinimumVersion"/>. False when either is missing or unreadable.</summary>
    /// <param name="appVersion">The running app's version.</param>
    /// <returns>True when a newer version is needed.</returns>
    public bool IsUpdateNeeded(string appVersion)
    {
        ArgumentNullException.ThrowIfNull(appVersion);
        return TryParseVersion(MinimumVersion, out var minimum) && TryParseVersion(appVersion, out var running) && running < minimum;
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var core = text.AsSpan();
        var end = core.IndexOfAny('+', '-');
        if (end >= 0)
        {
            core = core[..end];
        }

        if (!Version.TryParse(core, out var parsed) || parsed.Build < 0)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>Gets the safe built-in defaults used when there is no usable signed configuration: online features off, capture and detection never enabled by the service.</summary>
    public static RemoteConfigSnapshot SafeDefaults { get; } = new(true, true, false, [], null, false);
}

/// <summary>Source of the merged remote configuration.</summary>
public interface IRemoteConfigSource
{
    /// <summary>Raised (on any thread) after <see cref="Current"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current merged configuration.</summary>
    RemoteConfigSnapshot Current { get; }
}
