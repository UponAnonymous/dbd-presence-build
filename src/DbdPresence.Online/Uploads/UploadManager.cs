using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Images;
using DbdPresence.Core.Online;
using DbdPresence.Core.Pictures;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Online.Uploads;

/// <summary>User-facing messages for uploads (plain language; never tokens or addresses).</summary>
public static class UploadMessages
{
    public const string PolicyRequired = "Read and accept the upload policy first.";
    public const string NotPng = "Only images the app converted to a clean PNG can be uploaded. Choose the image again as your own picture for the slot.";
    public const string TooLarge = "That image is larger than 2 MB or 1024 by 1024 pixels, so it cannot be uploaded. Choose a simpler picture.";
    public const string Uploaded = "Image uploaded. Discord shows it at an unlisted address.";
    public const string AlreadyUploaded = "This image was already uploaded.";
    public const string Deleted = "Upload deleted. " + UploadPolicy.DiscordCache;
    public const string DeletedFilePending = "Upload deleted: its address no longer shows the image, and the service deletes the stored copy later. " + UploadPolicy.DiscordCache;
    public const string AnswerMismatch = "The service's answer did not match the image that was sent, so it was not used. Try again later.";
    public const string NotFound = "That upload no longer exists on the service.";
    public const string Blocked = "Uploads are turned off for your account by the service owner.";
    public const string OnlineOff = "Online features are paused by the service owner. Your Discord status keeps working.";
    public const string NeedsInvite = "Enter an invite code on the Account page before uploading.";
    public const string StorageFull = "The service has no room for new images right now. Try again later.";
    public const string Refreshed = "Uploads are up to date.";

    /// <summary>The queue's sentence while saving is paused for everyone (the service's 6 GB storage stop, D18).</summary>
    public const string SavingPaused = "Saving pictures online is paused for everyone for now. Pictures already online keep working; the rest stay in your preview and are saved when it's back.";

    /// <summary>The queue's sentence while the owner turned saving off for this account or for everyone.</summary>
    public const string SavingBlocked = "The service owner turned off saving pictures online. Pictures already online keep working; the rest stay in your preview.";

    /// <summary>The queue's sentence while today's saving limit is reached.</summary>
    public const string SavingTomorrow = "Today's limit for saving pictures online is reached. Saving resumes tomorrow.";

    /// <summary>The queue's sentence while the service cannot be reached.</summary>
    public const string SavingOffline = "The service can't be reached, so pictures wait to be saved online. The app tries again later.";

    /// <summary>The queue's sentence after the account was withdrawn.</summary>
    public const string SavingRevoked = "Your account was withdrawn by the service owner, so pictures are not saved online.";

    /// <summary>The message for a limit reached.</summary>
    /// <param name="limit">The per-user limit.</param>
    /// <returns>The message.</returns>
    public static string LimitReached(int limit) => $"You have reached the limit of {limit} uploaded images. Delete one before uploading another.";

    /// <summary>The message for the per-person online quota reached (D28, 409 <c>upload_limit</c>).</summary>
    /// <param name="usage">The quota as last reported, or null when unknown.</param>
    /// <param name="limit">Which cap the service named: <c>bytes</c> or <c>files</c> (null when not named).</param>
    /// <returns>The message.</returns>
    public static string QuotaFull(UploadUsage? usage, string? limit) => limit == "files" && usage is not null
        ? $"Your online storage holds at most {usage.LimitFiles.ToString(CultureInfo.InvariantCulture)} pictures and it's full. Pictures not saved yet stay in your preview; remove pictures you don't use and the rest are saved."
        : usage is not null
            ? $"Your online storage is full ({Megabytes(usage.UsedBytes)} of {Megabytes(usage.LimitBytes)} MB used). Pictures not saved yet stay in your preview; remove pictures you don't use and the rest are saved."
            : "Your online storage is full. Pictures not saved yet stay in your preview; remove pictures you don't use and the rest are saved.";

    /// <summary>Megabytes with one decimal (MB = 1 048 576 bytes, as in the 2 MB and 60 MB rules).</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The text, for example <c>12.4</c>.</returns>
    public static string Megabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The message for a file the service refused.</summary>
    /// <param name="reason">The service's reason code.</param>
    /// <returns>The message.</returns>
    public static string Refused(string? reason) => reason switch
    {
        "too_large" or "dimensions_out_of_range" => TooLarge,
        "metadata_not_allowed" or "chunk_not_allowed" or "marker_not_allowed" or "animation_not_allowed" => "The service refused the image because it carries extra data (metadata, animation or unknown parts).",
        _ => "The service refused the image because it is not a plain PNG or JPEG file.",
    };
}

