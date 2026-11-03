using DbdPresence.Configuration;
using DbdPresence.Core.Capture;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Core.Roster;
using DbdPresence.Vision.Screens;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Detection;

/// <summary>Everything the live loop is built from. The Windows app passes the Windows capture and text-recognition adapters; the tests pass fakes.</summary>
/// <param name="Settings">The user's settings (detection on or off, scan intervals, low-impact mode).</param>
/// <param name="Remote">The signed remote configuration; it can only switch detection and capture off (<see cref="RemoteFeatureGate"/>).</param>
/// <param name="Game">The confirmed game lifecycle.</param>
/// <param name="CreateCapture">Makes the capture source for a game process id (<c>WgcWindowCaptureSource</c> in the app).</param>
/// <param name="Recognizer">The text recogniser (Windows English OCR in the app).</param>
/// <param name="Profile">The HUD profile in use.</param>
/// <param name="Vocabulary">The closed lists.</param>
/// <param name="Time">The clock (the engine, the change gate and the cadence all use it).</param>
/// <param name="Maps">Resolves map ids to names for publication; null means maps are never published.</param>
/// <param name="RequestedDetectorSettings">
/// The tiers the configuration asks for. Null (the app today) asks for nothing, so every detector stays Disabled. Whatever is asked for passes through
/// <see cref="HudDetectionPipeline.GateSettings"/> first: a tier above Disabled is refused unless the profile is Measured and is the validated one.
/// </param>
/// <param name="Engine">Engine thresholds; null uses the documented initial guesses.</param>
/// <param name="GameVersionOf">
/// Reads the game's executable file version for a process (SAFETY_AND_PRIVACY §2.1 allows the on-disk file version). The reader is not built yet, so the
/// app passes null and the context carries no game version: a tier validated on a game version then counts as Degraded, which fails closed.
/// </param>
/// <param name="PromptSink">Receives the power prompts of a match once the scoreboard names the killer; the app keeps them on this PC. Null drops them.</param>
/// <param name="StatusTemplates">The own-row state icons the owner's captures taught this PC (FR-AUTO-03). Null means none: the own-status detector then reads nothing.</param>
/// <param name="UnknownCharacters">
/// Receives each character name read in a character-name position that the vocabulary does not contain, once per match (D35, ADR-0017). The app passes the online
/// reporter, which sends only for the owner's own account. Null reads none. Detection never changes because of a report: a learned character reaches the vocabulary
/// only through the signed roster and <see cref="DetectionLoop.UpdateVocabulary"/>.
/// </param>
/// <param name="OtherPersonName">Gives the signed-in Discord display name (a person's name that must never be reported as a character), or null. Read for each frame.</param>
public sealed record DetectionLoopParts(
    ISettingsService Settings,
    IRemoteConfigSource Remote,
    IGameLifecycleSource Game,
    Func<int, IWindowCaptureSource> CreateCapture,
    ITextRecognizer Recognizer,
    HudProfile Profile,
    HudVocabulary Vocabulary,
    TimeProvider Time,
    IMapNameResolver? Maps = null,
    IReadOnlyDictionary<DetectorId, DetectorSettings>? RequestedDetectorSettings = null,
    EngineOptions? Engine = null,
    Func<GameProcessInfo, string?>? GameVersionOf = null,
    IPromptObservationSink? PromptSink = null,
    OwnStatusTemplates? StatusTemplates = null,
    IUnknownCharacterSink? UnknownCharacters = null,
    Func<string?>? OtherPersonName = null);

