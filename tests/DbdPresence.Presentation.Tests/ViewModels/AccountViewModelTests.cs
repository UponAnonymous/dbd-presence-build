// Covers: FR-ONL-01 (sign-in from the UI), FR-ONL-02 (invite entry only when needed), FR-ONL-11 (sign out; delete needs a typed
// confirmation), FR-ONL-05 (offline state is shown, presence is not touched), FR-ONL-13 / D32 (the lock screen: sign-in and invite only
// while locked, no invite box in public mode, withdrawn accounts keep the Account page).
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Tests.Support;
using DbdPresence.Presentation.ViewModels;

namespace DbdPresence.Presentation.Tests.ViewModels;

public sealed class AccountViewModelTests
{
    private static readonly AppInfo s_app = new("1.0.0", "abc1234", IsSigned: false, "settings", "data", "notice.md");

    [Fact]
    public async Task SignIn_ShowsTheDisplayNameOnly_AndTheSignedInState()
    {
        var account = new FakeAccountService();
        using var vm = new AccountViewModel(account, new InlineDispatcher());
        Assert.True(vm.IsSignedOut);
        Assert.True(vm.SignInCommand.CanExecute(null));

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedIn);
        Assert.Equal("Meg", vm.DisplayName);
        Assert.Equal("Signed in.", vm.Message);
        Assert.Contains("Online features are available", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(AccountViewModel).GetProperties(), p => p.Name.Contains("Avatar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailedSignIn_ShowsTheMessage_AndStaysSignedOut()
    {
        var account = new FakeAccountService { SignInResult = AccountResult.Fail(AccountFailure.Offline, "Could not reach the service.") };
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedOut);
        Assert.Equal("Could not reach the service.", vm.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task InviteEntry_IsOfferedOnlyWhileTheServiceAsks_AndClearsOnSuccess()
    {
        var account = new FakeAccountService { SignInState = OnlineState.NeedsInvite };
        using var vm = new AccountViewModel(account, new InlineDispatcher());
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsInvite);
        Assert.False(vm.RedeemInviteCommand.CanExecute(null));

        vm.InviteCode = "ABCD-EFGH-JKMN-PQRS";
        Assert.True(vm.RedeemInviteCommand.CanExecute(null));
        await vm.RedeemInviteCommand.ExecuteAsync(null);

        Assert.False(vm.NeedsInvite);
        Assert.Equal(string.Empty, vm.InviteCode);
        Assert.Contains("redeem:ABCD-EFGH-JKMN-PQRS", account.Calls);
    }

    [Fact]
    public async Task Delete_NeedsTheTypedWord_AndSignsOut()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.Active);
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        Assert.False(vm.DeleteAccountCommand.CanExecute(null));
        vm.DeleteConfirmation = "delet";
        Assert.False(vm.DeleteAccountCommand.CanExecute(null));
        vm.DeleteConfirmation = "delete";
        Assert.True(vm.DeleteAccountCommand.CanExecute(null));
        await vm.DeleteAccountCommand.ExecuteAsync(null);

        Assert.Equal(["delete"], account.Calls);
        Assert.True(vm.IsSignedOut);
        Assert.Equal(string.Empty, vm.DeleteConfirmation);
    }

