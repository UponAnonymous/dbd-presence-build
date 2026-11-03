using System.Globalization;
using DbdPresence.Vision.Evaluation;

namespace DbdPresence.Eval;

/// <summary>
/// Finds the pictures that belong to units the report counts as errors, so that freeing up space keeps them (DETECTION_VALIDATION §21). A unit is judged on its
/// own with the evaluator's own rule (<see cref="DetectionEvaluator"/>, the same classifier and options as the report), so the judgement is the report's. A unit
/// is kept when it is not a clean result: a confirmed value that differs from the truth (wrong), a readable truth where nothing was confirmed (missed), or a value
/// confirmed where the owner could not tell what was on screen (the reading stayed up when it should have stayed silent). Judging is cheap: the readings of every
/// picture were collected once already, so no picture is read again.
/// </summary>
internal static class ErrorUnits
{
    /// <summary>Adds the pictures of every error unit of one detector to <paramref name="keep"/> as (session id, file name).</summary>
    public static void Collect(
        IReadOnlyList<EvaluationUnit> units,
        IReadOnlyDictionary<string, DatasetSession> sessions,
        string detector,
        FrameClassifier classifier,
        EvaluationOptions options,
        ISet<(string Session, string File)> keep)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keep);
        foreach (var unit in units)
        {
            var single = DetectionEvaluator.Run([unit], classifier, options);
            var clean = unit.TruthLabel is null ? single.ConfirmedWithoutTruth == 0 : single.Correct == 1;
            if (clean)
            {
                continue;
            }

            // BuildUnits numbers the units of one detector in one session in label order: ".../<detector>/<index>".
            var session = sessions[unit.SessionId];
            var index = int.Parse(unit.UnitId[(unit.UnitId.LastIndexOf('/') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
            var label = session.Labels.Where(l => string.Equals(l.Detector, detector, StringComparison.Ordinal)).ElementAt(index);
            var inside = false;
            foreach (var frame in session.Frames)
            {
                inside |= frame.FrameId == label.FromFrame;
                if (inside)
                {
                    keep.Add((session.Metadata.SessionId, frame.File));
                }

                if (frame.FrameId == label.ToFrame)
                {
                    break;
                }
            }
        }
    }
}
