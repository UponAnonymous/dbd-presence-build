using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbdPresence.Core.Images;
using DbdPresence.Core.Online;
using DbdPresence.Online.Api;
using DbdPresence.Online.Roster;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Index;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Signing;
using DbdPresence.Packs.Sync;

namespace DbdPresence.Online.Packs;

/// <summary>Says whether online features (and so pack sync) are available right now: a current licence, a sane clock and the emergency switch off (D8).</summary>
public interface IPackSyncGate
{
    /// <summary>Raised (on any thread) after <see cref="IsOpen"/> may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets a value indicating whether sync may contact the service.</summary>
    bool IsOpen { get; }
}

/// <summary>The gate of the real app: open exactly while <see cref="AccountSnapshot.OnlineFeaturesAllowed"/> is true.</summary>
public sealed class AccountPackSyncGate : IPackSyncGate, IDisposable
{
    private readonly IAccountService _account;

    /// <summary>Initializes a new instance of the <see cref="AccountPackSyncGate"/> class.</summary>
    /// <param name="account">The account.</param>
    public AccountPackSyncGate(IAccountService account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        _account.Changed += OnAccountChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool IsOpen => _account.Current.OnlineFeaturesAllowed;

    /// <inheritdoc />
    public void Dispose() => _account.Changed -= OnAccountChanged;

    private void OnAccountChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A gate that is always open: UI tests that read packs from a local folder (no licence, no network) and unit tests.</summary>
public sealed class OpenPackSyncGate : IPackSyncGate
{
    /// <inheritdoc />
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool IsOpen => true;
}

/// <summary>What <see cref="PackSyncService"/> is built from.</summary>
/// <param name="Transport">Where the index and files come from.</param>
/// <param name="Verifier">Checks the signed index (purpose <c>pack-index</c>); shares its version store with the other verifiers.</param>
/// <param name="Cache">The verified file cache.</param>
/// <param name="Files">File operations for the sync state and thumbnails.</param>
/// <param name="Directory">The folder that holds the cache (state and thumbnails live next to it).</param>
/// <param name="Loader">Opens JPEG files in the image helper process.</param>
/// <param name="Gate">Online features gate.</param>
/// <param name="Time">The clock.</param>
/// <param name="Log">Diagnostics.</param>
/// <param name="Roster">The roster check that a check the user starts ("Check for new packs") also runs, or null.</param>
public sealed record PackSyncDependencies(
    IPackSyncTransport Transport,
    SignedDocumentVerifier Verifier,
    PackCache Cache,
    IPackFileStore Files,
    string Directory,
    ISafeImageLoader Loader,
    IPackSyncGate Gate,
    TimeProvider Time,
    IOnlineLog Log,
    IRosterSyncJob? Roster = null);

/// <summary>
/// Pack sync (FR-ONL-07, FR-PACK-03/04, PLAN_V2 §6.3). One run: fetch the signed index, verify it (purpose <c>pack-index</c>, channel
/// <c>stable</c>, not expired, not older than the version in use), parse it, work out which files are missing, download only those
/// (size cap, SHA-256 and size check, then <see cref="StrictPng"/> for PNG or the image helper for JPEG), swap the index in atomically
/// (<see cref="PackCache.CommitIndex"/>, which also records the version and removes unreferenced files). Any failure before the swap leaves
/// the last good cache, index and version floor untouched. Cached packs keep showing when the service is unreachable or the cached
/// index is past its 30 days; the Packs page says that they could not be refreshed.
/// </summary>
public sealed class PackSyncService : IPackSyncService, IDisposable
{
    /// <summary>The channel packs are published on.</summary>
    public const string Channel = "stable";

    /// <summary>The shortest time between two checks the user starts by hand.</summary>
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromMinutes(1);

    private const string StateFile = "sync-state.json";
    private const string ThumbsFolder = "thumbs";
    private const string TempSuffix = ".tmp";
    private readonly PackSyncDependencies _d;
    private readonly string _statePath;
    private readonly string _thumbsDirectory;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Lock _gate = new();
    private CachedIndex? _current;
    private SyncedPacks? _synced;
    private DateTimeOffset? _lastSuccess;
    private PackSyncOutcome? _lastOutcome;
    private DateTimeOffset? _retryAt;
    private DateTimeOffset? _manualNotBefore;
    private string? _note;
    private bool _running;

    /// <summary>Initializes a new instance of the <see cref="PackSyncService"/> class and loads the cached index, if any.</summary>
    /// <param name="dependencies">The collaborators.</param>
    public PackSyncService(PackSyncDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _d = dependencies;
        _statePath = Path.Combine(dependencies.Directory, StateFile);
        _thumbsDirectory = Path.Combine(dependencies.Directory, ThumbsFolder);
        _d.Gate.Changed += OnGateChanged;
        LoadCached();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public SyncedPacks? Synced
    {
        get
        {
            lock (_gate)
            {
                return _synced;
            }
        }
    }

    /// <inheritdoc />
    public PackSyncStatus Status
    {
        get
        {
            var now = _d.Time.GetUtcNow();
            lock (_gate)
            {
                var expired = _current is { } cached && now >= cached.Envelope.ExpiresAt;
                var note = _note ?? (expired ? PackSyncMessages.Expired : null);
                return new PackSyncStatus(_d.Gate.IsOpen, _running, _lastSuccess, _lastOutcome, expired, _retryAt is { } at && at > now ? at : null, note);
            }
        }
    }

    /// <inheritdoc />
    public async Task<PackSyncStatus> SyncAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!await _run.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Status;
        }

        try
        {
            lock (_gate)
            {
                _running = true;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            var outcome = await RunAsync(manual, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _lastOutcome = outcome.Outcome;
                _note = outcome.Note;
                _retryAt = outcome.RetryAt;
            }

            // "Check for new packs" also checks the character roster (the schedule runs it itself, PackSyncScheduler).
            if (manual && _d.Roster is { } roster && outcome.Outcome is not (PackSyncOutcome.NotAllowed or PackSyncOutcome.RateLimited))
            {
                await roster.SyncAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }

            _run.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Status;
    }

    /// <summary>Stops listening to the gate.</summary>
    public void Dispose()
    {
        _d.Gate.Changed -= OnGateChanged;
        _run.Dispose();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<RunResult> RunAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!_d.Gate.IsOpen)
        {
            return new RunResult(PackSyncOutcome.NotAllowed, null, null);
        }

        var now = _d.Time.GetUtcNow();
        if (manual && _manualNotBefore is { } notBefore && now < notBefore)
        {
            return new RunResult(PackSyncOutcome.RateLimited, PackSyncMessages.Wait(notBefore - now), notBefore);
        }

        if (manual)
        {
            _manualNotBefore = now + ManualCooldown;
        }

        var fetched = await _d.Transport.GetIndexAsync(Channel, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsOk)
        {
            return Failed(fetched.Outcome, fetched.RetryAfter, PackSyncMessages.Offline);
        }

        var documentBytes = Encoding.UTF8.GetBytes(fetched.Value!);
        var verified = _d.Verifier.Verify(documentBytes, SignedPurposes.PackIndex, Channel);
        if (!verified.IsValid)
        {
            _d.Log.Warning("Pack index refused: " + verified.Outcome);
            return new RunResult(PackSyncOutcome.IndexRefused, PackSyncMessages.IndexRefused(verified.Outcome), null);
        }

        var envelope = verified.Envelope!;
        var parsed = PackIndexParser.Parse(envelope.PayloadJson);
        if (parsed.Index is not { } index)
        {
            _d.Log.Warning("Pack index refused: its payload is not a valid index (" + parsed.Errors.Count + " problems).");
            return new RunResult(PackSyncOutcome.IndexRefused, PackSyncMessages.IndexInvalid, null);
        }

        try
        {
            return await ApplyAsync(documentBytes, envelope, index, cancellationToken).ConfigureAwait(false);
        }
        catch (PackCacheLimitException)
        {
            return new RunResult(PackSyncOutcome.CacheFull, PackSyncMessages.CacheFull, null);
        }
        catch (IOException ex)
        {
            _d.Log.Warning("Pack sync could not write its files: " + ex.GetType().Name);
            return new RunResult(PackSyncOutcome.StorageFailed, PackSyncMessages.StorageFailed, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            _d.Log.Warning("Pack sync could not write its files: " + ex.GetType().Name);
            return new RunResult(PackSyncOutcome.StorageFailed, PackSyncMessages.StorageFailed, null);
        }
    }

    private async Task<RunResult> ApplyAsync(byte[] documentBytes, SignedEnvelope envelope, PackIndex index, CancellationToken cancellationToken)
    {
        var previous = _current;
        _d.Files.CreateDirectory(_thumbsDirectory);

        // Leftovers of an earlier failed attempt are not part of the index in use or the new one: remove them first so they cannot fill the cache.
        _d.Cache.CollectGarbage(new PackIndex(index.Packs, [.. (previous?.Index.Entries ?? []).Concat(index.Entries)]));

        var missing = _d.Cache.Missing(index);
        foreach (var entry in missing)
        {
            var failure = await FetchOneAsync(entry, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }
        }

        var unchanged = previous is not null && missing.Count == 0 && previous.Document.AsSpan().SequenceEqual(documentBytes);
        if (!unchanged)
        {
            _d.Cache.CommitIndex(documentBytes, index, _d.Verifier, envelope);
            PruneThumbnails(index);
        }

        var withdrawn = previous is null ? 0 : PackCache.WithdrawnLicensed(previous.Index, index).Count;
        var now = _d.Time.GetUtcNow();
        lock (_gate)
        {
            _current = new CachedIndex(documentBytes, envelope, index);
            _synced = BuildSynced(index);
            _lastSuccess = now;
        }

        SaveState(now);
        return new RunResult(unchanged ? PackSyncOutcome.UpToDate : PackSyncOutcome.Updated, withdrawn > 0 ? PackSyncMessages.Withdrawn(withdrawn) : null, null);
    }

    /// <summary>Downloads, checks and stores one file; returns the reason to stop, or null when the file is in the cache.</summary>
    private async Task<RunResult?> FetchOneAsync(PackEntry entry, CancellationToken cancellationToken)
    {
        var result = await _d.Transport.GetFileAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!result.IsOk)
        {
            if (result.ErrorCode == ServiceClient.FileTooLargeCode)
            {
                _d.Log.Warning("A pack file was larger than the size in the index and was refused.");
                return new RunResult(PackSyncOutcome.FileRefused, PackSyncMessages.FileRefused, null);
            }

            return Failed(result.Outcome, result.RetryAfter, PackSyncMessages.Offline);
        }

        var bytes = result.Value!;
        if (bytes.Length != entry.Size || !string.Equals(Sha256Hex(bytes), entry.Sha256, StringComparison.Ordinal))
        {
            _d.Log.Warning("A pack file did not match the size or SHA-256 in the index and was refused.");
            return new RunResult(PackSyncOutcome.FileRefused, PackSyncMessages.FileRefused, null);
        }

        byte[]? thumbnail = null;
        if (entry.Format == PackImageFormat.Png)
        {
            if (!StrictPng.TryValidate(bytes, out var width, out var height, out _) || width != entry.Width || height != entry.Height)
            {
                _d.Log.Warning("A pack PNG did not pass the strict PNG check and was refused.");
                return new RunResult(PackSyncOutcome.FileRefused, PackSyncMessages.FileRefused, null);
            }
        }
        else
        {
            var loaded = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
                ? await _d.Loader.LoadBytesAsync(bytes, cancellationToken).ConfigureAwait(false)
                : SafeImageResult.Failure(SafeImageStatus.BadInput);
            if (loaded.Status != SafeImageStatus.Ok || loaded.PngBytes is not { } png)
            {
                _d.Log.Warning("A pack JPEG did not pass the image helper and was refused: " + loaded.Status);
                return new RunResult(PackSyncOutcome.FileRefused, PackSyncMessages.FileRefused, null);
            }

            thumbnail = png;
        }

        switch (_d.Cache.Add(entry, bytes))
        {
            case PackFileAddResult.Stored or PackFileAddResult.AlreadyPresent:
                break;
            case PackFileAddResult.CacheFull:
                return new RunResult(PackSyncOutcome.CacheFull, PackSyncMessages.CacheFull, null);
            default:
                return new RunResult(PackSyncOutcome.FileRefused, PackSyncMessages.FileRefused, null);
        }

        if (thumbnail is not null)
        {
            var target = ThumbnailPath(entry);
            var temp = $"{target}.{Guid.NewGuid():N}{TempSuffix}";
            _d.Files.WriteAllBytesDurable(temp, thumbnail);
            _d.Files.Move(temp, target);
        }

        return null;
    }

    private RunResult Failed(ApiOutcome outcome, TimeSpan? retryAfter, string note)
    {
        if (outcome == ApiOutcome.RateLimited)
        {
            var wait = retryAfter ?? ServiceClient.DefaultRetryAfter;
            return new RunResult(PackSyncOutcome.RateLimited, PackSyncMessages.Wait(wait), _d.Time.GetUtcNow() + wait);
        }

        return new RunResult(PackSyncOutcome.Offline, note, null);
    }

    private string ThumbnailPath(PackEntry entry) => Path.Combine(_thumbsDirectory, entry.Sha256 + ".png");

    private void PruneThumbnails(PackIndex index)
    {
        var wanted = index.Entries.Where(e => e.Format == PackImageFormat.Jpeg).Select(e => e.Sha256 + ".png").ToHashSet(StringComparer.Ordinal);
        foreach (var file in _d.Files.ListFiles(_thumbsDirectory))
        {
            var name = Path.GetFileName(file);
            if (name.Length == 68 && name.EndsWith(".png", StringComparison.Ordinal) && !wanted.Contains(name))
            {
                _d.Files.Delete(file);
            }
        }
    }

    private SyncedPacks BuildSynced(PackIndex index) =>
        new(index, _d.Cache, entry => _d.Transport.PublicUrlOf(entry), null, LocalPathOf);

    private string? LocalPathOf(PackEntry entry)
    {
        if (!_d.Cache.Contains(entry))
        {
            return null;
        }

        if (entry.Format == PackImageFormat.Png)
        {
            return _d.Cache.PathOf(entry);
        }

        var thumbnail = ThumbnailPath(entry);
        return _d.Files.Exists(thumbnail) ? thumbnail : null;
    }

    /// <summary>Startup: removes interrupted writes, restores the rollback floor from the cached index and shows the cached packs.</summary>
    private void LoadCached()
    {
        try
        {
            _d.Files.CreateDirectory(_d.Directory);
            _d.Cache.RemoveStaleTempFiles();
            var seeded = _d.Cache.SeedVersionFloor(_d.Verifier, Channel);
            if (seeded is { } outcome && outcome != VerificationOutcome.Valid)
            {
                _d.Log.Warning("The cached pack index was ignored: " + outcome);
                return;
            }

            if (_d.Cache.ReadIndexDocument() is { } document)
            {
                var verified = _d.Verifier.VerifyStored(document, SignedPurposes.PackIndex, Channel);
                if (verified.Envelope is { } envelope && verified.IsValid && PackIndexParser.Parse(envelope.PayloadJson).Index is { } index)
                {
                    _current = new CachedIndex(document, envelope, index);
                    _synced = BuildSynced(index);
                }
                else
                {
                    _d.Log.Warning("The cached pack index was ignored: " + verified.Outcome);
                }
            }

            _lastSuccess = ReadState();
        }
        catch (IOException ex)
        {
            _d.Log.Warning("The cached packs could not be read: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _d.Log.Warning("The cached packs could not be read: " + ex.GetType().Name);
        }
    }

    private DateTimeOffset? ReadState()
    {
        if (!_d.Files.Exists(_statePath) || _d.Files.Length(_statePath) > 4096)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(_d.Files.ReadAllBytes(_statePath));
            return json.RootElement.TryGetProperty("lastSuccessAt", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException)
        {
            _d.Log.Warning("The pack sync state file was ignored because it is not valid JSON.");
            return null;
        }
    }

    private void SaveState(DateTimeOffset lastSuccess)
    {
        var text = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["lastSuccessAt"] = lastSuccess.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        });
        var temp = $"{_statePath}.{Guid.NewGuid():N}{TempSuffix}";
        try
        {
            _d.Files.WriteAllBytesDurable(temp, Encoding.UTF8.GetBytes(text));
            _d.Files.Move(temp, _statePath);
        }
        catch (IOException ex)
        {
            // Only the "last updated" line is affected; the packs themselves were already swapped in.
            _d.Log.Warning("The pack sync state could not be saved: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _d.Log.Warning("The pack sync state could not be saved: " + ex.GetType().Name);
        }
    }

    private void OnGateChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record CachedIndex(byte[] Document, SignedEnvelope Envelope, PackIndex Index);

    private sealed record RunResult(PackSyncOutcome Outcome, string? Note, DateTimeOffset? RetryAt);
}

/// <summary>The sentences the Packs page shows about syncing.</summary>
internal static class PackSyncMessages
{
    public const string Offline = "Could not reach the pack service. The packs already on this PC keep working.";
    public const string IndexInvalid = "The pack list from the service was not valid, so nothing changed.";
    public const string FileRefused = "A pack image did not pass the safety checks, so nothing changed.";
    public const string CacheFull = "The packs need more room than the pack cache allows, so nothing changed.";
    public const string StorageFailed = "The packs could not be saved on this PC. Check free disk space and try again.";
    public const string Expired = "The pack list on this PC is older than 30 days and could not be refreshed. The packs you have keep working.";

    public static string IndexRefused(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.RolledBack => "The service offered an older pack list than the one in use, so it was ignored.",
        VerificationOutcome.WrongChannel => "The service answered with a pack list for another channel, so it was ignored.",
        VerificationOutcome.Expired => "The service's pack list has expired, so it was ignored.",
        _ => "The pack list from the service could not be verified, so nothing changed.",
    };

    public static string Wait(TimeSpan wait)
    {
        if (wait < TimeSpan.FromMinutes(2))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            return string.Create(CultureInfo.InvariantCulture, $"The pack service asked to wait. Try again in {seconds} second{(seconds == 1 ? string.Empty : "s")}.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"The pack service asked to wait. Try again in {(int)Math.Ceiling(wait.TotalMinutes)} minutes.");
    }

    public static string Withdrawn(int count) => string.Create(
        CultureInfo.InvariantCulture, $"{count} licensed image{(count == 1 ? " was" : "s were")} withdrawn by the owner and no longer shown.");
}
