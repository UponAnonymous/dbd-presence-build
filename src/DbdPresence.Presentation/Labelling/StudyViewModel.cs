using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdPresence.Configuration;
using DbdPresence.Core.Game;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Study;

namespace DbdPresence.Presentation.Labelling;

/// <summary>
/// The owner-only Study page (PLAN_AUTO_LABELLING section 4, FR-LAB-03 and FR-LAB-04): the recordings with their state, the automatic run that starts when the
/// game has closed, and the blind spot-check. The page exists only while the signed-in account is the owner's own game account
/// (<see cref="IOwnerAccountState"/>); otherwise <see cref="IsAvailable"/> is false and the shell shows no navigation item. Nothing is uploaded, automatically or by
/// hand, until the owner has confirmed once that the review pictures (which can show other players' names) go through the owner's service to the labelling account
/// (<see cref="StudyPrivacy"/>); the confirmation and the switch are read again just before a run starts and a run that is going is cancelled when either is turned off.
/// A run never starts while the game is launching or running (it would compete with capture for the processor) or while the study recorder is running, a run that is
/// going is cancelled when the game starts, and only one run is active at a time.
/// </summary>
public sealed partial class StudyViewModel : ObservableObject, IDisposable
{
    /// <summary>How long after the game closes before the run starts, so the recorder has finished writing its files.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait between two looks at whether the study recorder has stopped.</summary>
    public static readonly TimeSpan RecorderPoll = TimeSpan.FromSeconds(30);

    /// <summary>How many times the recorder is looked for before the automatic run gives up for this game session.</summary>
    public const int RecorderPolls = 20;

    /// <summary>The words shown when the labelling service is not set up (HTTP 503).</summary>
    public const string NotSetUpText = "Automatic labelling is not set up on your service yet. See step 8a of the Cloudflare guide.";

    /// <summary>The words shown when the day's allowance is used up.</summary>
    public const string DailyCapText = "Today's automatic labelling limit is used up; it continues tomorrow.";

    /// <summary>The words shown when the labelling service stayed busy.</summary>
    public const string BusyText = "The labelling service is busy; try again later.";

    /// <summary>The words shown when a run stopped at its own limit of requests.</summary>
    public const string RunLimitText = "This run stopped at its limit of requests (a guard against runaway cost). “Label new recordings now” continues where it stopped.";

    /// <summary>The words shown when “Label new recordings now” is used before the one-time confirmation.</summary>
    public const string NotConfirmedText = "Turn on automatic labelling above first: nothing is sent until you have confirmed where the pictures go.";

    /// <summary>The words shown when “Label new recordings now” is used while the game is launching or running.</summary>
    public const string GameRunningText = "The game is running, so labelling waits until it has closed.";

    /// <summary>The words shown when “Label new recordings now” is used while the study recorder is running.</summary>
    public const string RecorderRunningText = "The study recorder is still running. Stop it first, then label the recordings.";

    /// <summary>The words shown when some recordings could not be labelled for another reason.</summary>
    public const string OtherProblemText = "Some recordings could not be labelled. The list says why.";