/// <summary>
/// Uploads (FR-ONL-09, FR-ONL-11, D4, D12): the policy acceptance, the user's list as last read from the service (stored on this PC so presence
/// keeps using uploaded images while offline), upload and delete. Every call goes through <see cref="AccountManager.CallAuthorizedAsync{T}"/>:
/// a current licence is needed, and only the allow-listed API host is contacted (ADR-0016). Images are checked here before they leave the PC: a
/// strict PNG (<see cref="StrictPng"/>) of at most 1024 by 1024 pixels and 2 MB, as the image helper produces for the user's own pictures.
/// Signing out or deleting the account forgets the list and the policy acceptance.
/// </summary>
public sealed class UploadManager : IUploadService, IDisposable
{
    /// <summary>The largest file uploaded (the service's cap).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>The largest width or height uploaded.</summary>
    public const int MaxDimension = 1024;

    private readonly AccountManager _account;
    private readonly ServiceClient _api;
    private readonly IPackFileStore _files;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly IOnlineLog _log;
    /// <summary>The <see cref="UploadResult.Code"/> of a file refused on this PC before anything was sent.</summary>
    internal const string LocalRefusal = "local_refused";

    /// <summary>The <see cref="UploadResult.Code"/> of a delete refused on this PC because the id is not an upload id.</summary>
    internal const string InvalidId = "invalid_id";

    private static readonly Dictionary<string, PictureSaveState> s_noSaving = [];

    private readonly Lock _gate = new();
    private UploadsSnapshot _snapshot = UploadsSnapshot.Empty;
    private DateTimeOffset? _policyAcceptedAt;
    private PictureUploader? _queue;

    /// <summary>Initializes a new instance of the <see cref="UploadManager"/> class and loads the stored list.</summary>
    /// <param name="account">The account (session, licence, revocation handling).</param>
    /// <param name="api">The service client.</param>
    /// <param name="files">File access.</param>
    /// <param name="path">Where the list and the policy acceptance are stored.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">Diagnostics.</param>
    public UploadManager(AccountManager account, ServiceClient api, IPackFileStore files, string path, TimeProvider time, IOnlineLog log)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _account = account;
        _api = api;
        _files = files;
        _path = path;
        _time = time;
        _log = log;
        Load();
        _account.Changed += OnAccountChanged;
        OnAccountChanged(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    /// Raised after a refresh whose answer said whether this account is the owner's game account (<c>user.rosterReporter</c>), so the
    /// unknown-character reporter needs no call of its own and keeps nothing for other accounts.
    /// </summary>
    public event EventHandler<bool>? RosterReporterObserved;

    /// <summary>Gets the content host whose upload addresses are accepted (this build's environment), or null.</summary>
    public string? UploadHost => _api.UploadHost;

    /// <inheritdoc />
    public UploadsSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <inheritdoc />
    public void AcceptPolicy()
    {
        lock (_gate)
        {
            _policyAcceptedAt = _time.GetUtcNow();
            _snapshot = _snapshot with { PolicyAccepted = true };
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<UploadResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var (result, failure) = await _account.CallAuthorizedAsync((token, ct) => _api.GetUploadsAsync(token, ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return FromAccount(failure);
        }

        if (!result!.IsOk)
        {
            return Interpret(result.Outcome, result.ErrorCode, result.ErrorReason, result.RetryAfter);
        }

        Replace(result.Value!.Items, result.Value.Limit, result.Value.BlockedReason, result.Value.Usage);
        if (result.Value.RosterReporter is { } reporter)
        {
            RosterReporterObserved?.Invoke(this, reporter);
        }

        return new UploadResult(AccountFailure.None, UploadMessages.Refreshed);
    }

    /// <inheritdoc />
    public Task<UploadResult> UploadAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken) =>
        Current.PolicyAccepted
            ? UploadCheckedAsync(png, cancellationToken)
            : Task.FromResult(new UploadResult(AccountFailure.Rejected, UploadMessages.PolicyRequired));

    /// <summary>
    /// Starts the saving-online queue for the Characters page's own pictures (S5b-6, FR-PACK-15) and keeps it until this manager is disposed. The
    /// queue keeps its work next to the upload list and reads the picture files from <paramref name="pictures"/>.
    /// </summary>
    /// <param name="pictures">The picture store that holds the own pictures' files.</param>
    /// <param name="rules">Reads whether the user accepted the picture rules; nothing is uploaded unless they did.</param>
    /// <returns>The queue, to hand to the Characters page as its <see cref="IPictureUploadSink"/>.</returns>
    public PictureUploader StartPictureQueue(PictureStore pictures, Func<PictureRules> rules)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            if (_queue is not null)
            {
                return _queue;
            }
        }

        var directory = Path.GetDirectoryName(_path);
        var queue = new PictureUploader(new PictureUploaderDependencies(
            this, _account, PictureUploader.StoreReader(pictures), rules, _files, string.IsNullOrEmpty(directory) ? "picture-queue.json" : Path.Combine(directory, "picture-queue.json"), _time, _log));
        lock (_gate)
        {
            _queue = queue;
        }

        queue.Start();
        return queue;
    }

