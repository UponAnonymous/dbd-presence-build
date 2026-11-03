using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>The startup wizard's four steps (design §13).</summary>
public enum FirstRunStep
{
    /// <summary>What the app does and does not do.</summary>
    Welcome,

    /// <summary>The app finds Discord by itself.</summary>
    ConnectDiscord,

    /// <summary>The account the app is unlocked with (D32: the sign-in itself is the lock screen).</summary>
    SignIn,

    /// <summary>The live status and the screen reading choice.</summary>
    Ready,
}

/// <summary>What the Discord check on step 2 found.</summary>
public enum DiscordCheck
{
    /// <summary>Looking for Discord on this PC.</summary>
    Looking,

    /// <summary>Connected to Discord.</summary>
    Connected,

    /// <summary>Discord is not open on this PC.</summary>
    NotOpen,
}

/// <summary>
/// The startup wizard (design §13, FR-LIFE-07): Welcome, Connect Discord, Sign in, Ready. It is shown only after the app is unlocked (D32), so the
/// sign-in step does not offer a skip or an optional account: sign-in and the invite code are the lock screen, and this step confirms who the app is
/// unlocked for. Step 2 checks Discord by itself from the shared connection state; step 4 shows the status as Discord has it now and asks about screen
/// reading. Finishing, or skipping, records that setup was completed; every choice stays editable in the app.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Stack<FirstRunStep> _history = new();

    /// <summary>Initializes a new instance of the <see cref="FirstRunViewModel"/> class.</summary>
    public FirstRunViewModel(
        ISettingsService settings, PresenceControlsViewModel controls, AccountViewModel account, ScreenReadingViewModel screenReading, HomeViewModel home)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(screenReading);
        ArgumentNullException.ThrowIfNull(home);
        _settings = settings;
        Controls = controls;
        Account = account;
        ScreenReading = screenReading;
        Home = home;
        IsOpen = !settings.Current.FirstRunCompleted;
        controls.PropertyChanged += OnControlsChanged;
        account.PropertyChanged += OnAccountChanged;
    }

    /// <summary>Raised when the flow closes.</summary>
    public event EventHandler? Completed;

    /// <summary>Gets the shared controls (the Discord connection).</summary>
    public PresenceControlsViewModel Controls { get; }

    /// <summary>Gets the account model.</summary>
    public AccountViewModel Account { get; }

    /// <summary>Gets the screen reading model.</summary>
    public ScreenReadingViewModel ScreenReading { get; }

    /// <summary>Gets the Home model, for the live card on the last step.</summary>
    public HomeViewModel Home { get; }

    /// <summary>Gets the number of steps ("Step n of 4").</summary>
    public int StepCount { get; } = Enum.GetValues<FirstRunStep>().Length;

    /// <summary>Gets the short summary of what the service keeps (the first three lines of the notice).</summary>
    public IReadOnlyList<string> NoticeSummary { get; } = [.. AppCopy.AccountNotice.Take(3)];

    /// <summary>Gets the whole notice, shown after "Read all of it".</summary>
    public IReadOnlyList<string> NoticeAll { get; } = AppCopy.AccountNotice;

    /// <summary>Gets or sets a value indicating whether the whole notice is shown.</summary>
    [ObservableProperty]
    public partial bool ShowFullNotice { get; set; }

    /// <summary>Gets a value indicating whether the flow is showing.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>Gets the current step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepNumber), nameof(CanGoBack), nameof(IsLastStep), nameof(NextLabel), nameof(SkipLabel), nameof(CanSkip), nameof(CanFinish), nameof(FinishCaption))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(SkipCommand))]
    public partial FirstRunStep Step { get; private set; }

    /// <summary>Gets or sets a value indicating whether the user chose to turn screen reading on (step 4). Off is the default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish), nameof(FinishCaption), nameof(ChoseNotNow))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool TurnOnScreenReading { get; set; }

    /// <summary>Gets or sets a value indicating whether the user ticked "I understand" (step 4, only when turning screen reading on).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinish), nameof(FinishCaption))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool Understood { get; set; }

    /// <summary>Gets a value indicating whether "Not now" is chosen.</summary>
    public bool ChoseNotNow
    {
        get => !TurnOnScreenReading;
        set => TurnOnScreenReading = !value;
    }

    /// <summary>Gets the 1-based step number for "Step n of 4".</summary>
    public int StepNumber => (int)Step + 1;

    /// <summary>Gets a value indicating whether there is a previous step (from step 2).</summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>Gets a value indicating whether this is the last step.</summary>
    public bool IsLastStep => Step == FirstRunStep.Ready;

    /// <summary>Gets what the Discord check on step 2 found.</summary>
    public DiscordCheck Discord => Controls.DiscordCheck;

    /// <summary>Gets the label of the primary button.</summary>
    public string NextLabel => Step switch
    {
        FirstRunStep.Welcome => "Get started",
        FirstRunStep.Ready => "Finish",
        _ => "Next",
    };

    /// <summary>Gets the label of the skip button, or an empty string when the step cannot be skipped.</summary>
    public string SkipLabel => Step switch
    {
        FirstRunStep.Welcome => "Skip setup",
        FirstRunStep.ConnectDiscord when Discord != DiscordCheck.Connected => "Skip for now",
        _ => string.Empty,
    };

    /// <summary>Gets a value indicating whether the skip button is shown: only where skipping is safe (design §13).</summary>
    public bool CanSkip => SkipLabel.Length > 0;

    /// <summary>Gets a value indicating whether the primary button works. Only the last step can hold it back: turning screen reading on needs the tick.</summary>
    public bool CanFinish => Step != FirstRunStep.Ready || !TurnOnScreenReading || Understood;

    /// <summary>Gets why Finish is disabled, or an empty string when it is not.</summary>
    public string FinishCaption => CanFinish ? string.Empty : "Tick “I understand” to turn on screen reading, or choose Not now.";

    /// <summary>Gets the signed-in line on step 3.</summary>
    public string SignedInLine => Account.IsSignedIn && !Account.ShowSignIn
        ? $"Signed in as {Account.DisplayName}. {Account.StatusText}"
        : Account.IsSignedIn ? Account.StatusText : Account.LockText;

    /// <summary>Gets a value indicating whether the signed-in line is good news (a live session); false when the session ended or nobody is signed in, so the view shows a warning icon instead of a tick.</summary>
    public bool SignedInIsGood => Account.IsSignedIn && !Account.ShowSignIn;

    /// <summary>Re-opens the flow from the start (Settings, About, "Run setup again").</summary>
    public void Open()
    {
        _history.Clear();
        Step = FirstRunStep.Welcome;
        TurnOnScreenReading = false;
        Understood = false;
        ShowFullNotice = false;
        IsOpen = true;
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Controls.PropertyChanged -= OnControlsChanged;
        Account.PropertyChanged -= OnAccountChanged;
    }

    /// <summary>Goes to the next step, or finishes.</summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Next()
    {
        if (Step == FirstRunStep.Ready)
        {
            Finish();
            return;
        }

        GoTo(Step + 1);
    }

    /// <summary>Goes back one step.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        Step = _history.Pop();
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Skips: the whole setup on step 1 (every default works), or just the Discord check on step 2.</summary>
    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        if (Step == FirstRunStep.Welcome)
        {
            Finish();
        }
        else
        {
            GoTo(Step + 1);
        }
    }

    /// <summary>Shows or hides the whole notice.</summary>
    [RelayCommand]
    private void ToggleNotice() => ShowFullNotice = !ShowFullNotice;

    /// <summary>Closes the flow and remembers that setup ran; every choice stays editable in the app.</summary>
    [RelayCommand]
    private void Finish()
    {
        if (TurnOnScreenReading && Understood)
        {
            ScreenReading.TurnOnConfirmed();
        }

        _settings.Update(s => s with { FirstRunCompleted = true });
        IsOpen = false;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void GoTo(FirstRunStep next)
    {
        _history.Push(Step);
        Step = next;
        OnPropertyChanged(nameof(CanGoBack));
    }

    private void OnControlsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PresenceControlsViewModel.DiscordCheck))
        {
            OnPropertyChanged(nameof(Discord));
            OnPropertyChanged(nameof(SkipLabel));
            OnPropertyChanged(nameof(CanSkip));
            SkipCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.Snapshot))
        {
            OnPropertyChanged(nameof(SignedInLine));
            OnPropertyChanged(nameof(SignedInIsGood));
        }
    }
}