/// <summary>
/// The live loop (FR-DET-25): capture source, then the change-gated reader and the six detectors (<see cref="HudDetectionPipeline"/>), then
/// observations into <see cref="MatchStateEngine"/>, which alone confirms. Out the other end the engine's snapshot goes to the presence coordinator
/// through <see cref="PublicationPolicy"/> (<see cref="IDetectionFeed"/>) and to the app's own status line (<see cref="IDetectionStatusSource"/>).
/// <list type="bullet">
/// <item>It does nothing unless the user turned detection on <b>and</b> <see cref="RemoteFeatureGate"/> allows capture: with either off, no capture source exists.</item>
/// <item>One cycle at a time: a cycle that finds the previous one still running is skipped and counted (review U5 F9), because the reader and the pipeline are not thread-safe.</item>
/// <item>The cadence and the busy-time budget are profile values (<see cref="HudSampling"/>, <see cref="DetectionCadence"/>).</item>
/// <item>Tiers above Disabled need a Measured profile that is the validated one (<see cref="HudDetectionPipeline.GateSettings"/>); the engine is built from the gated settings, so nothing new reaches Discord from the initial-guess profile.</item>
/// <item>It never saves a picture and never logs screen text; a failure inside a cycle is counted and reported through <see cref="CycleFailed"/>, never swallowed.</item>
/// </list>
/// </summary>
public sealed class DetectionLoop : IDetectionFeed, IDetectionStatusSource, IDisposable
{
    private readonly DetectionLoopParts _parts;
    private readonly RecognizerWatch _watch;
    private readonly HudDetectionPipeline _pipeline;
    private readonly MatchStateEngine _engine;
    private readonly IReadOnlyDictionary<DetectorId, DetectorSettings> _effective;
    private readonly IReadOnlyList<string> _refused;
    private readonly IReadOnlyList<NormalizedRegion> _regions;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Lock _timerGate = new();
    private ITimer? _timer;
    private IWindowCaptureSource? _capture;
    private int? _capturePid;
    private string? _gameVersion;
    private readonly Lock _listsGate = new();
    private HudVocabulary _lists;
    private HudVocabulary? _pendingLists;
    private int _busy;
    private long _cycles;
    private long _skipped;
    private long _failures;
    private bool _active;
    private bool _disposed;
    private DetectionStatus _status = DetectionStatus.Off;

    /// <summary>Initializes a new instance of the <see cref="DetectionLoop"/> class. Nothing runs until <see cref="Start"/>.</summary>
    public DetectionLoop(DetectionLoopParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _parts = parts;
        _watch = new RecognizerWatch(parts.Recognizer);
        _pipeline = new HudDetectionPipeline(parts.Profile, parts.Vocabulary, _watch, parts.Time, promptSink: parts.PromptSink, statusTemplates: parts.StatusTemplates, unknownCharacters: parts.UnknownCharacters, excludedName: parts.OtherPersonName);
        var gate = _pipeline.GateSettings(parts.RequestedDetectorSettings ?? new Dictionary<DetectorId, DetectorSettings>());
        _effective = gate.Effective;
        _refused = gate.Refused;
        _lists = parts.Vocabulary;
        // The engine asks the current lists at each call, so a roster update takes effect for observations and for what the user declares at once.
        var options = (parts.Engine ?? new EngineOptions()) with
        {
            IsKnownMapId = id => Lists.MapEntry(id) is not null,
            IsKnownCharacterId = id => Lists.IsCharacterId(id),
            IsKnownKillerId = id => Lists.IsKillerId(id),
            IsKnownEventModeId = id => Lists.IsEventModeId(id),
        };
        _engine = new MatchStateEngine(parts.Time, options, _effective);
        _regions = [.. _pipeline.Regions.Select(r => new NormalizedRegion(r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height))];
        _engine.SnapshotChanged += OnEngineChanged;
    }

    /// <summary>Raised (on any thread) after <see cref="Status"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Raised when a cycle failed unexpectedly (the app logs it). The loop goes on with its next cycle.</summary>
    public event EventHandler<Exception>? CycleFailed;

    private HudVocabulary Lists => Volatile.Read(ref _lists);

    /// <summary>
    /// Starts using a larger vocabulary (the embedded one plus the signed roster's new characters) without a restart. The engine's id checks change
    /// at once; the readers and detectors are rebuilt on the new lists at the start of the next cycle, so a frame is never read with half of each. The
    /// new vocabulary must hold every character the old one did (the roster is append-only); the power prompt map is kept.
    /// </summary>
    /// <param name="vocabulary">The new vocabulary.</param>
    /// <exception cref="ArgumentException">A killer of the prompt map is missing from the new vocabulary; nothing is changed.</exception>
    public void UpdateVocabulary(Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        lock (_listsGate)
        {
            var next = Lists.WithVocabulary(vocabulary);
            Volatile.Write(ref _lists, next);
            Volatile.Write(ref _pendingLists, next);
        }
    }

    /// <summary>Gets the engine. Exposed for the app's diagnostics and for tests; only the loop submits observations to it.</summary>
    public MatchStateEngine Engine => _engine;

    /// <summary>Gets how many cycles failed unexpectedly.</summary>
    public long FailureCount => Interlocked.Read(ref _failures);

