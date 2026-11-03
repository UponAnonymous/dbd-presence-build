using System.Globalization;
using DbdPresence.Core.Game;
using GameRole = DbdPresence.Core.Game.Role;

namespace DbdPresence.Core.Detection;

/// <summary>
/// The single writer of match state (ADR-0005, ARCHITECTURE §4). Detectors submit <see cref="Observation"/>s; only this
/// engine confirms values. It applies K-of-N confirmation with a score floor and dwell, the scene transition guards,
/// time-to-live expiry, trial sessions that reset match-scoped fields, mode gating and detector health, and publishes
/// immutable <see cref="MatchSnapshot"/>s. All times come from the injected <see cref="TimeProvider"/>.
/// <para>
/// The engine never decides what reaches Discord: that is <see cref="PublicationPolicy"/>, which applies each detector's
/// tier. A detector that is Disabled is still confirmed here (for in-app diagnostics) but is never published.
/// </para>
/// <para>Calls are serialised by an internal lock; <see cref="SnapshotChanged"/> is raised outside it.</para>
/// </summary>
public sealed class MatchStateEngine
{
    private static readonly DetectorId[] s_supported = [DetectorId.Scene, DetectorId.Role, DetectorId.Map, DetectorId.Generators, DetectorId.Character, DetectorId.QueueMode, DetectorId.Killer, DetectorId.KillerPrompt, DetectorId.Status, DetectorId.SurvivorResult, DetectorId.KillerResult, DetectorId.HookCount];

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly EngineOptions _options;
    private readonly IReadOnlyDictionary<DetectorId, DetectorSettings> _settings;
    private readonly Dictionary<DetectorId, DetectorHealthTracker> _health = [];
    private readonly Dictionary<ObservationKind, DateTimeOffset> _lastAccepted = [];
    private readonly ObservationWindow _sceneWindow;
    private readonly FieldTrack _role;
    private readonly FieldTrack _map;
    private readonly FieldTrack _generators;
    private readonly FieldTrack _character;
    private readonly FieldTrack _queueMode;
    private readonly FieldTrack _killer;
    private readonly FieldTrack _killerPrompt;
    private readonly FieldTrack _ownStatus;
    private readonly FieldTrack _survivorResult;
    private readonly FieldTrack _killerResult;
    private readonly FieldTrack _hookCount;

    private GameState _game = GameState.NotRunning;
    private bool _paused;
    private DateTimeOffset _pauseStart;
    private Scene _scene = Scene.Unknown;
    private DateTimeOffset? _sceneConfirmedAt;
    private DateTimeOffset _sceneLastSupport;
    private (Scene Scene, DateTimeOffset LastSupport)? _beforeLoss;
    private MatchSession? _session;
    private LastSessionSummary? _summary;
    private DateTimeOffset? _gapSince;
    private long _sessionCounter;
    private long _sequence;
    private bool _dirty;
    private string _healthSignature = string.Empty;

    private GameMode _declaredMode = GameMode.Unknown;
    private GameRole _declaredRole = GameRole.None;
    private string? _declaredCharacter;

    private long _accepted;
    private long _rejected;
    private long _illegalTransitions;
    private long _monotonicViolations;
    private long _undetectedBoundaries;

    /// <summary>Initializes a new instance of the <see cref="MatchStateEngine"/> class.</summary>
    /// <param name="time">The clock; tests pass a <c>FakeTimeProvider</c>.</param>
    /// <param name="options">Thresholds and time constants; null uses the documented initial guesses.</param>
    /// <param name="settings">Per-detector settings; a detector without an entry is Disabled with initial-guess thresholds.</param>
    public MatchStateEngine(TimeProvider time, EngineOptions? options = null, IReadOnlyDictionary<DetectorId, DetectorSettings>? settings = null)
    {
        _time = time;
        _options = options ?? new EngineOptions();
        _options.Validate();
        _settings = settings ?? new Dictionary<DetectorId, DetectorSettings>();
        foreach (var (id, s) in _settings)
        {
            if (s.Problem() is { } problem)
            {
                throw new ArgumentException($"Detector {id}: {problem}.", nameof(settings));
            }
        }

        _sceneWindow = new ObservationWindow(_options.Scene.N * 2);
        _role = new FieldTrack(_options.Role.N * 2);
        _map = new FieldTrack(_options.Map.N * 2);
        _generators = new FieldTrack(_options.Generators.N * 2);
        _character = new FieldTrack(_options.Character.N * 2);
        _queueMode = new FieldTrack(_options.QueueMode.N * 2);
        _killer = new FieldTrack(_options.Killer.N * 2);
        _killerPrompt = new FieldTrack(_options.KillerPrompt.N * 2);
        _ownStatus = new FieldTrack(_options.OwnStatus.N * 2);
        _survivorResult = new FieldTrack(_options.ResultOutcome.N * 2);
        _killerResult = new FieldTrack(_options.ResultOutcome.N * 2);
        _hookCount = new FieldTrack(_options.HookCount.N * 2);
        foreach (var id in s_supported)
        {
            _health[id] = new DetectorHealthTracker();
        }
    }