    private readonly IStudyWorkbench _workbench;
    private readonly IOwnerAccountState _owner;
    private readonly ISettingsService _settings;
    private readonly IGameLifecycleSource _game;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Func<bool> _recorderRunning;
    private readonly IStudyRecorder? _recorder;
    private readonly Func<bool> _consoleRecorderRunning;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _waitCts;
    private CancellationTokenSource? _runCts;
    private bool _runIsAutomatic;
    private bool _gameActive;
    private bool _sawRunning;
    private bool _busy;
    private int _readyCount;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="StudyViewModel"/> class.</summary>
    /// <param name="workbench">The labelling pipeline.</param>
    /// <param name="owner">Whether this is the owner's own game account.</param>
    /// <param name="settings">The settings (the automatic run switch).</param>
    /// <param name="game">The game lifecycle.</param>
    /// <param name="dispatcher">Runs work on the UI thread.</param>
    /// <param name="time">The clock; the delays come from it so tests can advance time.</param>
    /// <param name="recorderRunning">Tells whether the separate study recorder tool (console) is running.</param>
    /// <param name="recorder">The in-app study recorder behind the owner-only Start recording button (FR-LAB-06), or null when this build has none.</param>
    public StudyViewModel(IStudyWorkbench workbench, IOwnerAccountState owner, ISettingsService settings, IGameLifecycleSource game, IUiDispatcher dispatcher, TimeProvider time, Func<bool> recorderRunning, IStudyRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(recorderRunning);
        _workbench = workbench;
        _owner = owner;
        _settings = settings;
        _game = game;
        _dispatcher = dispatcher;
        _time = time;
        _recorder = recorder;

        // The console tool and the in-app recorder both keep labelling away from a recording that is still being written.
        _consoleRecorderRunning = recorderRunning;
        _recorderRunning = () => recorderRunning() || recorder?.Status.IsActive == true;
        _gameActive = game.Current.State is GameState.Running or GameState.Launching;
        _sawRunning = game.Current.State == GameState.Running;
        IsAvailable = owner.IsOwner == true;
        game.LifecycleChanged += OnLifecycleChanged;
        owner.Changed += OnOwnerChanged;
        settings.Changed += OnSettingsChanged;
        if (recorder is not null)
        {
            recorder.StatusChanged += OnRecorderChanged;
        }

        if (IsAvailable)
        {
            LastWork = RefreshAsync();
        }
    }

    /// <summary>Raised on the UI thread when an automatic run has made a spot-check ready (the app shows a notice).</summary>
    public event EventHandler? SpotCheckReady;

    /// <summary>Raised on the UI thread when <see cref="IsAvailable"/> changes (the shell adds or removes the navigation item).</summary>
    public event EventHandler? AvailabilityChanged;

    /// <summary>Gets a value indicating whether the page exists: the signed-in account is the owner's own game account.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the task of the latest background work (completed when none runs); tests await it.</summary>
    public Task LastWork { get; private set; } = Task.CompletedTask;

    /// <summary>Gets how long closing the app waits for a recording to finish its files (a stuck disk must not hang the exit).</summary>
    public TimeSpan RecorderStopWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the page title.</summary>
    public string Title { get; } = "Study";

    /// <summary>Gets the line under the title.</summary>
    public string Intro { get; } = "Your recordings of the game, labelled automatically after you close it once you have turned labelling on. Each labelled recording needs a spot-check of about 2 minutes from you before it counts. Labelling sends the recording's review pictures, which can show other players' names, through your own service to your labelling account; nothing else leaves this PC.";

    /// <summary>Gets the heading of the one-time confirmation.</summary>
    public string ConfirmTitle { get; } = StudyPrivacy.ConfirmTitle;

    /// <summary>Gets the paragraphs of the one-time confirmation.</summary>
    public IReadOnlyList<string> ConfirmParagraphs { get; } = StudyPrivacy.ConfirmParagraphs;

    /// <summary>Gets the text of the button that confirms.</summary>
    public string ConfirmButtonText { get; } = StudyPrivacy.ConfirmButton;

    /// <summary>Gets the one-line privacy words shown while labelling is on.</summary>
    public string PrivacyNotice { get; } = StudyPrivacy.Notice;

    /// <summary>Gets a value indicating whether the owner has confirmed where the pictures go (stored in the settings).</summary>
    public bool LabellingConfirmed => _settings.Current.Study.LabellingConfirmed;

    /// <summary>Gets a value indicating whether the confirmation panel is shown: the page exists and nothing was confirmed yet.</summary>
    public bool IsConfirmationNeeded => IsAvailable && !LabellingConfirmed;

    /// <summary>Gets a value indicating whether the labelling controls are shown: the page exists and labelling was confirmed.</summary>
    public bool IsLabellingOn => IsAvailable && LabellingConfirmed;

    /// <summary>Gets the recordings, oldest first.</summary>
    public ObservableCollection<RecordingRowViewModel> Recordings { get; } = [];

