using DbdPresence.Core.Online;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Presentation.Characters;

/// <summary>
/// The picture environment of the running app: what the account and the saved-online list say, read from the services' cached views (FR-ONL-09).
/// Reading it never starts a network call. It carries
/// the owner's default pack (<c>default</c> flag) and each owner entry's <c>sourceSha256</c>, read from the synced index; with no sync, or no pack flagged
/// default, there is no default portrait (the page says so; nothing is guessed). The storage meter's use and limit are what <c>GET /v1/me</c>
/// last reported (D28, FR-PACK-17); before it reports, the use is the sum of the listed uploads and the limit is the default 60 MB per person.
/// Each own picture's saving state comes from the saving-online queue's view in the same upload list (S5b-6).
/// </summary>
public sealed class OnlinePictureEnvironment : IPictureEnvironment, IDisposable
{
    private readonly IAccountService _account;
    private readonly IUploadService _uploads;
    private readonly PackLibrary _library;

    /// <summary>Initializes a new instance of the <see cref="OnlinePictureEnvironment"/> class.</summary>
    /// <param name="account">The account.</param>
    /// <param name="uploads">The pictures saved online.</param>
    /// <param name="library">The pack library, which holds the synced index.</param>
    public OnlinePictureEnvironment(IAccountService account, IUploadService uploads, PackLibrary library)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _account = account;
        _uploads = uploads;
        _account.Changed += OnSourceChanged;
        _uploads.Changed += OnSourceChanged;
        _library.SyncedChanged += OnSourceChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool SignedIn => _account.Current.OnlineFeaturesAllowed;

    /// <inheritdoc />
    public bool UploadsBlocked => _uploads.Current.BlockedReason is not null;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> SavedUrlBySha256 => _uploads.Current.UrlBySha256;

    /// <inheritdoc />
    public long UsedBytes => _uploads.Current.UsedBytes;

    /// <inheritdoc />
    public long LimitBytes => _uploads.Current.Usage?.LimitBytes ?? PictureLimits.OnlineQuotaBytes;

    /// <inheritdoc />
    public string? DefaultPackId => _library.Synced?.Index.Packs.FirstOrDefault(p => p.Default)?.Id;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? SourceShaByEntryId => _library.Synced is { } synced
        ? synced.Index.Entries.Where(e => e.SourceSha256 is not null).ToDictionary(e => e.Id, e => e.SourceSha256!, StringComparer.Ordinal)
        : null;

    /// <inheritdoc />
    public PictureOnlineStatus? SavingStatusOf(string sha256) => _uploads.Current.Saving is { } saving && saving.TryGetValue(sha256, out var state)
        ? StatusOf(state)
        : null;

    /// <summary>The page's status for a saving-online state.</summary>
    /// <param name="state">The queue's state.</param>
    /// <returns>The status.</returns>
    public static PictureOnlineStatus StatusOf(PictureSaveState state) => state switch
    {
        PictureSaveState.StorageFull => PictureOnlineStatus.StorageFull,
        PictureSaveState.Paused => PictureOnlineStatus.Paused,
        PictureSaveState.Blocked => PictureOnlineStatus.Blocked,
        PictureSaveState.DailyLimit => PictureOnlineStatus.DailyLimit,
        PictureSaveState.Failed => PictureOnlineStatus.Failed,
        PictureSaveState.Offline => PictureOnlineStatus.ServiceUnreachable,
        _ => PictureOnlineStatus.Saving,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _account.Changed -= OnSourceChanged;
        _uploads.Changed -= OnSourceChanged;
        _library.SyncedChanged -= OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
