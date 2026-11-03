using System.IO.Pipes;
using System.Security.Principal;

namespace DbdPresence.Platform.Windows.Startup;

/// <summary>
/// Ensures one running instance per Windows user (FR-LIFE-03). The first instance owns a named mutex and
/// listens on a per-user local pipe; a second launch sends "activate" and exits, and the first instance
/// raises <see cref="ActivationRequested"/> to show its window.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "activate";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(500);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private EventHandler? _activationRequested;
    private bool _activationPending;
    private Task? _listener;

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Raised (on a thread-pool thread) when another launch asked this instance to show itself. A request that
    /// arrives before anyone subscribes (during startup) is delivered to the first subscriber instead of being lost.
    /// </summary>
    public event EventHandler? ActivationRequested
    {
        add
        {
            bool replay;
            lock (_gate)
            {
                _activationRequested += value;
                replay = _activationPending && value is not null;
                _activationPending = false;
            }

            if (replay)
            {
                value!.Invoke(this, EventArgs.Empty);
            }
        }

        remove
        {
            lock (_gate)
            {
                _activationRequested -= value;
            }
        }
    }

    /// <summary>Raised when the activation listener stopped because of an unexpected error (the app logs it).</summary>
    public event EventHandler<Exception>? ListenerFailed;

    /// <summary>
    /// Tries to become the primary instance. Returns null when another instance owns the mutex, after
    /// asking it to activate.
    /// </summary>
    public static SingleInstance? TryAcquire(string appId = "DBDPresence")
    {
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = $"{appId}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId)))[..16]}";
        // Existence of the named mutex marks the running instance; it is never owned or waited on, so disposing it
        // has no thread affinity (ReleaseMutex from another thread would throw).
        var mutex = new Mutex(initiallyOwned: false, $@"Local\{suffix}", out var createdNew);
        var pipeName = $"{suffix}-activate";
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExisting(pipeName);
            return null;
        }

        var instance = new SingleInstance(mutex, pipeName);
        instance._listener = Task.Run(() => instance.ListenAsync(instance._stop.Token));
        return instance;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // hygiene: allow-swallow the listener only ends by cancellation during shutdown; nothing to report.
        }

        _stop.Dispose();
        _mutex.Dispose();
    }

    private static void SignalExisting(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client);
            writer.Write(ActivateMessage);
        }
        catch (TimeoutException)
        {
            // hygiene: allow-swallow the first instance is still starting or shutting down; this launch exits either way.
        }
        catch (IOException)
        {
            // hygiene: allow-swallow as above: activation is a convenience, not required for correctness.
        }
    }

    private async Task ListenAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using var serverScope = server.ConfigureAwait(false);
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var message = await reader.ReadToEndAsync(cancellation).ConfigureAwait(false);
                if (message == ActivateMessage)
                {
                    RaiseActivation();
                }
            }
            catch (OperationCanceledException)
            {
                return; // hygiene: allow-swallow cancellation ends the listener on shutdown.
            }
            catch (IOException)
            {
                // A client disconnected mid-message, or the pipe is briefly unavailable: wait, then keep listening.
                if (!await DelayAsync(cancellation).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // The pipe name is held by something we may not use; activation cannot work this session.
                ListenerFailed?.Invoke(this, ex);
                return;
            }
        }
    }

    private static async Task<bool> DelayAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(s_retryDelay, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // hygiene: allow-swallow cancellation during the retry delay ends the listener.
        }
    }

    private void RaiseActivation()
    {
        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _activationRequested;
            _activationPending = handlers is null;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }
}
