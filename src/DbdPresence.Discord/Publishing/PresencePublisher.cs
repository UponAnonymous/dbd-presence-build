using System.Globalization;
using System.Threading.Channels;
using DbdPresence.Core.Presence;
using DbdPresence.Discord.Ipc;

namespace DbdPresence.Discord;

/// <summary>
/// Keeps Discord's Rich Presence equal to the latest activity handed to <see cref="Publish"/> (ARCHITECTURE
/// §5 steps 5 and 6; FR-RPC-01, FR-RPC-02, FR-RPC-03, FR-RPC-05, FR-RPC-07, FR-RPC-08; NFR-REL-02).
/// </summary>
/// <remarks>
/// <para>
/// One background loop owns the connection. It connects with jittered exponential backoff, coalesces bursts
/// (latest wins, 1 s debounce), keeps to 4 updates per 20 s, skips an update equal to the last acknowledged
/// one, and sends strictly one request at a time, waiting up to 5 s for each response. The acknowledged
/// baseline and any remembered rejection are reset on every connection, so the current activity is always
/// re-sent after a reconnect or an application id change. When Discord answers with an error the identical
/// payload is not sent again on that connection until it changes. The reconnect backoff restarts only after a
/// healthy session (an acknowledged update, or <see cref="PresencePublisherOptions.HealthySessionDuration"/>).
/// </para>
/// <para>
/// Exceptions never leave the loop: they are recorded in <see cref="Health"/> and the loop continues with
/// backoff. The class needs no logger; the application observes <see cref="HealthChanged"/>, and a subscriber
/// that throws is isolated and counted in <see cref="RpcHealth.HandlerFailures"/>. All time, delays and
/// timers come from the injected <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed class PresencePublisher : IAsyncDisposable
{
    private readonly IDiscordTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly PresencePublisherOptions _options;
    private readonly ReconnectBackoff _backoff;
    private readonly SendRateLimiter _rateLimiter;
    private readonly long _epoch;
    private readonly object _gate = new();
    private readonly Channel<bool> _wakeUp = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    // Guarded by _gate: what the application wants.
    private ValidatedActivity? _desired;
    private string? _applicationId;
    private TimeSpan? _firstChangeAt;
    private TimeSpan? _lastChangeAt;
    private bool _shutdownRequested;

    // Owned by the loop: what Discord is known to have.
    private ValidatedActivity? _acknowledged;
    private ValidatedActivity? _rejected;
    private bool _hasRejected;
    private bool _activityMaySet;
    private TimeSpan? _sessionStartedAt;
    private int _handlerFailures;
    private string? _lastHandlerFailureType;

    private RpcHealth _health = new(RpcStatus.NotConfigured, null, null, null, 0, null);
    private DateTimeOffset? _lastAckUtc;
    private long _signalsRaised;
    private long _signalsHandled;
    private Task[]? _idleWaits;
    private DiscordIpcClient? _currentClient;

    /// <summary>Initializes the publisher and starts its loop. Without an application id it stays idle.</summary>
    /// <param name="transportFactory">Creates one transport per connection attempt.</param>
    /// <param name="timeProvider">Time source; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Timing and limits; defaults to the values in ARCHITECTURE §5.</param>
    /// <param name="random">Source of reconnect jitter; defaults to <see cref="Random.Shared"/>.</param>
    public PresencePublisher(
        IDiscordTransportFactory transportFactory,
        TimeProvider? timeProvider = null,
        PresencePublisherOptions? options = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        _transportFactory = transportFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new PresencePublisherOptions();
        _backoff = new ReconnectBackoff(_options.MinBackoff, _options.MaxBackoff, _options.BackoffJitter, random ?? Random.Shared);
        _rateLimiter = new SendRateLimiter(_options.UpdatesPerWindow, _options.UpdateWindow);
        _epoch = _timeProvider.GetTimestamp();
        _loop = RunAsync();
    }

    /// <summary>
    /// Raised, on the publisher's loop thread, whenever <see cref="Health"/> changes. Handlers must return
    /// quickly and must not call <see cref="ShutdownAsync"/> synchronously. Each handler runs in its own guard:
    /// one that throws is counted in <see cref="RpcHealth.HandlerFailures"/> and never changes publishing.
    /// </summary>
    public event EventHandler<RpcHealth>? HealthChanged;

    /// <summary>Gets the latest health snapshot.</summary>
    public RpcHealth Health => Volatile.Read(ref _health);

    /// <summary>
    /// Returns <see langword="true"/> for a Discord application (client) id: a snowflake of 17 to 20 digits.
    /// </summary>
    /// <param name="applicationId">The candidate id.</param>
    /// <returns>Whether the id is well-formed.</returns>
    public static bool IsValidApplicationId(string? applicationId) =>
        applicationId is { Length: >= 17 and <= 20 } && applicationId.All(char.IsAsciiDigit);

    /// <summary>
    /// Sets the Discord application id. A missing or malformed id makes the publisher idle
    /// (<see cref="RpcStatus.NotConfigured"/>); a changed id reconnects with the new application.
    /// </summary>
    /// <param name="applicationId">The id, or <see langword="null"/> to stop publishing.</param>
    public void SetApplicationId(string? applicationId)
    {
        var normalized = applicationId?.Trim();
        lock (_gate)
        {
            if (_shutdownRequested || string.Equals(_applicationId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _applicationId = normalized;
        }

        WakeLoop();
    }

    /// <summary>
    /// Sets the activity Discord should show; <see langword="null"/> clears it. Latest wins. This is
    /// thread-safe and never blocks: the loop sends the update after the debounce and rate limit.
    /// </summary>
    /// <param name="desired">The validated activity, or <see langword="null"/> for none.</param>
    public void Publish(ValidatedActivity? desired)
    {
        lock (_gate)
        {
            if (_shutdownRequested || EqualityComparer<ValidatedActivity?>.Default.Equals(_desired, desired))
            {
                return;
            }

            _desired = desired;
            var now = Now();
            _firstChangeAt ??= now;
            _lastChangeAt = now;
        }

        WakeLoop();
    }

    /// <summary>
    /// Clears the activity (within a 500 ms budget) if one may be shown, closes the connection and stops the
    /// loop. Idempotent: later calls wait for the first and do nothing more.
    /// </summary>
    /// <returns>A task that completes when the loop has stopped.</returns>
    public async ValueTask ShutdownAsync()
    {
        bool first;
        lock (_gate)
        {
            first = !_shutdownRequested;
            _shutdownRequested = true;
        }

        if (first)
        {
            // Counted like any other stimulus: the loop is not idle until it has started the clear (and its
            // budget timer) or ended. See IsQuiescent.
            Interlocked.Increment(ref _signalsRaised);
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        await _loop.ConfigureAwait(false);
        if (first)
        {
            _stop.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ShutdownAsync();

    /// <summary>
    /// True when the loop has nothing left to do until something outside happens: it is parked on a wait that
    /// has not completed (a timer, a wake-up, a response), or it has ended. It is exact, not a timing guess:
    /// a wait that completed but whose continuation has not run yet counts as work. Tests use it to let the
    /// loop finish reacting before they move the fake clock.
    /// </summary>
    internal bool IsQuiescent =>
        _loop.IsCompleted
        || (Interlocked.Read(ref _signalsHandled) >= Interlocked.Read(ref _signalsRaised)
            && (Volatile.Read(ref _idleWaits) is { } waits
                ? waits.All(wait => !wait.IsCompleted)
                : Volatile.Read(ref _currentClient)?.IsAwaitingResponse ?? false));

    private static TimeSpan Earlier(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static TimeSpan Later(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private TimeSpan Now() => _timeProvider.GetElapsedTime(_epoch);

    private void WakeLoop()
    {
        Interlocked.Increment(ref _signalsRaised);
        _ = _wakeUp.Writer.TryWrite(true);
    }

    private string? ValidApplicationId()
    {
        lock (_gate)
        {
            return IsValidApplicationId(_applicationId) ? _applicationId : null;
        }
    }

    /// <summary>The loop. It ends only on shutdown; every other failure is recorded and retried.</summary>
    private async Task RunAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            TimeSpan? faultDelay = null;
            try
            {
                await RunCycleAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // hygiene: allow-swallow Cancellation is the shutdown signal; the loop condition ends the loop.
            }
#pragma warning disable CA1031 // Loop boundary (ARCHITECTURE §6): any failure is recorded in health and retried with backoff.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                faultDelay = _backoff.Next();
                UpdateHealth(
                    RpcStatus.Faulted,
                    null,
                    $"Unexpected {ex.GetType().Name} in the Discord connection; it will be retried.",
                    _timeProvider.GetUtcNow() + faultDelay.Value);
            }

            if (faultDelay is { } delay)
            {
                await WaitForRetryAsync(delay, ValidApplicationId(), token).ConfigureAwait(false);
            }
        }

        UpdateHealth(
            ValidApplicationId() is null ? RpcStatus.NotConfigured : RpcStatus.Disconnected,
            null,
            Health.LastError,
            null);
    }

    /// <summary>One connection lifecycle: idle, or connect, publish until the connection ends, then back off.</summary>
    private async Task RunCycleAsync(CancellationToken token)
    {
        TakeSignal();
        var applicationId = ValidApplicationId();
        if (applicationId is null)
        {
            UpdateHealth(RpcStatus.NotConfigured, null, null, null);
            await WaitAsync(null, null, token).ConfigureAwait(false);
            return;
        }

        UpdateHealth(RpcStatus.Connecting, null, Health.LastError, null);
        var end = await ConnectAndPublishAsync(applicationId, token).ConfigureAwait(false);
        if (end.Kind == SessionEndKind.ApplicationIdChanged)
        {
            _backoff.Reset();
        }
        else if (end.Kind == SessionEndKind.Dropped)
        {
            if (_sessionStartedAt is { } started && Now() - started >= _options.HealthySessionDuration)
            {
                _backoff.Reset();
            }

            await BackOffAsync(applicationId, end.Reason, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One connection attempt. It tries Discord's pipes in order; when a pipe accepts but the handshake fails
    /// or times out, the same attempt continues with the next pipe instead of retrying the first one (security
    /// review S-L2). Only when no pipe is left does the attempt end and the backoff start.
    /// </summary>
    private async Task<SessionEnd> ConnectAndPublishAsync(string applicationId, CancellationToken token)
    {
        _sessionStartedAt = null;
        var firstEndpoint = 0;
        while (true)
        {
            var transport = _transportFactory.Create(firstEndpoint);
            var client = new DiscordIpcClient(transport, _timeProvider, _options.AckTimeout);
            Volatile.Write(ref _currentClient, client);
            try
            {
                var handshake = await client.ConnectAndHandshakeAsync(applicationId, token).ConfigureAwait(false);
                if (!handshake.Success)
                {
                    var next = transport.EndpointIndex + 1;
                    if (client.Endpoint is not null && next < transport.EndpointCount)
                    {
                        firstEndpoint = next;
                        continue;
                    }

                    return SessionEnd.Dropped(handshake.Message ?? "Discord refused the connection.");
                }

                // A new connection starts from scratch: assets belong to the application and an error may have been
                // transient, so a remembered rejection must not outlive the connection that produced it (Q-M2).
                // The backoff is NOT reset here: READY alone does not prove the session is healthy (Q-M1).
                _acknowledged = null;
                _hasRejected = false;
                _activityMaySet = false;
                _sessionStartedAt = Now();
                UpdateHealth(RpcStatus.Connected, client.Endpoint, null, null);
                return await PublishWhileConnectedAsync(client, applicationId, token).ConfigureAwait(false);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _currentClient, null);
            }
        }
    }

    private async Task<SessionEnd> PublishWhileConnectedAsync(DiscordIpcClient client, string applicationId, CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TakeSignal();

                if (client.Disconnected.IsCompleted)
                {
                    return SessionEnd.Dropped((await client.Disconnected.ConfigureAwait(false)).Message);
                }

                var plan = Snapshot();
                if (!string.Equals(plan.ApplicationId, applicationId, StringComparison.Ordinal))
                {
                    return SessionEnd.ApplicationIdChanged;
                }

                var wanted = plan.Desired;
                if (!NeedsSend(wanted))
                {
                    ForgetChangeTimes(wanted);
                    await WaitAsync(null, client.Disconnected, token).ConfigureAwait(false);
                    continue;
                }

                var now = Now();
                var coalescedUntil = plan is { FirstChangeAt: { } first, LastChangeAt: { } last }
                    ? Earlier(last + _options.Debounce, first + _options.MaxCoalesceDelay)
                    : now;
                var sendAt = Later(coalescedUntil, _rateLimiter.NextAllowedAt(now));
                if (sendAt > now)
                {
                    await WaitAsync(sendAt - now, client.Disconnected, token).ConfigureAwait(false);
                    continue;
                }

                if (await SendAsync(client, wanted, now, token).ConfigureAwait(false) is { } ended)
                {
                    return ended;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await ClearOnShutdownAsync(client).ConfigureAwait(false);
            return SessionEnd.Stopped;
        }
    }

    /// <summary>True when Discord does not already show <paramref name="wanted"/> and has not rejected it.</summary>
    private bool NeedsSend(ValidatedActivity? wanted) =>
        !EqualityComparer<ValidatedActivity?>.Default.Equals(wanted, _acknowledged)
        && !(_hasRejected && EqualityComparer<ValidatedActivity?>.Default.Equals(wanted, _rejected));

    /// <summary>Sends one update and records the outcome; returns the session end if the connection is gone.</summary>
    private async Task<SessionEnd?> SendAsync(DiscordIpcClient client, ValidatedActivity? wanted, TimeSpan now, CancellationToken token)
    {
        ForgetChangeTimes(wanted);
        _rateLimiter.Record(now);
        _activityMaySet |= wanted is not null;

        var result = await client.SetActivityAsync(wanted, _options.ProcessId, token).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case SetActivityOutcome.Success:
                _acknowledged = wanted;
                _hasRejected = false;
                _activityMaySet = wanted is not null;
                _backoff.Reset();
                _lastAckUtc = _timeProvider.GetUtcNow();
                UpdateHealth(RpcStatus.Connected, client.Endpoint, null, null);
                return null;

            case SetActivityOutcome.DiscordError:
                _rejected = wanted;
                _hasRejected = true;
                UpdateHealth(RpcStatus.Connected, client.Endpoint, DescribeDiscordError(result), null);
                return null;

            default:
                return SessionEnd.Dropped(result.Message ?? "The connection to Discord ended.");
        }
    }

    /// <summary>Describes a Discord error response; Discord's own words (already sanitised) are quoted (S-M2).</summary>
    private static string DescribeDiscordError(SetActivityResult result)
    {
        var head = result.Code is { } code
            ? $"Discord rejected the activity (error {code.ToString(CultureInfo.InvariantCulture)})"
            : "Discord rejected the activity";
        return result.Message is { } said ? $"{head}. {DiscordText.Quote(said)}" : $"{head}.";
    }

    private static string DescribeFailure(SetActivityResult result) =>
        result.Outcome == SetActivityOutcome.DiscordError ? DescribeDiscordError(result) : result.Message ?? "No further detail.";

    private async Task ClearOnShutdownAsync(DiscordIpcClient client)
    {
        if (!_activityMaySet || client.Disconnected.IsCompleted)
        {
            return;
        }

        // The clear is deliberately exempt from the 4-per-20-s limiter (ARCHITECTURE §5): leaving a stale activity on
        // the profile is worse than one extra update. It can be at most the 5th send in a window, which is still
        // within Discord's documented 5 updates per 20 s (QA review Q-L1).
        using var budget = new CancellationTokenSource(_options.ClearBudget, _timeProvider);
        Interlocked.Exchange(ref _signalsHandled, Interlocked.Read(ref _signalsRaised));
        try
        {
            var result = await client.SetActivityAsync(null, _options.ProcessId, budget.Token).ConfigureAwait(false);
            if (result.Outcome == SetActivityOutcome.Success)
            {
                _acknowledged = null;
                _activityMaySet = false;
                _lastAckUtc = _timeProvider.GetUtcNow();
            }
            else
            {
                UpdateHealth(Health.Status, Health.Endpoint, $"Clearing the activity failed: {DescribeFailure(result)}", null);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            UpdateHealth(
                Health.Status,
                Health.Endpoint,
                $"Clearing the activity did not finish within {_options.ClearBudget.TotalMilliseconds:0} ms.",
                null);
        }
    }

    /// <summary>Records the failure and waits out the backoff delay; an application id change ends the wait early.</summary>
    private async Task BackOffAsync(string applicationId, string reason, CancellationToken token)
    {
        var delay = _backoff.Next();
        UpdateHealth(RpcStatus.Disconnected, null, reason, _timeProvider.GetUtcNow() + delay);
        await WaitForRetryAsync(delay, applicationId, token).ConfigureAwait(false);
    }

    private async Task WaitForRetryAsync(TimeSpan delay, string? applicationId, CancellationToken token)
    {
        var deadline = Now() + delay;
        while (!token.IsCancellationRequested)
        {
            TakeSignal();
            if (!string.Equals(ValidApplicationId(), applicationId, StringComparison.Ordinal))
            {
                _backoff.Reset();
                return;
            }

            var remaining = deadline - Now();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await WaitAsync(remaining, null, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the wake-up signal is raised, <paramref name="delay"/> elapses, <paramref name="also"/>
    /// completes, or shutdown is requested (in which case it simply returns; callers check the token).
    /// </summary>
    private async Task WaitAsync(TimeSpan? delay, Task? also, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var waits = new List<Task>(3) { _wakeUp.Reader.WaitToReadAsync(linked.Token).AsTask() };
        if (delay is { } wait)
        {
            // Task.Delay works in whole milliseconds and completes at once for a sub-millisecond delay. Rounding up
            // keeps a jittered fractional delay from turning the loop into a busy spin.
            var wholeMilliseconds = TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds));
            waits.Add(Task.Delay(wholeMilliseconds, _timeProvider, linked.Token));
        }

        if (also is not null)
        {
            waits.Add(also);
        }

        Volatile.Write(ref _idleWaits, [.. waits]);
        await Task.WhenAny(waits).ConfigureAwait(false);
        Volatile.Write(ref _idleWaits, null);
        await linked.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Consumes a pending wake-up and marks every call made so far as taken up. The count is read after the
    /// read: a call that slips in between leaves its item in the channel, so the loop simply wakes once more.
    /// </summary>
    private void TakeSignal()
    {
        _ = _wakeUp.Reader.TryRead(out _);
        Interlocked.Exchange(ref _signalsHandled, Interlocked.Read(ref _signalsRaised));
    }

    private Plan Snapshot()
    {
        lock (_gate)
        {
            return new Plan(_desired, IsValidApplicationId(_applicationId) ? _applicationId : null, _firstChangeAt, _lastChangeAt);
        }
    }

    /// <summary>Forgets the debounce clock once the latest wanted activity is being handled; a newer change keeps its own.</summary>
    private void ForgetChangeTimes(ValidatedActivity? handled)
    {
        lock (_gate)
        {
            if (EqualityComparer<ValidatedActivity?>.Default.Equals(_desired, handled))
            {
                _firstChangeAt = null;
                _lastChangeAt = null;
            }
        }
    }

    private void UpdateHealth(RpcStatus status, string? endpoint, string? lastError, DateTimeOffset? nextRetryUtc)
    {
        var updated = new RpcHealth(status, endpoint, _lastAckUtc, lastError, _rateLimiter.CountInWindow(Now()), nextRetryUtc)
        {
            HandlerFailures = _handlerFailures,
            LastHandlerFailureType = _lastHandlerFailureType,
        };
        if (updated == _health)
        {
            return;
        }

        Volatile.Write(ref _health, updated);
        RaiseHealthChanged(updated);
    }

    /// <summary>
    /// Calls every subscriber in its own guard. A subscriber is outside code on the loop thread; its failure
    /// must neither stop the loop nor alter what is published. It is not hidden: it is counted, its type is
    /// kept in <see cref="RpcHealth.LastHandlerFailureType"/> and, if no other error is shown, in LastError.
    /// </summary>
    private void RaiseHealthChanged(RpcHealth snapshot)
    {
        if (HealthChanged is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<RpcHealth>)handler)(this, snapshot);
            }
#pragma warning disable CA1031 // Subscriber boundary: outside code must not break the publisher loop; the failure is counted and surfaced in health.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _handlerFailures++;
                _lastHandlerFailureType = ex.GetType().Name;
                var current = Health;
                Volatile.Write(
                    ref _health,
                    current with
                    {
                        LastError = current.LastError ?? $"A health subscriber threw {ex.GetType().Name}.",
                        HandlerFailures = _handlerFailures,
                        LastHandlerFailureType = _lastHandlerFailureType,
                    });
            }
        }
    }

    private readonly record struct Plan(ValidatedActivity? Desired, string? ApplicationId, TimeSpan? FirstChangeAt, TimeSpan? LastChangeAt);

    private enum SessionEndKind
    {
        Stopped,
        ApplicationIdChanged,
        Dropped,
    }

    private readonly record struct SessionEnd(SessionEndKind Kind, string Reason)
    {
        public static SessionEnd Stopped { get; } = new(SessionEndKind.Stopped, string.Empty);

        public static SessionEnd ApplicationIdChanged { get; } = new(SessionEndKind.ApplicationIdChanged, string.Empty);

        public static SessionEnd Dropped(string reason) => new(SessionEndKind.Dropped, reason);
    }
}