    /// <summary>
    /// Uploads one own picture for the saving-online queue. The queue's own gate (signed in, picture rules accepted) replaces the older upload
    /// policy acceptance of the Account page; every file check is the same as <see cref="UploadAsync"/>.
    /// </summary>
    /// <param name="png">The PNG bytes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    internal Task<UploadResult> UploadPictureAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken) => UploadCheckedAsync(png, cancellationToken);

    /// <summary>
    /// Deletes one upload by id even when the list on this PC does not hold it (the queue's deletes, which survive a restart). An answer of
    /// <c>not_found</c> counts as deleted: it is gone, or it was never this account's.
    /// </summary>
    /// <param name="id">The upload id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome.</returns>
    internal async Task<UploadResult> DeleteByIdAsync(string id, CancellationToken cancellationToken)
    {
        if (!UploadPolicy.IsUploadId(id))
        {
            // Never sent: only an id the service issued (a lower-case UUID) names an upload.
            return new UploadResult(AccountFailure.Rejected, UploadMessages.NotFound, Code: InvalidId);
        }

        var (result, failure) = await _account.CallAuthorizedAsync((token, ct) => _api.DeleteUploadAsync(token, id, ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return FromAccount(failure);
        }

        if ((result!.IsOk && result.Value is { Deleted: true }) || (result.Outcome == ApiOutcome.Rejected && result.ErrorCode == "not_found"))
        {
            Forget(id);
            return new UploadResult(AccountFailure.None, !result.IsOk ? UploadMessages.NotFound : result.Value!.FilesRemoved ? UploadMessages.Deleted : UploadMessages.DeletedFilePending);
        }

        return Interpret(result.Outcome, result.ErrorCode, result.ErrorReason, result.RetryAfter);
    }

    /// <summary>Publishes the saving-online queue's view (by SHA-256) into <see cref="Current"/>.</summary>
    /// <param name="saving">The pictures waiting and why.</param>
    /// <param name="message">The queue's sentence, or null.</param>
    internal void SetSaving(IReadOnlyDictionary<string, PictureSaveState> saving, string? message)
    {
        lock (_gate)
        {
            var same = _snapshot.SavingMessage == message
                && (_snapshot.Saving ?? s_noSaving).Count == saving.Count
                && saving.All(p => _snapshot.Saving!.TryGetValue(p.Key, out var v) && v == p.Value);
            if (same)
            {
                return;
            }

            _snapshot = _snapshot with { Saving = saving.Count == 0 ? null : saving, SavingMessage = message };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<UploadResult> UploadCheckedAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken)
    {
        // Size first, then the header's dimensions, so an oversized picture is named as such; then the full strict walk.
        if (png.Length > MaxBytes || (PngHeader.TryReadSize(png.Span, out var w, out var h) && (w > MaxDimension || h > MaxDimension)))
        {
            return new UploadResult(AccountFailure.Rejected, UploadMessages.TooLarge, Code: LocalRefusal);
        }

        if (!StrictPng.TryValidate(png.Span, out _, out _, out _))
        {
            return new UploadResult(AccountFailure.Rejected, UploadMessages.NotPng, Code: LocalRefusal);
        }

        var (result, failure) = await _account.CallAuthorizedAsync((token, ct) => _api.UploadAsync(token, png, "image/png", ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return FromAccount(failure);
        }

        if (!result!.IsOk)
        {
            return Interpret(result.Outcome, result.ErrorCode, result.ErrorReason, result.RetryAfter);
        }

        var grant = result.Value!;
        // The service must answer with the hash of exactly the bytes sent (an upload address is also checked by ServiceClient).
        if (!string.Equals(grant.Upload.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png.Span)), StringComparison.Ordinal))
        {
            _log.Warning("Upload answer refused: the hash does not match the bytes sent.");
            return new UploadResult(AccountFailure.Rejected, UploadMessages.AnswerMismatch);
        }

        lock (_gate)
        {
            var known = _snapshot.Items.Any(i => i.Id == grant.Upload.Id);
            var others = _snapshot.Items.Where(i => i.Id != grant.Upload.Id);
            // The quota as the service counts it: a new file adds its bytes; the same bytes again add nothing.
            var usage = _snapshot.Usage is { } u && !known && !grant.Existing ? u with { UsedBytes = u.UsedBytes + grant.Upload.Size, UsedFiles = u.UsedFiles + 1 } : _snapshot.Usage;
            _snapshot = _snapshot with { Items = [.. others, grant.Upload], IsKnown = _snapshot.IsKnown, Usage = usage };
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return new UploadResult(AccountFailure.None, grant.Existing ? UploadMessages.AlreadyUploaded : UploadMessages.Uploaded, grant.Upload);
    }

    /// <inheritdoc />
    public async Task<UploadResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (Current.Items.All(i => i.Id != id))
        {
            return new UploadResult(AccountFailure.Rejected, UploadMessages.NotFound);
        }

        // Gone on the service either way: deleted now, or already deleted (taken down by the owner, or from another PC).
        return await DeleteByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops listening to the account and stops the saving-online queue.</summary>
    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        PictureUploader? queue;
        lock (_gate)
        {
            queue = _queue;
            _queue = null;
        }

        queue?.Dispose();
    }

    private static UploadResult FromAccount(AccountResult failure) => new(failure.Failure, failure.Message, null, failure.RetryAfter);

    private UploadResult Interpret(ApiOutcome outcome, string? code, string? reason, TimeSpan? retryAfter) => (outcome, code) switch
    {
        (ApiOutcome.RateLimited, _) => new UploadResult(AccountFailure.RateLimited, AccountMessages.RateLimited(retryAfter), null, retryAfter is { } d && d > TimeSpan.Zero ? d : ServiceClient.DefaultRetryAfter),
        (_, "upload_limit") => new UploadResult(
            AccountFailure.Rejected, Current.Usage is null && reason is null ? UploadMessages.LimitReached(Current.Limit) : UploadMessages.QuotaFull(Current.Usage, reason), Code: code),
        (_, "uploads_blocked") => Blocked("uploads_blocked", UploadMessages.Blocked),
        (_, "online_features_off") => Blocked("online_features_off", UploadMessages.OnlineOff),
        (_, "invite_required") => new UploadResult(AccountFailure.Rejected, UploadMessages.NeedsInvite, Code: code),
        // The 6 GB stop (D18): uploads stay off until GET /v1/me no longer reports it.
        (_, "upload_storage_full") => Blocked("upload_storage_full", UploadMessages.StorageFull),
        (_, "storage_full") => new UploadResult(AccountFailure.Rejected, UploadMessages.StorageFull, Code: code),
        (_, "body_too_large") => new UploadResult(AccountFailure.Rejected, UploadMessages.TooLarge, Code: code),
        (_, "image_rejected") => new UploadResult(AccountFailure.Rejected, UploadMessages.Refused(reason), Code: code),
        (ApiOutcome.ServerError, _) => new UploadResult(AccountFailure.Offline, AccountMessages.Offline, Code: code),
        _ => new UploadResult(AccountFailure.Rejected, AccountMessages.Rejected, Code: code),
    };

    private UploadResult Blocked(string reason, string message)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { BlockedReason = reason };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new UploadResult(AccountFailure.Rejected, message, Code: reason);
    }

