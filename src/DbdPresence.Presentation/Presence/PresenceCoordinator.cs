using DbdPresence.Configuration;
using DbdPresence.Core.Composition;
using DbdPresence.Core.Detection;
using DbdPresence.Core.Game;
using DbdPresence.Core.Online;
using DbdPresence.Core.Presence;
using DbdPresence.Discord;
using DbdPresence.Presentation.Detection;

namespace DbdPresence.Presentation.Presence;

/// <summary>
/// The single writer of presence (ARCHITECTURE §5): on every input change it composes the activity from the
/// confirmed game lifecycle, the active profile and the privacy state, hands it to the sink, and publishes a
/// <see cref="PresenceSnapshot"/> for the UI. Inputs arrive on any thread; composition is serialised by a lock.
/// </summary>
public sealed class PresenceCoordinator : IDisposable
{
    /// <summary>Periodic recompose so minute-resolution tokens such as {duration} stay current.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long the connection-test activity stays up.</summary>
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(15);

    /// <summary>
    /// An acknowledgement only counts for the test when it arrives at least this long after the test started: the
    /// publisher debounces for 1 s, so an earlier ack belongs to an update that was already in flight.
    /// </summary>
    public static readonly TimeSpan TestAckGrace = TimeSpan.FromSeconds(1);

    private static readonly ValidatedActivity s_testActivity = ActivityValidator.Validate(new ActivityDraft
    {
        Details = "Testing DBD Presence",
        State = "Connection test — this clears itself",
    }).Activity;

    private readonly ISettingsService _settings;
    private readonly IApplicationCatalog _catalog;
    private readonly IGameLifecycleSource _game;
    private readonly IPresenceSink _sink;
    private readonly TimeProvider _time;
    private readonly PackArtworkProvider? _packs;
    private readonly IAccountService? _account;
    private readonly IDetectionFeed? _detection;
    private readonly Lock _gate = new();
    private ITimer? _refreshTimer;
    private ITimer? _testTimer;
    private bool _paused;
    private ConnectionTest? _test;
    private PresenceSnapshot _snapshot;
    private bool _started;
    private bool _sinkOpen;
    private bool _disposed;
    private int _failures;