    /// <inheritdoc />
    public DetectionStatus Status => Volatile.Read(ref _status);

    /// <inheritdoc />
    public MatchSnapshot? Snapshot => Volatile.Read(ref _active) ? _engine.Snapshot : null;

    /// <inheritdoc />
    public IReadOnlyDictionary<DetectorId, DetectorSettings> DetectorSettings => _effective;

    /// <inheritdoc />
    public PublicationOptions Options { get; } = new();

    /// <inheritdoc />
    public IMapNameResolver? Maps => _parts.Maps;

    /// <summary>Starts the timer. The first cycle runs after one idle interval. Idempotent.</summary>
    public void Start()
    {
        lock (_timerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
            {
                return;
            }

            _parts.Settings.Changed += OnSettingsChanged;
            _timer = _parts.Time.CreateTimer(_ => _ = RunScheduledAsync(), null, DetectionCadence.IdleInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Runs one cycle now unless one is already running (then the call returns at once and the skip is counted). The timer calls this; tests call it
    /// directly.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle.</param>
    /// <returns>How long to wait before the next cycle.</returns>
    public async Task<TimeSpan> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _skipped);
            return DetectionCadence.IdleInterval;
        }

        var started = _parts.Time.GetTimestamp();
        var scene = Scene.Unknown;
        var running = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
            (running, scene) = await CycleAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _cancel.IsCancellationRequested)
        {
            return DetectionCadence.IdleInterval; // stopping: the cycle was asked to end, so no next cycle is planned from it
        }
#pragma warning disable CA1031 // Loop boundary (ARCHITECTURE §6): any failure is counted, shown in the status and raised through CycleFailed; the next cycle starts clean.
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            StopReading();
            Publish(Status with { Run = DetectionRunState.Problem, Detail = DetectionText.Problem, Match = null });
            CycleFailed?.Invoke(this, ex);
        }