    // Removes one upload from the list after the service deleted it, and gives its bytes back in the quota.
    private void Forget(string id)
    {
        lock (_gate)
        {
            var gone = _snapshot.Items.FirstOrDefault(i => i.Id == id);
            var usage = _snapshot.Usage is { } u && gone is not null
                ? u with { UsedBytes = Math.Max(0, u.UsedBytes - gone.Size), UsedFiles = Math.Max(0, u.UsedFiles - 1) }
                : _snapshot.Usage;
            _snapshot = _snapshot with { Items = [.. _snapshot.Items.Where(i => i.Id != id)], Usage = usage };
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Replace(IReadOnlyList<UploadInfo> items, int limit, string? blocked, UploadUsage? usage)
    {
        lock (_gate)
        {
            _snapshot = new UploadsSnapshot(items, limit, blocked, _policyAcceptedAt is not null, true, usage, _snapshot.Saving, _snapshot.SavingMessage);
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (_account.Current.IsSignedIn)
        {
            return;
        }

        bool had;
        lock (_gate)
        {
            had = _snapshot.Items.Count > 0 || _policyAcceptedAt is not null || _snapshot.IsKnown;
            _snapshot = UploadsSnapshot.Empty;
            _policyAcceptedAt = null;
        }

        if (!had)
        {
            return;
        }

        try
        {
            _files.Delete(_path);
        }
        catch (IOException ex)
        {
            _log.Warning("The stored upload list could not be removed: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning("The stored upload list could not be removed: " + ex.GetType().Name);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        UploadsSnapshot snapshot;
        DateTimeOffset? accepted;
        lock (_gate)
        {
            snapshot = _snapshot;
            accepted = _policyAcceptedAt;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["policyAcceptedAt"] = accepted?.ToString("O", CultureInfo.InvariantCulture),
            ["known"] = snapshot.IsKnown,
            ["limit"] = snapshot.Limit,
            ["blocked"] = snapshot.BlockedReason,
            ["usage"] = snapshot.Usage is { } usage
                ? new Dictionary<string, long> { ["usedBytes"] = usage.UsedBytes, ["usedFiles"] = usage.UsedFiles, ["limitBytes"] = usage.LimitBytes, ["limitFiles"] = usage.LimitFiles }
                : null,
            ["items"] = snapshot.Items.Select(i => new Dictionary<string, object>
            {
                ["id"] = i.Id,
                ["url"] = i.Url,
                ["sha256"] = i.Sha256,
                ["format"] = i.Format,
                ["size"] = i.Size,
                ["width"] = i.Width,
                ["height"] = i.Height,
                ["createdAt"] = i.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            }).ToList(),
        });
        try
        {
            _files.WriteAllBytesDurable(_path + ".tmp", json);
            _files.Move(_path + ".tmp", _path);
        }
        catch (IOException ex)
        {
            _log.Warning("The upload list could not be stored: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning("The upload list could not be stored: " + ex.GetType().Name);
        }
    }

    private void Load()
    {
        try
        {
            if (!_files.Exists(_path) || _files.Length(_path) > 1024 * 1024)
            {
                return;
            }

            using var document = JsonDocument.Parse(_files.ReadAllBytes(_path), new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            var root = document.RootElement;
            DateTimeOffset? accepted = root.GetProperty("policyAcceptedAt").ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(root.GetProperty("policyAcceptedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;
            // The stored list is checked like a service answer: only upload addresses on this build's content host.
            var items = root.GetProperty("items").EnumerateArray().Select(u => ServiceClient.ParseUpload(u, _api.UploadHost)).ToList();
            var blocked = root.GetProperty("blocked");
            // The quota was added in S5b-6: a list stored before it has none, and it is read again at the next refresh.
            UploadUsage? usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                ? new UploadUsage(u.GetProperty("usedBytes").GetInt64(), u.GetProperty("usedFiles").GetInt32(), u.GetProperty("limitBytes").GetInt64(), u.GetProperty("limitFiles").GetInt32())
                : null;
            _policyAcceptedAt = accepted;
            _snapshot = new UploadsSnapshot(
                items,
                root.GetProperty("limit").GetInt32(),
                blocked.ValueKind == JsonValueKind.String ? blocked.GetString() : null,
                accepted is not null,
                root.GetProperty("known").GetBoolean(),
                usage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A damaged or foreign file is ignored: the list is read from the service again at the next refresh.
            _log.Warning("The stored upload list could not be read: " + ex.GetType().Name);
            _policyAcceptedAt = null;
            _snapshot = UploadsSnapshot.Empty;
        }
    }
}
