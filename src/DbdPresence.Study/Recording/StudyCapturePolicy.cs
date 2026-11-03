namespace DbdPresence.StudyCapture;

/// <summary>Why a frame is saved.</summary>
internal enum FrameReason
{
    /// <summary>Not yet.</summary>
    None,

    /// <summary>The 2-second cadence elapsed.</summary>
    Scheduled,

    /// <summary>The window changed size and has stayed at the new size (one extra frame per change).</summary>
    Resize,
}

/// <summary>
/// When to save a frame and when to stop. Pure and clock-free: callers pass the current time, so it is unit tested on Linux
/// through a linked source file in the Core test project.
/// </summary>
internal sealed class StudyCapturePolicy
{
    /// <summary>Time between scheduled frames.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>How long a new window size must stay unchanged before its extra frame is taken (a drag-resize makes one frame, not many).</summary>
    public static readonly TimeSpan ResizeSettle = TimeSpan.FromSeconds(1);

    /// <summary>Minimum time between attempts while the window cannot be read (minimised, loading, blocked).</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(1);

    /// <summary>The per-session size cap: 2 GB.</summary>
    public const long MaxSessionBytes = 2L * 1024 * 1024 * 1024;

    private DateTimeOffset? _lastSavedAt;
    private (int Width, int Height) _lastSavedSize;
    private (int Width, int Height)? _pendingSize;
    private DateTimeOffset _pendingSince;

    /// <summary>Gets the bytes written so far in this session.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Gets a value indicating whether the session cap is reached.</summary>
    public bool CapReached => TotalBytes >= MaxSessionBytes;

    /// <summary>Decides whether a frame is due, given the window's current pixel size.</summary>
    public FrameReason Decide(DateTimeOffset now, int width, int height)
    {
        if (CapReached || width < 1 || height < 1)
        {
            return FrameReason.None;
        }

        if (_lastSavedAt is not { } last)
        {
            return FrameReason.Scheduled;
        }

        var size = (width, height);
        var scheduledDue = now - last >= Interval;
        if (size == _lastSavedSize)
        {
            _pendingSize = null;
            return scheduledDue ? FrameReason.Scheduled : FrameReason.None;
        }

        if (_pendingSize != size)
        {
            _pendingSize = size;
            _pendingSince = now;
        }

        if (now - _pendingSince >= ResizeSettle)
        {
            return scheduledDue ? FrameReason.Scheduled : FrameReason.Resize;
        }

        return scheduledDue ? FrameReason.Scheduled : FrameReason.None;
    }

    /// <summary>Records a saved frame.</summary>
    public void RecordSaved(DateTimeOffset now, int width, int height, long bytes)
    {
        _lastSavedAt = now;
        _lastSavedSize = (width, height);
        _pendingSize = null;
        TotalBytes += bytes;
    }
}
