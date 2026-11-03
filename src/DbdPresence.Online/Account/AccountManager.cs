using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Config;
using DbdPresence.Online.SignIn;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Account;

/// <summary>Everything <see cref="AccountManager"/> needs, so the composition root and tests build it the same way.</summary>
/// <param name="Api">The service client.</param>
/// <param name="Keys">The device key store.</param>
/// <param name="Secrets">Protected storage for tokens, licence and clock mark.</param>
/// <param name="Verifier">Verifier trusting the licence key for purpose <c>licence</c>; its version store is persistent.</param>
/// <param name="Config">The remote configuration service.</param>
/// <param name="Browser">Opens Discord's authorize page.</param>
/// <param name="Listeners">Starts the loopback listener.</param>
/// <param name="Time">The clock.</param>
/// <param name="Log">Diagnostics (no secrets).</param>
/// <param name="DiscordClientId">The Discord application id used as OAuth client id (from the application catalog).</param>
public sealed record AccountDependencies(
    ServiceClient Api,
    IDeviceKeyStore Keys,
    ISecretStore Secrets,
    SignedDocumentVerifier Verifier,
    RemoteConfigService Config,
    IBrowserLauncher Browser,
    ICallbackListenerFactory Listeners,
    TimeProvider Time,
    IOnlineLog Log,
    string DiscordClientId);

/// <summary>
/// Sign-in, session, licence, invite and deletion logic (FR-ONL-01 to FR-ONL-06, FR-ONL-11).
/// <para>
/// D8: local presence is never stopped by an outage, an expired licence or the clock rule. The state becomes
/// <see cref="OnlineState.Revoked"/> only when a <b>signed</b> licence with <c>status: "revoked"</c> for this user and this device's
/// key thumbprint has been received and verified; an unsigned 403 body is an ordinary error.
/// </para>
/// </summary>
public sealed class AccountManager : IAccountService, IDisposable
{
    /// <summary>The licence channel.</summary>
    public const string LicenceChannel = "stable";

    /// <summary>How long before the 1-hour access token lapses a refresh is made first.</summary>
    public static readonly TimeSpan AccessTokenMargin = TimeSpan.FromSeconds(60);

    /// <summary>How often the session, licence and configuration are refreshed when online.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>How far the PC clock may trail the newest server time seen before online features pause.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>How long after a rotation the service accepts the same refresh token again as an idempotent retry (service contract, ADR-0011).</summary>
    public static readonly TimeSpan RefreshRetryWindow = TimeSpan.FromSeconds(30);

    /// <summary>Safety margin kept off <see cref="RefreshRetryWindow"/> for the time a retry spends on the wire.</summary>
    public static readonly TimeSpan RefreshRetryMargin = TimeSpan.FromSeconds(5);

    private const string RefreshTokenSecret = "refresh-token";
    private const string AccessTokenSecret = "access-token";
    private const string LicenceSecret = "licence";
    private const string ProfileSecret = "profile";
    private const string ClockMarkSecret = "clock-mark";

    /// <summary>The well-formed code sent to claim access in public mode; the service ignores the code in public mode (ADR-0011) and refuses it as an invalid invite otherwise.</summary>
    private const string PublicModePlaceholderCode = "0000-0000-0000-0000";
    private const string InviteAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly AccountDependencies _d;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Lock _state = new();
    private string? _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;
    private string? _licenceDocument;
    private Profile? _profile;
    private DateTimeOffset? _clockMark;
    private bool _networkFailed;
    private string? _thumbprint;
    private AccountSnapshot _snapshot = AccountSnapshot.SignedOut;

