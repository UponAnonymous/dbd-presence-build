using System.Globalization;
using System.Text.Json;
using DbdPresence.Core.Online;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Cache;
using DbdPresence.Packs.Pictures;

namespace DbdPresence.Online.Uploads;

/// <summary>What the saving-online queue works with.</summary>
/// <param name="Uploads">The upload list and the upload and delete calls (the account's session and licence checks are inside).</param>
/// <param name="Account">The account: nothing is sent unless online features are allowed.</param>
/// <param name="ReadPicture">Reads an own picture's stored file by its SHA-256, or null when it is missing or changed on disk.</param>
/// <param name="Rules">Whether the user accepted the picture rules; nothing is uploaded unless they did.</param>
/// <param name="Files">File access for the queue's own record.</param>
/// <param name="Path">Where the queue keeps its work, so it survives a restart.</param>
/// <param name="Time">The clock; its timers wake the queue for retries.</param>
/// <param name="Log">Diagnostics (never picture names, addresses or tokens).</param>
public sealed record PictureUploaderDependencies(
    UploadManager Uploads,
    IAccountService Account,
    Func<string, byte[]?> ReadPicture,
    Func<PictureRules> Rules,
    IPackFileStore Files,
    string Path,
    TimeProvider Time,
    IOnlineLog Log);

/// <summary>
/// Saves the user's own pictures online (D28, FR-PACK-15, PLAN_V2_S5B §2.6): the <see cref="IPictureUploadSink"/> of the Characters page. A picture
/// saved on this PC is handed over with <see cref="Enqueue"/> and uploaded through <see cref="UploadManager"/>, one at a time, the pictures in
/// use first and then in the order they were added. Nothing is uploaded unless the user is signed in with online features allowed and accepted
/// the picture rules: the page checks this before it hands a picture over, and the queue checks it again before every upload (and forgets its
/// waiting pictures when either stops being true; the page hands them over again on sign-in and on accepting the rules). A saved picture's
/// unlisted address comes back in the upload list by its SHA-256, which the picture resolver gives to Discord.
/// <list type="bullet">
/// <item>Over the person's quota (409 <c>upload_limit</c>) the queue stops; the rest stay preview-only ("storage full") until space is freed
/// (a removal, or <c>GET /v1/me</c> reporting more room).</item>
/// <item>At the service's storage stop (507 <c>upload_storage_full</c>) or an owner's block the queue pauses until <c>GET /v1/me</c> reports
/// uploads allowed again (read at start, after sign-in and every 6 hours).</item>
/// <item>A daily limit (429) waits for the service's <c>Retry-After</c>; an outage or another failure is retried after 1 and then 5 minutes, at
/// most three attempts per picture a day.</item>
/// </list>
/// <see cref="Remove"/> deletes the online copy of a picture removed from every character; a delete that cannot be sent now is kept and sent later.
/// Publishing a status never calls the queue (FR-PACK-13).
/// </summary>
public sealed class PictureUploader : IPictureUploadSink, IDisposable
{
    /// <summary>How long after start the first run happens (the account refresh has run by then).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    /// <summary>How often <c>GET /v1/me</c> is read while the queue is signed in and allowed.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>At most this many attempts per picture in one (UTC) day.</summary>
    public const int MaxAttemptsPerDay = 3;

    private const int SchemaVersion = 1;
    private const int MaxEntries = 2000;
    private static readonly TimeSpan[] s_backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly PictureUploaderDependencies _d;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Entry> _entries = [];
    private readonly List<PendingDelete> _deletes = [];
    private readonly HashSet<string> _removedWhileSaving = new(StringComparer.Ordinal);
    private long _sequence;
    private Halt _halt;
    private DateTimeOffset? _haltUntil;
    private UploadUsage? _usageAtHalt;
    private DateTimeOffset _refreshDue;
    private string? _uploading;
    private bool _running;
    private bool _again;
    private bool _wasAllowed;
    private bool _disposed;
    private ITimer? _timer;
    private Task _lastRun = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="PictureUploader"/> class and loads the stored work.</summary>
    /// <param name="dependencies">The collaborators.</param>
    public PictureUploader(PictureUploaderDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(dependencies.Uploads);
        ArgumentNullException.ThrowIfNull(dependencies.Account);
        ArgumentNullException.ThrowIfNull(dependencies.ReadPicture);
        ArgumentNullException.ThrowIfNull(dependencies.Rules);
        ArgumentNullException.ThrowIfNull(dependencies.Files);
        ArgumentException.ThrowIfNullOrEmpty(dependencies.Path);
        ArgumentNullException.ThrowIfNull(dependencies.Time);
        ArgumentNullException.ThrowIfNull(dependencies.Log);
        _d = dependencies;
        _refreshDue = _d.Time.GetUtcNow();
        Load();
    }

