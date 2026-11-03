// Covers: FR-ONL-03 (refresh retry within the 30 s grace, session end), FR-ONL-05 + D8 (only a verified signed revocation stops presence;
// a ended session never does), FR-ONL-01/02/11 (429 with Retry-After, the 409 codes redeem_conflict / user_changed / account_changed),
// ADR-0011 (Refresh contract, Revocation responses, Bans, device JWK canonical encoding). Stub HttpMessageHandler throughout.
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbdPresence.Core.Online;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;

namespace DbdPresence.Online.Tests;

public sealed partial class ServiceContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex Coordinate();

    private static async Task<AccountHarness> SignedIn(string access = "granted")
    {
        var h = new AccountHarness { Access = access };
        var result = await h.Account.SignInAsync(Ct);
        Assert.True(result.Succeeded, result.Message);
        return h;
    }

    private static string Nonce(Recorded r) => JsonDocument.Parse(r.Body).RootElement.GetProperty("proof").GetProperty("nonce").GetString()!;

    private static string TokenOf(Recorded r) => JsonDocument.Parse(r.Body).RootElement.GetProperty("refreshToken").GetString()!;

    private static void AssertProofIsValid(AccountHarness h, Recorded r)
    {
        using var body = JsonDocument.Parse(r.Body);
        var proof = body.RootElement.GetProperty("proof");
        var message = ServiceClient.ProofMessage(TokenOf(r), proof.GetProperty("timestamp").GetInt64(), Nonce(r));
        Assert.True(h.Keys.Verify(message, Base64Url.DecodeFromChars(proof.GetProperty("signature").GetString()!)));
    }

    private static string Error(string code) => $$"""{"schemaVersion":1,"error":"{{code}}"}""";

    // ---- 1. refresh retries -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LostRefreshResponse_RetriedWithANewProofWithin30Seconds_KeepsTheUserSignedIn()
    {
        using var h = await SignedIn();
        var original = h.CurrentRefreshToken;
        var issued = h.GrantJson(withUser: false); // the pair the service rotated to; the first response never arrived
        var rotatedTo = h.CurrentRefreshToken;
        var refreshCalls = 0;
        h.Service.Handler = r =>
        {
            if (r.Path != "/v1/auth/refresh")
            {
                return h.DefaultHandler(r);
            }

            refreshCalls++;
            return refreshCalls == 1 ? throw new HttpRequestException("connection reset after the server rotated") : FakeService.Json(200, issued);
        };

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(result.Succeeded, result.Message);
        var sent = h.Service.To("/v1/auth/refresh").ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(original, TokenOf(sent[0]));
        Assert.Equal(original, TokenOf(sent[1])); // the same token again, as the contract's idempotent retry
        Assert.NotEqual(Nonce(sent[0]), Nonce(sent[1])); // but a NEW proof
        AssertProofIsValid(h, sent[0]);
        AssertProofIsValid(h, sent[1]);
        Assert.Equal(rotatedTo, h.Secrets.Read("refresh-token"));
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
        Assert.True(h.Account.Current.IsSignedIn);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.Equal(OnlineState.Active, h.Restart().Current.State); // and it survives a restart with the new token
    }

    [Fact]
    public async Task RefreshTimeout_IsRetriedOnceWithANewProof_AndKeepsTheSession()
    {
        using var h = await SignedIn();
        var issued = h.GrantJson(withUser: false);
        var calls = 0;
        h.Service.Handler = r => r.Path != "/v1/auth/refresh" ? h.DefaultHandler(r) : ++calls == 1 ? throw new TaskCanceledException("timed out") : FakeService.Json(200, issued);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(result.Succeeded, result.Message);
        var sent = h.Service.To("/v1/auth/refresh").ToArray();
        Assert.Equal(2, sent.Length);
        Assert.NotEqual(Nonce(sent[0]), Nonce(sent[1]));
        Assert.True(h.Account.Current.IsSignedIn);
    }

    [Fact]
    public async Task RefreshServerError_IsRetriedOnceWithANewProof()
    {
        using var h = await SignedIn();
        var issued = h.GrantJson(withUser: false);
        var calls = 0;
        h.Service.Handler = r => r.Path != "/v1/auth/refresh" ? h.DefaultHandler(r) : ++calls == 1 ? FakeService.Json(503, Error("unavailable")) : FakeService.Json(200, issued);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, h.Service.To("/v1/auth/refresh").Count());
    }

    [Fact]
    public async Task RefreshRetry_IsNotMadeOnceTheGraceCouldHaveClosed()
    {
        using var h = await SignedIn();
        h.Service.Handler = r =>
        {
            if (r.Path != "/v1/auth/refresh")
            {
                return h.DefaultHandler(r);
            }

            h.Time.Advance(TimeSpan.FromSeconds(26)); // the first attempt hung for 26 s: a retry could land after the 30 s grace
            throw new HttpRequestException("timed out");
        };

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.Offline, result.Failure);
        Assert.Single(h.Service.To("/v1/auth/refresh")); // no retry that would only be treated as token reuse
        Assert.True(h.Account.Current.IsSignedIn);
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task RefreshRetry_IsMadeOnlyOnce_SoAnOutageNeverLoops()
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? throw new HttpRequestException("down") : h.DefaultHandler(r);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.Offline, result.Failure);
        Assert.Equal(2, h.Service.To("/v1/auth/refresh").Count());
        Assert.NotNull(h.Secrets.Read("refresh-token"));
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task ProofReplayed_RetriesOnceWithANewProof_AndRevokesNothing()
    {
        using var h = await SignedIn();
        var issued = h.GrantJson(withUser: false);
        var calls = 0;
        h.Service.Handler = r => r.Path != "/v1/auth/refresh" ? h.DefaultHandler(r) : ++calls == 1 ? FakeService.Json(401, Error("proof_replayed")) : FakeService.Json(200, issued);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(result.Succeeded, result.Message);
        var sent = h.Service.To("/v1/auth/refresh").ToArray();
        Assert.Equal(2, sent.Length);
        Assert.NotEqual(Nonce(sent[0]), Nonce(sent[1]));
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
    }

    [Fact]
    public async Task ProofReplayed_Twice_StopsAfterOneRetry_AndKeepsTheSession()
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? FakeService.Json(401, Error("proof_replayed")) : h.DefaultHandler(r);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.Rejected, result.Failure);
        Assert.Equal(AccountMessages.RefreshReplayed, result.Message);
        Assert.Equal(2, h.Service.To("/v1/auth/refresh").Count());
        Assert.NotNull(h.Secrets.Read("refresh-token"));
        Assert.True(h.Account.Current.IsSignedIn);
    }

    [Theory]
    [InlineData("refresh_token_reused")]
    [InlineData("session_revoked")]
    public async Task ReuseOrRevokedFamily_MeansSignInAgain_NotAPresenceStoppingRevocation(string code)
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? FakeService.Json(401, Error(code)) : h.DefaultHandler(r);

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.SignInRequired, result.Failure);
        Assert.Equal(AccountMessages.SignInAgain, result.Message);
        Assert.Single(h.Service.To("/v1/auth/refresh")); // never retried: the session is gone
        Assert.Equal(OnlineState.OfflineWithGrace, h.Account.Current.State);
        Assert.NotEqual(OnlineState.Revoked, h.Restart().Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task ReuseAnswerToTheRetry_AlsoMeansSignInAgain()
    {
        using var h = await SignedIn();
        var calls = 0;
        h.Service.Handler = r => r.Path != "/v1/auth/refresh" ? h.DefaultHandler(r) : ++calls == 1 ? throw new HttpRequestException("lost") : FakeService.Json(401, Error("refresh_token_reused"));

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.SignInRequired, result.Failure);
        Assert.Equal(2, h.Service.To("/v1/auth/refresh").Count());
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    // ---- 2. rate limits ----------------------------------------------------------------------------------------------------------

    private static HttpResponseMessage Limited(TimeSpan? retryAfter) => FakeService.Json(429, Error("rate_limited"), retryAfter);

    [Fact]
    public async Task RefreshRateLimited_HonoursRetryAfter_WithoutRetryingOrContactingTheServiceMeanwhile()
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? Limited(TimeSpan.FromMinutes(2)) : h.DefaultHandler(r);

        var first = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.RateLimited, first.Failure);
        Assert.Equal(TimeSpan.FromMinutes(2), first.RetryAfter);
        Assert.Contains("2 minute", first.Message, StringComparison.Ordinal);
        Assert.Single(h.Service.To("/v1/auth/refresh")); // a 429 is not retried in the same call
        Assert.Equal(OnlineState.Active, h.Account.Current.State); // a rate limit is not an outage
        Assert.True(h.Account.Current.IsSignedIn);

        h.Time.Advance(TimeSpan.FromSeconds(100));
        var during = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.RateLimited, during.Failure);
        Assert.Equal(TimeSpan.FromSeconds(20), during.RetryAfter); // what is left of the wait
        Assert.Contains("20 second", during.Message, StringComparison.Ordinal);
        Assert.Single(h.Service.To("/v1/auth/refresh")); // nothing left the PC

        var issued = h.GrantJson(withUser: false);
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? FakeService.Json(200, issued) : h.DefaultHandler(r);
        h.Time.Advance(TimeSpan.FromSeconds(21));

        var after = await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(after.Succeeded, after.Message);
        Assert.Equal(2, h.Service.To("/v1/auth/refresh").Count());
    }

    [Fact]
    public async Task ConsecutiveRateLimits_BackOffExponentially_EvenWhenRetryAfterIsTiny()
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? Limited(TimeSpan.FromSeconds(1)) : h.DefaultHandler(r);

        var first = await h.Account.RefreshAsync(force: true, Ct);
        Assert.Equal(ServiceClient.BackoffFloor, first.RetryAfter);

        h.Time.Advance(ServiceClient.BackoffFloor);
        var second = await h.Account.RefreshAsync(force: true, Ct);
        Assert.Equal(ServiceClient.BackoffFloor * 2, second.RetryAfter);

        h.Time.Advance(ServiceClient.BackoffFloor * 2);
        var third = await h.Account.RefreshAsync(force: true, Ct);
        Assert.Equal(ServiceClient.BackoffFloor * 4, third.RetryAfter);
        Assert.Equal(3, h.Service.To("/v1/auth/refresh").Count());
    }

    [Fact]
    public async Task RateLimitWait_IsPerRoute_AndForgottenAfterASuccess()
    {
        using var h = await SignedIn(access: "invite_required");
        h.Service.Handler = r => r.Path == "/v1/invites/redeem" ? Limited(TimeSpan.FromMinutes(1)) : h.DefaultHandler(r);
        var limited = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);
        Assert.Equal(AccountFailure.RateLimited, limited.Failure);

        var refresh = await h.Account.RefreshAsync(force: true, Ct); // another route is unaffected
        Assert.True(refresh.Succeeded, refresh.Message);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Service.Handler = h.DefaultHandler;
        var redeemed = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);
        Assert.True(redeemed.Succeeded, redeemed.Message);

        h.Service.Handler = r => r.Path == "/v1/invites/redeem" ? Limited(TimeSpan.FromSeconds(1)) : h.DefaultHandler(r);
        var again = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);
        Assert.Equal(ServiceClient.BackoffFloor, again.RetryAfter); // the backoff started over after the success
    }

    [Fact]
    public async Task RateLimitWithoutAUsableHeader_WaitsTheDefault()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Limited(null);

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountFailure.RateLimited, result.Failure);
        Assert.Equal(ServiceClient.DefaultRetryAfter, result.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDate_IsHonoured_AndAbsurdValuesAreCapped()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ =>
        {
            var response = FakeService.Json(429, Error("rate_limited"));
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(h.Time.GetUtcNow().AddSeconds(90));
            return response;
        };
        var dated = await h.Account.SignInAsync(Ct);
        Assert.Equal(TimeSpan.FromSeconds(90), dated.RetryAfter);

        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Service.Handler = _ => Limited(TimeSpan.FromDays(3));
        var huge = await h.Account.SignInAsync(Ct);
        Assert.Equal(ServiceClient.MaxRetryAfter, huge.RetryAfter);
    }

    [Fact]
    public async Task RateLimitedSignIn_ShowsACalmMessage_AndDoesNotOpenTheBrowserAgainForTheWait()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Limited(TimeSpan.FromSeconds(45));

        var first = await h.Account.SignInAsync(Ct);
        Assert.Equal("Too many attempts. Wait about 45 second(s) and try again.", first.Message);

        var second = await h.Account.SignInAsync(Ct);
        Assert.Equal(AccountFailure.RateLimited, second.Failure);
        Assert.Single(h.Service.To("/v1/auth/discord"));
    }

    [Fact]
    public async Task RateLimitedConfig_IsNotHammered()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Limited(TimeSpan.FromMinutes(3));

        _ = await h.Account.RefreshAsync(force: false, Ct);
        _ = await h.Account.RefreshAsync(force: false, Ct);

        Assert.Single(h.Service.To("/v1/config"));
    }

    // ---- 3. 409 codes -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RedeemConflict_IsRetriedOnce_AndThenSucceeds()
    {
        using var h = await SignedIn(access: "invite_required");
        var calls = 0;
        h.Service.Handler = r => r.Path == "/v1/invites/redeem" && ++calls == 1 ? FakeService.Json(409, Error("redeem_conflict")) : h.DefaultHandler(r);

        var result = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, h.Service.To("/v1/invites/redeem").Count());
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
    }

    [Fact]
    public async Task RedeemConflict_Twice_ExplainsNothingWasUsedUp_AndKeepsTheInviteEntry()
    {
        using var h = await SignedIn(access: "invite_required");
        h.Service.Handler = r => r.Path == "/v1/invites/redeem" ? FakeService.Json(409, Error("redeem_conflict")) : h.DefaultHandler(r);

        var result = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);

        Assert.Equal(AccountFailure.Rejected, result.Failure);
        Assert.Equal(AccountMessages.RedeemConflict, result.Message);
        Assert.Equal(2, h.Service.To("/v1/invites/redeem").Count()); // one retry, not a loop
        Assert.Equal(OnlineState.NeedsInvite, h.Account.Current.State);
    }

    [Theory]
    [InlineData("account_changed")]
    [InlineData("user_changed")]
    public async Task DeleteChangedConflict_KeepsEverything_AndIsNotRetriedAutomatically(string code)
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/me" ? FakeService.Json(409, Error(code)) : h.DefaultHandler(r);

        var result = await h.Account.DeleteAccountAsync(Ct);

        Assert.Equal(AccountFailure.Rejected, result.Failure);
        Assert.Equal(AccountMessages.AccountChanged, result.Message);
        Assert.Single(h.Service.To("/v1/me"));
        Assert.Equal(0, h.Keys.Deleted);
        Assert.NotNull(h.Secrets.Read("refresh-token"));
        Assert.True(h.Account.Current.IsSignedIn);
    }

    [Fact]
    public async Task DeleteRateLimited_ShowsTheWait_AndKeepsEverything()
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/me" ? Limited(TimeSpan.FromMinutes(5)) : h.DefaultHandler(r);

        var result = await h.Account.DeleteAccountAsync(Ct);

        Assert.Equal(AccountFailure.RateLimited, result.Failure);
        Assert.Contains("5 minute", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Keys.Deleted);
    }

    // ---- 4. device JWK -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_SendsCanonicalUnpaddedBase64UrlCoordinates()
    {
        using var h = new AccountHarness();

        await h.Account.SignInAsync(Ct);

        using var body = JsonDocument.Parse(Assert.Single(h.Service.To("/v1/auth/discord")).Body);
        var jwk = body.RootElement.GetProperty("devicePublicKeyJwk");
        Assert.Equal(["crv", "kty", "x", "y"], jwk.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "x", "y" })
        {
            var text = jwk.GetProperty(name).GetString()!;
            Assert.Matches(Coordinate(), text);
            Assert.DoesNotContain('=', text);
            Assert.Equal(text, Base64Url.EncodeToString(Base64Url.DecodeFromChars(text))); // canonical: re-encoding gives the same text
            Assert.Contains(text[^1], "AEIMQUYcgkosw048"); // 32 bytes leave 2 spare bits in the 43rd character; they must be zero
        }
    }

    [Fact]
    public void Coordinates_AreCanonical_ForManyKeys_IncludingLeadingZeroBytes()
    {
        for (var i = 0; i < 64; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var q = key.ExportParameters(false).Q;
            var device = new DevicePublicKey(q.X!, q.Y!);

            Assert.Matches(Coordinate(), device.X);
            Assert.Matches(Coordinate(), device.Y);
            Assert.Equal(q.X, Base64Url.DecodeFromChars(device.X));
            Assert.Equal(q.Y, Base64Url.DecodeFromChars(device.Y));
        }

        var zeros = new byte[32];
        var padded = new DevicePublicKey(zeros, zeros);
        Assert.Equal(43, padded.X.Length); // a leading-zero coordinate keeps its full 32 bytes
        Assert.Throws<ArgumentException>(() => new DevicePublicKey(new byte[31], zeros)); // a short coordinate is refused locally
    }

    // ---- 5. banned people ----------------------------------------------------------------------------------------------------------

    private static string Revocation(AccountHarness h, string sub, string? cnf = null) => h.LicenceNow("revoked", sub, cnf);

    private static HttpResponseMessage Revoked(string revocation) => FakeService.Json(403, $$"""{"schemaVersion":1,"error":"revoked","revocation":{{JsonSerializer.Serialize(revocation)}}}""");

    [Fact]
    public async Task BannedFreshSignIn_WithARandomSub_IsHandled_KeyedOnCnfAndStatus()
    {
        using var h = new AccountHarness();
        var revocation = Revocation(h, "0b6c5f0e-3a43-4d0a-9d3e-5c1b8e2f7a11"); // a random id: no account exists for this person
        h.Service.Handler = _ => Revoked(revocation);

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountFailure.Rejected, result.Failure);
        Assert.Equal(AccountMessages.Revoked, result.Message);
        Assert.Equal(OnlineState.Revoked, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.Null(h.Secrets.Read("refresh-token")); // no session was created
        Assert.Equal(OnlineState.Revoked, h.Restart().Current.State);
    }

    [Fact]
    public async Task BannedSignInAgain_WithAnotherRandomSub_IsStillHandled()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Revoked(Revocation(h, "random-id-one"));
        await h.Account.SignInAsync(Ct);
        h.Service.Handler = _ => Revoked(Revocation(h, "random-id-two")); // every attempt names a new random id

        var again = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountMessages.Revoked, again.Message);
        Assert.Equal(OnlineState.Revoked, h.Account.Current.State);
    }

    [Fact]
    public async Task BannedSignIn_WithTheWrongDeviceKey_IsIgnored()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Revoked(Revocation(h, "random-id", cnf: "someone-elses-device-thumbprint"));

        var result = await h.Account.SignInAsync(Ct);

        Assert.Equal(AccountMessages.Rejected, result.Message);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed); // nothing unlocked it (D32)
    }

    [Fact]
    public async Task BannedSignIn_WithAnActiveStatusDocument_IsNotARevocation()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Revoked(h.LicenceNow("active", "random-id"));

        await h.Account.SignInAsync(Ct);

        Assert.NotEqual(OnlineState.Revoked, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed); // an active document with no session unlocks nothing (D32)
    }

    [Fact]
    public async Task ABannedSub_DoesNotRevokeAnotherUsersLiveSession()
    {
        using var h = await SignedIn();
        h.Service.Handler = _ => Revoked(Revocation(h, "somebody-else")); // a refresh answer naming a different subject

        await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(OnlineState.Active, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task AfterABan_SignInWorksAgainOnceTheServiceAccepts()
    {
        using var h = new AccountHarness();
        h.Service.Handler = _ => Revoked(Revocation(h, "random-id"));
        await h.Account.SignInAsync(Ct);
        Assert.Equal(OnlineState.Revoked, h.Account.Current.State);

        h.Service.Handler = h.DefaultHandler;
        var result = await h.Account.SignInAsync(Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
    }
}
