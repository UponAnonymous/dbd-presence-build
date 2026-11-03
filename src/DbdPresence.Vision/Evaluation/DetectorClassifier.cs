using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;

namespace DbdPresence.Vision.Evaluation;

/// <summary>Adapts an <see cref="IDetector"/> to the evaluator: the real detector code runs on every frame.</summary>
public static class DetectorClassifier
{
    /// <summary>
    /// Creates a classifier that crops the detector's regions out of each frame, runs <see cref="IDetector.Analyze"/> and reports the
    /// observation of <paramref name="kind"/>. An unreadable observation becomes an abstention.
    /// </summary>
    public static FrameClassifier Create(IDetector detector, ObservationKind kind, ContextKey context)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(context);
        return frame =>
        {
            var detection = new DetectionContext(context, frame.TimestampUtc);
            var crops = FrameCrops.FromFrame(frame.Image, detector.Regions(detection));
            var observation = detector.Analyze(crops, detection).Observations.FirstOrDefault(o => o.Kind == kind);
            return observation is null ? new FramePrediction(null, 0) : new FramePrediction(observation.Value, observation.Score);
        };
    }
}