    /// <summary>Raised, outside the engine lock, whenever visible state changes.</summary>
    public event EventHandler<MatchSnapshot>? SnapshotChanged;

    /// <summary>Gets the current snapshot.</summary>
    public MatchSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshot(_time.GetUtcNow());
            }
        }
    }

    /// <summary>Sets the process-level state. Anything but <see cref="GameState.Running"/> clears the session, all detected fields and the summary.</summary>
    public void SetGameState(GameState state) => Run(_ =>
    {
        if (state == _game)
        {
            return;
        }

        _game = state;
        if (state != GameState.Running)
        {
            ResetAll();
        }

        _dirty = true;
    });

    /// <summary>
    /// Pauses or resumes sampling (minimised, occluded or unfocused window). Pausing marks every detected field stale at
    /// once and holds the scene for <see cref="EngineOptions.SceneHold"/>; the session survives (ARCHITECTURE §4.2).
    /// </summary>
    public void SetCapturePaused(bool paused) => Run(now =>
    {
        if (_game != GameState.Running || paused == _paused)
        {
            return;
        }

        _paused = paused;
        if (paused)
        {
            _pauseStart = now;
            _sceneWindow.Clear();
            StaleFields("capture paused", includeRole: true);
            if (_session is not null)
            {
                _gapSince ??= now;
            }
        }
        else if (_scene != Scene.Unknown)
        {
            _sceneLastSupport = now;
            _gapSince = null;
        }

        _dirty = true;
    });

    /// <summary>Sets the user-declared game mode (FR-DET-12). Generators are enabled only for Standard and Custom.</summary>
    public void SetDeclaredMode(GameMode mode) => Run(_ =>
    {
        _declaredMode = mode;
        _dirty = true;
    });

    /// <summary>Sets the user-declared role; <see cref="GameRole.None"/> clears it.</summary>
    public void DeclareRole(GameRole role) => Run(_ =>
    {
        _declaredRole = role;
        _dirty = true;
    });

    /// <summary>Sets the user-declared character (no detector exists in v1); null clears it.</summary>
    public void DeclareCharacter(string? name) => Run(_ =>
    {
        _declaredCharacter = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _dirty = true;
    });

    /// <summary>Applies time-based rules: scene loss, time-to-live, session loss, summary expiry and health. Call about once a second.</summary>
    public void Tick() => Run(_ => { });

    /// <summary>Submits one observation.</summary>
    /// <returns>What the engine did with it.</returns>
    public SubmitResult Submit(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var result = SubmitResult.Accepted;
        Run(now => result = SubmitCore(observation, now));
        return result;
    }

    private void Run(Action<DateTimeOffset> action)
    {
        MatchSnapshot? changed = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            action(now);
            Expire(now);
            RefreshHealthSignature(now);
            if (_dirty)
            {
                _dirty = false;
                _sequence++;
                changed = BuildSnapshot(now);
            }
        }

        if (changed is not null)
        {
            SnapshotChanged?.Invoke(this, changed);
        }
    }

    private SubmitResult SubmitCore(Observation o, DateTimeOffset now)
    {
        if (double.IsNaN(o.Score) || o.Score is < 0 or > 1 || Observation.KindOf(o.Detector) is not { } expected)
        {
            return Reject(SubmitResult.RejectedInvalid);
        }

        if (expected != o.Kind)
        {
            return Reject(SubmitResult.RejectedKindMismatch);
        }

        if (_game != GameState.Running)
        {
            return Reject(SubmitResult.IgnoredNotRunning);
        }

        if (_paused)
        {
            return Reject(SubmitResult.IgnoredPaused);
        }

        if (o.ObservedAtUtc < now - _options.MaxObservationAge)
        {
            return Reject(SubmitResult.RejectedTooOld);
        }

        if (o.ObservedAtUtc > now + _options.MaxClockSkew)
        {
            return Reject(SubmitResult.RejectedFromFuture);
        }

        if (_lastAccepted.TryGetValue(o.Kind, out var last) && o.ObservedAtUtc <= last)
        {
            return Reject(SubmitResult.RejectedNotNewer);
        }

        _lastAccepted[o.Kind] = o.ObservedAtUtc;
        var valid = o.Value is null || IsValidValue(o.Kind, o.Value);
        _health[o.Detector].Record(o.ObservedAtUtc, o.Value is null || !valid, o.Context, SettingsOf(o.Detector));
        if (!valid)
        {
            return Reject(SubmitResult.RejectedInvalid);
        }

        if (o.Value is null)
        {
            return Accept(); // Unreadable frames neither confirm nor refute.
        }

        switch (o.Kind)
        {
            case ObservationKind.Scene:
                FeedScene(Enum.Parse<Scene>(o.Value), o, now);
                return Accept();
            case ObservationKind.Role:
                if (_scene is not (Scene.Lobby or Scene.Loading or Scene.InTrial or Scene.Spectating or Scene.InMatchMenu or Scene.Results))
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                FeedField(FieldKind.Role, _role, _options.Role, o, now);
                return Accept();
            case ObservationKind.Character:
                // The owner's row is printed in the pre-match lobby, in TAB match details and on the scoreboard (hud-cue-catalogue).
                if (_scene is not (Scene.Lobby or Scene.InMatchMenu or Scene.Results))
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                FeedField(FieldKind.Character, _character, _options.Character, o, now);
                return Accept();
            case ObservationKind.Killer:
                // The killer's name is printed only in TAB match details and on the scoreboard; it is never inferred.
                if (_scene is not (Scene.InMatchMenu or Scene.Results))
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                FeedField(FieldKind.Killer, _killer, _options.Killer, o, now);
                return Accept();
            case ObservationKind.KillerPrompt:
                // The power prompts are printed at the bottom centre of the trial HUD, and only to the player who owns the power.
                if (_scene != Scene.InTrial)
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                FeedField(FieldKind.KillerPrompt, _killerPrompt, _options.KillerPrompt, o, now);
                return Accept();
            case ObservationKind.QueueMode:
                // The mode list (Other) and the searching screen print the queue; the lobby keeps the choice.
                if (_scene is not (Scene.MainMenu or Scene.Other or Scene.Lobby or Scene.Matchmaking))
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                FeedField(FieldKind.QueueMode, _queueMode, _options.QueueMode, o, now);
                return Accept();
            case ObservationKind.Map:
                if (_scene is not (Scene.InTrial or Scene.Spectating or Scene.InMatchMenu))
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                if (_session is null || o.ObservedAtUtc < _session.StartedUtc)
                {
                    return Reject(SubmitResult.RejectedNoSession);
                }

                FeedField(FieldKind.Map, _map, _options.Map, o, now);
                return Accept();
            case ObservationKind.OwnStatus:
                // The owner's row (a state icon left of the owner's name) exists only in the survivor's trial HUD.
                if (_scene != Scene.InTrial)
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                if (EffectiveRole() != GameRole.Survivor)
                {
                    return Reject(SubmitResult.RejectedRoleGate);
                }

                if (_session is null || o.ObservedAtUtc < _session.StartedUtc)
                {
                    return Reject(SubmitResult.RejectedNoSession);
                }

                FeedField(FieldKind.OwnStatus, _ownStatus, _options.OwnStatus, o, now);
                return Accept();
            case ObservationKind.SurvivorResult or ObservationKind.KillerResult:
                return SubmitOutcome(o, now);
            case ObservationKind.HookCount:
                // The killer's own HUD prints the hook counter. Same mode gate as the generators: both counters belong to the standard trial.
                if (!ModeSupportsGenerators)
                {
                    return Reject(SubmitResult.RejectedModeGate);
                }

                if (_scene != Scene.InTrial)
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                if (EffectiveRole() != GameRole.Killer)
                {
                    return Reject(SubmitResult.RejectedRoleGate);
                }

                if (_session is null || o.ObservedAtUtc < _session.StartedUtc)
                {
                    return Reject(SubmitResult.RejectedNoSession);
                }

                FeedField(FieldKind.HookCount, _hookCount, _options.HookCount, o, now);
                return Accept();
            case ObservationKind.Generators:
                if (!ModeSupportsGenerators)
                {
                    return Reject(SubmitResult.RejectedModeGate);
                }

                if (_scene != Scene.InTrial)
                {
                    return Reject(SubmitResult.RejectedSceneGate);
                }

                if (_session is null || o.ObservedAtUtc < _session.StartedUtc)
                {
                    return Reject(SubmitResult.RejectedNoSession);
                }

                FeedField(FieldKind.Generators, _generators, _options.Generators, o, now);
                return Accept();
            default:
                return Reject(SubmitResult.RejectedInvalid);
        }
    }

    /// <summary>
    /// An outcome of the result screen. It attaches to the session that ended at most <see cref="EngineOptions.ResultAttach"/> earlier, which is
    /// the last-session summary; with none, or an older one, it is dropped (ARCHITECTURE §4.3). An outcome that contradicts the confirmed role is dropped.
    /// </summary>
    private SubmitResult SubmitOutcome(Observation o, DateTimeOffset now)
    {
        if (_scene != Scene.Results)
        {
            return Reject(SubmitResult.RejectedSceneGate);
        }

        var role = EffectiveRole();
        if ((o.Kind == ObservationKind.SurvivorResult && role == GameRole.Killer) || (o.Kind == ObservationKind.KillerResult && role == GameRole.Survivor))
        {
            return Reject(SubmitResult.RejectedRoleGate);
        }

        if (_summary is null || o.ObservedAtUtc - _summary.EndedUtc > _options.ResultAttach)
        {
            return Reject(SubmitResult.RejectedNoSession);
        }

        var survivor = o.Kind == ObservationKind.SurvivorResult;
        FeedField(survivor ? FieldKind.SurvivorResult : FieldKind.KillerResult, survivor ? _survivorResult : _killerResult, _options.ResultOutcome, o, now);
        return Accept();
    }

    private GameRole EffectiveRole() => _declaredRole != GameRole.None
        ? _declaredRole
        : _role.State == FieldState.Confirmed && TryParseName<GameRole>(_role.Value ?? string.Empty, out var role) ? role : GameRole.None;

    private SubmitResult Accept()
    {
        _accepted++;
        return SubmitResult.Accepted;
    }

    private SubmitResult Reject(SubmitResult result)
    {
        _rejected++;
        return result;
    }

    private bool IsValidValue(ObservationKind kind, string value) => kind switch
    {
        ObservationKind.Scene => TryParseName<Scene>(value, out var s) && s != Scene.Unknown,
        ObservationKind.Role => value is nameof(GameRole.Survivor) or nameof(GameRole.Killer),
        ObservationKind.Map => value.Length is > 0 and <= 128 && (_options.IsKnownMapId?.Invoke(value) ?? true),
        ObservationKind.Character => value.Length is > 0 and <= 128 && (_options.IsKnownCharacterId?.Invoke(value) ?? true),
        ObservationKind.Killer => value.Length is > 0 and <= 128 && (_options.IsKnownKillerId?.Invoke(value) ?? true),
        ObservationKind.KillerPrompt => value.Length is > 0 and <= 128 && (_options.IsKnownKillerId?.Invoke(value) ?? true),
        ObservationKind.QueueMode => value == QueueModes.Normal || (value.Length is > 0 and <= 128 && (_options.IsKnownEventModeId?.Invoke(value) ?? true)),
        ObservationKind.OwnStatus => OwnStatuses.IsValid(value),
        ObservationKind.SurvivorResult => ResultOutcomes.IsSurvivorOutcome(value),
        ObservationKind.KillerResult => ResultOutcomes.IsKillerOutcome(value),
        ObservationKind.HookCount => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hooks) && hooks <= _options.MaxHookCount,
        _ => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= _options.MaxGenerators,
    };

    private static bool TryParseName<T>(string text, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private DetectorSettings SettingsOf(DetectorId id) => _settings.GetValueOrDefault(id, DetectorSettings.Default);

    private bool ModeSupportsGenerators => _declaredMode is GameMode.Standard or GameMode.Custom;

    // ---- Scene -------------------------------------------------------------------------------------------------

    private void FeedScene(Scene observed, Observation o, DateTimeOffset now)
    {
        var rule = _options.Scene;
        _sceneWindow.Trim(now, rule.MaxAge);
        _sceneWindow.Add(o.Value!, o.Score, o.ObservedAtUtc);
        if (observed == _scene && o.Score >= rule.MinScore)
        {
            _sceneLastSupport = o.ObservedAtUtc;
        }

        var normal = _sceneWindow.Evaluate(rule, 1);
        var extended = _sceneWindow.Evaluate(rule, 2);
        for (var i = 0; i < 2; i++)
        {
            if ((i == 0 ? normal : extended) is not { } verdict)
            {
                continue;
            }

            var target = Enum.Parse<Scene>(verdict.Value);
            if (target == _scene)
            {
                return;
            }

            if (NeedsStrongEntry(_scene, target) && !HasStrongSupport(rule, i == 0 ? 1 : 2, verdict.Value))
            {
                continue; // weak trial cues alone never carry a menu scene into a trial (review U5 F1)
            }

            switch (SceneTransitions.Classify(_scene, target))
            {
                case TransitionKind.Legal:
                    ApplyScene(target, verdict, now, viaUnknown: false);
                    return;
                case TransitionKind.Illegal when i == 1:
                    _illegalTransitions++;
                    ApplyScene(target, verdict, now, viaUnknown: true);
                    return;
                default:
                    break;
            }
        }
    }

    /// <summary>A trial entered from a menu-type scene must be carried by strong cues (see <see cref="EngineOptions.TrialEntryFromMenuMinScore"/>).</summary>
    private static bool NeedsStrongEntry(Scene current, Scene target) =>
        target is Scene.InTrial or Scene.Spectating && current is Scene.MainMenu or Scene.Other or Scene.Lobby or Scene.Matchmaking or Scene.Results;

    private bool HasStrongSupport(ConfirmationRule rule, int factor, string value)
    {
        var strong = rule with { MinScore = Math.Max(rule.MinScore, _options.TrialEntryFromMenuMinScore) };
        return _sceneWindow.Evaluate(strong, factor) is { } verdict && string.Equals(verdict.Value, value, StringComparison.Ordinal);
    }

    private void ApplyScene(Scene target, WindowVerdict verdict, DateTimeOffset now, bool viaUnknown)
    {
        var previous = _scene;
        var previousSupport = _sceneLastSupport;
        if (viaUnknown)
        {
            EnterUnknown();
            previous = Scene.Unknown;
        }

        var lostRunUp = _beforeLoss;
        _beforeLoss = null;
        if (target != Scene.Results)
        {
            // An outcome belongs to its result screen and lasts only until the scene changes (ARCHITECTURE §4.4).
            _survivorResult.Reset();
            _killerResult.Reset();
        }

        _scene = target;
        _sceneConfirmedAt = now;
        _sceneLastSupport = verdict.LastUtc;
        _sceneWindow.Clear();
        _gapSince = null;
        _dirty = true;

        switch (target)
        {
            case Scene.InTrial or Scene.InMatchMenu:
                if (_session is null)
                {
                    // The loading screen has no catalogued cue, so a pre-match lobby that gives way to the trial within the same window counts as
                    // the run-up too (ARCHITECTURE §4.2). A trial first seen behind a menu is a late join like any other.
                    // The run-up may have been lost to Unknown while the loading screen (which has no cue) was up, so look through it.
                    var (runUp, runUpSupport) = previous == Scene.Unknown && lostRunUp is { } lost ? lost : (previous, previousSupport);
                    var exact = runUp is Scene.Loading or Scene.Lobby && verdict.FirstUtc - runUpSupport <= _options.LoadingToTrial;
                    _session = new MatchSession(++_sessionCounter, verdict.FirstUtc, exact ? StartQuality.Exact : StartQuality.JoinedLate, _declaredMode);
                }

                break;
            case Scene.Results or Scene.MainMenu or Scene.Lobby or Scene.Matchmaking or Scene.Loading:
                EndSession(now);
                if (target is Scene.Matchmaking or Scene.Loading)
                {
                    _summary = null;
                }

                if (target == Scene.MainMenu)
                {
                    _role.Reset();
                    _queueMode.Reset();
                }

                if (target is not Scene.Loading)
                {
                    // The pre-match lobby confirms the character before the loading screen, so only a new screen of the next round clears it.
                    _character.Reset();
                    _killer.Reset();
                    _killerPrompt.Reset();
                }

                break;
            case Scene.Spectating:
                _generators.MarkStale("spectating");
                _ownStatus.MarkStale("spectating");
                _hookCount.MarkStale("spectating");
                break;
            case Scene.Other:
                StaleFields("not in the trial view", includeRole: false);
                break;
            default:
                break;
        }
    }

    private void EnterUnknown()
    {
        if (_scene == Scene.Unknown)
        {
            return;
        }

        _beforeLoss = (_scene, _sceneLastSupport);
        _scene = Scene.Unknown;
        _sceneConfirmedAt = null;
        _sceneWindow.Clear();
        StaleFields("scene evidence lost", includeRole: true);
        if (_session is not null)
        {
            _gapSince ??= _time.GetUtcNow();
        }

        _dirty = true;
    }

    // ---- Fields ------------------------------------------------------------------------------------------------

    private void FeedField(FieldKind kind, FieldTrack track, ConfirmationRule rule, Observation o, DateTimeOffset now)
    {
        track.Window.Trim(now, rule.MaxAge);
        track.Window.Add(o.Value!, o.Score, o.ObservedAtUtc);
        if (track.State == FieldState.Confirmed && string.Equals(track.Value, o.Value, StringComparison.Ordinal) && o.Score >= rule.MinScore)
        {
            track.Renew(o.ObservedAtUtc, TtlOf(kind));
        }

        var normal = track.Window.Evaluate(rule, 1);
        var extended = track.Window.Evaluate(rule, 2);
        for (var i = 0; i < 2; i++)
        {
            if ((i == 0 ? normal : extended) is not { } verdict)
            {
                continue;
            }

            if (track.State == FieldState.Confirmed && string.Equals(track.Value, verdict.Value, StringComparison.Ordinal))
            {
                return;
            }

            if (kind == FieldKind.Generators && track.Value is not null && Number(verdict.Value) > Number(track.Value))
            {
                if (i == 0)
                {
                    continue; // An increase needs doubled evidence (ARCHITECTURE §4.4).
                }

                _monotonicViolations++;
            }

            ConfirmField(kind, track, verdict, now);
            return;
        }
    }

    private void ConfirmField(FieldKind kind, FieldTrack track, WindowVerdict verdict, DateTimeOffset now)
    {
        if (kind == FieldKind.Map && _session is not null && track.Value is not null && !string.Equals(track.Value, verdict.Value, StringComparison.Ordinal))
        {
            // A different map inside one session means a match boundary was missed (ARCHITECTURE §4.3).
            _undetectedBoundaries++;
            EndSession(now);
            _session = new MatchSession(++_sessionCounter, verdict.FirstUtc, StartQuality.JoinedLate, _declaredMode);
        }

        track.Confirm(verdict.Value, now, verdict.LastUtc, TtlOf(kind));
        if (kind is FieldKind.SurvivorResult or FieldKind.KillerResult && _summary is not null)
        {
            _summary = _summary with { OutcomeId = verdict.Value }; // the summary is updated in place when the outcome attaches late
        }

        _dirty = true;
    }

    private TimeSpan? TtlOf(FieldKind kind) => kind switch
    {
        FieldKind.Generators => _options.GeneratorsTtl,
        FieldKind.OwnStatus => _options.OwnStatusTtl,
        FieldKind.HookCount => _options.HookCountTtl,
        _ => null,
    };

    private static int Number(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    private void StaleFields(string reason, bool includeRole)
    {
        if (includeRole)
        {
            _role.MarkStale(reason);
            _character.MarkStale(reason);
            _killer.MarkStale(reason);
            _killerPrompt.MarkStale(reason);
            _queueMode.MarkStale(reason);
        }

        _map.MarkStale(reason);
        _generators.MarkStale(reason);
        _ownStatus.MarkStale(reason);
        _hookCount.MarkStale(reason);
        _survivorResult.MarkStale(reason);
        _killerResult.MarkStale(reason);
        _dirty = true;
    }

    // ---- Sessions ----------------------------------------------------------------------------------------------

    private void EndSession(DateTimeOffset now)
    {
        if (_session is null)
        {
            return;
        }

        var role = _role.State == FieldState.Confirmed && TryParseName<GameRole>(_role.Value ?? string.Empty, out var r) ? r : GameRole.None;
        var mapId = _map.State == FieldState.Confirmed ? _map.Value : null;
        if (role != GameRole.None || mapId is not null)
        {
            _summary = new LastSessionSummary(_session.Number, role, mapId, now);
        }

        _session = null;
        _map.Reset();
        _generators.Reset();
        _ownStatus.Reset();
        _hookCount.Reset();
        _gapSince = null;
        _dirty = true;
    }

    private void ResetAll()
    {
        _beforeLoss = null;
        _scene = Scene.Unknown;
        _sceneConfirmedAt = null;
        _sceneWindow.Clear();
        _role.Reset();
        _map.Reset();
        _generators.Reset();
        _character.Reset();
        _queueMode.Reset();
        _killer.Reset();
        _killerPrompt.Reset();
        _ownStatus.Reset();
        _survivorResult.Reset();
        _killerResult.Reset();
        _hookCount.Reset();
        _session = null;
        _summary = null;
        _gapSince = null;
        _paused = false;
        _lastAccepted.Clear();
        foreach (var tracker in _health.Values)
        {
            tracker.Reset();
        }
    }

    // ---- Time --------------------------------------------------------------------------------------------------

    private void Expire(DateTimeOffset now)
    {
        if (_game != GameState.Running)
        {
            return;
        }

        if (_scene != Scene.Unknown)
        {
            if (_paused)
            {
                if (now - _pauseStart >= _options.SceneHold)
                {
                    EnterUnknown();
                }
            }
            else
            {
                var lost = _scene is Scene.InTrial or Scene.Spectating ? _options.SceneLostTrial : _options.SceneLostMenu;
                if (now - _sceneLastSupport > lost)
                {
                    EnterUnknown();
                }
            }
        }

        ExpireTtl(_generators, now);
        ExpireTtl(_ownStatus, now);
        ExpireTtl(_hookCount, now);

        if (_session is not null && _gapSince is { } gap && now - gap > _options.SessionLost)
        {
            EndSession(now);
        }

        if (_summary is not null && now - _summary.EndedUtc >= _options.SummaryTtl)
        {
            _summary = null;
            _dirty = true;
        }
    }

    private void ExpireTtl(FieldTrack track, DateTimeOffset now)
    {
        if (track.State == FieldState.Confirmed && track.ExpiresAt is { } expires && now >= expires)
        {
            track.MarkStale("expired");
            _dirty = true;
        }
    }

    // ---- Snapshot ----------------------------------------------------------------------------------------------

    private Dictionary<DetectorId, DetectorHealth> EvaluateHealth(DateTimeOffset now)
    {
        var expecting = _game == GameState.Running && !_paused;
        var result = new Dictionary<DetectorId, DetectorHealth>();
        foreach (var id in s_supported)
        {
            result[id] = _health[id].Evaluate(now, _options, SettingsOf(id), expecting);
        }

        return result;
    }

    private void RefreshHealthSignature(DateTimeOffset now)
    {
        var signature = string.Join(
            ';',
            EvaluateHealth(now).OrderBy(h => h.Key).Select(h => FormattableString.Invariant($"{h.Key}={h.Value.State}")));
        if (!string.Equals(signature, _healthSignature, StringComparison.Ordinal))
        {
            _healthSignature = signature;
            _dirty = true;
        }
    }

    private MatchSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var health = EvaluateHealth(now);
        var sceneField = _scene == Scene.Unknown
            ? ConfirmedField.Empty(_paused ? "capture paused" : "no scene confirmed")
            : new ConfirmedField(FieldState.Confirmed, _scene.ToString(), FieldSource.Detected, _sceneConfirmedAt, null, _paused ? "held while capture is paused" : null);

        return new MatchSnapshot
        {
            Sequence = _sequence,
            Game = _game,
            CapturePaused = _paused,
            Scene = ApplyHealth(sceneField, health[DetectorId.Scene]),
            Role = _declaredRole != GameRole.None
                ? new ConfirmedField(FieldState.Confirmed, _declaredRole.ToString(), FieldSource.UserDeclared, null, null, null)
                : ApplyHealth(_role.ToField(), health[DetectorId.Role]),
            Character = _declaredCharacter is { } c
                ? new ConfirmedField(FieldState.Confirmed, c, FieldSource.UserDeclared, null, null, null)
                : ApplyHealth(_character.ToField(), health[DetectorId.Character]),
            QueueMode = ApplyHealth(_queueMode.ToField(), health[DetectorId.QueueMode]),
            Killer = ApplyHealth(_killer.ToField(), health[DetectorId.Killer]),
            KillerFromPrompt = ApplyHealth(_killerPrompt.ToField(), health[DetectorId.KillerPrompt]),
            Map = ApplyHealth(_map.ToField(), health[DetectorId.Map]),
            Mode = _declaredMode != GameMode.Unknown
                ? new ConfirmedField(FieldState.Confirmed, _declaredMode.ToString(), FieldSource.UserDeclared, null, null, null)
                : ConfirmedField.Empty("game mode not declared"),
            Generators = ModeSupportsGenerators
                ? ApplyHealth(_generators.ToField(), health[DetectorId.Generators])
                : new ConfirmedField(FieldState.Disabled, null, FieldSource.Detected, null, null, "game mode is not declared as Standard or Custom"),
            OwnStatus = ApplyHealth(_ownStatus.ToField(), health[DetectorId.Status]),
            SurvivorResult = ApplyHealth(_survivorResult.ToField(), health[DetectorId.SurvivorResult]),
            KillerResult = ApplyHealth(_killerResult.ToField(), health[DetectorId.KillerResult]),
            HookCount = ModeSupportsGenerators
                ? ApplyHealth(_hookCount.ToField(), health[DetectorId.HookCount])
                : new ConfirmedField(FieldState.Disabled, null, FieldSource.Detected, null, null, "game mode is not declared as Standard or Custom"),
            Session = _session,
            LastSession = _summary,
            Health = health,
            Diagnostics = new EngineDiagnostics(_accepted, _rejected, _illegalTransitions, _monotonicViolations, _undetectedBoundaries),
        };
    }

    private static ConfirmedField ApplyHealth(ConfirmedField field, DetectorHealth health) =>
        health.State == HealthState.Degraded && field.State == FieldState.Confirmed
            ? field with { State = FieldState.Degraded, Reason = health.Reason }
            : field;

    private sealed class FieldTrack(int capacity)
    {
        public ObservationWindow Window { get; } = new(capacity);

        public string? Value { get; private set; }

        public FieldState State { get; private set; } = FieldState.Unknown;

        public DateTimeOffset? ConfirmedAt { get; private set; }

        public DateTimeOffset? ExpiresAt { get; private set; }

        public string? Reason { get; private set; }

        public void Reset()
        {
            Window.Clear();
            Value = null;
            State = FieldState.Unknown;
            ConfirmedAt = null;
            ExpiresAt = null;
            Reason = null;
        }

        public void Confirm(string value, DateTimeOffset now, DateTimeOffset lastSupport, TimeSpan? ttl)
        {
            Value = value;
            State = FieldState.Confirmed;
            ConfirmedAt = now;
            ExpiresAt = ttl is { } t ? lastSupport + t : null;
            Reason = null;
        }

        public void Renew(DateTimeOffset at, TimeSpan? ttl)
        {
            if (ttl is { } t)
            {
                ExpiresAt = at + t;
            }
        }

        public void MarkStale(string reason)
        {
            Window.Clear();
            if (State == FieldState.Confirmed)
            {
                State = FieldState.Stale;
                ExpiresAt = null;
                Reason = reason;
            }
        }

        public ConfirmedField ToField() => new(State, Value, FieldSource.Detected, ConfirmedAt, ExpiresAt, Reason);
    }
}
