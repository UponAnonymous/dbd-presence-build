// Covers: D32 / FR-ONL-13 (the app is locked until the device holds a verified licence: sign-in plus invite, or sign-in alone while a signed,
// unexpired configuration says the access mode is public; never on an unsigned, forged or expired configuration), D8 / FR-ONL-05 (once
// unlocked, an outage, a lapsed licence or a changed clock never lock the app; only a received, signed revocation, sign-out or deletion does),
// ADR-0011 (licence lifetime and grace rule), ADR-0012 (signed documents).
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Online.Account;

namespace DbdPresence.Online.Tests;

public sealed class InviteLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void ServeConfig(AccountHarness h, Func<string> document) =>
        h.Service.Handler = r => r.Path == "/v1/config"
            ? FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(document())}}}""")
            : h.DefaultHandler(r);

    private static async Task<AccountHarness> SignedIn(string access = "granted")
    {
        var h = new AccountHarness { Access = access };
        var result = await h.Account.SignInAsync(Ct);
        Assert.True(result.Succeeded, result.Message);
        return h;
    }

    // ---- locked ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FreshInstall_IsLocked_AndSendsNothing()
    {
        using var h = new AccountHarness();

        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.True(h.Account.Current.IsLocked);
        Assert.False(h.Account.Current.PublicAccess);
        Assert.Empty(h.Service.Requests);
    }

    [Fact]
    public async Task SignedInWithoutAnInvite_IsStillLocked_UntilTheCodeIsRedeemed()
    {
        using var h = await SignedIn(access: "invite_required");

        Assert.Equal(OnlineState.NeedsInvite, h.Account.Current.State);
        Assert.True(h.Account.Current.IsLocked);
        Assert.False(h.Restart().Current.PresenceAllowed);

        var result = await h.Account.RedeemInviteAsync("ABCD-EFGH-JKMN-PQRS", Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.True(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task AGrantWhoseLicenceCannotBeVerified_LeavesTheAppLocked_AndTheNextScheduledRefreshRetries()
    {
        using var h = new AccountHarness { LicenceOverride = "not a signed document" };

        var result = await h.Account.SignInAsync(Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountMessages.LicenceUnverified, result.Message);
        Assert.Equal(OnlineState.LicenceNeeded, h.Account.Current.State);
        Assert.True(h.Account.Current.IsLocked);

        h.LicenceOverride = null;
        var retry = await h.Account.RefreshAsync(force: false, Ct); // not yet due by age: a locked device does not wait a day

        Assert.True(retry.Succeeded, retry.Message);
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task ALicenceForAnotherDevice_OrSignedByAnotherKey_NeverUnlocks()
    {
        using var h = await SignedIn();
        Assert.True(h.Account.Current.PresenceAllowed);

        h.Secrets.Write("licence", h.LicenceNow(cnf: "another-devices-thumbprint"));
        Assert.Equal(OnlineState.LicenceNeeded, h.Restart().Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);

        h.Secrets.Write("licence", h.Issuer.Licence(h.Time.GetUtcNow(), AccountHarness.UserId, h.Thumbprint, withOtherKey: true));
        Assert.False(h.Restart().Current.PresenceAllowed);

        h.Secrets.Delete("licence");
        Assert.False(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task ALicenceForAnotherUser_NeverUnlocks()
    {
        using var h = await SignedIn();

        h.Secrets.Write("licence", h.LicenceNow(sub: "somebody-else"));

        Assert.False(h.Restart().Current.PresenceAllowed);
    }

    // ---- unlocked: D8 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OfflineAfterUnlock_StillAllowed_AlsoAcrossARestart()
    {
        using var h = await SignedIn();
        h.Service.Throw = new HttpRequestException("down");

        var result = await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(AccountFailure.Offline, result.Failure);
        Assert.Equal(OnlineState.OfflineWithGrace, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.True(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task LicenceLapsedWhileOffline_ForAMonth_DoesNotLockTheApp_ButPausesOnlineFeatures()
    {
        using var h = await SignedIn();
        var expiresAt = h.Account.Current.LicenceExpiresAt;
        h.Service.Throw = new HttpRequestException("down");

        h.Time.Advance(TimeSpan.FromDays(30));
        h.Account.Reevaluate();

        Assert.NotNull(expiresAt);
        Assert.True(h.Time.GetUtcNow() > expiresAt);
        Assert.Equal(OnlineState.OfflineWithGrace, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed);
        Assert.True(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task ChangingTheClock_NeverLocksAnUnlockedApp()
    {
        using var h = await SignedIn();

        h.Time.Rewind(TimeSpan.FromDays(3 * 365));
        h.Account.Reevaluate();
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.False(h.Account.Current.OnlineFeaturesAllowed); // the clock rule still pauses online features
        Assert.True(h.Restart().Current.PresenceAllowed);

        h.Time.ResetSkew();
        h.Time.Advance(TimeSpan.FromDays(5 * 365));
        h.Account.Reevaluate();
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public void ChangingTheClock_NeverUnlocksALockedApp()
    {
        using var h = new AccountHarness();

        h.Time.Advance(TimeSpan.FromDays(5 * 365));
        h.Account.Reevaluate();
        Assert.False(h.Account.Current.PresenceAllowed);

        h.Time.Rewind(TimeSpan.FromDays(10 * 365));
        h.Account.Reevaluate();
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.False(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task SignedRevocation_LocksTheApp_AndItStaysLockedAfterARestart()
    {
        using var h = await SignedIn();
        var revocation = h.LicenceNow("revoked");
        h.Service.Handler = r => r.Path == "/v1/auth/refresh"
            ? FakeService.Json(403, $$"""{"schemaVersion":1,"error":"revoked","revocation":{{JsonSerializer.Serialize(revocation)}}}""")
            : h.DefaultHandler(r);

        await h.Account.RefreshAsync(force: true, Ct);

        Assert.Equal(OnlineState.Revoked, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.False(h.Restart().Current.PresenceAllowed);
    }

    [Fact]
    public async Task UnsignedRevokedAnswer_DoesNotLock()
    {
        using var h = await SignedIn();
        h.Service.Handler = r => r.Path == "/v1/auth/refresh" ? FakeService.Json(403, """{"schemaVersion":1,"error":"revoked"}""") : h.DefaultHandler(r);

        await h.Account.RefreshAsync(force: true, Ct);

        Assert.True(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task SigningOut_Locks_AndSigningInAgainUnlocks()
    {
        using var h = await SignedIn();

        await h.Account.SignOutAsync(Ct);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.False(h.Restart().Current.PresenceAllowed);

        var result = await h.Account.SignInAsync(Ct);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(h.Account.Current.PresenceAllowed);
    }

    // ---- public mode ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PublicMode_SignInAlone_Unlocks_WithoutTypingACode()
    {
        using var h = new AccountHarness { Access = "invite_required" };
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "public"));

        var result = await h.Account.SignInAsync(Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(h.Account.Current.PublicAccess);
        Assert.Equal(OnlineState.Active, h.Account.Current.State);
        Assert.True(h.Account.Current.PresenceAllowed);
        var claim = Assert.Single(h.Service.To("/v1/invites/redeem"));
        Assert.StartsWith("Bearer at_token", claim.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicMode_StillNeedsSignIn_ThePublicConfigAloneNeverUnlocks()
    {
        using var h = new AccountHarness();
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "public"));

        await h.Account.RefreshAsync(force: false, Ct);

        Assert.True(h.Account.Current.PublicAccess);
        Assert.Equal(OnlineState.SignedOut, h.Account.Current.State);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.Empty(h.Service.To("/v1/invites/redeem"));
    }

    [Fact]
    public async Task PublicMode_ASignedInAccountThatWaitedForAnInvite_ClaimsAccessAtTheNextRefresh()
    {
        using var h = await SignedIn(access: "invite_required");
        Assert.False(h.Account.Current.PresenceAllowed);
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "public"));

        var result = await h.Account.RefreshAsync(force: false, Ct);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(h.Account.Current.PresenceAllowed);
        Assert.Single(h.Service.To("/v1/invites/redeem"));
    }

    [Fact]
    public async Task PublicMode_WhenTheServiceIsInviteOnlyAfterAll_StaysLocked_WithAnHonestMessage()
    {
        using var h = await SignedIn(access: "invite_required");
        h.Service.Handler = r => r.Path switch
        {
            "/v1/config" => FakeService.Json(200, $$"""{"schemaVersion":1,"document":{{JsonSerializer.Serialize(h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "public"))}}}"""),
            "/v1/invites/redeem" => FakeService.Json(400, """{"schemaVersion":1,"error":"invite_invalid"}"""),
            _ => h.DefaultHandler(r),
        };

        var result = await h.Account.RefreshAsync(force: false, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(AccountMessages.PublicClosed, result.Message);
        Assert.False(h.Account.Current.PresenceAllowed);
    }

    [Fact]
    public async Task ForgedPublicConfig_SignedByAnotherKey_IsIgnored_AndTheAppStaysLocked()
    {
        using var h = await SignedIn(access: "invite_required");
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "public", withOtherKey: true));

        await h.Account.RefreshAsync(force: true, Ct);

        Assert.False(h.Account.Current.PublicAccess);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.Empty(h.Service.To("/v1/invites/redeem"));
    }

    [Fact]
    public async Task UnsignedPublicConfig_IsIgnored_AndTheAppStaysLocked()
    {
        using var h = await SignedIn(access: "invite_required");
        const string unsigned = """{"schemaVersion":1,"accessMode":"public","applications":[],"disable":{"capture":false,"detection":false},"emergencyStop":false}""";
        ServeConfig(h, () => unsigned);

        await h.Account.RefreshAsync(force: true, Ct);

        Assert.False(h.Account.Current.PublicAccess);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.Empty(h.Service.To("/v1/invites/redeem"));
    }

    [Fact]
    public async Task ExpiredPublicConfig_IsNoLongerTrusted_AndTheAppStaysLocked()
    {
        using var h = new AccountHarness { Access = "invite_required" };
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "public", lifetime: TimeSpan.FromHours(1)));
        Assert.True(await h.Config.RefreshAsync(Ct));
        Assert.True(h.Config.Current.AccessPublic);

        h.Time.Advance(TimeSpan.FromHours(2));
        h.Service.Handler = r => r.Path == "/v1/config" ? FakeService.Json(503, """{"schemaVersion":1,"error":"unavailable"}""") : h.DefaultHandler(r);
        await h.Account.SignInAsync(Ct);

        Assert.False(h.Config.Current.AccessPublic);
        Assert.False(h.Account.Current.PublicAccess);
        Assert.False(h.Account.Current.PresenceAllowed);
        Assert.Empty(h.Service.To("/v1/invites/redeem"));
    }

    [Fact]
    public async Task InviteOnlyConfig_DoesNotSkipTheInvite()
    {
        using var h = new AccountHarness { Access = "invite_required" };
        ServeConfig(h, () => h.Issuer.Config(h.Time.GetUtcNow(), accessMode: "invite-only"));

        await h.Account.SignInAsync(Ct);

        Assert.False(h.Account.Current.PublicAccess);
        Assert.Equal(OnlineState.NeedsInvite, h.Account.Current.State);
        Assert.Empty(h.Service.To("/v1/invites/redeem"));
    }

    [Fact]
    public void ReleaseCodeHasNoBypass_TheOnlyWayToUnlockIsALicenceThatVerifies()
    {
        using var h = new AccountHarness();
        h.Secrets.Write("profile", """{"userId":"user-1","displayName":"Meg","hasAccess":true,"revoked":false,"lastRefreshUtc":null}""");
        h.Secrets.Write("refresh-token", "rt_forged");

        // A hand-edited profile that claims access, with no licence behind it, unlocks nothing.
        Assert.False(h.Restart().Current.PresenceAllowed);
        Assert.Equal(OnlineState.LicenceNeeded, h.Account.Current.State);
    }
}
