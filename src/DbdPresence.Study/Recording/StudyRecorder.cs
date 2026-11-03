using DbdPresence.Core.Capture;
using DbdPresence.Core.Game;
using DbdPresence.StudyCapture;

namespace DbdPresence.Study;

/// <summary>
/// The study recorder (QUALITY_GATES C6 data collection, SAFETY_AND_PRIVACY sections 2.1 and 3, FR-LAB-06): it waits for the game, then saves a picture of the game
/// window (only that window, through <see cref="IStudyFrameSource"/>) as a PNG plus <c>metadata.json</c> in <c>&lt;root&gt;\&lt;date and time&gt;</c> until the game closes,
/// the session reaches 2 GB, the disk refuses, the account may no longer record or it is stopped. The app's Study page (owner only) and the console tool run this same
/// code, with the same folders and files, so the labelling flow reads either's recordings unchanged. Nothing is uploaded and this code has no network access. Time and
/// waiting are injected, so tests are deterministic. Every way a recording can end publishes <see cref="StudyRecorderState.Stopped"/> with a reason (and a note when
/// something went wrong), clears the recorder's state first and can then be started again; <see cref="StopAsync(string)"/> never throws.
/// </summary>
public sealed class StudyRecorder : IStudyRecorder
{
    /// <summary>How long the loop waits between two looks at the window.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the game is looked for, and checked to be still running.</summary>
    public static readonly TimeSpan GamePoll = TimeSpan.FromSeconds(2);

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly Func<GameLifecycle> _game;
    private readonly Func<int, IStudyFrameSource> _createSource;
    private readonly string _toolVersion;
    private readonly Func<bool> _mayRecord;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _warn;
    private readonly Lock _gate = new();
    private readonly List<string> _notes = [];
    private CancellationTokenSource? _cts;
    private TaskCompletionSource _done = Finished();
    private string _stopReason = "owner stopped";
    private StudyRecorderStatus _status = StudyRecorderStatus.Initial;

    /// <summary>Initializes a new instance of the <see cref="StudyRecorder"/> class.</summary>
    /// <param name="root">The folder that holds the sessions (<c>Documents\DBD Presence Study</c> for the app and the console tool).</param>
    /// <param name="time">The clock.</param>
    /// <param name="game">Reads the game's state when called (the console tool polls the process list first).</param>
    /// <param name="createSource">Makes the picture source for the game's process id; the recorder disposes it.</param>
    /// <param name="toolVersion">The version written into <c>metadata.json</c>.</param>
    /// <param name="mayRecord">Whether this account may record at all (the owner gate in the app). Checked when a recording starts and again on every step, so a recording ends when it turns false.</param>
    /// <param name="delay">Waits; the default uses <paramref name="time"/>. Tests pass a fake that advances their clock.</param>
    /// <param name="warn">Receives one plain line for every failure (never a path), for the app's log.</param>
    public StudyRecorder(string root, TimeProvider time, Func<GameLifecycle> game, Func<int, IStudyFrameSource> createSource, string toolVersion, Func<bool> mayRecord, Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(createSource);
        ArgumentNullException.ThrowIfNull(toolVersion);
        ArgumentNullException.ThrowIfNull(mayRecord);
        _root = root;
        _time = time;
        _game = game;
        _createSource = createSource;
        _toolVersion = toolVersion;
        _mayRecord = mayRecord;
        _delay = delay ?? ((span, token) => Task.Delay(span, time, token));
        _warn = warn;
    }

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    /// <inheritdoc />
    public string RootFolder => _root;

    /// <inheritdoc />
    public StudyRecorderStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    private static TaskCompletionSource Finished()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <inheritdoc />
    Task IStudyRecorder.StartAsync() => StartAsync();

    /// <inheritdoc />
    Task IStudyRecorder.StopAsync() => StopAsync();