    private enum Halt
    {
        None,
        StorageFull,
        Paused,
        Blocked,
        DailyLimit,
    }

    /// <summary>Gets the task of the most recent run (completed when none is running); tests await it.</summary>
    public Task LastRun
    {
        get
        {
            lock (_gate)
            {
                return _lastRun;
            }
        }
    }

    /// <summary>Gets the SHA-256 of each picture waiting to be saved online, in the order they are tried.</summary>
    public IReadOnlyList<string> Waiting
    {
        get
        {
            lock (_gate)
            {
                return [.. Ordered().Select(e => e.Sha256)];
            }
        }
    }

    /// <summary>Gets the number of online copies still to be deleted.</summary>
    public int PendingDeletes
    {
        get
        {
            lock (_gate)
            {
                return _deletes.Count;
            }
        }
    }

    /// <summary>A reader of own pictures' files from the picture store (the file is checked against its hash there).</summary>
    /// <param name="store">The picture store.</param>
    /// <returns>The reader.</returns>
    public static Func<string, byte[]?> StoreReader(PictureStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return sha => PictureKeys.IsSha256(sha) ? store.Read(new PictureItem(sha, null, 0, sha + ".png", null, PictureOrigin.Pc, default)) : null;
    }

    /// <summary>Starts listening to the account and runs the first pass after <see cref="StartDelay"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _wasAllowed = Allowed();
            _d.Account.Changed += OnAccountChanged;
            _timer = _d.Time.CreateTimer(_ => Kick(), null, StartDelay, Timeout.InfiniteTimeSpan);
        }

        Publish();
    }

    /// <inheritdoc />
    public void Enqueue(PictureUploadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PictureKeys.IsCharacterKey(request.Key) || !PictureKeys.IsSha256(request.Sha256) || !PictureLimits.FitsFileLimit(request.Bytes))
        {
            _d.Log.Warning("A picture handed to the saving-online queue was refused: not a character picture file under 2 MB.");
            return;
        }

        // The page's gate, checked again here: nothing waits to go online before the rules are accepted or while signed out.
        if (!Allowed())
        {
            return;
        }

        if (_d.Uploads.Current.UrlBySha256.ContainsKey(request.Sha256))
        {
            return; // already saved online
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _removedWhileSaving.Remove(request.Sha256);
            var index = _entries.FindIndex(e => e.Sha256 == request.Sha256);
            if (request.InUse)
            {
                // Only one picture per character is in use: the one handed over last.
                for (var i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Key == request.Key && _entries[i].InUse)
                    {
                        _entries[i] = _entries[i] with { InUse = false };
                    }
                }
            }

            if (index >= 0)
            {
                var entry = _entries[index];
                var today = Today();
                // "Try again": a failed picture is tried again, within the daily attempts.
                var retry = entry.Failed && (entry.Day != today || entry.Attempts < MaxAttemptsPerDay);
                _entries[index] = entry with
                {
                    InUse = request.InUse || entry.InUse,
                    Failed = !retry && entry.Failed,
                    NotBefore = retry ? null : entry.NotBefore,
                };
            }
            else if (_entries.Count < MaxEntries)
            {
                _entries.Add(new Entry(request.Key, request.Sha256, request.Bytes, request.InUse, ++_sequence, 0, Today(), null, false, false));
            }
            else
            {
                _d.Log.Warning("The saving-online queue is full; a picture was not queued.");
                return;
            }
        }

        Save();
        Publish();
        Kick();
    }

    /// <inheritdoc />
    public void Remove(string sha256)
    {
        if (!PictureKeys.IsSha256(sha256))
        {
            return;
        }

        var online = _d.Uploads.Current.Items.FirstOrDefault(i => i.Sha256 == sha256);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _entries.RemoveAll(e => e.Sha256 == sha256);
            if (online is not null)
            {
                AddDelete(new PendingDelete(online.Id, null));
            }
            else if (_uploading == sha256)
            {
                _removedWhileSaving.Add(sha256); // deleted as soon as its upload answers
            }
            else if (!_d.Uploads.Current.IsKnown)
            {
                AddDelete(new PendingDelete(null, sha256)); // the list is not read yet: it may be online
            }

            // Space is about to be freed: a stopped queue may go on.
            if (_halt == Halt.StorageFull)
            {
                _halt = Halt.None;
            }
        }

        Save();
        Publish();
        Kick();
    }

    /// <summary>Reads <c>GET /v1/me</c> at the next pass and runs it now (the "Check for updates" path, and tests).</summary>
    public void CheckNow()
    {
        lock (_gate)
        {
            _refreshDue = _d.Time.GetUtcNow();
        }

        Kick();
    }

    /// <summary>Stops the queue; an upload in progress is cancelled and its picture stays queued.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _d.Account.Changed -= OnAccountChanged;
            _timer?.Dispose();
            _timer = null;
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private static bool IsPaused(Halt halt) => halt is Halt.Paused or Halt.Blocked;

    private bool Allowed() => _d.Account.Current.OnlineFeaturesAllowed && _d.Rules() == PictureRules.Accepted;

    private DateOnly Today() => DateOnly.FromDateTime(_d.Time.GetUtcNow().UtcDateTime);

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        var account = _d.Account.Current;
        bool opened;
        var changed = false;
        lock (_gate)
        {
            if (!account.IsSignedIn || account.State == OnlineState.Revoked)
            {
                // Signed out or withdrawn: the waiting pictures are forgotten (the page hands them over again after the next sign-in). Deletes
                // are kept: they are sent once this account is signed in again, and another account's are answered not_found.
                changed = _entries.Count > 0 || _halt != Halt.None;
                _entries.Clear();
                _removedWhileSaving.Clear();
                _halt = Halt.None;
                _haltUntil = null;
                _usageAtHalt = null;
            }

            var allowed = account.OnlineFeaturesAllowed;
            opened = allowed && !_wasAllowed;
            _wasAllowed = allowed;
            if (opened)
            {
                _refreshDue = _d.Time.GetUtcNow(); // read the list and the quota first
            }
        }

        if (changed)
        {
            Save();
        }

        Publish();
        if (opened)
        {
            Kick();
        }
    }

    private void Kick()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                _again = true;
                return;
            }

            _running = true;
            _again = false;
            _lastRun = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var ended = false;
