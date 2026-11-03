using CommunityToolkit.Mvvm.ComponentModel;
using DbdPresence.Core.Online;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// The unlock screen (D32, FR-ONL-13; the startup wizard's sign-in and invite steps, docs/design/whole-app.md §13). While the app is locked it
/// is the only thing shown: the shell, the pages and every presence control stay hidden. It adds no logic of its own; sign-in, the invite code
/// and "Check again" are the <see cref="AccountViewModel"/> commands, so the lock and the Account page can never disagree. A withdrawn account
/// (a signed revocation) is not shown here: it keeps the Account page so the person can still read why and delete their data.
/// </summary>
public sealed partial class UnlockViewModel : ObservableObject, IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="UnlockViewModel"/> class.</summary>
    /// <param name="account">The account model that owns the commands.</param>
    /// <param name="privacy">The privacy page model, for the path of the installed privacy notice.</param>
    public UnlockViewModel(AccountViewModel account, PrivacyViewModel privacy)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(privacy);
        Account = account;
        Privacy = privacy;
        account.PropertyChanged += OnAccountChanged;
    }

    /// <summary>Gets the account model (commands and state).</summary>
    public AccountViewModel Account { get; }

    /// <summary>Gets the privacy model (the notice path).</summary>
    public PrivacyViewModel Privacy { get; }

    /// <summary>Gets a value indicating whether the unlock screen replaces the app: locked, and not withdrawn.</summary>
    public bool IsShown => Account.IsLocked && Account.State != OnlineState.Revoked;

    /// <summary>Gets the title.</summary>
    public string Title { get; } = AppCopy.UnlockTitle;

    /// <summary>Gets the honest reason the app is locked and what unlocks it.</summary>
    public string Reason => Account.LockText;

    /// <summary>Gets a value indicating whether the Sign in step is shown (not signed in).</summary>
    public bool ShowSignIn => Account.IsSignedOut;

    /// <summary>Gets a value indicating whether the invite step is shown: signed in, an invite is needed and the access mode is not public.</summary>
    public bool ShowInvite => Account.ShowInvite;

    /// <summary>Gets what the service keeps, in plain sentences.</summary>
    public IReadOnlyList<string> Notice { get; } = AppCopy.AccountNotice;

    /// <inheritdoc />
    public void Dispose() => Account.PropertyChanged -= OnAccountChanged;

    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.Snapshot))
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