    /// <summary>Starts waiting for the game and recording. The 30-day clean-up runs here, once, before anything new is written.</summary>
    /// <param name="confirmAfterCleanUp">Asked once the clean-up has run and its notes are in <see cref="Status"/>; when it answers false nothing is recorded (the console tool asks its Y question here).</param>
    /// <returns>A task that ends once the recorder is started (not when the recording ends).</returns>
    public async Task StartAsync(Func<bool>? confirmAfterCleanUp = null)
    {
        if (!_mayRecord())
        {
            _warn?.Invoke("A recording was not started: this account may not record.");
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_status.IsActive)
            {
                return;
            }

            _notes.Clear();
            _cts = cts = new CancellationTokenSource();
            _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopReason = "owner stopped";
            _status = new StudyRecorderStatus(StudyRecorderState.WaitingForGame, Notes: []);
        }

        Raise();

        // The 30-day clean-up (PLAN_V2 D25) runs here, on the path that starts a recording and nowhere else, before anything new is written.
        RetentionReport report;
        try
        {
            report = await Task.Run(() => SessionRetention.Sweep(_root, _time)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Recorder boundary: whatever the clean-up throws is logged by type, shown to the owner and ends the start cleanly, so the recorder is never left waiting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _warn?.Invoke($"The clean-up of old recordings failed ({ex.GetType().Name}).");
            Complete(cts, null, null, "unexpected error", $"The clean-up of old recordings failed ({ex.GetType().Name}), so nothing was recorded. Check that the recordings folder can be read.");
            return;
        }

        AddRetentionNotes(report);
        if (confirmAfterCleanUp is not null && !confirmAfterCleanUp())
        {
            Complete(cts, null, null, "cancelled before it started", null);
            return;
        }

        _ = Task.Run(() => RunAsync(cts));
    }

    /// <summary>Ends the recording and waits until its files are closed.</summary>
    /// <param name="reason">The reason written into <c>metadata.json</c>.</param>
    /// <returns>A task that ends when the recorder has stopped.</returns>
    public async Task StopAsync(string reason = "owner stopped")
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        Task wait;
        lock (_gate)
        {
            if (_cts is { } cts)
            {
                _stopReason = reason;
                cts.Cancel();
            }

            wait = _done.Task;
        }

