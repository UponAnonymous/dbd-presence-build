using DbdPresence.Core.Capture;
using DbdPresence.Platform.Windows.Capture;
using DbdPresence.Study;

namespace DbdPresence.App.Composition;

/// <summary>Gives the study recorder whole pictures of the game window from the Windows capture adapter (ADR-0003); the same adapter the detection loop uses.</summary>
/// <param name="source">The capture session for the game's process.</param>
internal sealed class WgcStudyFrameSource(WgcWindowCaptureSource source) : IStudyFrameSource
{
    /// <inheritdoc />
    public (int Width, int Height) LatestContentSize => source.LatestContentSize;

    /// <inheritdoc />
    public Task<FullFrameSample> CaptureFullFrameAsync(CancellationToken cancellationToken) => source.CaptureFullFrameAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => source.Dispose();
}
