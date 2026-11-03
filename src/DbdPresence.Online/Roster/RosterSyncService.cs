using System.Text;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Packs;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Roster;
using DbdPresence.Packs.Signing;

namespace DbdPresence.Online.Roster;

/// <summary>Where the roster sync gets the signed roster from (the service; nothing in UI tests).</summary>
public interface IRosterTransport
{
    /// <summary>Fetches the signed roster document text (unverified).</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document text.</returns>
    Task<ApiResult<string>> GetRosterAsync(string channel, CancellationToken cancellationToken);
}

/// <summary>A roster check that pack sync runs with its own (at start, every 6 hours, on "Check for new packs").</summary>
public interface IRosterSyncJob
{
    /// <summary>Checks for a new roster now. Never throws for network, content or disk problems.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome.</returns>
    Task<RosterSyncOutcome> SyncAsync(CancellationToken cancellationToken);
}

/// <summary>The real transport: <c>GET /v1/roster</c> on the compiled-in API host.</summary>
/// <param name="api">The service client (shares its 429 backoff).</param>
public sealed class HttpRosterTransport(ServiceClient api) : IRosterTransport
{
    /// <inheritdoc />
    public Task<ApiResult<string>> GetRosterAsync(string channel, CancellationToken cancellationToken) => api.GetRosterAsync(channel, cancellationToken);
}

/// <summary>
/// The transport of the UI-test build, which replaces the network with a local folder for packs: it has no roster, so the app keeps the
/// embedded vocabulary (the same as an app that has not reached the service yet).
/// </summary>
public sealed class NoRosterTransport : IRosterTransport
{
    /// <inheritdoc />
    public Task<ApiResult<string>> GetRosterAsync(string channel, CancellationToken cancellationToken) =>
        Task.FromResult(new ApiResult<string>(ApiOutcome.Unreachable, null, null, null, null));
}

/// <summary>What <see cref="RosterSyncService"/> is built from.</summary>
/// <param name="Transport">Where the document comes from.</param>
/// <param name="Verifier">Checks the signed roster (purpose <c>roster</c>); shares its version store with the other verifiers.</param>
/// <param name="Files">File operations for the cached document.</param>
/// <param name="Path">The cached document's file.</param>
/// <param name="Embedded">The roster built from the embedded vocabulary (the append-only baseline).</param>
/// <param name="Gate">Online features gate.</param>
/// <param name="Time">The clock.</param>
/// <param name="Log">Diagnostics.</param>
public sealed record RosterSyncDependencies(
    IRosterTransport Transport,
    SignedDocumentVerifier Verifier,
    IPackFileStore Files,
    string Path,
    CharacterRoster Embedded,
    IPackSyncGate Gate,
    TimeProvider Time,
    IOnlineLog Log);

/// <summary>
/// Roster sync (PLAN_V2_S5B §2.3–2.4, FR-ROS-04, S5b-4): fetch the signed roster, verify it first (purpose <c>roster</c>, channel
/// <c>stable</c>, not expired, not older than the version in use), parse it, check the append-only rules against the embedded vocabulary
/// and the last good roster, merge it through the composition root's <see cref="IRosterConsumer"/>, write the cache, and only then record
/// the version. Any failure keeps the last good roster, its cache and its version. After expiry the last good roster stays in use with the
/// "could not refresh" note; nothing is removed, because pictures depend on the ids. It runs with pack sync: at start, every 6 hours, when
/// online features become available, and on "Check for new packs".
/// </summary>
public sealed class RosterSyncService : IRosterSource, IRosterSyncJob, IDisposable
{
    /// <summary>The channel the roster is published on.</summary>
    public const string Channel = "stable";

    private const string TempSuffix = ".tmp";
    private readonly RosterSyncDependencies _d;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Lock _gate = new();
    private RosterDocument? _current;
    private SignedEnvelope? _envelope;
    private IRosterConsumer? _consumer;
    private DateTimeOffset? _lastSuccess;
    private RosterSyncOutcome? _lastOutcome;
    private string? _note;