        await wait.ConfigureAwait(false);
    }

    private static string Describe(string what, Exception ex) => $"{what} ({ex.GetType().Name}).";

    private void AddRetentionNotes(RetentionReport report)
    {
        lock (_gate)
        {
            if (report.Deleted.Count > 0)
            {
                _notes.Add($"Deleted {report.Deleted.Count} recording(s) older than {SessionRetention.Days} days: {string.Join(", ", report.Deleted)}.");
            }

            foreach (var problem in report.Problems)
            {
                _notes.Add($"Could not delete the old recording \"{problem.FolderName}\": {problem.Reason}. Delete that folder yourself if you no longer need it.");
            }

            if (report.Deleted.Count == 0 && report.Problems.Count == 0)
            {
                _notes.Add($"No recordings are older than {SessionRetention.Days} days. Nothing was deleted.");
            }

            _status = _status with { Notes = [.. _notes] };
        }

        Raise();
    }

    private void Publish(Func<StudyRecorderStatus, StudyRecorderStatus> change, string? note = null)
    {
        lock (_gate)
        {
            if (note is not null)
            {
                _notes.Add(note);
            }

            _status = change(_status) with { Notes = [.. _notes] };
        }

        Raise();
    }

    private void Raise() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private string StopReason()
    {
        lock (_gate)
        {
            return _stopReason;
        }
    }

    private async Task RunAsync(CancellationTokenSource owner)
    {
        var token = owner.Token;
        var reason = "unexpected error";
        string? errorNote = null;
        SessionFolder? session = null;
        SessionMetadata? metadata = null;
        try
        {
            var pid = await WaitForGameAsync(token).ConfigureAwait(false);

            // Checked again here, just before the folder is made, so nothing is written once the account may no longer record.
            if (!token.IsCancellationRequested && !_mayRecord())
            {
                reason = "owner status lost";
                errorNote = "Stopped before the game started: this account is no longer recognised as the owner's. Nothing was saved.";
            }
            else if (pid is null)
            {
                reason = "stopped before the game started";
                errorNote = "Stopped before the game started. Nothing was saved.";
            }
            else
            {
                try
                {
                    session = SessionFolder.Create(_root, _time.GetLocalNow());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    reason = "folder error";
                    errorNote = $"Could not create the folder for the pictures: {ex.Message}";
                }

                if (session is not null)
                {
                    metadata = new SessionMetadata
                    {
                        ToolVersion = _toolVersion,
                        OsVersion = Environment.OSVersion.VersionString,
                        OsBuild = Environment.OSVersion.Version.Build,
                        StartedUtc = _time.GetUtcNow(),
                    };

                    // Written before the first picture so that a crash still leaves a folder the 30-day clean-up can recognise.
                    var expiry = SessionRetention.ExpiryDate(metadata.StartedUtc);
                    var written = session.TryWriteMetadata(metadata, out var firstProblem);
                    var folder = session.Path;
                    Publish(
                        s => s with { State = StudyRecorderState.Recording, Folder = folder, ExpiryDate = expiry },
                        written ? null : $"metadata.json could not be written: {firstProblem}");

                    using var source = _createSource(pid.Value);
                    reason = await CaptureLoopAsync(source, session, metadata, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            reason = metadata is null ? "stopped before the game started" : StopReason();
        }
#pragma warning disable CA1031 // Recorder boundary: any failure ends the recording with a reason, a note and a log line, and the files are finished; nothing escapes into the thread pool.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            reason = "unexpected error";
            errorNote = Describe("The recording stopped because of an unexpected error", ex);
            _warn?.Invoke(Describe("The study recorder stopped because of an unexpected error", ex));
        }

        Complete(owner, session, metadata, reason, errorNote);
    }

    /// <summary>
    /// Ends a run on every path: finishes the files, clears the run's state first (so a handler that starts again finds a clean recorder), then publishes
    /// <see cref="StudyRecorderState.Stopped"/> and releases anyone waiting in <see cref="StopAsync(string)"/>.
    /// </summary>
    private void Complete(CancellationTokenSource owner, SessionFolder? session, SessionMetadata? metadata, string reason, string? note)
    {
        string? metadataNote = null;
        if (session is not null && metadata is not null)
        {
            metadata.StopReason = reason;
            metadata.EndedUtc = _time.GetUtcNow();
            if (!session.TryWriteMetadata(metadata, out var problem))
            {
                metadataNote = $"metadata.json could not be written: {problem}";
            }
        }

        TaskCompletionSource done;
        lock (_gate)
        {
            if (ReferenceEquals(_cts, owner))
            {
                _cts = null;
            }

            if (note is not null)
            {
                _notes.Add(note);
            }

            if (metadataNote is not null)
            {
                _notes.Add(metadataNote);
            }

            _status = _status with
            {
                State = StudyRecorderState.Stopped,
                StopReason = reason,
                Pictures = metadata?.Frames.Count ?? _status.Pictures,
                Bytes = metadata?.TotalBytes ?? _status.Bytes,
                Notes = [.. _notes],
            };
            done = _done;
        }

        owner.Dispose();
        try
        {
            Raise();
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private async Task<int?> WaitForGameAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_mayRecord())
                {
                    return null;
                }

                var game = _game();
                if (game.State == GameState.Running && game.Process is { } process)
                {
                    return process.ProcessId;
                }

                await _delay(GamePoll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // hygiene: allow-swallow Stopping while waiting is the normal end; the caller turns the null into the "stopped before the game started" result.
            return null;
        }
    }

    private async Task<string> CaptureLoopAsync(IStudyFrameSource source, SessionFolder session, SessionMetadata metadata, CancellationToken cancellationToken)
    {
        var policy = new StudyCapturePolicy();
        var lastGamePoll = _time.GetUtcNow();
        string? lastStatus = null;
        DateTimeOffset? lastFailedAttempt = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_mayRecord())
                {
                    return "owner status lost";
                }

                if (policy.CapReached)
                {
                    return "2 GB limit reached";
                }

                var now = _time.GetUtcNow();
                if (now - lastGamePoll >= GamePoll)
                {
                    lastGamePoll = now;
                    if (_game().State != GameState.Running)
                    {
                        return "game closed";
                    }
                }

                var (width, height) = source.LatestContentSize;
                var retryDue = lastFailedAttempt is not { } failed || now - failed >= StudyCapturePolicy.RetryAfterFailure;
                var reason = width < 1 ? FrameReason.Scheduled : policy.Decide(now, width, height);
                if (reason != FrameReason.None && retryDue)
                {
                    var sample = await source.CaptureFullFrameAsync(cancellationToken).ConfigureAwait(false);

                    // Taking a picture can wait a few seconds (a capture start, a slow frame): check again just before it is written.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_mayRecord())
                    {
                        return "owner status lost";
                    }

                    ReportStatus(metadata, sample.State, ref lastStatus, now);
                    lastFailedAttempt = sample.Frame is null ? now : null;
                    if (sample.Frame is { } frame && SaveFrame(session, metadata, policy, frame, sample, reason) is { } stopped)
                    {
                        return stopped;
                    }
                }

                await _delay(Tick, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // hygiene: allow-swallow Stopping is the normal end; the caller finishes the files with this reason.
            return StopReason();
        }
    }

    private string? SaveFrame(SessionFolder session, SessionMetadata metadata, StudyCapturePolicy policy, PixelBuffer frame, FullFrameSample sample, FrameReason reason)
    {
        var index = metadata.Frames.Count + 1;
        var name = $"frame-{index:D5}.png";
        var bytes = PngEncoder.Encode(frame.Width, frame.Height, frame.Bgra.Span);
        try
        {
            session.WriteFile(name, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Publish(s => s, $"Could not save a picture ({ex.Message}). Is the disk full?");
            return "disk error";
        }

        metadata.Frames.Add(new FrameRecord(index, name, sample.CapturedAt, frame.Width, frame.Height, bytes.Length, reason == FrameReason.Resize ? "window-resized" : "scheduled"));
        metadata.TotalBytes += bytes.Length;
        metadata.FirstFrameWidth ??= frame.Width;
        metadata.FirstFrameHeight ??= frame.Height;
        metadata.Dpi ??= sample.State.Dpi;
        metadata.WindowMode = sample.State.DisplayMode;
        metadata.ClientAreaOnly = (metadata.ClientAreaOnly ?? true) && sample.State.ClientAreaOnly;
        var resolution = $"{frame.Width}x{frame.Height}";
        if (!metadata.DistinctResolutions.Contains(resolution))
        {
            metadata.DistinctResolutions.Add(resolution);
        }

        policy.RecordSaved(sample.CapturedAt, frame.Width, frame.Height, bytes.Length);
        if ((index == 1 || index % 10 == 0) && !session.TryWriteMetadata(metadata, out var problem))
        {
            Publish(s => s, $"metadata.json could not be written: {problem}");
        }

        Publish(s => s with { Pictures = metadata.Frames.Count, Bytes = metadata.TotalBytes });
        return null;
    }

    private void ReportStatus(SessionMetadata metadata, CaptureState state, ref string? lastStatus, DateTimeOffset now)
    {
        var key = state.Status.ToString();
        if (key == lastStatus)
        {
            return;
        }

        lastStatus = key;
        metadata.StatusChanges.Add(new StatusRecord(now, key, state.Detail));
        if (state.Status != CaptureStatus.Ready)
        {
            Publish(s => s, $"Capture status: {state.Status}. {state.Detail}");
        }
    }
}