    /// <summary>Initializes a new instance of the <see cref="PresenceCoordinator"/> class. Nothing is published until <see cref="Start"/>.</summary>
    public PresenceCoordinator(
        ISettingsService settings, IApplicationCatalog catalog, IGameLifecycleSource game, IPresenceSink sink, TimeProvider time, PackArtworkProvider? packs = null, IAccountService? account = null, IDetectionFeed? detection = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);
        _settings = settings;
        _catalog = catalog;
        _game = game;
        _sink = sink;
        _time = time;
        _packs = packs;
        _account = account;
        _detection = detection;
        _snapshot = Compose();
    }

    /// <summary>
    /// Raised (on any thread) after <see cref="Snapshot"/> changes. Handlers should read <see cref="Snapshot"/>
    /// for the latest value and must not block.
    /// </summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>Raised when composing or publishing failed (ARCHITECTURE §6 loop boundary); the app logs it.</summary>
    public event EventHandler<Exception>? RecomputeFailed;

    /// <summary>Gets how many recomputes have failed since start (shown in diagnostics; never silent).</summary>
    public int FailureCount => Volatile.Read(ref _failures);

    /// <summary>Gets the latest snapshot.</summary>
    public PresenceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Gets the image packs the artwork comes from, if any (the preview reads their local files; D29).</summary>
    public PackArtworkProvider? Packs => _packs;

    /// <summary>Gets a value indicating whether presence is paused.</summary>
    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>Subscribes to inputs, configures the sink and publishes the first activity. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _settings.Changed += OnSettingsChanged;
            _game.LifecycleChanged += OnLifecycleChanged;
            _sink.HealthChanged += OnHealthChanged;
            if (_account is not null)
            {
                _account.Changed += OnAccountChanged;
            }

            if (_packs is not null)
            {
                _packs.Library.SyncedChanged += OnPacksChanged;
            }

            if (_detection is not null)
            {
                _detection.Changed += OnDetectionChanged;
            }

            OpenSinkIfUnlocked();
            _refreshTimer = _time.CreateTimer(_ => SafeRecompute(), null, RefreshInterval, RefreshInterval);
        }

        Recompute();
    }

    /// <summary>Pauses or resumes presence (runtime only; not persisted, FR-VIS-03).</summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return;
            }

            _paused = paused;
        }

        Recompute();
    }

    /// <summary>
    /// Shows a temporary test activity so the user can confirm Discord displays it, then restores the normal
    /// activity. Refused when no valid application id is set, or while the user has paused or hidden their activity
    /// (a test must not override that choice).
    /// </summary>
    public ConnectionTestStart StartConnectionTest()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var settings = _settings.Current;
            if (IsLocked)
            {
                return ConnectionTestStart.Locked;
            }

            if (!ResolvedApplication.From(settings.Discord, _catalog).IsConfigured)
            {
                return ConnectionTestStart.NotConfigured;
            }

            if (_paused || settings.Privacy.HideFullActivity)
            {
                return ConnectionTestStart.ActivityHidden;
            }

            var now = _time.GetUtcNow();
            _test = new ConnectionTest(now, now + TestDuration, null);
            _testTimer?.Dispose();
            _testTimer = _time.CreateTimer(_ => SafeRecompute(), null, TestDuration, Timeout.InfiniteTimeSpan);
        }

        Recompute();
        return ConnectionTestStart.Started;
    }

    /// <summary>Composes and publishes now.</summary>
    public void Recompute()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _snapshot = Compose();
            if (_started && OpenSinkIfUnlocked())
            {
                _sink.Publish(_snapshot.Published);
            }
        }

        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops listening to inputs. The sink is owned (and shut down) by the caller.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settings.Changed -= OnSettingsChanged;
            _game.LifecycleChanged -= OnLifecycleChanged;
            _sink.HealthChanged -= OnHealthChanged;
            if (_account is not null)
            {
                _account.Changed -= OnAccountChanged;
            }

            if (_packs is not null)
            {
                _packs.Library.SyncedChanged -= OnPacksChanged;
            }

            if (_detection is not null)
            {
                _detection.Changed -= OnDetectionChanged;
            }

            _refreshTimer?.Dispose();
            _testTimer?.Dispose();
        }
    }

    /// <summary>Maps the process lifecycle to a presence state (screen detection adds finer states in Stage C).</summary>
    internal static PresenceState StateFor(GameState state) => state switch
    {
        GameState.Launching => PresenceState.Launching,
        GameState.Running => PresenceState.Running,
        _ => PresenceState.NotRunning,
    };

    /// <summary>Gets the application the current settings connect with (the catalog entry, or the user's own).</summary>
    public ResolvedApplication Application => ResolvedApplication.From(_settings.Current.Discord, _catalog);

    /// <summary>Gets a value indicating whether the app is locked (D32): no verified licence, or a signed revocation. A coordinator built without an account (tests only) is never locked.</summary>
    private bool IsLocked => _account is { Current.PresenceAllowed: false };

    /// <summary>
    /// The lock gate (D32): the sink is told nothing (no application id, no activity, so Discord is never contacted) until the account first
    /// reports an unlocked state. After that it stays open, so a later signed revocation still reaches Discord as one clearing update.
    /// </summary>
    /// <returns>True when the sink may be used.</returns>
    private bool OpenSinkIfUnlocked()
    {
        if (!_sinkOpen)
        {
            if (IsLocked)
            {
                return false;
            }

            _sinkOpen = true;
            _sink.SetApplicationId(Application.ApplicationId);
        }

        return true;
    }

    private PresenceSnapshot Compose()
    {
        var settings = _settings.Current;
        var game = _game.Current;
        var health = _sink.Health;
        var now = _time.GetUtcNow();
        var state = StateFor(game.State);
        var inputs = DetectedInputs(state, now, game.StartedUtc) ?? new PresenceInputs { State = state, Now = now, GameStartedUtc = game.StartedUtc };
        // D32/D8: nothing is composed for Discord while locked (before sign-in and invite, or after a signed revocation). Once unlocked, only a
        // received, signed revocation locks it again; a lapsed licence, an outage or a clock change do not.
        var privacy = new PrivacyState(_paused, settings.Privacy.StreamerMode, settings.Privacy.HideFullActivity, IsLocked);
        var profile = settings.ActiveProfile;
        var artwork = _packs?.Apply(settings, profile, inputs.DetectedRole, inputs.CharacterName, inputs.OwnStatus, inputs.OutcomeId) ?? profile.Artwork;
        var composition = PresenceComposer.Compose(inputs, profile with { Artwork = artwork }, privacy, settings.Discord.DisplayName);

        // While paused, the UI also shows what will appear on resume: the same composition with only the pause removed (D29).
        var unpaused = _paused
            ? PresenceComposer.Compose(inputs, profile with { Artwork = artwork }, privacy with { Paused = false }, settings.Discord.DisplayName).Activity
            : null;

        if (_test is { AcknowledgedUtc: null } pending && health.LastAckUtc is { } ack && ack >= pending.StartedUtc + TestAckGrace && ack <= pending.EndsUtc)
        {
            _test = pending with { AcknowledgedUtc = ack };
        }

        var testRunning = !IsLocked && _test is not null && now < _test.EndsUtc && !_paused && !settings.Privacy.HideFullActivity;
        var configured = ResolvedApplication.From(settings.Discord, _catalog).IsConfigured;
        return new PresenceSnapshot(
            game, state, composition, testRunning ? s_testActivity : composition.Activity, health, _paused, settings.Privacy.StreamerMode,
            configured, _test, now, artwork, unpaused, inputs);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var previous = ResolvedApplication.From(e.Previous.Discord, _catalog).ApplicationId;
        var current = ResolvedApplication.From(e.Current.Discord, _catalog).ApplicationId;
        if (!string.Equals(previous, current, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                // While the sink has never been opened (locked), the new id is applied when it opens.
                if (_sinkOpen)
                {
                    _sink.SetApplicationId(current);
                }
            }
        }

        SafeRecompute();
    }

    private void OnLifecycleChanged(object? sender, GameLifecycle e) => SafeRecompute();

    private void OnHealthChanged(object? sender, RpcHealth e) => SafeRecompute();

    private void OnAccountChanged(object? sender, EventArgs e) => SafeRecompute();

    private void OnPacksChanged(object? sender, EventArgs e) => SafeRecompute();

    private void OnDetectionChanged(object? sender, EventArgs e) => SafeRecompute();

    /// <summary>
    /// What detection adds to the inputs, or null when it adds nothing. Every value goes through <see cref="PublicationPolicy"/>: a detector that is
    /// Disabled, has initial-guess thresholds, is degraded or is stale publishes nothing, so with no validation report the result is the same inputs as
    /// without detection. The composer has no beta marker yet, so a field that would publish at the Experimental tier makes this return null
    /// (fail closed) rather than reach Discord unmarked.
    /// </summary>
    private PresenceInputs? DetectedInputs(PresenceState state, DateTimeOffset now, DateTimeOffset? gameStartedUtc)
    {
        if (_detection?.Snapshot is not { Game: GameState.Running } snapshot || state != PresenceState.Running)
        {
            return null;
        }

        var result = PublicationPolicy.Compose(snapshot, _detection.DetectorSettings, _detection.Options, _detection.Maps, now, gameStartedUtc);
        return result.BetaFields.Count > 0 ? null : result.Inputs;
    }

    /// <summary>
    /// The loop boundary for every background trigger (timers, settings, lifecycle, health): a failure is counted and
    /// reported, never thrown into a thread-pool thread or back into the settings service.
    /// </summary>
    private void SafeRecompute()
    {
#pragma warning disable CA1031 // Loop boundary (ARCHITECTURE §6): any failure is counted and raised through RecomputeFailed.
        try
        {
            Recompute();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            RecomputeFailed?.Invoke(this, ex);
        }
#pragma warning restore CA1031
    }
}