    /// <summary>Initializes a new instance of the <see cref="AccountManager"/> class and loads what is stored.</summary>
    /// <param name="dependencies">The collaborators.</param>
    public AccountManager(AccountDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _d = dependencies;
        LoadStored();
        _d.Config.Changed += OnConfigChanged;
        _snapshot = Evaluate();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public AccountSnapshot Current
    {
        get
        {
            lock (_state)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Recomputes the view from the clock (a licence can lapse and the clock can move without any call).</summary>
    public void Reevaluate() => Publish();

    /// <summary>Normalises typed invite text: upper case, separators removed, Crockford look-alikes mapped; null unless 16 valid characters remain.</summary>
    /// <param name="input">What the user typed.</param>
    /// <returns>The 16 characters, or null.</returns>
    public static string? NormalizeInviteCode(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var cleaned = new string([.. input.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-')])
            .Replace('O', '0').Replace('I', '1').Replace('L', '1');
        return cleaned.Length == 16 && cleaned.All(c => InviteAlphabet.Contains(c, StringComparison.Ordinal)) ? cleaned : null;
    }

    /// <inheritdoc />
    public async Task<AccountResult> SignInAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await SignInCoreAsync(cancellationToken).ConfigureAwait(false);
            return await ClaimPublicAccessAsync(result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> RedeemInviteAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeInviteCode(code);
        if (normalized is null)
        {
            return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.BadInviteFormat);
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var display = string.Join('-', Enumerable.Range(0, 4).Select(i => normalized.Substring(i * 4, 4)));

            // 409 redeem_conflict: a concurrent change; the service gave the code back, so one more try is safe (and only one).
            for (var attempt = 0; ; attempt++)
            {
                var (result, failure) = await CallWithSessionAsync((token, ct) => _d.Api.RedeemInviteAsync(token, display, ct), cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    return failure;
                }

                if (result!.Outcome == ApiOutcome.Rejected && result.ErrorCode == "redeem_conflict" && attempt == 0)
                {
                    continue;
                }

                return await InterpretRedeemAsync(result, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<AccountResult> InterpretRedeemAsync(ApiResult<RedeemGrant> result, CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                return await AcceptRedeemAsync(result.Value!, cancellationToken).ConfigureAwait(false);
            case ApiOutcome.Rejected when result.ErrorCode == "invite_invalid":
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.InviteInvalid);
            case ApiOutcome.Rejected when result.ErrorCode == "redeem_conflict":
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.RedeemConflict);
            case ApiOutcome.Rejected when AccountMessages.IsChangedConflict(result.ErrorCode):
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.AccountChanged);
            case ApiOutcome.RateLimited:
                return AccountMessages.RateLimitedResult(result.RetryAfter);
            case ApiOutcome.Forbidden:
                return HandleForbidden(result.RevocationDocument);
            case ApiOutcome.Unreachable:
            case ApiOutcome.ServerError:
                return OfflineResult();
            default:
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.Rejected);
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> SignOutAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } token && _accessExpiresAt > _d.Time.GetUtcNow())
            {
                // Best effort: signing out on this PC must work offline too, so the outcome of the server call does not matter.
                _ = await _d.Api.LogoutAsync(token, cancellationToken).ConfigureAwait(false);
            }

            return ClearSession(AccountMessages.SignedOut, deleteDeviceKey: false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> DeleteAccountAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (result, failure) = await CallWithSessionAsync((token, ct) => _d.Api.DeleteAccountAsync(token, ct), cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }

            if (result!.IsOk && result.Value is { Deleted: true } report)
            {
                return ClearSession(report.FilesRemoved ? AccountMessages.Deleted : AccountMessages.DeletedFilesPending, deleteDeviceKey: true);
            }

            // 409 account_changed / user_changed: the status changed while the delete ran and nothing was deleted. Not retried
            // automatically (a deletion is only repeated when the user asks again).
            return result.Outcome switch
            {
                ApiOutcome.Unreachable or ApiOutcome.ServerError => OfflineResult(),
                ApiOutcome.RateLimited => AccountMessages.RateLimitedResult(result.RetryAfter),
                ApiOutcome.Rejected when AccountMessages.IsChangedConflict(result.ErrorCode) => AccountResult.Fail(AccountFailure.Rejected, AccountMessages.AccountChanged),
                ApiOutcome.Forbidden => HandleForbidden(result.RevocationDocument),
                _ => AccountResult.Fail(AccountFailure.Rejected, AccountMessages.Rejected),
            };
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AccountResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await RefreshLockedAsync(force, cancellationToken).ConfigureAwait(false);
            return await ClaimPublicAccessAsync(result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<AccountResult> RefreshLockedAsync(bool force, CancellationToken cancellationToken)
    {
        var configOk = await _d.Config.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (_refreshToken is null)
        {
            RaiseClockMarkFrom(_d.Config.NewestIssuedAt);
            Publish();
            return configOk ? AccountResult.Ok("Up to date.") : OfflineResult();
        }

        // A signed-in device with no verified licence retries on every scheduled run, not once a day: it is locked until one arrives (D32).
        var due = _profile?.LastRefreshUtc is not { } last || _d.Time.GetUtcNow() - last >= RefreshInterval || Current.State == OnlineState.LicenceNeeded;
        if (!force && !due)
        {
            RaiseClockMarkFrom(_d.Config.NewestIssuedAt);
            Publish();
            return AccountResult.Ok("Up to date.");
        }

        RaiseClockMarkFrom(_d.Config.NewestIssuedAt);
        return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Public mode (D32, FR-ONL-13): when a signed, unexpired configuration says the access mode is public and this account still lacks access,
    /// the app asks the service to grant it, with no invite. The service decides (it grants only when its own setting is public); the licence it
    /// returns is verified like any other, and the app unlocks only on that licence. An unsigned, forged or expired configuration never reaches
    /// here, because <see cref="RemoteConfigSnapshot.AccessPublic"/> is false for it.
    /// </summary>
    private async Task<AccountResult> ClaimPublicAccessAsync(AccountResult previous, CancellationToken cancellationToken)
    {
        if (_refreshToken is null || _profile is not { HasAccess: false, Revoked: false } || !_d.Config.Current.AccessPublic)
        {
            return previous;
        }

        var (result, failure) = await CallWithSessionAsync((token, ct) => _d.Api.RedeemInviteAsync(token, PublicModePlaceholderCode, ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return previous.Succeeded ? failure : previous;
        }

        var claimed = await InterpretRedeemAsync(result!, cancellationToken).ConfigureAwait(false);
        if (claimed.Succeeded)
        {
            return AccountResult.Ok(AccountMessages.SignedIn);
        }

        if (claimed.Message == AccountMessages.InviteInvalid)
        {
            // The cached configuration is stale: the service is invite-only again. The next configuration refresh corrects the view.
            claimed = AccountResult.Fail(AccountFailure.Rejected, AccountMessages.PublicClosed);
        }

        return previous.Succeeded ? claimed : previous;
    }

    /// <summary>
    /// Runs one call that needs the session, for the other online services (uploads, FR-ONL-09): under the same lock as every account action,
    /// with the access token refreshed first when due and once more after a 401. A 403 carrying a signed revocation is acted on here exactly as for
    /// the account's own calls (D8: presence stops only on a received, verified revocation). Online features must be allowed (a current licence,
    /// a sane clock, the emergency switch off) or nothing is sent.
    /// </summary>
    /// <typeparam name="T">The success payload.</typeparam>
    /// <param name="call">The call, given the access token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The service's answer, or the account failure that stopped it before or after the call.</returns>
    internal async Task<(ApiResult<T>? Result, AccountResult? Failure)> CallAuthorizedAsync<T>(Func<string, CancellationToken, Task<ApiResult<T>>> call, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Current.OnlineFeaturesAllowed)
            {
                return (null, Current.IsSignedIn
                    ? AccountResult.Fail(AccountFailure.Rejected, Current.Notice ?? "Online features are not available right now. Your Discord status keeps working.")
                    : AccountResult.Fail(AccountFailure.SignInRequired, AccountMessages.NeedSignIn));
            }

            var (result, failure) = await CallWithSessionAsync(call, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return (null, failure);
            }

            if (result!.Outcome == ApiOutcome.Forbidden && result.RevocationDocument is not null)
            {
                return (null, HandleForbidden(result.RevocationDocument));
            }

            if (result.Outcome == ApiOutcome.Unreachable)
            {
                return (null, OfflineResult());
            }

            return (result, null);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Stops listening to configuration changes.</summary>
    public void Dispose()
    {
        _d.Config.Changed -= OnConfigChanged;
        _operation.Dispose();
    }

    private async Task<AccountResult> SignInCoreAsync(CancellationToken cancellationToken)
    {
        DevicePublicKey device;
        try
        {
            device = _d.Keys.GetOrCreatePublicKey();
        }
        catch (DeviceKeyException ex)
        {
            _d.Log.Warning("Device key unavailable: " + ex.GetType().Name);
            return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
        }

        var pkce = PkceChallenge.Create();
        using var listener = _d.Listeners.Start();
        var redirect = PkceChallenge.RedirectUri(listener.Port);
        if (!_d.Browser.TryOpen(pkce.BuildAuthorizeUrl(_d.DiscordClientId, redirect)))
        {
            return AccountResult.Fail(AccountFailure.NotCompleted, AccountMessages.BrowserFailed);
        }

        var callback = await listener.WaitAsync(pkce.State, cancellationToken).ConfigureAwait(false);
        switch (callback.Kind)
        {
            case CallbackKind.TimedOut:
                return AccountResult.Fail(AccountFailure.NotCompleted, AccountMessages.SignInTimedOut);
            case CallbackKind.Denied:
                return AccountResult.Fail(AccountFailure.NotCompleted, AccountMessages.SignInDenied);
            case CallbackKind.Invalid:
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.SignInInvalid);
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(callback.State ?? string.Empty), Encoding.UTF8.GetBytes(pkce.State)))
        {
            return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.StateMismatch);
        }

        var result = await _d.Api.SignInAsync(callback.Code!, pkce.Verifier, redirect, device, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                return await ApplyGrantAsync(result.Value!, signIn: true, cancellationToken).ConfigureAwait(false);
            case ApiOutcome.Forbidden when result.ErrorCode == "device_revoked":
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.DeviceRevoked);
            case ApiOutcome.Forbidden:
                return HandleForbidden(result.RevocationDocument, signingIn: true);
            case ApiOutcome.Rejected when result.ErrorCode == "device_limit":
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.DeviceLimit);
            case ApiOutcome.RateLimited:
                return AccountMessages.RateLimitedResult(result.RetryAfter);
            case ApiOutcome.ServerError when result.ErrorCode == "not_configured":
                return AccountResult.Fail(AccountFailure.Offline, AccountMessages.ServiceNotReady);
            case ApiOutcome.Unreachable:
            case ApiOutcome.ServerError:
                return OfflineResult();
            default:
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.Rejected);
        }
    }

    private async Task<AccountResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var token = _refreshToken!;
        RefreshProof proof;
        try
        {
            proof = BuildProof(token);
        }
        catch (DeviceKeyException ex)
        {
            _d.Log.Warning("Device key unavailable: " + ex.GetType().Name);
            return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
        }

        // A lost response or a timeout may mean the service rotated the token anyway. Within its 30 s grace the same token
        // presented again with a NEW proof (fresh nonce) returns the same new pair, so one retry is made, and only while
        // the grace can still be open. A resent request with the same nonce (401 proof_replayed) revokes nothing and is
        // retried the same way. Any other reuse is the service ending the session.
        var started = _d.Time.GetUtcNow();
        for (var attempt = 0; ; attempt++)
        {
            var result = await _d.Api.RefreshAsync(token, proof, cancellationToken).ConfigureAwait(false);
            var mayRetry = attempt == 0 && _d.Time.GetUtcNow() - started < RefreshRetryWindow - RefreshRetryMargin;
            if (mayRetry && (result.Outcome is ApiOutcome.Unreachable or ApiOutcome.ServerError || (result.Outcome == ApiOutcome.Unauthorized && result.ErrorCode == "proof_replayed")))
            {
                try
                {
                    proof = BuildProof(token);
                }
                catch (DeviceKeyException ex)
                {
                    _d.Log.Warning("Device key unavailable: " + ex.GetType().Name);
                    return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
                }

                continue;
            }

            return await InterpretRefreshAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AccountResult> InterpretRefreshAsync(ApiResult<SessionGrant> result, CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                _networkFailed = false;
                return await ApplyGrantAsync(result.Value!, signIn: false, cancellationToken).ConfigureAwait(false);
            case ApiOutcome.Unauthorized when result.ErrorCode is "invalid_refresh_token" or "refresh_token_reused" or "session_revoked":
                // The token family is gone (rotated elsewhere, reused, or revoked): only a new sign-in helps (FR-ONL-03). This is
                // not a signed revocation of the person, so the stored licence and the unlock stay (D8, D32); only online features wait.
                _d.Log.Warning("Session ended by the service: " + result.ErrorCode);
                return EndSession();
            case ApiOutcome.Unauthorized when result.ErrorCode == "proof_replayed":
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.RefreshReplayed);
            case ApiOutcome.Unauthorized:
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.ClockProof);
            case ApiOutcome.Forbidden:
                return HandleForbidden(result.RevocationDocument);
            case ApiOutcome.RateLimited:
                // Not an outage: the session and licence stand. The client's rate-limit gate keeps further calls off the network.
                return AccountMessages.RateLimitedResult(result.RetryAfter);
            case ApiOutcome.Unreachable:
            case ApiOutcome.ServerError:
                return OfflineResult();
            default:
                return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.Rejected);
        }
    }

    private RefreshProof BuildProof(string refreshToken)
    {
        var timestamp = _d.Time.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var signature = _d.Keys.Sign(ServiceClient.ProofMessage(refreshToken, timestamp, nonce));
        return new RefreshProof(timestamp, nonce, Base64Url.EncodeToString(signature));
    }

    private async Task<AccountResult> ApplyGrantAsync(SessionGrant grant, bool signIn, CancellationToken cancellationToken)
    {
        var existing = _profile;
        var profile = new Profile(
            signIn ? grant.UserId ?? string.Empty : existing?.UserId ?? grant.UserId ?? string.Empty,
            signIn ? CleanName(grant.DisplayName) : existing?.DisplayName,
            grant.HasAccess,
            Revoked: false,
            _d.Time.GetUtcNow());
        try
        {
            // The rotated refresh token is written first: losing it would lose the session.
            _d.Secrets.Write(RefreshTokenSecret, grant.RefreshToken);
            _d.Secrets.Write(AccessTokenSecret, grant.AccessToken + "\n" + grant.AccessExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            _d.Secrets.Write(ProfileSecret, profile.ToJson());
        }
        catch (SecretStoreException ex)
        {
            _d.Log.Warning("Secret store write failed: " + ex.GetType().Name);
            return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
        }

        _refreshToken = grant.RefreshToken;
        _accessToken = grant.AccessToken;
        _accessExpiresAt = grant.AccessExpiresAt;
        _profile = profile;
        _networkFailed = false;

        AccountResult outcome = AccountResult.Ok(signIn ? AccountMessages.SignedIn : "Up to date.");
        if (grant.HasAccess && !AcceptLicence(grant.LicenceDocument, expectRevoked: false))
        {
            outcome = AccountResult.Fail(AccountFailure.Rejected, AccountMessages.LicenceUnverified);
        }

        _ = await _d.Config.RefreshAsync(cancellationToken).ConfigureAwait(false);
        RaiseClockMarkFrom(_d.Config.NewestIssuedAt);
        Publish();
        return outcome;
    }

    private async Task<AccountResult> AcceptRedeemAsync(RedeemGrant grant, CancellationToken cancellationToken)
    {
        if (_profile is null)
        {
            return AccountResult.Fail(AccountFailure.SignInRequired, AccountMessages.NeedSignIn);
        }

        if (!AcceptLicence(grant.LicenceDocument, expectRevoked: false))
        {
            return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.LicenceUnverified);
        }

        _profile = _profile with { HasAccess = true };
        PersistProfile();
        _ = await _d.Config.RefreshAsync(cancellationToken).ConfigureAwait(false);
        Publish();
        return AccountResult.Ok(AccountMessages.InviteAccepted);
    }

    /// <summary>
    /// A 403 is an ordinary error unless it carries a signed revocation that verifies for this user and device (D8). The unsigned
    /// <c>error: "revoked"</c> text alone never changes the state.
    /// </summary>
    private AccountResult HandleForbidden(string? revocationDocument, bool signingIn = false)
    {
        if (revocationDocument is not null && AcceptLicence(revocationDocument, expectRevoked: true, signingIn))
        {
            Publish();
            return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.Revoked);
        }

        return AccountResult.Fail(AccountFailure.Rejected, AccountMessages.Rejected);
    }

    /// <summary>
    /// Verifies a licence document (purpose <c>licence</c>, trusted key, not expired, not rolled back) and checks <c>cnf</c> equals this
    /// device's thumbprint, <c>sub</c> equals the signed-in user, and <c>status</c> matches. Only then are state, floor and clock mark updated.
    /// </summary>
    private bool AcceptLicence(string? document, bool expectRevoked, bool signingIn = false)
    {
        if (document is null)
        {
            return false;
        }

        var verified = _d.Verifier.Verify(Encoding.UTF8.GetBytes(document), SignedPurposes.Licence, LicenceChannel);
        if (!verified.IsValid || LicenceInfo.TryParse(verified.Envelope!) is not { } licence)
        {
            _d.Log.Warning("Licence refused: " + verified.Outcome);
            return false;
        }

        // At a sign-in with no live session there is no user to compare with. A banned person has no account at all, so the
        // signed `sub` is a random id: the document is then judged on `cnf` (this device) and `status` only (ADR-0011).
        var knownUser = _profile?.UserId;
        var noSession = signingIn && (_profile is null || _refreshToken is null);
        var userMatches = noSession || (knownUser is { Length: > 0 } && licence.Subject == knownUser);
        if (licence.IsRevoked != expectRevoked || !userMatches || licence.DeviceThumbprint != DeviceThumbprint())
        {
            _d.Log.Warning("Licence refused: it is not for this user and device.");
            return false;
        }

        try
        {
            if (expectRevoked)
            {
                _profile = (noSession ? new Profile(licence.Subject, null, false, false, null) : _profile!) with { Revoked = true };
                _d.Secrets.Write(ProfileSecret, _profile.ToJson());
            }
            else
            {
                _d.Secrets.Write(LicenceSecret, document);
                _licenceDocument = document;
                if (_profile is { Revoked: true })
                {
                    _profile = _profile with { Revoked = false };
                    _d.Secrets.Write(ProfileSecret, _profile.ToJson());
                }
            }
        }
        catch (SecretStoreException ex)
        {
            _d.Log.Warning("Secret store write failed: " + ex.GetType().Name);
            return false;
        }

        _d.Verifier.CommitVersion(verified.Envelope!);
        RaiseClockMarkFrom(licence.ServerTime);
        return true;
    }

    private string? DeviceThumbprint()
    {
        if (_thumbprint is not null)
        {
            return _thumbprint;
        }

        try
        {
            _thumbprint = _d.Keys.GetOrCreatePublicKey().Thumbprint;
            return _thumbprint;
        }
        catch (DeviceKeyException ex)
        {
            _d.Log.Warning("Device key unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private async Task<(ApiResult<T>? Result, AccountResult? Failure)> CallWithSessionAsync<T>(Func<string, CancellationToken, Task<ApiResult<T>>> call, CancellationToken cancellationToken)
    {
        if (_refreshToken is null)
        {
            return (null, AccountResult.Fail(AccountFailure.SignInRequired, AccountMessages.NeedSignIn));
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_accessToken is null || _accessExpiresAt - _d.Time.GetUtcNow() <= AccessTokenMargin || attempt > 0)
            {
                var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!refreshed.Succeeded)
                {
                    return (null, refreshed);
                }
            }

            var result = await call(_accessToken!, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != ApiOutcome.Unauthorized)
            {
                return (result, null);
            }
        }

        return (null, AccountResult.Fail(AccountFailure.SignInRequired, AccountMessages.SignInAgain));
    }

    private AccountResult ClearSession(string message, bool deleteDeviceKey, AccountFailure failure = AccountFailure.None)
    {
        try
        {
            foreach (var name in new[] { RefreshTokenSecret, AccessTokenSecret, LicenceSecret, ProfileSecret })
            {
                _d.Secrets.Delete(name);
            }

            if (deleteDeviceKey)
            {
                _d.Keys.Delete();
            }
        }
        catch (SecretStoreException ex)
        {
            _d.Log.Warning("Secret store delete failed: " + ex.GetType().Name);
            return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
        }
        catch (DeviceKeyException ex)
        {
            _d.Log.Warning("Device key delete failed: " + ex.GetType().Name);
            return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
        }

        _refreshToken = null;
        _accessToken = null;
        _licenceDocument = null;
        _profile = null;
        _networkFailed = false;
        Publish();
        return failure == AccountFailure.None ? AccountResult.Ok(message) : AccountResult.Fail(failure, message);
    }

    /// <summary>Drops the tokens but keeps the profile and the stored licence, so the device stays unlocked (D8, D32) until the user signs out or a signed revocation arrives.</summary>
    private AccountResult EndSession()
    {
        try
        {
            _d.Secrets.Delete(RefreshTokenSecret);
            _d.Secrets.Delete(AccessTokenSecret);
        }
        catch (SecretStoreException ex)
        {
            _d.Log.Warning("Secret store delete failed: " + ex.GetType().Name);
            return AccountResult.Fail(AccountFailure.LocalStorage, AccountMessages.LocalStorage);
        }

        _refreshToken = null;
        _accessToken = null;
        _networkFailed = false;
        Publish();
        return AccountResult.Fail(AccountFailure.SignInRequired, AccountMessages.SignInAgain);
    }

    private AccountResult OfflineResult()
    {
        _networkFailed = true;
        Publish();

        // "Your Discord status keeps working" is true only once the device is unlocked (D32).
        return AccountResult.Fail(AccountFailure.Offline, Current.PresenceAllowed ? AccountMessages.Offline : AccountMessages.OfflineLocked);
    }

    private void LoadStored()
    {
        try
        {
            _refreshToken = _d.Secrets.Read(RefreshTokenSecret);
            if (_d.Secrets.Read(AccessTokenSecret)?.Split('\n') is [var access, var expires]
                && DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                _accessToken = access;
                _accessExpiresAt = at;
            }

            _licenceDocument = _d.Secrets.Read(LicenceSecret);
            _profile = Profile.TryParse(_d.Secrets.Read(ProfileSecret));
            if (DateTimeOffset.TryParse(_d.Secrets.Read(ClockMarkSecret), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var mark))
            {
                _clockMark = mark;
            }
        }
        catch (SecretStoreException ex)
        {
            // Unreadable secrets (another Windows user, damaged file) mean "not signed in"; local presence is unaffected (D8).
            _d.Log.Warning("Stored sign-in could not be read: " + ex.GetType().Name);
            _refreshToken = null;
            _accessToken = null;
            _licenceDocument = null;
            _profile = null;
        }

        if (_refreshToken is not null && _profile is null)
        {
            _refreshToken = null;
        }
    }

    private void PersistProfile()
    {
        try
        {
            _d.Secrets.Write(ProfileSecret, _profile!.ToJson());
        }
        catch (SecretStoreException ex)
        {
            _d.Log.Warning("Secret store write failed: " + ex.GetType().Name);
        }
    }

    private void RaiseClockMarkFrom(DateTimeOffset? serverTime)
    {
        if (serverTime is not { } time || (_clockMark is { } current && time <= current))
        {
            return;
        }

        _clockMark = time;
        try
        {
            _d.Secrets.Write(ClockMarkSecret, time.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (SecretStoreException ex)
        {
            _d.Log.Warning("Secret store write failed: " + ex.GetType().Name);
        }
    }

    private void OnConfigChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        AccountSnapshot next;
        lock (_state)
        {
            next = Evaluate();
            if (next == _snapshot)
            {
                return;
            }

            _snapshot = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The unlock rule (D32, D8, ADR-0011). The device is unlocked exactly while it stores a licence that verifies against the trusted keys
    /// (signature, purpose, channel), is bound to this user and this device's key and says <c>active</c>. Neither the licence's <c>exp</c> nor
    /// the system clock takes part: a lapsed licence only pauses online features, and moving the clock can neither unlock nor lock. A signed
    /// revocation (<see cref="Profile.Revoked"/>), signing out or deleting the account locks the app again.
    /// </summary>
    private AccountSnapshot Evaluate()
    {
        var now = _d.Time.GetUtcNow();
        var name = _profile?.DisplayName;
        var publicAccess = _d.Config.Current.AccessPublic;
        if (_profile is { Revoked: true })
        {
            return new AccountSnapshot(OnlineState.Revoked, name, false, null, AccountMessages.Revoked, publicAccess);
        }

        if (_profile is null)
        {
            return AccountSnapshot.SignedOut with { PublicAccess = publicAccess };
        }

        var licence = ReadStoredLicence();
        var hasSession = _refreshToken is not null;
        if (licence is null)
        {
            if (!hasSession)
            {
                return AccountSnapshot.SignedOut with { PublicAccess = publicAccess };
            }

            return new AccountSnapshot(_profile.HasAccess ? OnlineState.LicenceNeeded : OnlineState.NeedsInvite, name, false, null, _profile.HasAccess ? AccountMessages.LicenceUnverified : null, publicAccess);
        }

        var licenceValid = licence.ExpiresAt > now;
        var clockBehind = _clockMark is { } mark && now < mark - ClockTolerance;
        var features = hasSession && licenceValid && !clockBehind && _d.Config.Current.OnlineFeaturesEnabled;
        var notice = clockBehind ? AccountMessages.ClockBehind : !hasSession ? AccountMessages.SignInAgain : _networkFailed ? AccountMessages.Offline : null;
        var state = hasSession && licenceValid && !_networkFailed ? OnlineState.Active : OnlineState.OfflineWithGrace;
        return new AccountSnapshot(state, name, features, licence.ExpiresAt, notice, publicAccess, NeedsSignIn: !hasSession);
    }

    private LicenceInfo? ReadStoredLicence()
    {
        if (_licenceDocument is null)
        {
            return null;
        }

        var verified = _d.Verifier.VerifyStored(Encoding.UTF8.GetBytes(_licenceDocument), SignedPurposes.Licence, LicenceChannel);
        if (!verified.IsValid || LicenceInfo.TryParse(verified.Envelope!) is not { IsRevoked: false } licence)
        {
            return null;
        }

        return licence.Subject == _profile?.UserId && licence.DeviceThumbprint == DeviceThumbprint() ? licence : null;
    }

    private static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var clean = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? null : clean.Length > 64 ? clean[..64] : clean;
    }

    private sealed record Profile(string UserId, string? DisplayName, bool HasAccess, bool Revoked, DateTimeOffset? LastRefreshUtc)
    {
        public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["userId"] = UserId,
            ["displayName"] = DisplayName,
            ["hasAccess"] = HasAccess,
            ["revoked"] = Revoked,
            ["lastRefreshUtc"] = LastRefreshUtc?.ToString("O", CultureInfo.InvariantCulture),
        });

        public static Profile? TryParse(string? json)
        {
            if (json is null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new Profile(
                    root.GetProperty("userId").GetString() ?? string.Empty,
                    root.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    root.GetProperty("hasAccess").GetBoolean(),
                    root.GetProperty("revoked").GetBoolean(),
                    root.TryGetProperty("lastRefreshUtc", out var l) && l.ValueKind == JsonValueKind.String
                        ? DateTimeOffset.Parse(l.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                        : null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A damaged profile means "not signed in"; the user signs in again.
                return null;
            }
        }
    }

    private sealed record LicenceInfo(string Subject, string DeviceThumbprint, bool IsRevoked, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime)
    {
        public static LicenceInfo? TryParse(SignedEnvelope envelope)
        {
            try
            {
                using var doc = JsonDocument.Parse(envelope.PayloadJson);
                var root = doc.RootElement;
                if (root.GetProperty("schemaVersion").GetInt32() != 1)
                {
                    return null;
                }

                var status = root.GetProperty("status").GetString();
                if (status is not ("active" or "revoked"))
                {
                    return null;
                }

                return new LicenceInfo(
                    root.GetProperty("sub").GetString() ?? string.Empty,
                    root.GetProperty("cnf").GetString() ?? string.Empty,
                    status == "revoked",
                    envelope.ExpiresAt,
                    DateTimeOffset.Parse(root.GetProperty("serverTime").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Signed but not a licence payload: refused.
                return null;
            }
        }
    }
}