    [Fact]
    public async Task SignOut_IsOnlyOfferedWhenSignedIn()
    {
        var account = new FakeAccountService();
        using var vm = new AccountViewModel(account, new InlineDispatcher());
        Assert.False(vm.SignOutCommand.CanExecute(null));

        account.Set(OnlineState.OfflineWithGrace);
        Assert.True(vm.SignOutCommand.CanExecute(null));
        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedOut);
    }

    [Theory]
    [InlineData(OnlineState.SignedOut, "Not signed in")]
    [InlineData(OnlineState.NeedsInvite, "invite code")]
    [InlineData(OnlineState.Revoked, "withdrawn")]
    [InlineData(OnlineState.OfflineWithGrace, "Your Discord status keeps working")]
    public void StatusText_NamesEachState(OnlineState state, string expected)
    {
        var account = new FakeAccountService();
        account.Set(state);
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        Assert.Contains(expected, vm.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OnlineState.SignedOut, "invite-only for now")]
    [InlineData(OnlineState.LicenceNeeded, "licence")]
    public void StatusText_WhileLocked_StatesWhyAndWhatUnlocks(OnlineState state, string expected)
    {
        var account = new FakeAccountService();
        account.Set(state);
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        Assert.True(vm.IsLocked);
        Assert.Contains(expected, vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusText_InPublicMode_SaysNoInviteIsNeeded()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.SignedOut, publicAccess: true);
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        Assert.Contains("No invite code is needed", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("invite-only", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicMode_NeverShowsTheInviteBox_AndOffersCheckAgain()
    {
        var account = new FakeAccountService { RefreshState = OnlineState.Active };
        account.Set(OnlineState.NeedsInvite, publicAccess: true);
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        Assert.False(vm.ShowInvite);
        Assert.False(vm.RedeemInviteCommand.CanExecute(null));
        Assert.True(vm.CanCheckAgain);

        vm.CheckAgainCommand.Execute(null);

        Assert.Equal(["refresh"], account.Calls);
        Assert.False(vm.IsLocked);
        Assert.False(vm.CanCheckAgain);
    }

    [Fact]
    public void EndedSession_StaysUnlocked_ButOffersSignIn()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.OfflineWithGrace, features: false, needsSignIn: true);
        using var vm = new AccountViewModel(account, new InlineDispatcher());

        Assert.False(vm.IsLocked);
        Assert.True(vm.ShowSignIn);
        Assert.Contains("Your Discord status keeps working", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Unlock_ShowsOnlyWhileLocked_NotWhenWithdrawn()
    {
        var account = new FakeAccountService();
        using var accountVm = new AccountViewModel(account, new InlineDispatcher());
        using var unlock = new UnlockViewModel(accountVm, new PrivacyViewModel(new Harness().Settings, new InlineDispatcher(), s_app));

        Assert.True(unlock.IsShown);
        Assert.True(unlock.ShowSignIn);
        Assert.False(unlock.ShowInvite);
        Assert.Equal("DBD Presence is invite-only for now. Sign in with Discord and enter your invite code.", unlock.Reason);
        Assert.NotEmpty(unlock.Notice);

        account.Set(OnlineState.NeedsInvite);
        Assert.True(unlock.IsShown);
        Assert.False(unlock.ShowSignIn);
        Assert.True(unlock.ShowInvite);

        account.Set(OnlineState.Active);
        Assert.False(unlock.IsShown);

        account.Set(OnlineState.OfflineWithGrace, features: false);
        Assert.False(unlock.IsShown);

        account.Set(OnlineState.Revoked);
        Assert.False(unlock.IsShown); // the Account page explains and still offers deletion
        Assert.True(accountVm.IsLocked);
    }

    [Fact]
    public async Task Unlock_SignInThenInvite_UnlocksTheApp()
    {
        var account = new FakeAccountService { SignInState = OnlineState.NeedsInvite };
        using var accountVm = new AccountViewModel(account, new InlineDispatcher());
        using var unlock = new UnlockViewModel(accountVm, new PrivacyViewModel(new Harness().Settings, new InlineDispatcher(), s_app));

        await accountVm.SignInCommand.ExecuteAsync(null);
        Assert.True(unlock.IsShown);
        Assert.True(unlock.ShowInvite);
        Assert.False(accountVm.RedeemInviteCommand.CanExecute(null));

        accountVm.InviteCode = "ABCD-EFGH-JKMN-PQRS";
        await accountVm.RedeemInviteCommand.ExecuteAsync(null);

        Assert.False(unlock.IsShown);
        Assert.Contains("redeem:ABCD-EFGH-JKMN-PQRS", account.Calls);
    }

    [Fact]
    public void Unlock_InPublicMode_ReasonNamesNoInvite()
    {
        var account = new FakeAccountService();
        account.Set(OnlineState.SignedOut, publicAccess: true);
        using var accountVm = new AccountViewModel(account, new InlineDispatcher());
        using var unlock = new UnlockViewModel(accountVm, new PrivacyViewModel(new Harness().Settings, new InlineDispatcher(), s_app));

        Assert.True(unlock.IsShown);
        Assert.True(unlock.ShowSignIn);
        Assert.Equal(AppCopy.LockedPublic, unlock.Reason);
    }
}
