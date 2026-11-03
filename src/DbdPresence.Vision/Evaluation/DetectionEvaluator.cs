using DbdPresence.Core.Detection;

namespace DbdPresence.Vision.Evaluation;

/// <summary>
/// Offline evaluator (C5, FR-DET-16). It feeds each unit's frames to a classifier, applies the engine's own K-of-N
/// confirmation rule to the readings, and compares the confirmed value with the ground truth. It is deterministic: the same
/// units and classifier always give the same report, byte for byte. A unit counts as an error when <em>any</em> value confirmed inside it
/// differs from the truth ("silence over error").
/// <para>
/// The evaluator measures; it never decides a tier. Synthetic data yields mechanics-only reports that cannot earn a tier.
/// Accuracy numbers for real game data exist only when the owner supplies a held-out set (C6, PENDING-ENV).
/// </para>
/// </summary>
public static class DetectionEvaluator
{
    /// <summary>Evaluates a set of units.</summary>
    public static EvaluationReport Run(IEnumerable<EvaluationUnit> units, FrameClassifier classifier, EvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(options);
        options.Rule.Validate();

        var results = new List<UnitResult>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            if (!ids.Add(unit.UnitId))
            {
                throw new ArgumentException($"Duplicate unit id '{unit.UnitId}'.", nameof(units));
            }

            results.Add(Judge(unit, classifier, options.Rule));
        }

        var legible = results.Where(r => r.Truth is not null).ToList();
        var confirmed = legible.Where(r => r.Predicted is not null).ToList();
        var correct = confirmed.Where(r => r.Predicted == r.Truth).ToList();
        var classes = legible.Select(r => r.Truth!).Concat(confirmed.Select(r => r.Predicted!)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var perClass = classes.Select(c => ClassOf(c, legible)).ToList();
        var seconds = correct.Where(r => r.Seconds is not null).Select(r => r.Seconds!.Value).Order().ToList();
        var sessions = legible.Select(r => r.SessionId).Distinct(StringComparer.Ordinal).Count();

        var precision = new Proportion(correct.Count, confirmed.Count);
        var coverage = new Proportion(correct.Count, legible.Count);

        return new EvaluationReport
        {
            Options = options,
            Units = results.Count,
            LegibleUnits = legible.Count,
            ConfirmedWithoutTruth = results.Count(r => r.Truth is null && r.Predicted is not null),
            Sessions = sessions,
            Confirmed = confirmed.Count,
            Correct = correct.Count,
            Abstained = legible.Count - confirmed.Count,
            Precision = precision,
            Coverage = coverage,
            AbstentionRate = new Proportion(legible.Count - confirmed.Count, legible.Count),
            Classes = perClass,
            Confusion = Confusion(classes, legible),
            MedianSecondsToDetect = Median(seconds),
            P95SecondsToDetect = NearestRank(seconds, 0.95),
            Gates = Assess(options.Origin, legible.Count, sessions, precision, coverage),
        };
    }