    /// <summary>Gets a value indicating whether there are no recordings to show.</summary>
    public bool IsEmpty => Recordings.Count == 0 && !IsLoading;

    /// <summary>Gets a value indicating whether the list is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Gets a value indicating whether an automatic run is working.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LabelNowCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Gets what the run is doing now, or null.</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>Gets the plain sentence for a problem (not set up, limit used up, busy, another failure), or null.</summary>
    [ObservableProperty]
    public partial string? ProblemText { get; private set; }

    /// <summary>Gets the result of the last run or spot-check, or null.</summary>
    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    /// <summary>Gets the spot-check that is open, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpotCheckOpen))]
    public partial SpotCheckViewModel? SpotCheck { get; private set; }

    /// <summary>Gets a value indicating whether a spot-check is open.</summary>
    public bool IsSpotCheckOpen => SpotCheck is not null;

    /// <summary>Gets or sets a value indicating whether new recordings are labelled automatically after the game closes.</summary>
    public bool LabelAfterPlay
    {
        get => _settings.Current.Study.LabelAfterPlay;
        set
        {
            if (value != LabelAfterPlay)
            {
                _ = _settings.Update(s => s with { Study = s.Study with { LabelAfterPlay = value } });
            }
        }
    }

    /// <summary>Gets a value indicating whether a run may start now: the page exists, labelling was confirmed, none is running and the game is closed.</summary>
    public bool CanLabelNow => IsAvailable && LabellingConfirmed && !IsRunning && !GameActive;

    private bool GameActive
    {
        get
        {
            lock (_gate)
            {
                return _gameActive;
            }
        }
    }

    /// <summary>Records the owner's one-time confirmation that the review pictures go through the owner's service to the labelling account.</summary>
    [RelayCommand]
    public void ConfirmLabelling()
    {
        if (!IsAvailable || LabellingConfirmed)
        {
            return;
        }

        _ = _settings.Update(s => s with { Study = s.Study with { LabellingConfirmed = true } });
    }

