using DbdPresence.Core.Capture;

namespace DbdPresence.Study;

/// <summary>
/// Where the recorder gets whole pictures of the game window (ADR-0003, SAFETY_AND_PRIVACY section 2.1): the Windows capture adapter in the app and in the console
/// tool, a fake in tests. The recorder asks for a picture of the window and nothing else; it never reaches the game process.
/// </summary>
public interface IStudyFrameSource : IDisposable
{
    /// <summary>Gets the pixel size of the newest picture Windows delivered (0 x 0 before the first), read without copying any pixels.</summary>
    (int Width, int Height) LatestContentSize { get; }

    /// <summary>Takes the newest picture whole; the sample says why when there is none.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The picture and the capture state.</returns>
    Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken);
}
