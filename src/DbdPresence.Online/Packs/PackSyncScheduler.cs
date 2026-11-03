using DbdPresence.Core.Online;
using DbdPresence.Online.Roster;
using DbdPresence.Packs.Sync;

namespace DbdPresence.Online.Packs;

/// <summary>
/// Runs pack sync on start and every 6 hours (FR-ONL-07), and once more when online features become available (for example right after
/// sign-in). Each run asks the service itself whether online features are allowed, so a signed-out or switched-off app contacts nothing.
/// A failed run is logged and never reaches presence (D8). The character roster is checked right after the packs (PLAN_V2_S5B §2.3).
/// </summary>
public sealed class PackSyncScheduler : IDisposable
{
    /// <summary>How long after start the first run happens (the account refresh has run by then).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    /// <summary>The time between runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IPackSyncService _sync;
    private readonly IRosterSyncJob? _roster;
    private readonly IPackSyncGate _gate;
    private readonly TimeProvider _time;
    private readonly IOnlineLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private bool _wasOpen;
    private Task _lastRun = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="PackSyncScheduler"/> class.</summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="gate">The online features gate.</param>
    /// <param name="time">The clock; timers come from it so tests can advance time.</param>
    /// <param name="log">Diagnostics.</param>
    /// <param name="roster">The roster check run after each pack sync, or null.</param>
    public PackSyncScheduler(IPackSyncService sync, IPackSyncGate gate, TimeProvider time, IOnlineLog log, IRosterSyncJob? roster = null)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _sync = sync;
        _roster = roster;
        _gate = gate;
        _time = time;
        _log = log;
    }

    /// <summary>Gets the task of the most recent run (completed when none is running); tests await it.</summary>
    public Task LastRun
    {
        get
        {
            lock (_lock)
            {
                return _lastRun;
            }
        }
    }

    /// <summary>Starts the schedule.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_timer is not null)
            {
                return;
            }

            _wasOpen = _gate.IsOpen;
            _gate.Changed += OnGateChanged;
            _timer = _time.CreateTimer(_ => Trigger(), null, StartDelay, Interval);
        }
    }

    /// <summary>Stops the schedule and cancels a run in progress.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _gate.Changed -= OnGateChanged;
            _timer?.Dispose();
            _timer = null;
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnGateChanged(object? sender, EventArgs e)
    {
        var open = _gate.IsOpen;
        bool opened;
        lock (_lock)
        {
            opened = open && !_wasOpen;
            _wasOpen = open;
        }

        if (opened)
        {
            Trigger();
        }
    }

    private void Trigger()
    {
        lock (_lock)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _lastRun = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await _sync.SyncAsync(manual: false, _stop.Token).ConfigureAwait(false);
            if (_roster is not null)
            {
                await _roster.SyncAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Pack sync was stopped because the app is closing.");
        }
        catch (ObjectDisposedException)
        {
            _log.Warning("Pack sync was stopped because the app is closing.");
        }
    }
}
