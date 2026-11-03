using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Online;
using DbdPresence.Presentation.Presence;

namespace DbdPresence.Presentation.ViewModels;

/// <summary>Where a button on Home leads. The shell turns it into navigation, so Home never needs to know the pages.</summary>
public enum HomeLink
{
    /// <summary>The Discord status page.</summary>
    Status,

    /// <summary>The Account page.</summary>
    Account,

    /// <summary>Settings, Screen reading.</summary>
    SettingsScreenReading,

    /// <summary>Settings, Advanced.</summary>
    SettingsAdvanced,

    /// <summary>Settings, Advanced, where the data is kept.</summary>
    SettingsDataFolder,

    /// <summary>Settings, Screen reading, with the confirmation open.</summary>
    TurnOnScreenReading,
}

/// <summary>One suggestion under the headline (design §3.5).</summary>
/// <param name="Id">The suggestion id, for tests and automation.</param>
/// <param name="Sentence">What it suggests.</param>
/// <param name="ActionLabel">The button.</param>
/// <param name="DismissLabel">The "Not now" button, or null when it cannot be dismissed.</param>
public sealed record NextStep(string Id, string Sentence, string ActionLabel, string? DismissLabel);

/// <summary>
/// Home (design §3): is it working, what do my friends see, what does the app know. A headline with two facts and at most one button, at most two next
/// steps, the live card with its plain reasons, and one plain line about screen reading. Nothing is declared by hand here (D30), and the per-detail
/// learning state is not shown to users (owner decision, 2026-10-24).
/// </summary>
public sealed partial class HomeViewModel : SettingsPageViewModel
{
    private const int MaxNextSteps = 2;
    private readonly PresenceCoordinator _coordinator;
    private readonly TimeProvider _time;
    private readonly ITimer _clock;
    private readonly AccountViewModel? _account;
    private readonly ScreenReadingViewModel? _screenReading;
    private bool _saveFailed;

    /// <summary>Initializes a new instance of the <see cref="HomeViewModel"/> class.</summary>
    public HomeViewModel(
        PresenceControlsViewModel controls, PresenceCoordinator coordinator, ISettingsService settings, IUiDispatcher dispatcher, TimeProvider time,
        DetectionViewModel? detection = null, ScreenReadingViewModel? screenReading = null, AccountViewModel? account = null)
        : base(settings, dispatcher)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(time);
        Controls = controls;
        Detection = detection;
        _screenReading = screenReading;
        _account = account;
        _coordinator = coordinator;
        _time = time;
        Live = LivePreview.From(coordinator, settings.Current, time.GetUtcNow(), () => Controls.IsPaused = false);
        Hero = BuildHero();
        _coordinator.SnapshotChanged += OnSnapshotChanged;
        Settings.SaveFailed += OnSaveFailed;
        if (account is not null)
        {
            account.PropertyChanged += OnAccountChanged;
        }

        if (screenReading is not null)
        {
            screenReading.PropertyChanged += OnScreenReadingChanged;
        }

        Reload();
        Refresh();

