using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;

namespace DbdPresence.Discord;

/// <summary>
/// Connects to Discord through the local named pipes <c>discord-ipc-0</c> to <c>discord-ipc-9</c> and uses
/// the first that accepts (FR-RPC-01). Only the local machine (server name ".") is ever addressed; there is
/// no network access (NFR-SEC-01).
/// </summary>
/// <remarks>
/// The client connects with <see cref="PipeOptions.CurrentUserOnly"/> and
/// <see cref="TokenImpersonationLevel.Identification"/> (security review S-M1): a pipe server that another
/// account created is refused, and a server can identify us but never act as us. Such a pipe is skipped and
/// reported through <see cref="Notice"/>. Behaviour against an elevated Discord is PENDING-ENV (it needs a
/// Windows machine and cannot be shown on Linux).
/// </remarks>
internal sealed class NamedPipeDiscordTransport : IDiscordTransport
{
    internal const string UntrustedPipeNotice = "A program that isn't running as you is using Discord's pipe name; skipped.";

    private readonly string _pipeNamePrefix;
    private readonly int _pipeCount;
    private readonly TimeSpan _connectTimeout;
    private readonly int _firstIndex;
    private NamedPipeClientStream? _pipe;

    public NamedPipeDiscordTransport(string pipeNamePrefix, int pipeCount, TimeSpan connectTimeout, int firstIndex = 0)
    {
        _pipeNamePrefix = pipeNamePrefix;
        _pipeCount = pipeCount;
        _connectTimeout = connectTimeout;
        _firstIndex = firstIndex;
    }

    /// <inheritdoc />
    public string? Endpoint { get; private set; }

    /// <inheritdoc />
    public int EndpointCount => _pipeCount;

    /// <inheritdoc />
    public int EndpointIndex { get; private set; }

    /// <inheritdoc />
    public string? Notice { get; private set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        if (_pipe is not null)
        {
            throw new InvalidOperationException("The transport is already connected.");
        }

        for (var index = _firstIndex; index < _pipeCount; index++)
        {
            var name = _pipeNamePrefix + index.ToString(CultureInfo.InvariantCulture);
            var pipe = CreateLocalPipe(name);
            try
            {
                await pipe.ConnectAsync(_connectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // The server of this pipe is not owned by the current user (CurrentUserOnly): never talk to it.
                Notice = UntrustedPipeNotice;
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // This pipe does not exist or is busy; the next index may be Discord's.
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _pipe = pipe;
            Endpoint = name;
            EndpointIndex = index;
            return;
        }

        throw new IOException($"No Discord IPC pipe ({_pipeNamePrefix}{_firstIndex.ToString(CultureInfo.InvariantCulture)} to {_pipeNamePrefix}{(_pipeCount - 1).ToString(CultureInfo.InvariantCulture)}) accepted a connection.");
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
        Connected().WriteAsync(frame, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        Connected().ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _pipe?.DisposeAsync() ?? ValueTask.CompletedTask;

    // The server name is the literal "." (this machine); the architecture tests rely on it, on CurrentUserOnly and on
    // Identification being literal arguments of this construction.
    private static NamedPipeClientStream CreateLocalPipe(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);

    private NamedPipeClientStream Connected() =>
        _pipe ?? throw new InvalidOperationException("The transport is not connected.");
}
