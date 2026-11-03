using DbdPresence.Core.Online;

namespace DbdPresence.Presentation.Tests.Support;

/// <summary>An <see cref="IAccountService"/> whose state and results the test sets.</summary>
internal sealed class FakeAccountService : IAccountService
{
    public event EventHandler? Changed;

    public AccountSnapshot Current { get; private set; } = AccountSnapshot.SignedOut;

    public AccountResult SignInResult { get; set; } = AccountResult.Ok("Signed in.");

    public OnlineState SignInState { get; set; } = OnlineState.Active;

    public AccountResult RedeemResult { get; set; } = AccountResult.Ok("Invite accepted.");

    public List<string> Calls { get; } = [];

    /// <summary>Gets or sets the state a refresh moves to (for example after claiming public access), or null to leave it as it is.</summary>
    public OnlineState? RefreshState { get; set; }

    public void Set(OnlineState state, string? name = "Meg", bool features = true, bool publicAccess = false, bool needsSignIn = false, string? notice = null)
    {
        Current = state == OnlineState.SignedOut
            ? AccountSnapshot.SignedOut with { PublicAccess = publicAccess }
            : new AccountSnapshot(state, name, features, null, notice, publicAccess, needsSignIn);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<AccountResult> SignInAsync(CancellationToken cancellationToken)
    {
        Calls.Add("signin");
        if (SignInResult.Succeeded)
        {
            Set(SignInState);
        }

        return Task.FromResult(SignInResult);
    }

    public Task<AccountResult> RedeemInviteAsync(string code, CancellationToken cancellationToken)
    {
        Calls.Add("redeem:" + code);
        if (RedeemResult.Succeeded)
        {
            Set(OnlineState.Active);
        }

        return Task.FromResult(RedeemResult);
    }

    public Task<AccountResult> SignOutAsync(CancellationToken cancellationToken)
    {
        Calls.Add("signout");
        Set(OnlineState.SignedOut);
        return Task.FromResult(AccountResult.Ok("Signed out."));
    }

    public Task<AccountResult> DeleteAccountAsync(CancellationToken cancellationToken)
    {
        Calls.Add("delete");
        Set(OnlineState.SignedOut);
        return Task.FromResult(AccountResult.Ok("Deleted."));
    }

    public Task<AccountResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        Calls.Add("refresh");
        if (RefreshState is { } state)
        {
            Set(state, publicAccess: Current.PublicAccess);
        }

        return Task.FromResult(AccountResult.Ok("Up to date."));
    }
}
