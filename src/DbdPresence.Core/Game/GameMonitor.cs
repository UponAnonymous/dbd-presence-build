namespace DbdPresence.Core.Game;

/// <summary>
/// Polls the process list and maintains the debounced <see cref="GameLifecycle"/> (ARCHITECTURE §4.1, §6):
/// every 3 s while the game is not running and every 2 s while it runs. Process details are queried once
/// per PID. Faults in a poll are reported through <see cref="PollFailed"/> and never stop monitoring.
/// </summary>
public sealed class GameMonitor : IGameLifecycleSource, IAsyncDisposable
{
    /// <summary>Poll interval while the game is not running.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(3);

    /// <summary>Poll interval while the game is running.</summary>
    public static readonly TimeSpan RunningInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessSnapshotSource _source;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, ProcessDetails?> _details = [];
    private GameProcessMatcher _matcher;
    private Task? _loop;

    /// <summary>Initializes a new instance of the <see cref="GameMonitor"/> class.</summary>
    public GameMonitor(IProcessSnapshotSource source, TimeProvider time, GameProcessMatcher matcher)
    {
        _source = source;
        _time = time;
        _matcher = matcher;
    }

    /// <summary>Raised when the confirmed lifecycle changes (state or process).</summary>
    public event EventHandler<GameLifecycle>? LifecycleChanged;

    /// <summary>Raised when a poll fails; monitoring continues.</summary>
    public event EventHandler<Exception>? PollFailed;

    /// <summary>Gets the current confirmed lifecycle.</summary>
    public GameLifecycle Current { get; private set; } = GameLifecycle.Initial;

    /// <summary>Replaces the executable matcher (settings changed) without restarting.</summary>
    public void UpdateMatcher(GameProcessMatcher matcher) => Volatile.Write(ref _matcher, matcher);

    /// <summary>Starts polling in the background.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Runs a single poll (used by the loop and by tests).</summary>
    public void PollOnce()
    {
        var processes = _source.Snapshot();
        var matcher = Volatile.Read(ref _matcher);
        var game = matcher.FindGame(processes);
        GameProcessInfo? info = null;
        if (game is not null)
        {
            if (!_details.TryGetValue(game.Id, out var details))
            {
                details = _source.Details(game.Id);
                _details.Clear(); // Only the current game PID is ever cached.
                _details[game.Id] = details;
            }

            info = new GameProcessInfo(game.Id, game.Name, details?.StartedUtc);
        }

        var scan = new ProcessScan(_time.GetUtcNow(), info, matcher.HasLauncher(processes));
        var next = GameLifecycleTracker.Next(Current, scan);
        var changed = next.State != Current.State || next.Process?.ProcessId != Current.Process?.ProcessId;
        Current = next;
        if (changed)
        {
            LifecycleChanged?.Invoke(this, next);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
#pragma warning disable CA1031 // Loop boundary (ARCHITECTURE §6): any poll fault is reported and monitoring continues.
            catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
            {
                PollFailed?.Invoke(this, error);
            }

            var interval = Current.State == GameState.Running ? RunningInterval : IdleInterval;
            try
            {
                await Task.Delay(interval, _time, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // hygiene: allow-swallow cancellation is the normal way this loop ends.
            }
        }
    }
}
