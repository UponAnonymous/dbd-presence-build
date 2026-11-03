// Covers: FR-ONL-01/02/11 (rate-limited actions wait and say so calmly), FR-ONL-03 + D8 (a session that ended is "sign in again", never a
// presence-stopping revocation; a banned person's fresh sign-in is shown as withdrawn), ADR-0011 (Refresh contract, Bans, 409 codes).
// The HTTP behaviour behind these results is covered with a stub HttpMessageHandler in DbdPresence.Online.Tests (ServiceContractTests).
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class AccountServiceContractTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static AccountResult Limited(TimeSpan wait) => AccountResult.RateLimited($"Too many attempts. Wait about {(int)wait.TotalSeconds} second(s) and try again.", wait);

    [Fact]
    public void EndedSession_IsSaidOnce_TheNoticeRepeatIsHidden_ButOtherNoticesStay()
    {
        var account = new FakeAccountService();
        using var vm = new AccountViewModel(account, new InlineDispatcher(), new FakeTimeProvider(s_start));

        account.Set(OnlineState.OfflineWithGrace, "Ana", features: false, needsSignIn: true, notice: AccountSnapshot.SessionEndedNotice);
        Assert.StartsWith("Your session ended.", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.HasNotice);
        Assert.Null(vm.Notice);

        account.Set(OnlineState.OfflineWithGrace, "Ana", features: false, needsSignIn: true, notice: "Your PC's clock is earlier than the service's.");
        Assert.True(vm.HasNotice);
    }

    [Fact]
    public async Task RateLimitedSignIn_ShowsTheMessage_AndKeepsTheButtonOffUntilTheWaitHasPassed()
    {
        var time = new FakeTimeProvider(s_start);
        var account = new FakeAccountService { SignInResult = Limited(TimeSpan.FromSeconds(45)) };
        using var vm = new AccountViewModel(account, new InlineDispatcher(), time);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Contains("Wait about 45 second", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.IsWaiting);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.True(vm.IsSignedOut);

        time.Advance(TimeSpan.FromSeconds(44));
        Assert.False(vm.SignInCommand.CanExecute(null));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(vm.IsWaiting);
        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.Equal(["signin"], account.Calls); // the wait cost no extra call
    }

    [Fact]
    public async Task RateLimitedInvite_DisablesRedeemAndDelete_ButNotSignOut()
    {
        var time = new FakeTimeProvider(s_start);
        var account = new FakeAccountService { SignInState = OnlineState.NeedsInvite, RedeemResult = Limited(TimeSpan.FromMinutes(2)) };
        using var vm = new AccountViewModel(account, new InlineDispatcher(), time);
        await vm.SignInCommand.ExecuteAsync(null);
        vm.InviteCode = "ABCD-EFGH-JKMN-PQRS";
        vm.DeleteConfirmation = AccountViewModel.DeleteWord;

        await vm.RedeemInviteCommand.ExecuteAsync(null);

        Assert.False(vm.RedeemInviteCommand.CanExecute(null));
        Assert.False(vm.DeleteAccountCommand.CanExecute(null));
        Assert.True(vm.SignOutCommand.CanExecute(null)); // signing out works offline and is never held back
        Assert.True(vm.NeedsInvite);
        Assert.Equal("ABCD-EFGH-JKMN-PQRS", vm.InviteCode); // the typed code is kept for the next try

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(vm.RedeemInviteCommand.CanExecute(null));
        Assert.True(vm.DeleteAccountCommand.CanExecute(null));
    }

    [Fact]
    public async Task OtherFailures_DoNotStartAWait()
    {
        var time = new FakeTimeProvider(s_start);
        var account = new FakeAccountService { SignInResult = AccountResult.Fail(AccountFailure.Offline, "Could not reach the service.") };
        using var vm = new AccountViewModel(account, new InlineDispatcher(), time);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.False(vm.IsWaiting);
        Assert.True(vm.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task SessionEnded_IsSignInAgain_AndLeavesSignInOpen_NotRevoked()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.Active);
        using var vm = new AccountViewModel(account, new InlineDispatcher(), new FakeTimeProvider(s_start));

        // What the account service does after 401 refresh_token_reused / session_revoked: the tokens go, the licence (and the unlock) stay.
        account.Set(OnlineState.OfflineWithGrace, features: false, needsSignIn: true);

        Assert.True(vm.ShowSignIn);
        Assert.True(vm.Snapshot.PresenceAllowed);
        Assert.False(vm.IsWaiting);
        Assert.Contains("sign in with Discord again", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.SignInCommand.CanExecute(null));
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.False(vm.Snapshot.NeedsSignIn);
        Assert.True(vm.IsSignedIn);
    }

    [Fact]
    public async Task BannedFreshSignIn_ShowsWithdrawn_StopsPresence_AndDoesNotOfferAnotherSignIn()
    {
        var account = new FakeAccountService
        {
            SignInResult = AccountResult.Fail(AccountFailure.Rejected, "Access for this account was withdrawn by the service owner. Your Discord status is off until that changes."),
        };
        using var vm = new AccountViewModel(account, new InlineDispatcher(), new FakeTimeProvider(s_start));
        Assert.True(vm.SignInCommand.CanExecute(null));

        await vm.SignInCommand.ExecuteAsync(null);
        account.Set(OnlineState.Revoked, name: null, features: false); // the service verified a signed revocation (random sub, matching cnf)

        Assert.Contains("withdrawn", vm.Message, StringComparison.Ordinal);
        Assert.Contains("withdrawn", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.Snapshot.PresenceAllowed);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.False(vm.IsWaiting); // a ban is not a rate limit
    }

    [Theory]
    [InlineData("Your code was being applied at the same moment as another change. Nothing was used up; enter it again in a moment.")]
    [InlineData("Your account changed while this was happening, so nothing was changed. Check the Account page and try again if you still want to.")]
    public async Task ConflictMessages_AreShownAsTheyAre_AndTheActionCanBeRepeatedAtOnce(string message)
    {
        var account = new FakeAccountService { SignInState = OnlineState.NeedsInvite, RedeemResult = AccountResult.Fail(AccountFailure.Rejected, message) };
        using var vm = new AccountViewModel(account, new InlineDispatcher(), new FakeTimeProvider(s_start));
        await vm.SignInCommand.ExecuteAsync(null);
        vm.InviteCode = "ABCD-EFGH-JKMN-PQRS";

        await vm.RedeemInviteCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Message);
        Assert.False(vm.IsWaiting);
        Assert.True(vm.RedeemInviteCommand.CanExecute(null));
        Assert.True(vm.NeedsInvite);
    }

    [Fact]
    public void RateLimitedResult_CarriesTheWait_AndIsNotASuccess()
    {
        var result = AccountResult.RateLimited("wait", TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.Equal(AccountFailure.RateLimited, result.Failure);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Null(AccountResult.Fail(AccountFailure.Offline, "x").RetryAfter);
    }
}
