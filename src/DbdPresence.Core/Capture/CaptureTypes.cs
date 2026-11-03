namespace DbdPresence.Core.Capture;

/// <summary>The honest state of window capture (ADR-0003). Nothing is reported as working unless frames were really read.</summary>
public enum CaptureStatus
{
    /// <summary>No session has been tried yet.</summary>
    NotStarted,

    /// <summary>Frames are being delivered and contain image content.</summary>
    Ready,

    /// <summary>Windows.Graphics.Capture is not available on this system.</summary>
    Unsupported,

    /// <summary>No visible top-level window of the game process was found.</summary>
    WindowNotFound,

    /// <summary>The window is minimised, so Windows delivers no content.</summary>
    Minimized,

    /// <summary>The session could not be created or Windows refused or delivered no frame.</summary>
    Blocked,

    /// <summary>Frames arrive but every pixel is black (for example exclusive fullscreen or protected content).</summary>
    BlackFrames,

    /// <summary>The session was closed.</summary>
    Stopped,
}

/// <summary>How the window relates to its monitor. Exclusive fullscreen and borderless fullscreen cannot be told apart by bounds.</summary>
public enum WindowDisplayMode
{
    /// <summary>The bounds could not be read.</summary>
    Unknown,

    /// <summary>The window is smaller than its monitor or has a frame.</summary>
    Windowed,

    /// <summary>The window has no frame and covers its monitor exactly: borderless or exclusive fullscreen (UNKNOWN which, ADR-0003).</summary>
    FullscreenOrBorderless,
}

/// <summary>What happened when the yellow capture border was asked to be turned off (Windows 11).</summary>
public enum BorderOutcome
{
    /// <summary>Not attempted yet.</summary>
    NotAttempted,

    /// <summary>The border is drawn by Windows (default, and always on Windows 10).</summary>
    Required,

    /// <summary>Windows accepted the request and the border is off.</summary>
    Disabled,

    /// <summary>The operating system has no such option.</summary>
    NotAvailable,

    /// <summary>The system refused the request (the detail says why).</summary>
    Refused,
}

/// <summary>Snapshot of capture health.</summary>
/// <param name="Status">What works.</param>
/// <param name="Detail">One plain sentence explaining the status.</param>
/// <param name="FrameWidth">Pixel width of the last frame, 0 if none.</param>
/// <param name="FrameHeight">Pixel height of the last frame, 0 if none.</param>
/// <param name="Dpi">DPI of the window, 0 if unknown.</param>
/// <param name="DisplayMode">Window versus monitor bounds.</param>
/// <param name="Border">Capture border outcome.</param>
/// <param name="ClientAreaOnly">
/// True when the frame (and every crop) was cut to the window's client area, so title bar and border are gone and normalised regions
/// map onto the game's own picture. False means the frame is the whole captured window; for a windowed game that offsets every region.
/// </param>
public sealed record CaptureState(
    CaptureStatus Status,
    string Detail,
    int FrameWidth = 0,
    int FrameHeight = 0,
    uint Dpi = 0,
    WindowDisplayMode DisplayMode = WindowDisplayMode.Unknown,
    BorderOutcome Border = BorderOutcome.NotAttempted,
    bool ClientAreaOnly = false)
{
    /// <summary>Gets a value indicating whether frames can be read.</summary>
    public bool IsReady => Status == CaptureStatus.Ready;
}

/// <summary>One sampled frame: the requested crops, in request order.</summary>
/// <param name="State">Capture health when the frame was taken.</param>
/// <param name="Regions">Crops, one per requested region; empty when the state is not ready.</param>
/// <param name="CapturedAt">When the frame was read.</param>
public sealed record CaptureSample(CaptureState State, IReadOnlyList<PixelBuffer> Regions, DateTimeOffset CapturedAt);

/// <summary>A whole frame, for explicit owner-run study or diagnostic capture only.</summary>
/// <param name="State">Capture health when the frame was taken.</param>
/// <param name="Frame">The pixels, or null when the state is not ready.</param>
/// <param name="CapturedAt">When the frame was read.</param>
public sealed record FullFrameSample(CaptureState State, PixelBuffer? Frame, DateTimeOffset CapturedAt);

/// <summary>
/// Reads pixels of the game window only (ADR-0003). Implemented in Platform.Windows; faked in tests.
/// Region crops are the normal path; <see cref="CaptureFullFrameAsync"/> exists for explicit owner-run study capture only.
/// </summary>
public interface IWindowCaptureSource : IDisposable
{
    /// <summary>Gets the latest known state.</summary>
    CaptureState State { get; }

    /// <summary>Starts the session, finding the window by process id. Returns the resulting state.</summary>
    Task<CaptureState> StartAsync(CancellationToken cancellationToken);

    /// <summary>Takes the latest frame and returns the requested crops.</summary>
    Task<CaptureSample> CaptureAsync(IReadOnlyList<NormalizedRegion> regions, CancellationToken cancellationToken);

    /// <summary>Takes the latest frame whole. Only for explicit study capture (the owner-only study recorder in the app, or the console tool) and diagnostics; the detection loop never persists a frame.</summary>
    Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken);
}

/// <summary>Whether text recognition could run.</summary>
public enum TextRecognitionStatus
{
    /// <summary>Recognition ran (the text may still be empty).</summary>
    Recognized,

    /// <summary>The English recognition language is not installed (PLAN_V2 D17: English only).</summary>
    EnglishLanguageMissing,

    /// <summary>The image is too large for the engine.</summary>
    ImageTooLarge,

    /// <summary>Text recognition is not available on this system.</summary>
    Unavailable,
}

/// <summary>Result of reading text from an image.</summary>
/// <param name="Status">Whether recognition ran.</param>
/// <param name="Text">Recognised text (lines joined with a newline); empty unless recognition ran.</param>
/// <param name="Detail">One plain sentence, for example which language is missing.</param>
public sealed record TextRecognitionResult(TextRecognitionStatus Status, string Text, string Detail);

/// <summary>Reads English text from a pixel buffer (ADR-0004: OCR only for fixed HUD strings).</summary>
public interface ITextRecognizer
{
    /// <summary>Recognises English text.</summary>
    Task<TextRecognitionResult> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken);
}
