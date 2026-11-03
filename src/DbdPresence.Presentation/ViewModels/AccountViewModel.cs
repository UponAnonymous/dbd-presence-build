using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Core.Online;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>
/// The Account page and the unlock flow's sign-in and invite steps (FR-ONL-01, 02, 11, 13; D32; UI_SPEC "Account page"). It shows the Discord display
/// name only (never the avatar: remote images are banned) and never touches tokens: everything goes through <see cref="IAccountService"/>.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    /// <summary>The word the user types to confirm deleting the account.</summary>
    public const string DeleteWord = "DELETE";

    private readonly IAccountService _account;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _signIn;
    private ITimer? _waitTimer;

    /// <summary>Initializes a new instance of the <see cref="AccountViewModel"/> class.</summary>
    /// <param name="account">The account service.</param>
    /// <param name="dispatcher">Runs UI updates on the UI thread.</param>
    /// <param name="time">The clock for the wait after a rate-limited call; the system clock when null.</param>
    /// <param name="uploads">The uploads section (FR-ONL-09), or null in builds without uploads.</param>
    public AccountViewModel(IAccountService account, IUiDispatcher dispatcher, TimeProvider? time = null, UploadsViewModel? uploads = null)
    {
        Uploads = uploads;
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _account = account;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        _account.Changed += OnChanged;
        Snapshot = account.Current;
    }

    /// <summary>Gets the uploads section, or null in builds without uploads.</summary>
    public UploadsViewModel? Uploads { get; }

    /// <summary>Gets a value indicating whether the uploads section exists in this build.</summary>
    public bool ShowUploads => Uploads is not null;

    /// <summary>Gets what deleting the account removes, in words.</summary>
    public string DeleteAccountText => Uploads is null
        ? "This removes your account, your devices and your sessions from the service, then signs you out here. It cannot be undone, and copies in backups can remain for up to 30 days."
        : "This removes your account, your devices, your sessions and every image you uploaded from the service, then signs you out here. It cannot be undone, copies in backups can remain for up to 30 days, and Discord may keep showing a deleted image for a while.";

    /// <summary>Gets the current account view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(StatusText), nameof(DisplayName), nameof(IsSignedIn), nameof(IsSignedOut), nameof(NeedsInvite), nameof(ShowInvite), nameof(PublicAccess), nameof(ShowSignIn), nameof(CanCheckAgain), nameof(IsLocked), nameof(LockText), nameof(Notice), nameof(HasNotice))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAccountCommand), nameof(SignOutCommand), nameof(RedeemInviteCommand), nameof(CheckAgainCommand))]
    public partial AccountSnapshot Snapshot { get; private set; }

    /// <summary>Gets the account state.</summary>
    public OnlineState State => Snapshot.State;

    /// <summary>Gets the Discord display name (text only), or an empty string.</summary>
    public string DisplayName => Snapshot.DisplayName ?? string.Empty;

    /// <summary>Gets a value indicating whether the user is signed in.</summary>
    public bool IsSignedIn => Snapshot.IsSignedIn;

    /// <summary>Gets a value indicating whether the user is not signed in.</summary>
    public bool IsSignedOut => !Snapshot.IsSignedIn;

    /// <summary>Gets a value indicating whether the service wants an invite code.</summary>
    public bool NeedsInvite => Snapshot.State == OnlineState.NeedsInvite;

    /// <summary>Gets a value indicating whether the invite box is shown: the service wants a code and the signed configuration does not say the access mode is public (D32).</summary>
    public bool ShowInvite => NeedsInvite && !Snapshot.PublicAccess;

    /// <summary>Gets a value indicating whether the signed configuration says the access mode is public: no invite step.</summary>
    public bool PublicAccess => Snapshot.PublicAccess;

    /// <summary>Gets a value indicating whether the Sign in button is shown: signed out, or unlocked with an ended session.</summary>
    public bool ShowSignIn => !Snapshot.IsSignedIn || Snapshot.NeedsSignIn;

    /// <summary>Gets a value indicating whether "Check again" is offered: signed in but locked for a reason a refresh can fix (no licence yet, or public access to claim).</summary>
    public bool CanCheckAgain => Snapshot.State == OnlineState.LicenceNeeded || (Snapshot.State == OnlineState.NeedsInvite && Snapshot.PublicAccess);

    /// <summary>Gets a value indicating whether the app is locked (D32): no verified licence, or access withdrawn.</summary>
    public bool IsLocked => Snapshot.IsLocked;

    /// <summary>Gets the honest explanation of why the app is locked and what unlocks it (D32), or an empty string when it is not locked.</summary>
    public string LockText => Snapshot.State switch
    {
        OnlineState.SignedOut => Snapshot.PublicAccess ? AppCopy.LockedPublic : AppCopy.LockedInviteOnly,
        OnlineState.NeedsInvite => Snapshot.PublicAccess ? "Finishing your access. Press Check again if this takes more than a moment." : "Signed in. Enter your invite code to unlock DBD Presence.",
        OnlineState.LicenceNeeded => "Signed in, but this PC has not received its licence yet. Check your internet connection and press Check again.",
        OnlineState.Revoked => "Access for this account was withdrawn, so nothing is shown on Discord.",
        _ => string.Empty,
    };

    /// <summary>Gets the one-line status for the current state.</summary>
    public string StatusText => Snapshot.State switch
    {
        OnlineState.SignedOut => "Not signed in. " + (Snapshot.PublicAccess ? AppCopy.LockedPublic : AppCopy.LockedInviteOnly),
        OnlineState.NeedsInvite => Snapshot.PublicAccess ? "Signed in. Finishing your access." : "Signed in. Enter an invite code to finish.",
        OnlineState.LicenceNeeded => "Signed in, but this PC has not received its licence yet, so DBD Presence stays locked until it does.",
        OnlineState.OfflineWithGrace when Snapshot.NeedsSignIn => "Your session ended. Your Discord status keeps working; sign in with Discord again to use online features.",
        OnlineState.Active => Snapshot.OnlineFeaturesAllowed ? "Signed in. Online features are available." : "Signed in. Online features are paused for now.",
        OnlineState.Revoked => "Access for this account was withdrawn. Your Discord status is off until that changes.",
        _ => "Signed in, but the service can't be reached or your licence needs a refresh. Your Discord status keeps working.",
    };

    /// <summary>Gets an extra explanation (clock behind, service unreachable), or null.</summary>
    public string? Notice => Snapshot.NeedsSignIn && string.Equals(Snapshot.Notice, AccountSnapshot.SessionEndedNotice, StringComparison.Ordinal) ? null : Snapshot.Notice;

    /// <summary>Gets a value indicating whether <see cref="Notice"/> has text.</summary>
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>Gets the result message of the last action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether <see cref="Message"/> has text.</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Gets a value indicating whether an action is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(RedeemInviteCommand), nameof(SignOutCommand), nameof(DeleteAccountCommand), nameof(CancelCommand), nameof(CheckAgainCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets a value indicating whether no action is running.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Gets or sets the invite code as typed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedeemInviteCommand))]
    public partial string InviteCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the text typed to confirm deletion.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteAccountCommand))]
    public partial string DeleteConfirmation { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether the service asked the app to wait: sign-in, invite and delete stay disabled until the wait has passed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(RedeemInviteCommand), nameof(DeleteAccountCommand), nameof(CheckAgainCommand))]
    public partial bool IsWaiting { get; private set; }

    /// <summary>Stops listening to the account service.</summary>
    public void Dispose()
    {
        _account.Changed -= OnChanged;
        _waitTimer?.Dispose();
        _signIn?.Dispose();
    }

    /// <summary>Signs in with Discord in the system browser.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _signIn = cancellation;
        await RunAsync(() => _account.SignInAsync(cancellation.Token)).ConfigureAwait(true);
        _signIn = null;
    }

    private bool CanSignIn() => !IsBusy && !IsWaiting && Snapshot.State != OnlineState.Revoked;

    /// <summary>Stops waiting for the browser.</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _signIn?.Cancel();

    /// <summary>Redeems the typed invite code.</summary>
    [RelayCommand(CanExecute = nameof(CanRedeem))]
    private async Task RedeemInviteAsync()
    {
        await RunAsync(() => _account.RedeemInviteAsync(InviteCode, CancellationToken.None)).ConfigureAwait(true);
        if (!NeedsInvite)
        {
            InviteCode = string.Empty;
        }
    }

    private bool CanRedeem() => !IsBusy && !IsWaiting && ShowInvite && !string.IsNullOrWhiteSpace(InviteCode);

    /// <summary>Asks the service again now: fetches the licence this PC lacks, or claims access when the signed configuration says it is public (D32).</summary>
    [RelayCommand(CanExecute = nameof(CanCheckAgainNow))]
    private Task CheckAgainAsync() => RunAsync(() => _account.RefreshAsync(force: true, CancellationToken.None));

    private bool CanCheckAgainNow() => !IsBusy && !IsWaiting && CanCheckAgain;

    /// <summary>Signs out on this PC.</summary>
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private Task SignOutAsync() => RunAsync(() => _account.SignOutAsync(CancellationToken.None));

    private bool CanSignOut() => !IsBusy && Snapshot.IsSignedIn;

    /// <summary>Deletes the account after the typed confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAccountAsync()
    {
        await RunAsync(() => _account.DeleteAccountAsync(CancellationToken.None)).ConfigureAwait(true);
        if (!Snapshot.IsSignedIn)
        {
            DeleteConfirmation = string.Empty;
        }
    }

    private bool CanDelete() => !IsBusy && !IsWaiting && Snapshot.IsSignedIn && string.Equals(DeleteConfirmation.Trim(), DeleteWord, StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync(Func<Task<AccountResult>> action)
    {
        IsBusy = true;
        Message = string.Empty;
        try
        {
            var result = await action().ConfigureAwait(true);
            Message = result.Message;
            if (result is { Failure: AccountFailure.RateLimited, RetryAfter: { } wait } && wait > TimeSpan.Zero)
            {
                StartWaiting(wait);
            }
        }
        catch (OperationCanceledException)
        {
            Message = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
            Snapshot = _account.Current;
        }
    }

    /// <summary>Keeps the network actions disabled for the wait the service asked for; a timer on the injected clock enables them again.</summary>
    private void StartWaiting(TimeSpan wait)
    {
        _waitTimer?.Dispose();
        IsWaiting = true;
        _waitTimer = _time.CreateTimer(_ => _dispatcher.Post(() => IsWaiting = false), null, wait, Timeout.InfiniteTimeSpan);
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Snapshot = _account.Current);
}