#pragma warning disable CA1031 // Loop boundary of the background queue: an unexpected failure is logged and the queue stays usable for the next trigger; nothing is thrown into a thread-pool thread.
        try
        {
            while (true)
            {
                await PassAsync(_stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_again || _disposed)
                    {
                        // Cleared under the same lock that Kick reads, so a Kick from now on starts a new run.
                        _running = false;
                        ended = true;
                        break;
                    }

                    _again = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _d.Log.Warning("Saving pictures online was stopped because the app is closing.");
        }
        catch (ObjectDisposedException)
        {
            _d.Log.Warning("Saving pictures online was stopped because the app is closing.");
        }
        catch (Exception ex)
        {
            _d.Log.Warning("Saving pictures online stopped after an unexpected error (" + ex.GetType().Name + "). It tries again at the next change or retry time.");
        }
        finally
        {
            lock (_gate)
            {
                // A run that did not end normally must not leave the queue marked as running, or it would never run again.
                if (!ended)
                {
                    _running = false;
                }

                _uploading = null;
            }
        }
#pragma warning restore CA1031

        Save();
        Publish();
        ScheduleWake();
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        if (!_d.Account.Current.OnlineFeaturesAllowed)
        {
            return;
        }

        if (_d.Rules() != PictureRules.Accepted)
        {
            // The rules were declined or reopened: nothing waits to go online (the page hands pictures over again after acceptance).
            lock (_gate)
            {
                _entries.Clear();
                _halt = Halt.None;
            }
        }

        await RefreshIfDueAsync(cancellationToken).ConfigureAwait(false);
        await DeleteAsync(cancellationToken).ConfigureAwait(false);
        while (await UploadNextAsync(cancellationToken).ConfigureAwait(false))
        {
            Save();
            Publish();
        }
    }

    private async Task RefreshIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _d.Time.GetUtcNow();
        lock (_gate)
        {
            if (now < _refreshDue)
            {
                return;
            }

            _refreshDue = now + RefreshInterval;
        }

        var refreshed = await _d.Uploads.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed.Succeeded)
        {
            return;
        }

        var current = _d.Uploads.Current;
        lock (_gate)
        {
            // A paused or blocked queue goes on once the service allows uploads again; a full one once there is more room than when it stopped.
            if (IsPaused(_halt) && current.BlockedReason is null)
            {
                _halt = Halt.None;
            }

            if (_halt == Halt.StorageFull && current.Usage is { } usage && MoreRoom(usage))
            {
                _halt = Halt.None;
            }

            if (current.BlockedReason is { } blocked && _halt == Halt.None)
            {
                _halt = blocked == "upload_storage_full" ? Halt.Paused : Halt.Blocked;
            }

            // Pictures that are online now (saved from another PC, or before a restart) leave the queue; a delete waiting for the list is resolved.
            _entries.RemoveAll(e => current.UrlBySha256.ContainsKey(e.Sha256));
            for (var i = _deletes.Count - 1; i >= 0; i--)
            {
                if (_deletes[i].Id is null)
                {
                    var match = current.Items.FirstOrDefault(u => u.Sha256 == _deletes[i].Sha256);
                    if (match is null)
                    {
                        _deletes.RemoveAt(i);
                    }
                    else
                    {
                        _deletes[i] = new PendingDelete(match.Id, null);
                    }
                }
            }
        }
    }

    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string id;
            lock (_gate)
            {
                var next = _deletes.FirstOrDefault(d => d.Id is not null);
                if (next is null)
                {
                    return;
                }

                id = next.Id!;
            }

            var result = await _d.Uploads.DeleteByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (result.Code == UploadManager.InvalidId)
            {
                _d.Log.Warning("A queued delete was dropped: its id is not an upload id.");
                lock (_gate)
                {
                    _deletes.RemoveAll(d => d.Id == id);
                }

                continue;
            }

            if (!result.Succeeded)
            {
                // Kept and sent again at the next pass (an outage, a wait or a refused session); removing one's own copy is never given up.
                _d.Log.Warning($"An online copy could not be deleted yet: {result.Failure}");
                return;
            }

            lock (_gate)
            {
                _deletes.RemoveAll(d => d.Id == id);
                if (_halt == Halt.StorageFull)
                {
                    _halt = Halt.None; // space was freed
                }
            }

            Save();
        }
    }

    // Uploads the next picture that may go now. False when there is nothing more to do in this pass.
    private async Task<bool> UploadNextAsync(CancellationToken cancellationToken)
    {
        if (!Allowed())
        {
            return false;
        }

        var now = _d.Time.GetUtcNow();
        Entry entry;
        lock (_gate)
        {
            if (_halt == Halt.DailyLimit && _haltUntil is { } until && now >= until)
            {
                _halt = Halt.None;
                _haltUntil = null;
            }

            if (_halt != Halt.None)
            {
                return false;
            }

            var saved = _d.Uploads.Current.UrlBySha256;
            _entries.RemoveAll(e => saved.ContainsKey(e.Sha256));
            var next = Ordered().FirstOrDefault(e => !e.Failed && (e.NotBefore is null || e.NotBefore <= now));
            if (next is null)
            {
                return false;
            }

            entry = next.Day == Today() ? next : next with { Attempts = 0, Day = Today() };
            _uploading = entry.Sha256;
        }

        try
        {
            var png = _d.ReadPicture(entry.Sha256);
            if (png is null)
            {
                // Missing or changed on disk: nothing to save. The page shows the picture as missing from this PC.
                Drop(entry.Sha256);
                return true;
            }

            if (_d.Uploads.Current.Usage is { } usage && !usage.Fits(png.Length))
            {
                StopFull(usage);
                return false;
            }

            var result = await _d.Uploads.UploadPictureAsync(png, cancellationToken).ConfigureAwait(false);
            return Handle(entry, result, now);
        }
        finally
        {
            lock (_gate)
            {
                _uploading = null;
            }
        }
    }

    // Applies one upload's outcome. True to go on with the next picture.
    private bool Handle(Entry entry, UploadResult result, DateTimeOffset now)
    {
        if (result.Succeeded)
        {
            var removed = false;
            lock (_gate)
            {
                _entries.RemoveAll(e => e.Sha256 == entry.Sha256);
                if (_removedWhileSaving.Remove(entry.Sha256) && result.Upload is { } upload)
                {
                    AddDelete(new PendingDelete(upload.Id, null)); // removed on this PC while it was being saved
                    removed = true;
                }
            }

            if (removed)
            {
                Kick();
            }

            return true;
        }

        switch (result.Code)
        {
            case "upload_limit":
                StopFull(_d.Uploads.Current.Usage);
                return false;
            case "upload_storage_full" or "storage_full":
                SetHalt(Halt.Paused, null);
                return false;
            case "uploads_blocked" or "online_features_off" or "invite_required":
                SetHalt(Halt.Blocked, null);
                return false;
            case UploadManager.LocalRefusal or "image_rejected" or "body_too_large":
                // This file can never be accepted as it is: it stays in the preview with "couldn't save online".
                MarkFailed(entry, permanent: true, now);
                return true;
            default:
                break;
        }

        switch (result.Failure)
        {
            case AccountFailure.RateLimited:
                SetHalt(Halt.DailyLimit, now + (result.RetryAfter ?? Api.ServiceClient.DefaultRetryAfter));
                return false;
            case AccountFailure.SignInRequired:
                return false; // the account changed; its event decides what happens next
            case AccountFailure.Offline:
                MarkFailed(entry, permanent: false, now);
                return false; // the service is down: the rest wait too
            default:
                MarkFailed(entry, permanent: false, now);
                return true;
        }
    }

    // Whether the quota now has more room than when the queue stopped, so a refused picture is not sent again against the same 409. After a
    // restart the figures at the stop are not known: then the first waiting picture must fit by the reported figures.
    private bool MoreRoom(UploadUsage usage)
    {
        if (_usageAtHalt is { } before)
        {
            return usage.LimitBytes - usage.UsedBytes > before.LimitBytes - before.UsedBytes || usage.LimitFiles - usage.UsedFiles > before.LimitFiles - before.UsedFiles;
        }

        return Ordered().FirstOrDefault() is { } next && usage.Fits(next.Bytes);
    }

    private void StopFull(UploadUsage? usage)
    {
        lock (_gate)
        {
            _halt = Halt.StorageFull;
            _usageAtHalt = usage;
        }
    }

    private void SetHalt(Halt halt, DateTimeOffset? until)
    {
        lock (_gate)
        {
            _halt = halt;
            _haltUntil = until;
        }
    }

    private void MarkFailed(Entry entry, bool permanent, DateTimeOffset now)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Sha256 == entry.Sha256);
            if (index < 0)
            {
                return;
            }

            var attempts = entry.Attempts + 1;
            var exhausted = permanent || attempts >= MaxAttemptsPerDay;
            DateTimeOffset? notBefore = exhausted
                ? new DateTimeOffset(entry.Day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : now + s_backoff[Math.Min(attempts - 1, s_backoff.Length - 1)];
            _entries[index] = _entries[index] with { Attempts = attempts, Day = entry.Day, NotBefore = notBefore, Failed = permanent, Offline = !permanent };
        }
    }

    private void Drop(string sha256)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Sha256 == sha256);
        }
    }

    private void AddDelete(PendingDelete delete)
    {
        if (!_deletes.Contains(delete))
        {
            _deletes.Add(delete);
        }
    }

    // The order of trying: pictures in use first, then in the order they were added.
    private IEnumerable<Entry> Ordered() => _entries.OrderByDescending(e => e.InUse).ThenBy(e => e.Order);

    // Wakes the queue at the next retry, the end of a daily wait or the next refresh.
    private void ScheduleWake()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null)
            {
                return;
            }

            if (!_d.Account.Current.OnlineFeaturesAllowed)
            {
                // Nothing can be sent now; the account's change event starts the next pass.
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            var now = _d.Time.GetUtcNow();
            var times = new List<DateTimeOffset>();
            if (_entries.Count > 0 || _halt != Halt.None || _deletes.Count > 0)
            {
                times.Add(_refreshDue);
            }

            if (_halt == Halt.DailyLimit && _haltUntil is { } until)
            {
                times.Add(until);
            }

            if (_halt == Halt.None)
            {
                times.AddRange(_entries.Where(e => e.NotBefore is not null).Select(e => e.NotBefore!.Value));
            }

            if (_deletes.Count > 0)
            {
                times.Add(now + s_backoff[^1]);
            }

            if (times.Count == 0)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            var due = times.Min() - now;
            _timer.Change(due < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : due, Timeout.InfiniteTimeSpan);
        }
    }

    // Tells the upload list (and so the Characters page) why each waiting picture waits.
    private void Publish()
    {
        var account = _d.Account.Current;
        Dictionary<string, PictureSaveState> states;
        string? message;
        lock (_gate)
        {
            var now = _d.Time.GetUtcNow();
            var halted = _halt switch
            {
                Halt.StorageFull => PictureSaveState.StorageFull,
                Halt.Paused => PictureSaveState.Paused,
                Halt.Blocked => PictureSaveState.Blocked,
                Halt.DailyLimit => PictureSaveState.DailyLimit,
                _ => (PictureSaveState?)null,
            };
            states = new Dictionary<string, PictureSaveState>(StringComparer.Ordinal);
            foreach (var e in _entries)
            {
                states[e.Sha256] = e.Failed || (e.Attempts >= MaxAttemptsPerDay && e.Day == Today())
                    ? PictureSaveState.Failed
                    : halted ?? (e.Offline && e.NotBefore > now ? PictureSaveState.Offline : PictureSaveState.Waiting);
            }

            message = account.State == OnlineState.Revoked
                ? UploadMessages.SavingRevoked
                : _halt switch
                {
                    Halt.StorageFull => UploadMessages.QuotaFull(_d.Uploads.Current.Usage, null),
                    Halt.Paused => UploadMessages.SavingPaused,
                    Halt.Blocked => UploadMessages.SavingBlocked,
                    Halt.DailyLimit => UploadMessages.SavingTomorrow,
                    _ => states.Values.Any(s => s == PictureSaveState.Offline) ? UploadMessages.SavingOffline : null,
                };
        }

        _d.Uploads.SetSaving(states, message);
    }

    private void Save()
    {
        Dictionary<string, object?> document;
        lock (_gate)
        {
            document = new Dictionary<string, object?>
            {
                ["schemaVersion"] = SchemaVersion,
                ["sequence"] = _sequence,
                ["halt"] = _halt.ToString(),
                ["haltUntil"] = _haltUntil?.ToString("O", CultureInfo.InvariantCulture),
                ["items"] = _entries.Select(e => new Dictionary<string, object?>
                {
                    ["key"] = e.Key,
                    ["sha256"] = e.Sha256,
                    ["bytes"] = e.Bytes,
                    ["inUse"] = e.InUse,
                    ["order"] = e.Order,
                    ["attempts"] = e.Attempts,
                    ["day"] = e.Day.ToString("O", CultureInfo.InvariantCulture),
                    ["notBefore"] = e.NotBefore?.ToString("O", CultureInfo.InvariantCulture),
                    ["failed"] = e.Failed,
                }).ToList(),
                ["deletes"] = _deletes.Select(d => new Dictionary<string, string?> { ["id"] = d.Id, ["sha256"] = d.Sha256 }).ToList(),
            };
        }

        try
        {
            _d.Files.WriteAllBytesDurable(_d.Path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(document));
            _d.Files.Move(_d.Path + ".tmp", _d.Path);
        }
        catch (IOException ex)
        {
            _d.Log.Warning("The saving-online queue could not be stored: " + ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            _d.Log.Warning("The saving-online queue could not be stored: " + ex.GetType().Name);
        }
    }

    private void Load()
    {
        try
        {
            if (!_d.Files.Exists(_d.Path) || _d.Files.Length(_d.Path) > 2 * 1024 * 1024)
            {
                return;
            }

            using var json = JsonDocument.Parse(_d.Files.ReadAllBytes(_d.Path), new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            var root = json.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                return;
            }

            var entries = new List<Entry>();
            var droppedEntries = 0;
            var droppedDeletes = 0;
            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                var entry = new Entry(
                    item.GetProperty("key").GetString()!,
                    item.GetProperty("sha256").GetString()!,
                    item.GetProperty("bytes").GetInt64(),
                    item.GetProperty("inUse").GetBoolean(),
                    item.GetProperty("order").GetInt64(),
                    item.GetProperty("attempts").GetInt32(),
                    DateOnly.ParseExact(item.GetProperty("day").GetString()!, "O", CultureInfo.InvariantCulture),
                    item.GetProperty("notBefore").ValueKind == JsonValueKind.String
                        ? DateTimeOffset.Parse(item.GetProperty("notBefore").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        : null,
                    item.GetProperty("failed").GetBoolean(),
                    false);
                // Every stored value is checked like a new request: a hand-edited file cannot add something the page would not.
                if (PictureKeys.IsCharacterKey(entry.Key) && PictureKeys.IsSha256(entry.Sha256) && PictureLimits.FitsFileLimit(entry.Bytes)
                    && entries.Count < MaxEntries && entries.All(e => e.Sha256 != entry.Sha256))
                {
                    entries.Add(entry);
                }
                else
                {
                    droppedEntries++;
                }
            }

            var deletes = new List<PendingDelete>();
            foreach (var item in root.GetProperty("deletes").EnumerateArray())
            {
                var id = item.GetProperty("id").ValueKind == JsonValueKind.String ? item.GetProperty("id").GetString() : null;
                var sha = item.GetProperty("sha256").ValueKind == JsonValueKind.String ? item.GetProperty("sha256").GetString() : null;
                // The same strict checks as the service's answers: an upload id is a lower-case UUID, a hash is 64 lower-case hex.
                if (UploadPolicy.IsUploadId(id) || (id is null && PictureKeys.IsSha256(sha)))
                {
                    deletes.Add(new PendingDelete(id, id is null ? sha : null));
                }
                else
                {
                    droppedDeletes++;
                }
            }

            if (droppedEntries + droppedDeletes > 0)
            {
                _d.Log.Warning(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The stored saving-online queue had entries that are not valid; {droppedEntries} pictures and {droppedDeletes} deletes were left out."));
            }

            _entries.AddRange(entries);
            _deletes.AddRange(deletes.Distinct().Take(MaxEntries));
            _sequence = Math.Max(root.GetProperty("sequence").GetInt64(), entries.Count == 0 ? 0 : entries.Max(e => e.Order));
            _halt = Enum.TryParse<Halt>(root.GetProperty("halt").GetString(), out var halt) && Enum.IsDefined(halt) ? halt : Halt.None;
            _haltUntil = root.GetProperty("haltUntil").ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(root.GetProperty("haltUntil").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            // A damaged file is ignored: the Characters page hands its pictures over again after the next sign-in or acceptance of the rules.
            _d.Log.Warning("The stored saving-online queue could not be read: " + ex.GetType().Name);
            _entries.Clear();
            _deletes.Clear();
            _halt = Halt.None;
            _haltUntil = null;
        }
    }

    /// <summary>One picture waiting to be saved online.</summary>
    /// <param name="Key">The character key.</param>
    /// <param name="Sha256">The picture's hash.</param>
    /// <param name="Bytes">The size the page reported.</param>
    /// <param name="InUse">True when the character uses it now (tried first).</param>
    /// <param name="Order">The order it was added in.</param>
    /// <param name="Attempts">Attempts on <paramref name="Day"/>.</param>
    /// <param name="Day">The UTC day the attempts were counted on.</param>
    /// <param name="NotBefore">Not tried again before this time, or null.</param>
    /// <param name="Failed">True when it is not tried again until it is handed over again (or tomorrow, after three failed attempts).</param>
    /// <param name="Offline">True when the last attempt found the service unreachable.</param>
    private sealed record Entry(string Key, string Sha256, long Bytes, bool InUse, long Order, int Attempts, DateOnly Day, DateTimeOffset? NotBefore, bool Failed, bool Offline);

    /// <summary>An online copy to delete: by upload id, or by hash until the upload list is known.</summary>
    /// <param name="Id">The upload id, or null.</param>
    /// <param name="Sha256">The picture's hash while the id is not known yet, else null.</param>
    private sealed record PendingDelete(string? Id, string? Sha256);
}