#pragma warning restore CA1031
        finally
        {
            Interlocked.Increment(ref _cycles);
            Interlocked.Exchange(ref _busy, 0);
        }

        var busy = _parts.Time.GetElapsedTime(started);
        return running ? DetectionCadence.NextDelay(_parts.Profile.Sampling, _parts.Settings.Current.Detection, scene, busy) : DetectionCadence.IdleInterval;
    }

    /// <summary>Stops the loop, releases the capture source and forgets everything read.</summary>
    public void Dispose()
    {
        lock (_timerGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Dispose();
            _parts.Settings.Changed -= OnSettingsChanged;
        }

        _cancel.Cancel();
        _engine.SnapshotChanged -= OnEngineChanged;
        StopReading();
        _cancel.Dispose();
    }

    private async Task<(bool Running, Scene Scene)> CycleAsync(CancellationToken cancellationToken)
    {
        // Between frames: the one place the pipeline's lists change (only one cycle runs at a time).
        HudVocabulary? update;
        lock (_listsGate)
        {
            update = _pendingLists;
            _pendingLists = null;
        }

        if (update is not null)
        {
            _pipeline.SetVocabulary(update);
        }

        var settings = _parts.Settings.Current.Detection;
        var allowed = RemoteFeatureGate.For(settings, _parts.Remote.Current);
        if (!(settings.Enabled && settings.RiskAcknowledged))
        {
            StopReading();
            Publish(Make(DetectionRunState.Off, DetectionText.Off, null));
            return (false, Scene.Unknown);
        }

        if (!allowed.CaptureAllowed)
        {
            StopReading();
            Publish(Make(DetectionRunState.SwitchedOffRemotely, DetectionText.SwitchedOffRemotely, null));
            return (false, Scene.Unknown);
        }

        var game = _parts.Game.Current;
        if (game.State != GameState.Running || game.Process is null)
        {
            StopReading();
            Publish(Make(DetectionRunState.WaitingForGame, DetectionText.WaitingForGame, null));
            return (false, Scene.Unknown);
        }

        var processId = game.Process.ProcessId;
        if (_capture is null || _capturePid != processId)
        {
            StopReading();
            _capture = _parts.CreateCapture(processId);
            _capturePid = processId;
            _gameVersion = _parts.GameVersionOf?.Invoke(game.Process);
            _ = await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _active, true);
        _engine.SetGameState(GameState.Running);
        var sample = await _capture.CaptureAsync(_regions, cancellationToken).ConfigureAwait(false);
        var scene = _engine.Snapshot.ConfirmedScene;
        var state = sample.State;
        if (!state.IsReady || sample.Regions.Count != _regions.Count || state.FrameWidth < 1 || state.FrameHeight < 1)
        {
            // Minimised or gone: the engine holds what it knows for its hold time and marks fields stale at once. Any other failure is simply an unread cycle.
            _engine.SetCapturePaused(state.Status is CaptureStatus.Minimized or CaptureStatus.WindowNotFound);
            _engine.Tick();
            Publish(Make(DetectionRunState.WindowUnavailable, $"{DetectionText.WindowUnavailable}: {state.Detail}", state));
            return (true, scene);
        }

        _engine.SetCapturePaused(false);
        var crops = new Dictionary<string, PixelBuffer>(StringComparer.Ordinal);
        for (var i = 0; i < _regions.Count; i++)
        {
            crops[_pipeline.Regions[i].Id] = sample.Regions[i];
        }

        var context = new ContextKey(_parts.Profile.ProfileId, $"{state.FrameWidth}x{state.FrameHeight}", _parts.Settings.Current.Detection.Locale, _gameVersion);
        var frame = await _pipeline.ProcessAsync(crops, state.FrameWidth, state.FrameHeight, context, scene, cancellationToken, _engine.Snapshot.DetectedRole).ConfigureAwait(false);
        foreach (var observation in frame.Observations)
        {
            _ = _engine.Submit(observation);
        }

        _engine.Tick();
        Publish(_watch.TakeFailure() is { } failure
            ? Make(DetectionRunState.TextRecognitionUnavailable, failure, state)
            : Make(DetectionRunState.Reading, DetectionText.Reading, state));
        return (true, _engine.Snapshot.ConfirmedScene);
    }

    /// <summary>Remembers when text recognition could not run, so the status says so instead of claiming the window is being read.</summary>
    private sealed class RecognizerWatch(ITextRecognizer inner) : ITextRecognizer
    {
        private string? _failure;

        public async Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
        {
            var result = await inner.RecognizeAsync(image, cancellationToken).ConfigureAwait(false);
            if (result.Status != TextRecognitionStatus.Recognized)
            {
                // The detail is the recogniser's own sentence about the system (a missing language pack); it never holds text read from the screen.
                _failure = result.Status == TextRecognitionStatus.EnglishLanguageMissing ? result.Detail : DetectionText.TextRecognitionBroken;
            }

            return result;
        }

        /// <summary>Returns the failure seen since the last call, then forgets it.</summary>
        public string? TakeFailure() => Interlocked.Exchange(ref _failure, null);
    }

    private DetectionStatus Make(DetectionRunState run, string detail, CaptureState? capture) => new(
        run,
        detail,
        capture,
        Volatile.Read(ref _active) ? _engine.Snapshot : null,
        Interlocked.Read(ref _cycles) + 1,
        Interlocked.Read(ref _skipped),
        _pipeline.OcrCalls,
        _refused);

    private void Publish(DetectionStatus status)
    {
        var previous = Interlocked.Exchange(ref _status, status);
        if (!SameVisible(previous, status))
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Counters change on every cycle; only a change of what the user sees raises the event.</summary>
    private static bool SameVisible(DetectionStatus a, DetectionStatus b) =>
        a.Run == b.Run && a.Detail == b.Detail && a.Match?.Sequence == b.Match?.Sequence && ReferenceEquals(a.RefusedDetectors, b.RefusedDetectors);

    private void StopReading()
    {
        var wasActive = Interlocked.Exchange(ref _active, false);
        _capture?.Dispose();
        _capture = null;
        _capturePid = null;
        _gameVersion = null;
        if (wasActive)
        {
            _engine.SetGameState(GameState.NotRunning);
            _pipeline.Reset();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEngineChanged(object? sender, MatchSnapshot snapshot)
    {
        if (Volatile.Read(ref _active))
        {
            // What the user declares (role, character, mode) changes the snapshot between cycles; the status must show the latest one.
            Volatile.Write(ref _status, Status with { Match = snapshot });
        }

        Changed?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Detection != e.Current.Detection)
        {
            lock (_timerGate)
            {
                _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task RunScheduledAsync()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        var delay = await RunCycleAsync(_cancel.Token).ConfigureAwait(false);
        lock (_timerGate)
        {
            if (!_disposed)
            {
                _timer?.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }
    }
}