        // Discord's card counts seconds; refresh the elapsed text and the retry countdown once a second while the window is visible.
        _clock = time.CreateTimer(_ => Dispatcher.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Raised when a button on Home leads to another page.</summary>
    public event EventHandler<HomeLink>? LinkRequested;

    /// <summary>Gets or sets a value indicating whether the page is on screen; the 1 s clock idles otherwise.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets the shared controls (pause and the connection pill).</summary>
    public PresenceControlsViewModel Controls { get; }

    /// <summary>Gets the screen-reading status model, or null in a build without screen reading.</summary>
    public DetectionViewModel? Detection { get; }

    /// <summary>Gets the status headline.</summary>
    [ObservableProperty]
    public partial HeroStatus Hero { get; private set; }

    /// <summary>Gets the live card, reasons and captions (D29: exactly what Discord shows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(Reasons), nameof(ShownReasons), nameof(HasMoreReasons), nameof(HasReasons), nameof(EmptyText), nameof(IsEmpty), nameof(IsCardShown), nameof(DeliveryNote), nameof(Hints), nameof(ShowAllLabel))]
    public partial LivePreview Live { get; private set; }

    /// <summary>Gets the suggestions under the headline (at most two; none in the healthy live state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextSteps), nameof(NextStepsTitle))]
    public partial IReadOnlyList<NextStep> NextSteps { get; private set; } = [];

    /// <summary>Gets the one plain line about screen reading under "What the app sees".</summary>
    [ObservableProperty]
    public partial string SeenLine { get; private set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every reason is listed, not just the first three.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownReasons), nameof(ShowAllVisible))]
    public partial bool ShowAllReasons { get; set; }

    /// <summary>Gets the activity card.</summary>
    public ActivityPreview Preview => Live.Preview;

    /// <summary>Gets the reasons.</summary>
    public ReasonList Reasons => Live.Reasons;

    /// <summary>Gets the reasons to list now: three, or all of them after "Show all".</summary>
    public IReadOnlyList<string> ShownReasons => ShowAllReasons ? Live.Reasons.All : Live.Reasons.Shown;

    /// <summary>Gets a value indicating whether there are reasons to list.</summary>
    public bool HasReasons => Live.Reasons.All.Count > 0 && !Live.IsEmpty;

    /// <summary>Gets a value indicating whether more reasons exist than are listed.</summary>
    public bool HasMoreReasons => Live.Reasons.HasMore;

    /// <summary>Gets a value indicating whether the "Show all" button is shown.</summary>
    public bool ShowAllVisible => HasMoreReasons && !ShowAllReasons;

    /// <summary>Gets the label of the "Show all" button.</summary>
    public string ShowAllLabel => Live.Reasons.ShowAllLabel;

    /// <summary>Gets the sentence that replaces the card when nothing is on Discord.</summary>
    public string? EmptyText => Live.EmptyText;

    /// <summary>Gets a value indicating whether nothing is on Discord.</summary>
    public bool IsEmpty => Live.IsEmpty;

    /// <summary>Gets a value indicating whether the card is shown.</summary>
    public bool IsCardShown => !Live.IsEmpty;

    /// <summary>Gets the caption above the card while Discord is not connected.</summary>
    public string? DeliveryNote => Live.DeliveryNote;

    /// <summary>Gets the fix buttons of the preview (pictures that cannot be previewed). The paused hint is omitted: the headline already offers Resume.</summary>
    public IReadOnlyList<PreviewHint> Hints => [.. Live.Hints.Where(h => h.Id != "paused")];

    /// <summary>Gets the paused sentence above the card, or null when not paused.</summary>
    public string? PausedLine => Live.Hints.FirstOrDefault(h => h.Id == "paused")?.Message;

    /// <summary>Gets a value indicating whether there are next steps.</summary>
    public bool HasNextSteps => NextSteps.Count > 0;

    /// <summary>Gets the heading of the next steps card.</summary>
    public string NextStepsTitle => NextSteps.Count == 1 ? "Next step" : "Next steps";

    /// <summary>Runs the headline's button.</summary>
    [RelayCommand]
    private void RunHeroAction()
    {
        switch (Hero.Action?.Kind)
        {
            case HeroActionKind.OpenAccount:
                LinkRequested?.Invoke(this, HomeLink.Account);
                break;
            case HeroActionKind.ShowWhereSaved:
                LinkRequested?.Invoke(this, HomeLink.SettingsDataFolder);
                break;
            case HeroActionKind.Resume:
                Controls.IsPaused = false;
                break;
            case HeroActionKind.OpenAdvancedSettings:
                LinkRequested?.Invoke(this, HomeLink.SettingsAdvanced);
                break;
            default:
                break;
        }
    }

    /// <summary>Runs the button of a next step.</summary>
    /// <param name="id">The step id.</param>
    [RelayCommand]
    private void RunNextStep(string id)
    {
        if (id == NextStepIds.ScreenReading)
        {
            LinkRequested?.Invoke(this, HomeLink.TurnOnScreenReading);
        }
    }

    /// <summary>Dismisses a next step; the choice is remembered.</summary>
    /// <param name="id">The step id.</param>
    [RelayCommand]
    private void DismissNextStep(string id)
    {
        if (id == NextStepIds.ScreenReading)
        {
            Apply(s => s with { Lifecycle = s.Lifecycle with { ScreenReadingTipDismissed = true } });
        }
    }

    /// <summary>Opens Discord status ("Change how it looks").</summary>
    [RelayCommand]
    private void ChangeHowItLooks() => LinkRequested?.Invoke(this, HomeLink.Status);

    /// <summary>Opens Settings at Screen reading.</summary>
    [RelayCommand]
    private void OpenScreenReadingSettings() => LinkRequested?.Invoke(this, HomeLink.SettingsScreenReading);

    /// <summary>Lists every reason.</summary>
    [RelayCommand]
    private void ShowAllReasonsNow() => ShowAllReasons = true;

    /// <inheritdoc />
    protected override void Load(AppSettings settings)
    {
        _saveFailed = false;
        if (Live is not null)
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.SnapshotChanged -= OnSnapshotChanged;
            Settings.SaveFailed -= OnSaveFailed;
            if (_account is not null)
            {
                _account.PropertyChanged -= OnAccountChanged;
            }

            if (_screenReading is not null)
            {
                _screenReading.PropertyChanged -= OnScreenReadingChanged;
            }

            _clock.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => Dispatcher.Post(Refresh);

    private void OnSaveFailed(object? sender, SettingsSaveFailedEventArgs e) => Dispatcher.Post(() =>
    {
        _saveFailed = true;
        Refresh();
    });

    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.Snapshot))
        {
            Dispatcher.Post(Refresh);
        }
    }

    private void OnScreenReadingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenReadingViewModel.IsOn))
        {
            Dispatcher.Post(Refresh);
        }
    }

    private void Tick()
    {
        if (IsActive)
        {
            Refresh();
            Controls.RefreshStatus();
            Detection?.Refresh();
        }
    }

    private HeroStatus BuildHero()
    {
        var settings = Settings.Current;
        var snapshot = _coordinator.Snapshot;
        var inputs = snapshot.Inputs;
        var character = settings.ActiveProfile.Visibility.ShowCharacter && !string.IsNullOrWhiteSpace(inputs?.CharacterName) ? inputs!.CharacterName : null;
        return HeroStatusBuilder.Build(new HeroInputs(
            snapshot,
            _time.GetUtcNow(),
            _time.LocalTimeZone,
            _account?.State == OnlineState.Revoked,
            Settings.IsReadOnly || _saveFailed,
            settings.ActiveProfile.WhenPaused == Core.Settings.PauseBehavior.Generic,
            settings.ActiveProfile.WhenNotRunning == Core.Settings.NotRunningBehavior.ShowTemplate,
            character));
    }

    private void Refresh()
    {
        var settings = Settings.Current;
        var live = LivePreview.From(_coordinator, settings, _time.GetUtcNow(), () => Controls.IsPaused = false);
        if (!live.SameAs(Live))
        {
            Live = live;
        }

        var hero = BuildHero();
        if (hero != Hero)
        {
            Hero = hero;
        }

        SeenLine = BuildSeenLine();
        var steps = BuildNextSteps(settings, hero);
        if (!steps.SequenceEqual(NextSteps))
        {
            NextSteps = steps;
        }

        OnPropertyChanged(nameof(PausedLine));
    }

    private string BuildSeenLine()
    {
        var available = _screenReading?.IsAvailable ?? Detection is not null;
        if (!available)
        {
            return "Screen reading isn't part of this version yet. Discord shows only that you're playing and for how long.";
        }

        var on = _screenReading?.IsOn ?? Detection?.IsOn ?? false;
        return on
            ? Detection?.StatusLine ?? _screenReading?.StatusText ?? string.Empty
            : "Screen reading is off. Discord shows only that you're playing and for how long.";
    }

    private List<NextStep> BuildNextSteps(AppSettings settings, HeroStatus hero)
    {
        var steps = new List<NextStep>();
        var healthyLive = hero.Id == "H10";
        if (!healthyLive
            && _screenReading is { IsAvailable: true, IsOn: false }
            && !settings.Lifecycle.ScreenReadingTipDismissed)
        {
            steps.Add(new NextStep(
                NextStepIds.ScreenReading,
                "Show your role, character and map on Discord by letting the app read your game screen.",
                "Turn on screen reading…",
                "Not now"));
        }

        return [.. steps.Take(MaxNextSteps)];
    }

    /// <summary>The ids of the suggestions.</summary>
    public static class NextStepIds
    {
        /// <summary>Suggests turning screen reading on.</summary>
        public const string ScreenReading = "screen-reading";
    }
}
