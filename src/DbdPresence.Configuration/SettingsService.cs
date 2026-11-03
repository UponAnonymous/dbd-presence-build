namespace DbdPresence.Configuration;

/// <summary>Raised after settings changed (FR live apply, ADR-0007).</summary>
public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    /// <summary>Gets the settings before the change.</summary>
    public AppSettings Previous { get; } = previous;

    /// <summary>Gets the settings after the change.</summary>
    public AppSettings Current { get; } = current;
}

/// <summary>Raised when saving failed; the in-memory settings stay current and saving is retried on the next change.</summary>
public sealed class SettingsSaveFailedEventArgs(Exception error) : EventArgs
{
    /// <summary>Gets the failure.</summary>
    public Exception Error { get; } = error;
}

/// <summary>
/// The single owner of the live settings (ARCHITECTURE §8): validates every change, notifies subscribers
/// immediately so changes apply without restart, and writes to disk debounced (500 ms) through one writer.
/// Thread-safe.
/// </summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    /// <summary>Debounce interval for writes.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly SettingsStore _store;
    private readonly ITimer _saveTimer;
    private readonly Lock _gate = new();

    // Serialises store access (the store is not thread-safe). Always taken before _gate, never inside it, so disk IO
    // never blocks readers of Current (the UI thread).
    private readonly Lock _storeGate = new();
    private long _version;
    private AppSettings _current;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="SettingsService"/> class and loads settings.</summary>
    public SettingsService(SettingsStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        LoadResult = store.Load();
        _current = LoadResult.Settings;
        _saveTimer = time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised synchronously after a change was applied.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Raised when a save failed.</summary>
    public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    /// <summary>Gets how settings were loaded at startup.</summary>
    public SettingsLoadResult LoadResult { get; private set; }

    /// <summary>Gets a value indicating whether changes are kept in memory only (newer settings file).</summary>
    public bool IsReadOnly => _store.IsReadOnly;

    /// <summary>Gets the current settings.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Applies a change: validates, publishes and schedules a save. Returns validation issues.</summary>
    public IReadOnlyList<SettingsIssue> Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings previous;
        AppSettings next;
        IReadOnlyList<SettingsIssue> issues;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            (next, issues) = SettingsValidator.Validate(change(previous));
            _current = next;
            _dirty = true;
            _version++;
            if (!_store.IsReadOnly)
            {
                _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
        return issues;
    }

    /// <summary>Restores a backup and publishes it as a change.</summary>
    public void RestoreBackup(int index)
    {
        AppSettings previous;
        lock (_storeGate)
        {
            var restored = _store.RestoreBackup(index);
            lock (_gate)
            {
                previous = _current;
                LoadResult = restored;
                _current = restored.Settings;
                _dirty = false;
                _version++;
            }
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, LoadResult.Settings));
    }

    /// <summary>Writes pending changes now (called by the debounce timer and on shutdown).</summary>
    public void Flush()
    {
        Exception? failure = null;
        lock (_storeGate)
        {
            AppSettings snapshot;
            long version;
            lock (_gate)
            {
                if (!_dirty || _store.IsReadOnly)
                {
                    return;
                }

                snapshot = _current;
                version = _version;
            }

            try
            {
                _store.Save(snapshot);
                lock (_gate)
                {
                    // A change made during the write stays dirty and is written by the next flush.
                    _dirty = _version != version;
                }
            }
            catch (IOException error)
            {
                failure = error;
            }
            catch (UnauthorizedAccessException error)
            {
                failure = error;
            }
        }

        if (failure is not null)
        {
            SaveFailed?.Invoke(this, new SettingsSaveFailedEventArgs(failure));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _saveTimer.Dispose();
        Flush();
    }
}
