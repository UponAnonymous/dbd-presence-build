using DbdPresence.Packs.Local;

namespace DbdPresence.Packs.Sync;

/// <summary>How one pack sync ended (FR-ONL-07). Every outcome other than <see cref="Updated"/> and <see cref="UpToDate"/> keeps the last good cache.</summary>
public enum PackSyncOutcome
{
    /// <summary>A new index was verified, its files were downloaded and checked, and the cache was swapped.</summary>
    Updated,

    /// <summary>The service's index is the one already cached and every file is present.</summary>
    UpToDate,

    /// <summary>Online features are not available (not signed in, no current licence, the emergency switch is on, or the clock is behind). Nothing was contacted.</summary>
    NotAllowed,

    /// <summary>Another sync is already running.</summary>
    AlreadyRunning,

    /// <summary>The service or the CDN could not be reached, or answered with an error.</summary>
    Offline,

    /// <summary>The service asked the app to wait (429 with <c>Retry-After</c>); see <see cref="PackSyncStatus.RetryAt"/>.</summary>
    RateLimited,

    /// <summary>The index was refused: bad signature, unknown key, wrong purpose or channel, expired, an older version than the one in use, or not a valid index.</summary>
    IndexRefused,

    /// <summary>A file was refused: wrong size or hash, too large, or not a clean PNG or JPEG.</summary>
    FileRefused,

    /// <summary>The packs need more room than the 200 MB cache limit allows.</summary>
    CacheFull,

    /// <summary>A local file operation failed (disk full, folder not writable).</summary>
    StorageFailed,
}

/// <summary>What the Packs page shows about syncing.</summary>
/// <param name="CanSync">True when online features are available, so a check can run. Without it the page shows no check button.</param>
/// <param name="IsRunning">True while a sync runs.</param>
/// <param name="LastSuccessAt">When the packs were last checked successfully, or null if never.</param>
/// <param name="LastOutcome">How the latest attempt ended, or null before the first attempt.</param>
/// <param name="IndexExpired">True when the cached index is past its 30-day lifetime: the cached packs keep showing, with a "could not refresh" note.</param>
/// <param name="RetryAt">When a rate-limited or throttled route may be tried again, or null.</param>
/// <param name="Note">A plain-language sentence about the latest attempt, or null when there is nothing to say.</param>
public sealed record PackSyncStatus(
    bool CanSync,
    bool IsRunning,
    DateTimeOffset? LastSuccessAt,
    PackSyncOutcome? LastOutcome,
    bool IndexExpired,
    DateTimeOffset? RetryAt,
    string? Note)
{
    /// <summary>Gets the status of an app that has no sync service.</summary>
    public static PackSyncStatus Unavailable { get; } = new(false, false, null, null, false, null, null);

    /// <summary>Gets a value indicating whether the cached packs are older than the service's latest (the last attempt did not succeed or the index expired).</summary>
    public bool CouldNotRefresh => IndexExpired
        || LastOutcome is not (null or PackSyncOutcome.Updated or PackSyncOutcome.UpToDate or PackSyncOutcome.NotAllowed or PackSyncOutcome.AlreadyRunning);
}

/// <summary>The synced packs as the Packs page and the composition root use them (implemented by <c>DbdPresence.Online</c>; Presentation never references that assembly).</summary>
public interface IPackSyncService
{
    /// <summary>Raised (on any thread) after <see cref="Status"/> or <see cref="Synced"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current status.</summary>
    PackSyncStatus Status { get; }

    /// <summary>Gets the packs in the verified cache, or null when none were ever synced.</summary>
    SyncedPacks? Synced { get; }

    /// <summary>Checks for new packs now. Never throws for network, content or disk problems: they come back as an outcome.</summary>
    /// <param name="manual">True when the user asked; a manual check may not run more often than once a minute.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The status after the attempt.</returns>
    Task<PackSyncStatus> SyncAsync(bool manual, CancellationToken cancellationToken);
}