    /// <summary>Initializes a new instance of the <see cref="RosterSyncService"/> class and loads the cached roster, if any.</summary>
    /// <param name="dependencies">The collaborators.</param>
    public RosterSyncService(RosterSyncDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _d = dependencies;
        LoadCached();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Releases the run lock.</summary>
    public void Dispose() => _run.Dispose();

    /// <inheritdoc />
    public RosterDocument? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public RosterSyncStatus Status
    {
        get
        {
            var now = _d.Time.GetUtcNow();
            lock (_gate)
            {
                var expired = _envelope is { } envelope && now >= envelope.ExpiresAt;
                return new RosterSyncStatus(_lastSuccess, _lastOutcome, expired, _note ?? (expired ? RosterMessages.Expired : null));
            }
        }
    }

    /// <inheritdoc />
    public void Attach(IRosterConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        RosterDocument? current;
        lock (_gate)
        {
            _consumer = consumer;
            current = _current;
        }

        if (current is not null && !TryMerge(consumer, current))
        {
            // The cached roster no longer merges (for example after a vocabulary change in a release): the embedded vocabulary stays.
            lock (_gate)
            {
                _current = null;
                _note = RosterMessages.Refused;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Checks for a new roster now. Never throws for network, content or disk problems: they come back as an outcome.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome.</returns>
    public async Task<RosterSyncOutcome> SyncAsync(CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        RosterSyncOutcome outcome;
        try
        {
            outcome = await RunAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _lastOutcome = outcome;
                if (outcome is RosterSyncOutcome.Updated or RosterSyncOutcome.UpToDate)
                {
                    _lastSuccess = _d.Time.GetUtcNow();
                    _note = null;
                }
                else if (outcome != RosterSyncOutcome.NotAllowed)
                {
                    _note = outcome switch
                    {
                        RosterSyncOutcome.Refused => RosterMessages.Refused,
                        RosterSyncOutcome.StorageFailed => RosterMessages.StorageFailed,
                        _ => RosterMessages.Offline,
                    };
                }
            }
        }
        finally
        {
            _run.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    private async Task<RosterSyncOutcome> RunAsync(CancellationToken cancellationToken)
    {
        if (!_d.Gate.IsOpen)
        {
            return RosterSyncOutcome.NotAllowed;
        }

        var fetched = await _d.Transport.GetRosterAsync(Channel, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsOk)
        {
            return fetched.Outcome == ApiOutcome.RateLimited ? RosterSyncOutcome.RateLimited : RosterSyncOutcome.Offline;
        }

        var bytes = Encoding.UTF8.GetBytes(fetched.Value!);
        var verified = _d.Verifier.Verify(bytes, SignedPurposes.Roster, Channel);
        if (!verified.IsValid)
        {
            _d.Log.Warning("Roster refused: " + verified.Outcome);
            return RosterSyncOutcome.Refused;
        }

        RosterDocument? previous;
        IRosterConsumer? consumer;
        SignedEnvelope? previousEnvelope;
        lock (_gate)
        {
            previous = _current;
            consumer = _consumer;
            previousEnvelope = _envelope;
        }

        var envelope = verified.Envelope!;
        if (previous is not null && previousEnvelope is not null && string.Equals(previousEnvelope.PayloadJson, envelope.PayloadJson, StringComparison.Ordinal))
        {
            // The same content (each answer carries a fresh signature). A weekly re-sign extends its life: keep the newer copy.
            if (envelope.ExpiresAt > previousEnvelope.ExpiresAt)
            {
                if (!Save(bytes))
                {
                    return RosterSyncOutcome.StorageFailed;
                }

                _d.Verifier.CommitVersion(envelope);
                lock (_gate)
                {
                    _envelope = envelope;
                }
            }

            return RosterSyncOutcome.UpToDate;
        }

        if (Accept(envelope, previous) is not { } roster)
        {
            return RosterSyncOutcome.Refused;
        }

        if (consumer is not null && !TryMerge(consumer, roster))
        {
            // A consumer may have taken the roster before another one refused it: put the last good roster back so the page and detection agree.
            RestorePrevious(consumer, previous);
            return RosterSyncOutcome.Refused;
        }

        if (!Save(bytes))
        {
            RestorePrevious(consumer, previous);
            return RosterSyncOutcome.StorageFailed;
        }

        // Only now, after the merge and the cache write, is the version recorded (PLAN_V2_S5B §2.3).
        _d.Verifier.CommitVersion(envelope);
        lock (_gate)
        {
            _current = roster;
            _envelope = envelope;
        }

        return RosterSyncOutcome.Updated;
    }

    /// <summary>Replaces the cached document atomically; false (logged) when the disk refused.</summary>
    private bool Save(byte[] bytes)
    {
        try
        {
            var temp = $"{_d.Path}.{Guid.NewGuid():N}{TempSuffix}";
            _d.Files.WriteAllBytesDurable(temp, bytes);
            _d.Files.Move(temp, _d.Path);
            return true;
        }
        catch (IOException ex)
        {
            _d.Log.Warning("The roster could not be saved: " + ex.GetType().Name);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _d.Log.Warning("The roster could not be saved: " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Parses the payload and applies the append-only rules; null (logged) when either refuses it.</summary>
    private RosterDocument? Accept(SignedEnvelope envelope, RosterDocument? previous)
    {
        var parsed = RosterParser.Parse(envelope.PayloadJson);
        if (parsed.Roster is not { } roster)
        {
            _d.Log.Warning("Roster refused: its payload is not a valid roster (" + parsed.Errors.Count + " problems).");
            return null;
        }

        var broken = RosterParser.CheckAppendOnly(roster, _d.Embedded, previous);
        if (broken.Count > 0)
        {
            _d.Log.Warning("Roster refused: it drops or changes characters the app knows (" + broken.Count + " problems).");
            return null;
        }

        return roster;
    }

    private bool TryMerge(IRosterConsumer consumer, RosterDocument roster)
    {
        try
        {
            consumer.Apply(roster);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _d.Log.Warning("Roster refused: it does not merge with the vocabulary (" + ex.GetType().Name + ").");
            return false;
        }
    }

    /// <summary>The new roster was merged but not saved: the consumer goes back to the last good one, so memory and disk agree.</summary>
    private void RestorePrevious(IRosterConsumer? consumer, RosterDocument? previous)
    {
        if (consumer is not null && previous is not null && !TryMerge(consumer, previous))
        {
            lock (_gate)
            {
                _current = null;
            }
        }
    }

    /// <summary>Startup: raises the rollback floor from the cached document and uses it (even when expired) as the last good roster.</summary>
    private void LoadCached()
    {
        try
        {
            if (!_d.Files.Exists(_d.Path))
            {
                return;
            }

            var bytes = _d.Files.ReadAllBytes(_d.Path);
            var seeded = _d.Verifier.SeedFromCachedDocument(bytes, SignedPurposes.Roster, Channel);
            var verified = _d.Verifier.VerifyStored(bytes, SignedPurposes.Roster, Channel);
            if (seeded is not (VerificationOutcome.Valid or VerificationOutcome.Expired) || verified.Envelope is not { } envelope || !verified.IsValid)
            {
                _d.Log.Warning("The cached roster was ignored: " + verified.Outcome);
                return;
            }

            if (Accept(envelope, null) is { } roster)
            {
                _current = roster;
                _envelope = envelope;
            }
        }
        catch (IOException ex)
        {
            _d.Log.Warning("The cached roster could not be read: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _d.Log.Warning("The cached roster could not be read: " + ex.GetType().Name);
        }
    }
}

/// <summary>The sentences the Characters page shows about the roster.</summary>
internal static class RosterMessages
{
    public const string Offline = "Could not reach the service to refresh the character list. The characters on this PC keep working.";
    public const string Refused = "The character list from the service did not pass the safety checks, so the list on this PC stays.";
    public const string StorageFailed = "The character list could not be saved on this PC. Check free disk space.";
    public const string Expired = "The character list on this PC is older than 30 days and could not be refreshed. The characters you have keep working.";
}