    /// <summary>Withdraws the confirmation: a run that is going is cancelled and nothing more is sent until it is confirmed again.</summary>
    [RelayCommand]
    public void WithdrawLabelling()
    {
        if (LabellingConfirmed)
        {
            _ = _settings.Update(s => s with { Study = s.Study with { LabellingConfirmed = false } });
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _game.LifecycleChanged -= OnLifecycleChanged;
        _owner.Changed -= OnOwnerChanged;
        _settings.Changed -= OnSettingsChanged;
        try
        {
            if (_recorder is not null)
            {
                _recorder.StatusChanged -= OnRecorderChanged;

                // The app is closing: end a recording that is going so its files are finished, but never wait more than a few seconds (a stuck disk must not hang the exit).
                // StopAsync never throws. It runs off the UI thread so the wait cannot deadlock it.
                // A failure inside it is not rethrown here: it is the task's own, reported by the app's unobserved-task logging.
                Task.WhenAny(Task.Run(() => _recorder.StopAsync()), Task.Delay(RecorderStopWait)).GetAwaiter().GetResult();
            }
        }
        finally
        {
            _stop.Cancel();
            _stop.Dispose();
            lock (_gate)
            {
                _waitCts?.Dispose();
                _waitCts = null;
            }
        }
    }

    /// <summary>Reads the list of recordings again.</summary>
    /// <returns>A task that ends when the list is shown.</returns>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsAvailable || _disposed)
        {
            return;
        }

        Post(() => IsLoading = true);
        try
        {
            var list = await _workbench.ListAsync(_stop.Token).ConfigureAwait(false);
            Post(() => ShowList(list));
        }
        catch (OperationCanceledException)
        {
            Post(() => IsLoading = false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Post(() =>
            {
                IsLoading = false;
                ProblemText = "The study folder could not be read.";
            });
        }
    }

    /// <summary>Labels the recordings that have no labels yet, now. Does nothing while the game or another run is going.</summary>
    /// <returns>A task that ends when the run has ended.</returns>
    [RelayCommand(CanExecute = nameof(CanLabelNow))]
    public Task LabelNowAsync()
    {
        if (!IsAvailable || IsRunning)
        {
            return Task.CompletedTask;
        }

        // A plain sentence for every refusal; nothing is uploaded in any of these cases.
        if (!LabellingConfirmed)
        {
            Post(() => ProblemText = NotConfirmedText);
            return Task.CompletedTask;
        }

        if (GameActive)
        {
            Post(() => ProblemText = GameRunningText);
            return Task.CompletedTask;
        }

        if (_recorderRunning())
        {
            Post(() => ProblemText = RecorderRunningText);
            return Task.CompletedTask;
        }

        return RunAsync(manual: true);
    }

    /// <summary>Opens the spot-check of a recording.</summary>
    /// <param name="row">The recording.</param>
    /// <returns>A task that ends when the rows are shown.</returns>
    [RelayCommand]
    public async Task OpenSpotCheckAsync(RecordingRowViewModel? row)
    {
        if (row is null || !IsAvailable || !row.CanSpotCheck)
        {
            return;
        }

        Post(() => ResultText = null);
        try
        {
            var sheet = await _workbench.OpenSpotCheckAsync(row.Id, _stop.Token).ConfigureAwait(false);
            Post(() =>
            {
                if (sheet is null)
                {
                    ProblemText = "This recording has no spot-check due.";
                    return;
                }

                ProblemText = null;
                SpotCheck = new SpotCheckViewModel(sheet);
            });
        }
        catch (OperationCanceledException)
        {
            Post(() => ProgressText = "Stopped.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            Post(() => ProblemText = "The spot-check could not be prepared: its pictures could not be read.");
        }
    }

    /// <summary>Grades the open spot-check with the answers on the page and writes the result.</summary>
    /// <returns>A task that ends when the answers are saved or refused.</returns>
    [RelayCommand]
    public async Task SaveSpotCheckAsync()
    {
        var open = SpotCheck;
        if (open is null || !IsAvailable)
        {
            return;
        }

        var answers = open.CollectAnswers();
        if (answers is null)
        {
            return;
        }

        try
        {
            var saved = await _workbench.SaveSpotCheckAsync(open.RecordingId, answers, _stop.Token).ConfigureAwait(false);
            Post(() =>
            {
                if (saved.Verdict is not { } verdict)
                {
                    open.Message = saved.Problem ?? "The answers could not be saved. Nothing was changed.";
                    return;
                }

                SpotCheck = null;
                ProblemText = null;
                ResultText = Describe(open.RecordingId, verdict);
            });
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Post(() => ProgressText = "Stopped. The answers were not saved and the spot-check stays due.");
        }
    }

    /// <summary>Closes the open spot-check without saving; it stays due.</summary>
    [RelayCommand]
    public void CloseSpotCheck() => SpotCheck = null;

    private static string Describe(string id, SpotCheckVerdict verdict)
    {
        if (verdict.Passed)
        {
            return $"Spot-check of {id} done: your answers agree with the automatic labels, so the recording now counts.";
        }

        var kappa = verdict.Kappa is { } k ? string.Create(CultureInfo.InvariantCulture, $" Agreement on the screens: {k:0.00} (0.90 is needed).") : string.Empty;
        var facts = verdict.FactDisagreements.Count > 0 ? " Different: " + string.Join(", ", verdict.FactDisagreements.Distinct(StringComparer.Ordinal)) + "." : string.Empty;
        return $"Spot-check of {id}: disputed — rework it in the study tool.{kappa}{facts}";
    }

    private void Post(Action action) => _dispatcher.Post(action);

    private void OnOwnerChanged(object? sender, EventArgs e) => Post(() =>
    {
        var available = _owner.IsOwner == true;
        if (available == IsAvailable)
        {
            return;
        }

        IsAvailable = available;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsConfirmationNeeded));
        OnPropertyChanged(nameof(IsLabellingOn));
        LabelNowCommand.NotifyCanExecuteChanged();
        NotifyRecordingChanged();
        if (!available)
        {
            // Recording is owner-only: when the account stops being the owner's, a recording that is going ends.
            if (_recorder is not null)
            {
                LastWork = _recorder.StopAsync();
            }

            CancelRuns(includeManual: true);
            Recordings.Clear();
            SpotCheck = null;
            ProblemText = null;
            ResultText = null;
            ProgressText = null;
        }

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        if (available)
        {
            LastWork = RefreshAsync();
        }
    });

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Study.RecordingConfirmed != e.Current.Study.RecordingConfirmed)
        {
            Post(() =>
            {
                OnPropertyChanged(nameof(RecordingConfirmed));
                NotifyRecordingChanged();
            });
        }

        var switchChanged = e.Previous.Study.LabelAfterPlay != e.Current.Study.LabelAfterPlay;
        var confirmChanged = e.Previous.Study.LabellingConfirmed != e.Current.Study.LabellingConfirmed;
        if (!switchChanged && !confirmChanged)
        {
            return;
        }

        // "Off" wins over everything that is waiting or running: the pictures are not sent once the owner has turned it off.
        if (!e.Current.Study.LabelAfterPlay || !e.Current.Study.LabellingConfirmed)
        {
            CancelRuns(includeManual: !e.Current.Study.LabellingConfirmed);
        }

        Post(() =>
        {
            if (switchChanged)
            {
                OnPropertyChanged(nameof(LabelAfterPlay));
            }

            if (confirmChanged)
            {
                OnPropertyChanged(nameof(LabellingConfirmed));
                OnPropertyChanged(nameof(IsConfirmationNeeded));
                OnPropertyChanged(nameof(IsLabellingOn));
                LabelNowCommand.NotifyCanExecuteChanged();
            }
        });
    }

    /// <summary>Cancels the wait after a game close and the run that is going (the automatic one; the owner's own button press too when <paramref name="includeManual"/>).</summary>
    private void CancelRuns(bool includeManual)
    {
        lock (_gate)
        {
            CancelQuietly(_waitCts);
            if (includeManual || _runIsAutomatic)
            {
                CancelQuietly(_runCts);
            }
        }
    }

    /// <summary>Cancels a source. Every source held in a field is cancelled only while <c>_gate</c> is held and is disposed only under the same lock, so it is never disposed here.</summary>
    private static void CancelQuietly(CancellationTokenSource? source) => source?.Cancel();

    private void OnLifecycleChanged(object? sender, GameLifecycle lifecycle)
    {
        CancellationToken waitToken = default;
        var closed = false;
        lock (_gate)
        {
            var active = lifecycle.State is GameState.Running or GameState.Launching;
            if (active)
            {
                // The game (or its launcher) is up: a pending wait and a run that is going give way at once.
                CancelQuietly(_waitCts);
                CancelQuietly(_runCts);
                _sawRunning |= lifecycle.State == GameState.Running;
            }
            else if (_sawRunning)
            {
                closed = true;
                _sawRunning = false;
            }

            _gameActive = active;
            if (closed && IsAvailable && AutomaticAllowed)
            {
                // Close, start, close: the wait of the earlier close was cancelled above, and this one counts its full delay from the last close.
                CancelQuietly(_waitCts);
                _waitCts?.Dispose();
                _waitCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                waitToken = _waitCts.Token;
            }
            else
            {
                closed = false;
            }
        }

        Post(() => LabelNowCommand.NotifyCanExecuteChanged());
        if (closed)
        {
            LastWork = AfterGameClosedAsync(waitToken);
        }
    }

    /// <summary>Gets a value indicating whether the automatic run may happen: labelling was confirmed and the switch is on. Read again just before a run starts.</summary>
    private bool AutomaticAllowed => LabelAfterPlay && LabellingConfirmed;

    /// <summary>Waits a moment, waits for the recorder to stop, and runs. The wait is cancelled when the app closes, the game starts again, or the owner turns labelling off.</summary>
    private async Task AfterGameClosedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SettleDelay, _time, token).ConfigureAwait(false);
            for (var look = 0; _recorderRunning(); look++)
            {
                if (look >= RecorderPolls)
                {
                    Post(() => ProgressText = "The study recorder is still running, so automatic labelling waits. Use “Label new recordings now” when it has stopped.");
                    return;
                }

                await Task.Delay(RecorderPoll, _time, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            if (!AutomaticAllowed)
            {
                Post(() => ProgressText = null);
                return;
            }

            await RunAsync(manual: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Post(() => ProgressText = "Stopped.");
        }
    }

    private async Task RunAsync(bool manual)
    {
        CancellationToken runToken;
        lock (_gate)
        {
            if (_busy || _gameActive || !IsAvailable || !LabellingConfirmed || (!manual && !AutomaticAllowed))
            {
                return;
            }

            _busy = true;
            _runIsAutomatic = !manual;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            runToken = _runCts.Token;
        }

        var before = Volatile.Read(ref _readyCount);
        Post(() =>
        {
            IsRunning = true;
            ProblemText = null;
            ResultText = null;
            ProgressText = manual ? "Starting…" : "The game has closed. Labelling new recordings…";
        });
        try
        {
            var report = await _workbench.LabelNewAsync(manual, new PostedProgress(this), runToken).ConfigureAwait(false);
            if (report.Problem == StudyProblem.NotAllowed)
            {
                // The service no longer treats this account as the owner's: the page goes away.
                _owner.Note(false);
            }

            var list = await _workbench.ListAsync(_stop.Token).ConfigureAwait(false);
            Post(() =>
            {
                if (!IsAvailable)
                {
                    return;
                }

                ShowList(list);
                ProblemText = ProblemOf(report.Problem);
                ResultText = ResultOf(report);
                ProgressText = null;
                if (Volatile.Read(ref _readyCount) > before && report.Labelled > 0)
                {
                    SpotCheckReady?.Invoke(this, EventArgs.Empty);
                }
            });
        }
        catch (OperationCanceledException)
        {
            Post(() => ProgressText = "Stopped.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Post(() =>
            {
                ProblemText = "Automatic labelling stopped: the study folder could not be read or written.";
                ProgressText = null;
            });
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
                _runCts?.Dispose();
                _runCts = null;
            }

            Post(() => IsRunning = false);
        }
    }

    private static string? ProblemOf(StudyProblem problem) => problem switch
    {
        StudyProblem.NotSetUp => NotSetUpText,
        StudyProblem.DailyCap => DailyCapText,
        StudyProblem.Busy => BusyText,
        StudyProblem.RunLimit => RunLimitText,
        StudyProblem.Other => OtherProblemText,
        _ => null,
    };

    private static string? ResultOf(StudyRunReport report)
    {
        var sent = report.Requests > 0
            ? string.Create(CultureInfo.InvariantCulture, $" Last run: {report.Requests} {(report.Requests == 1 ? "request" : "requests")}, {report.InputTokens:N0} tokens read, {report.OutputTokens:N0} written.")
            : string.Empty;
        if (report.Labelled > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Labelled {report.Labelled} {(report.Labelled == 1 ? "recording" : "recordings")}. Spot-check ready (about 2 minutes).") + sent;
        }

        return report.Problem == StudyProblem.None && report.Failed == 0 ? "Nothing new to label." + sent : sent.Length > 0 ? sent.TrimStart() : null;
    }

    private void ShowList(IReadOnlyList<RecordingStatus> list)
    {
        Recordings.Clear();
        foreach (var status in list)
        {
            Recordings.Add(new RecordingRowViewModel(status));
        }

        Volatile.Write(ref _readyCount, list.Count(r => r.Stage == RecordingStage.SpotCheckReady));
        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
        LabelNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanLabelNow));

    /// <summary>Reports progress on the UI thread without depending on a synchronisation context.</summary>
    private sealed class PostedProgress(StudyViewModel owner) : IProgress<string>
    {
        public void Report(string value) => owner.Post(() => owner.ProgressText = value);
    }
}