    private static UnitResult Judge(EvaluationUnit unit, FrameClassifier classifier, ConfirmationRule rule)
    {
        var window = new ObservationWindow(rule.N * 2);
        string? current = null;
        DateTimeOffset? firstFrame = null;
        DateTimeOffset? truthConfirmedAt = null;
        var confirmedValues = new List<string>();
        foreach (var frame in unit.Frames)
        {
            firstFrame ??= frame.TimestampUtc;
            var prediction = classifier(frame);
            if (prediction.Label is null)
            {
                continue; // Unreadable frames neither confirm nor refute.
            }

            window.Trim(frame.TimestampUtc, rule.MaxAge);
            window.Add(prediction.Label, prediction.Score, frame.TimestampUtc);
            if (window.Evaluate(rule, 1) is { } verdict && !string.Equals(verdict.Value, current, StringComparison.Ordinal))
            {
                current = verdict.Value;
                confirmedValues.Add(current);
                if (unit.TruthLabel is not null && string.Equals(current, unit.TruthLabel, StringComparison.Ordinal))
                {
                    truthConfirmedAt ??= frame.TimestampUtc;
                }
            }
        }

        if (confirmedValues.Count == 0)
        {
            return new UnitResult(unit.UnitId, unit.SessionId, unit.TruthLabel, null, null);
        }

        var wrong = unit.TruthLabel is null ? null : confirmedValues.FirstOrDefault(v => !string.Equals(v, unit.TruthLabel, StringComparison.Ordinal));
        var predicted = unit.TruthLabel is null ? confirmedValues[0] : wrong ?? unit.TruthLabel;
        double? seconds = wrong is null && truthConfirmedAt is { } at && firstFrame is { } first ? (at - first).TotalSeconds : null;
        return new UnitResult(unit.UnitId, unit.SessionId, unit.TruthLabel, predicted, seconds);
    }

    private static ClassMetrics ClassOf(string label, List<UnitResult> legible)
    {
        var truthUnits = legible.Count(r => r.Truth == label);
        var asClass = legible.Count(r => r.Predicted == label);
        var tp = legible.Count(r => r.Truth == label && r.Predicted == label);
        var fp = legible.Count(r => r.Truth != label && r.Predicted == label);
        var abstained = legible.Count(r => r.Truth == label && r.Predicted is null);
        var others = legible.Count - truthUnits;
        return new ClassMetrics(
            label,
            truthUnits,
            asClass,
            tp,
            fp,
            abstained,
            new Proportion(tp, asClass),
            new Proportion(tp, truthUnits),
            new Proportion(fp, others),
            Publishable: truthUnits >= 10 && fp == 0);
    }

    private static ConfusionMatrix Confusion(List<string> classes, List<UnitResult> legible)
    {
        var rows = new List<IReadOnlyList<int>>();
        foreach (var truth in classes)
        {
            var row = new int[classes.Count + 1];
            foreach (var unit in legible.Where(r => r.Truth == truth))
            {
                row[unit.Predicted is null ? classes.Count : classes.IndexOf(unit.Predicted)]++;
            }

            rows.Add(row);
        }

        return new ConfusionMatrix(classes, rows);
    }

    private static double? Median(List<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double? NearestRank(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static GateAssessment Assess(DataOrigin origin, int legible, int sessions, Proportion precision, Proportion coverage)
    {
        if (origin == DataOrigin.Synthetic)
        {
            return new GateAssessment(false, false, false, ["Synthetic data: this report proves mechanics only. It is not accuracy and cannot earn a tier."], []);
        }

        if (origin == DataOrigin.Tuning)
        {
            return new GateAssessment(false, false, false, ["Tuning split: these sessions are the data the thresholds are calibrated on. This report is not accuracy and cannot earn a tier."], []);
        }

        var errors = precision.Trials - precision.Successes;
        var cov = coverage.Value ?? 0.0;
        var experimental = legible >= 30 && sessions >= 5 && errors == 0 && cov >= 0.5;
        var lower = precision.Wilson?.Lower;
        var validated = lower is >= 0.99 && sessions >= 10 && cov >= 0.8;
        var notes = new List<string>
        {
            $"Experimental measured gates (>= 30 units, >= 5 sessions, 0 errors, coverage >= 50%): {(experimental ? "met" : "not met")}.",
            $"Validated measured gates (Wilson lower bound >= 99%, >= 10 sessions, coverage >= 80%): {(validated ? "met" : "not met")}.",
        };
        return new GateAssessment(
            true,
            experimental,
            validated,
            notes,
            ["maximum stale duration", "per-scene-class p95 time to detect limits", "RPC correctness on replay", "per-class publishable vocabulary beyond the class table"]);
    }

    private sealed record UnitResult(string UnitId, string SessionId, string? Truth, string? Predicted, double? Seconds);
}
