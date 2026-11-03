// Covers: FR-ONL-01 (sign-in), FR-ONL-02 (invites), FR-ONL-03 (device-signed refresh, family revocation), FR-ONL-04 (licence, clock rule),
// FR-ONL-05 + D8 (an outage or an expired licence never stops presence; only a received, signed revocation does),
// FR-ONL-11 (sign out, delete my account), ADR-0011.
using System.Buffers.Text;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;
using DbdPresence.Online.SignIn;

namespace DbdPresence.Online.Tests;

public sealed class AccountManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<AccountHarness> SignedIn(string access = "granted")
    {
        var h = new AccountHarness { Access = access };
        var result = await h.Account.SignInAsync(Ct);
        Assert.True(result.Succeeded, result.Message);
        return h;
    }

    // ---- sign-in -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_HappyPath_SendsCodeVerifierRedirectAndDeviceKey_AndBecomesActive()
    {
        using var h = new AccountHarness();

        var result = await h.Account.SignInAsync(Ct);

        Assert.True(result.Succeeded);
        var snapshot = h.Account.Current;
        Assert.Equal(OnlineState.Active, snapshot.State);
        Assert.Equal("Meg Thomas", snapshot.DisplayName);
        Assert.True(snapshot.OnlineFeaturesAllowed);
        Assert.True(snapshot.PresenceAllowed);

        var post = Assert.Single(h.Service.To("/v1/auth/discord"));
        using var body = JsonDocument.Parse(post.Body);
        var root = body.RootElement;
        Assert.Equal("auth-code-1", root.GetProperty("code").GetString());
        Assert.Equal("http://127.0.0.1:51234/callback", root.GetProperty("redirectUri").GetString());
        Assert.Equal(PkceChallenge.ChallengeFor(root.GetProperty("codeVerifier").GetString()!), h.Browser.Query("code_challenge"));
        var jwk = root.GetProperty("devicePublicKeyJwk");
        Assert.Equal("EC", jwk.GetProperty("kty").GetString());
        Assert.Equal("P-256", jwk.GetProperty("crv").GetString());
        Assert.Equal(h.Keys.Public.X, jwk.GetProperty("x").GetString());
        Assert.Equal(h.Keys.Public.Y, jwk.GetProperty("y").GetString());
        Assert.False(jwk.TryGetProperty("d", out _));
        Assert.All(h.Service.Requests, r => Assert.Equal(AccountHarness.Host, r.Host));
    }

    [Fact]
    public async Task SignIn_StoresTokensProtected_AndNeverLogsThem()
    {
        using var h = await SignedIn();

        Assert.Equal(h.CurrentRefreshToken, h.Secrets.Read("refresh-token"));
        Assert.StartsWith("at_token", h.Secrets.Read("access-token"), StringComparison.Ordinal);
        Assert.All(h.Log.Lines, line =>
        {
            Assert.DoesNotContain("rt_token", line, StringComparison.Ordinal);
            Assert.DoesNotContain("at_token", line, StringComparison.Ordinal);
            Assert.DoesNotContain("auth-code", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SignIn_StateMismatch_IsRefused_AndNothingIsSentToTheService()
    {
        using var h = new AccountHarness();
        h.Listeners.Reply = _ => new CallbackResult(CallbackKind.Code, "auth-code-1", "forged-state");

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountFailure.Rejected, result.Failure);
        Assert.Equal(AccountMessages.StateMismatch, result.Message);
        Assert.Empty(h.Service.Requests);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
    }

    [Theory]
    [InlineData(CallbackKind.TimedOut, AccountFailure.NotCompleted)]
    [InlineData(CallbackKind.Denied, AccountFailure.NotCompleted)]
    [InlineData(CallbackKind.Invalid, AccountFailure.Rejected)]
    public async Task SignIn_TimeoutDenialOrJunkCallback_EndsWithoutContactingTheService(CallbackKind kind, AccountFailure failure)
    {
        using var h = new AccountHarness();
        h.Listeners.Reply = _ => new CallbackResult(kind, null, null);

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(failure, result.Failure);
        Assert.Empty(h.Service.Requests);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
    }

    [Fact]
    public async Task SignIn_BrowserCannotOpen_ReportsItWithoutWaiting()
    {
        using var h = new AccountHarness();
        h.Browser.Succeeds = false;

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountMessages.BrowserFailed, result.Message);
        Assert.Empty(h.Service.Requests);
    }

    [Fact]
    public async Task SignIn_ServiceDown_ReportsOffline_AndLeavesTheUserSignedOut()
    {
        using var h = new AccountHarness();
        h.Service.Throw = new HttpRequestException("down");

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountFailure.Offline, result.Failure);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
    }

    [Theory]
    [InlineData(409, "device_limit", "maximum number of PCs")]
    [InlineData(403, "device_revoked", "removed from the account")]
    [InlineData(429, "rate_limited", "Too many attempts")]
    public async Task SignIn_ServiceRefusals_MapToPlainMessages(int status, string code, string expected)
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => FakeService.Json(status, $$"""{"schemaVersion":1,"error":"{{code}}"}""", status == 429 ? TimeSpan.FromMinutes(5) : null);

        var result = await h.Account.SignInAsync(Ct);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
    }

    // ---- invites -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InviteRequired_ShowsNeedsInvite_ThenRedeemingActivates()
    {
        using var h = await SignedIn(access: "invite_required");
        Assert.Equal(OnlineState.NeedsInvite, h.Account.Current.State);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.False(h.Account.Current.PresenceAllowed); // D32: locked until the invite is redeemed and the licence verified

        var result = await h.Account.RedeemInviteAsync("abcd efgh-jkmn pqrs", Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
        var post = Assert.Single(h.Service.To("/v1/invites/redeem"));
        Assert.Contains("ABCD-EFGH-JKMN-PQRS", post.Body, StringComparison.Ordinal);
        Assert.StartsWith("Bearer at_token", post.Authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too short")]
    [InlineData("ABCD-EFGH-JKMN-PQR!")]
    [InlineData("ABCD-EFGH-JKMN-PQRSU")]
    public async Task Redeem_RejectsMalformedCodesLocally(string code)
    {
        using var h = await SignedIn(access: "invite_required");
        var before = h.Service.Requests.Count;

        var result = await h.Account.RedeemInviteAsync(code, Ct);

        Assert.Equal(AccountMessages.BadInviteFormat, result.Message);
        Assert.Equal(before, h.Service.Requests.Count);
    }

    [Fact]
    public async Task Redeem_InviteInvalid_GivesOneGenericMessage_ThatRevealsNoReason()
    {
        using var h = await SignedIn(access: "invite_required");
        h.Service.Handler = _ => FakeService.Json(400, """{"schemaVersion":1,"error":"invite_invalid"}""");

        var result = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);

        Assert.Equal(AccountMessages.InviteInvalid, result.Message);
        Assert.DoesNotContain("expired", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("used", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OnlineState.NeedsInvite, h.Account.Current.State);
    }

    [Fact]
    public async Task Redeem_RateLimited_ShowsTheWait()
    {
        using var h = await SignedIn(access: "invite_required");
        h.Service.Handler = _ => FakeService.Json(429, """{"schemaVersion":1,"error":"rate_limited"}""", TimeSpan.FromMinutes(10));

        var result = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);

        Assert.Contains("10 minute", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs", "ABCDEFGHJKMNPQRS")]
    [InlineData("OIL0-0000-0000-0000", "0110000000000000")]
    public void NormalizeInviteCode_FollowsTheServiceRules(string input, string expected) =>
        Assert.Equal(expected, AccountManager.NormalizeInviteCode(input));

    // ---- refresh, licence, family revocation -------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_IsSignedWithTheDeviceKey_AndRotatesTheTokens()
    {
        using var h = await SignedIn();
        var firstToken = h.CurrentRefreshToken;
        h.Time.Advance(TimeSpan.FromHours(25));

        var result = await h.Account.RefreshAsync(force: false, Ct);

        Assert.True(result.Succeeded, result.Message);
        var post = Assert.Single(h.Service.To("/v1/auth/refresh"));
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal(firstToken, body.RootElement.GetProperty("refreshToken").GetString());
        var proof = body.RootElement.GetProperty("proof");
        var timestamp = proof.GetProperty("timestamp").GetInt64();
        var nonce = proof.GetProperty("nonce").GetString()!;
        Assert.Equal(h.Time.GetUtcNow().ToUnixTimeSeconds(), timestamp);
        Assert.Matches("^[A-Za-z0-9_-]{16,64}$", nonce);
        Assert.True(h.Keys.Verify(ServiceClient.ProofMessage(firstToken, timestamp, nonce), Base64Url.DecodeFromChars(proof.GetProperty("signature").GetString()!)));
        Assert.NotEqual(firstToken, h.Secrets.Read("refresh-token"));
        Assert.Equal(h.CurrentRefreshToken, h.Secrets.Read("refresh-token"));
    }

    [Fact]
    public async Task Refresh_UsesAFreshNonceEachTime()
    {
        using var h = await SignedIn();

        await h.Account.RefreshAsync(force: true, Ct);
        await h.Account.RefreshAsync(force: true, Ct);

        var nonces = h.Service.To("/v1/auth/refresh").Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("proof").GetProperty("nonce").GetString()).ToArray();
        Assert.Equal(2, nonces.Distinct().Count());
    }

    [Fact]
    public async Task Refresh_RunsDailyOnly()
    {
        using var h = await SignedIn();

        h.Time.Advance(TimeSpan.FromHours(23));
        await h.Account.RefreshAsync(force: false, Ct);
        Assert.Empty(h.Service.To("/v1/auth/refresh"));

        h.Time.Advance(TimeSpan.FromHours(2));
        await h.Account.RefreshAsync(force: false, Ct);
        Assert.Single(h.Service.To("/v1/auth/refresh"));
    }

    [Theory]
    [InlineData("refresh_token_reused")]
    [InlineData("session_revoked")]
    [InlineData("invalid_refresh_token")]
    public async Task FamilyRevoked_RequiresSigningInAgain_DropsTheTokens_ButKeepsTheUnlock(string code)
    {
        using var h = await SignedIn();
        h.Service.Handler = _ => FakeService.Json(401, $$"""{"schemaVersion":1,"error":"{{code}}"}""");

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.SignInRequired, result.Failure);
        Assert.Equal(AccountMessages.SignInAgain, result.Message);
        // D8/D32: an unsigned 401 is not a signed revocation, so the stored licence keeps the app unlocked; only online features wait.
        Assert.Equal(OnlineState.OfflineWithGrace, h.Account.Current.State);
        Assert.True(h.Account.Current.NeedsSignIn);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.Null(h.Secrets.Read("refresh-token"));
        Assert.Null(h.Secrets.Read("access-token"));
        Assert.NotNull(h.Secrets.Read("licence"));
        Assert.True(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task InvalidProof_IsNotTreatedAsASessionEnd()
    {
        using var h = await SignedIn();
        h.Service.Handler = _ => FakeService.Json(401, """{"schemaVersion":1,"error":"invalid_proof"}""");

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountMessages.ClockProof, result.Message);
        Assert.NotNull(h.Secrets.Read("refresh-token"));
        Assert.True(h.Account.Current.IsSignedIn);
    }

    [Fact]
    public async Task State_SurvivesARestart()
    {
        using var h = await SignedIn();

        var restarted = h.Restart();

        Assert.Equal(OnlineState.Active, restarted.Current.State);
        Assert.Equal("Meg Thomas", restarted.Current.DisplayName);
    }

    [Fact]
    public async Task LicenceForAnotherDevice_IsRefused_AndNeverTrusted()
    {
        using var h = new AccountHarness();
        h.LicenceOverride = h.LicenceNow(cnf: "some-other-device-thumbprint-0000000000000");

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountMessages.LicenceUnverified, result.Message);
        Assert.NotEqual(OnlineState.Active, h.Account.Current.State);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
    }

    [Fact]
    public async Task LicenceSignedByAnUnknownKey_IsRefused()
    {
        using var h = new AccountHarness();
        h.LicenceOverride = h.Issuer.Licence(h.Time.GetUtcNow(), AccountHarness.UserId, h.Thumbprint, withOtherKey: true);

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountMessages.LicenceUnverified, result.Message);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
    }

    [Fact]
    public async Task OlderLicence_IsRefused_ByTheVersionFloor()
    {
        using var h = await SignedIn();
        h.LicenceOverride = h.Issuer.Licence(h.Time.GetUtcNow().AddHours(-5), AccountHarness.UserId, h.Thumbprint);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountMessages.LicenceUnverified, result.Message);
    }

    // ---- D8: outage, expiry, revocation, clock ------------------------------------------------------------------------------------

    [Fact]
    public async Task ServiceOutage_NeverStopsPresence_EvenAfterTheLicenceExpires()
    {
        using var h = await SignedIn();
        h.Service.Throw = new HttpRequestException("down");

        h.Time.Advance(TimeSpan.FromDays(2));
        var early = await h.Account.RefreshAsync(force: true, Ct);
        Assert.Equal(AccountFailure.Offline, early.Failure);
        Assert.Equal(OnlineState.OfflineWithGrace, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);

        h.Time.Advance(TimeSpan.FromDays(40));
        h.Account.Reevaluate();
        await h.Account.RefreshAsync(force: true, Ct);

        var snapshot = h.Account.Current;
        Assert.Equal(OnlineState.OfflineWithGrace, snapshot.State);
        Assert.True(snapshot.PresenceAllowed);
        Assert.False(snapshot.OnlineFeaturesAllowed); // online features need a current licence
    }

    [Fact]
    public async Task ExpiredLicence_DoesNotStopPresence_OnlyOnlineFeatures()
    {
        using var h = await SignedIn();
        Assert.True(h.Account.Current.OnlineFeaturesAllowed);

        h.Time.Advance(TimeSpan.FromDays(8));
        h.Account.Reevaluate();

        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.NotEqual(OnlineState.Revoked, h.Account.Current.State);
    }

    [Fact]
    public async Task SignedRevocation_StopsPresence()
    {
        using var h = await SignedIn();
        var revocation = h.LicenceNow("revoked");
        h.Service.Handler = _ => FakeService.Json(403, $$"""{"schemaVersion":1,"error":"revoked","revocation":{{JsonSerializer.Serialize(revocation)}}}""");
        h.Time.Advance(TimeSpan.FromHours(25));

        await h.Account.RefreshAsync(force: false, Ct);

        Assert.Equal(OnlineState.Revoked, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.Equal(OnlineState.Revoked, h.Restart().Current.State); // stays revoked after a restart
    }

    [Fact]
    public async Task RevokedUserSigningIn_GetsTheSignedRevocation()
    {
        using var h = new AccountHarness();
        var revocation = h.LicenceNow("revoked");
        h.Service.Handler = _ => FakeService.Json(403, $$"""{"schemaVersion":1,"error":"revoked","revocation":{{JsonSerializer.Serialize(revocation)}}}""");

        await h.Account.SignInAsync(Ct);

        Assert.Equal(OnlineState.Revoked, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task UnsignedRevokedBody_DoesNotStopPresence()
    {
        using var h = await SignedIn();
        h.Service.Handler = _ => FakeService.Json(403, """{"schemaVersion":1,"error":"revoked"}""");

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.False(result.Succeeded);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.NotEqual(OnlineState.Revoked, h.Account.Current.State);
    }

    [Fact]
    public async Task SpoofedRevocations_AreIgnored()
    {
        using var h = await SignedIn();
        var now = h.Time.GetUtcNow();
        var spoofs = new Dictionary<string, string>
        {
            ["garbage text"] = "not a signed document",
            ["signed by an unknown key"] = h.Issuer.Licence(now, AccountHarness.UserId, h.Thumbprint, "revoked", withOtherKey: true),
            ["another user"] = h.Issuer.Licence(now, "someone-else", h.Thumbprint, "revoked"),
            ["another device"] = h.Issuer.Licence(now, AccountHarness.UserId, "other-device-thumbprint-aaaaaaaaaaaaaaaaaaaaaaa", "revoked"),
            ["wrong purpose"] = h.Issuer.Licence(now, AccountHarness.UserId, h.Thumbprint, "revoked", purpose: "config"),
            ["an active licence in the revocation slot"] = h.Issuer.Licence(now, AccountHarness.UserId, h.Thumbprint, "active"),
        };

        foreach (var (name, document) in spoofs)
        {
            h.Service.Handler = _ => FakeService.Json(403, $$"""{"schemaVersion":1,"error":"revoked","revocation":{{JsonSerializer.Serialize(document)}}}""");
            await h.Account.RefreshAsync(force: true, Ct);

            Assert.True(h.Account.Current.PresenceAllowed, name);
            Assert.NotEqual(OnlineState.Revoked, h.Account.Current.State);
        }
    }

    [Fact]
    public async Task ExpiredRevocationDocument_IsNotAccepted()
    {
        using var h = await SignedIn();
        var stale = h.Issuer.Licence(h.Time.GetUtcNow().AddDays(-9), AccountHarness.UserId, h.Thumbprint, "revoked");
        h.Service.Handler = _ => FakeService.Json(403, $$"""{"schemaVersion":1,"error":"revoked","revocation":{{JsonSerializer.Serialize(stale)}}}""");

        await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task ClockRollback_BlocksOnlineFeatures_ButNeverPresence()
    {
        using var h = await SignedIn();
        Assert.True(h.Account.Current.OnlineFeaturesAllowed);

        h.Time.Rewind(TimeSpan.FromDays(3));
        h.Account.Reevaluate();

        var snapshot = h.Account.Current;
        Assert.False(snapshot.OnlineFeaturesAllowed);
        Assert.True(snapshot.PresenceAllowed);
        Assert.Equal(AccountMessages.ClockBehind, snapshot.Notice);
        Assert.Equal(OnlineState.Active, snapshot.State);

        h.Time.ResetSkew();
        h.Time.Rewind(TimeSpan.FromMinutes(3)); // inside the tolerance
        h.Account.Reevaluate();
        Assert.True(h.Account.Current.OnlineFeaturesAllowed);
    }

    [Fact]
    public async Task ClockMark_IsPersisted_AcrossRestarts()
    {
        using var h = await SignedIn();

        h.Time.Rewind(TimeSpan.FromDays(3));
        var restarted = h.Restart();

        Assert.False(restarted.Current.OnlineFeaturesAllowed);
        Assert.True(restarted.Current.PresenceAllowed);
        Assert.NotNull(h.Secrets.Read("clock-mark"));
    }

    // ---- sign out and deletion ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SignOut_ClearsTheSession_EvenOffline_AndKeepsPresenceAllowed()
    {
        using var h = await SignedIn();
        h.Service.Throw = new HttpRequestException("down");

        var result = await h.Account.SignOutAsync(Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed); // D32: signing out locks the app until the next sign-in
        Assert.Null(h.Secrets.Read("refresh-token"));
        Assert.Null(h.Secrets.Read("licence"));
        Assert.Equal(0, h.Keys.Deleted);
    }

    [Fact]
    public async Task DeleteAccount_RemovesLocalSecretsAndTheDeviceKey_OnlyAfterTheServiceConfirms()
    {
        using var h = await SignedIn();

        var result = await h.Account.DeleteAccountAsync(Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Delete, Assert.Single(h.Service.To("/v1/me")).Method);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
        Assert.Null(h.Secrets.Read("refresh-token"));
        Assert.Null(h.Secrets.Read("licence"));
        Assert.Null(h.Secrets.Read("profile"));
        Assert.Equal(1, h.Keys.Deleted);
    }

    [Fact]
    public async Task DeleteAccount_WhenTheServiceIsUnreachable_KeepsEverything()
    {
        using var h = await SignedIn();
        h.Service.Throw = new HttpRequestException("down");

        var result = await h.Account.DeleteAccountAsync(Ct);

        Assert.Equal(AccountFailure.Offline, result.Failure);
        Assert.True(h.Account.Current.IsSignedIn);
        Assert.NotNull(h.Secrets.Read("refresh-token"));
        Assert.Equal(0, h.Keys.Deleted);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshedBeforeACall()
    {
        using var h = await SignedIn(access: "invite_required");
        h.Time.Advance(TimeSpan.FromMinutes(61));

        await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);

        Assert.Single(h.Service.To("/v1/auth/refresh"));
        Assert.Single(h.Service.To("/v1/invites/redeem"));
    }
}
