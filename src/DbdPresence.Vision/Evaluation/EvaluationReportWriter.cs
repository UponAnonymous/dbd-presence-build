using System.Globalization;
using System.Text;

namespace DbdPresence.Vision.Evaluation;

/// <summary>
/// Renders an <see cref="EvaluationReport"/> as Markdown for <c>docs/evidence/detection-*.md</c>. Figures are copied from the
/// report, never typed by hand; anything that could not be computed prints as <c>UNKNOWN</c>. The output has no timestamps, so a
/// re-run on the same data is identical.
/// </summary>
public static class EvaluationReportWriter
{
    /// <summary>Renders the report.</summary>
    public static string ToMarkdown(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var o = report.Options;
        var sb = new StringBuilder();
        sb.Append("# Detection evaluation: ").Append(o.Detector).Append(" v").Append(o.DetectorVersion.ToString(CultureInfo.InvariantCulture)).Append('\n').Append('\n');
        if (o.Origin == DataOrigin.Synthetic)
        {
            sb.Append("> **MECHANICS ONLY.** Synthetic data. These numbers show that the evaluator works. They are not detector accuracy and cannot earn a tier.\n\n");
        }

        if (o.Origin == DataOrigin.Tuning)
        {
            sb.Append("> **TUNING SPLIT.** These sessions are the data the thresholds are calibrated on. These numbers are not accuracy and cannot earn a tier.\n\n");
        }

        sb.Append("| Item | Value |\n|---|---|\n");
        Row(sb, "Dataset", o.DatasetId);
        Row(sb, "Context", o.Context);
        Row(sb, "Data origin", o.Origin.ToString());
        Row(sb, "Confirmation rule", string.Create(CultureInfo.InvariantCulture, $"K={o.Rule.K}, N={o.Rule.N}, dwell={o.Rule.Dwell.TotalSeconds:0.##} s, score floor={o.Rule.MinScore:0.##}"));
        Row(sb, "Units (supplied / with legible truth)", string.Create(CultureInfo.InvariantCulture, $"{report.Units} / {report.LegibleUnits}"));
        Row(sb, "Sessions (legible units)", report.Sessions.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Confirmed / correct / abstained", string.Create(CultureInfo.InvariantCulture, $"{report.Confirmed} / {report.Correct} / {report.Abstained}"));
        Row(sb, "Confirmed on units without legible truth (not counted)", report.ConfirmedWithoutTruth.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Precision of published values", Describe(report.Precision));
        Row(sb, "Coverage (recall)", Describe(report.Coverage));
        Row(sb, "Abstention rate", Describe(report.AbstentionRate));
        Row(sb, "Time to detect, median", Seconds(report.MedianSecondsToDetect));
        Row(sb, "Time to detect, p95 (nearest rank)", Seconds(report.P95SecondsToDetect));

        sb.Append("\n## Per class\n\n| Class | Truth units | Confirmed as class | TP | FP | Abstained | Precision | Recall | FP rate | Publishable class |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var c in report.Classes)
        {
            sb.Append("| ").Append(c.Label)
                .Append(" | ").Append(c.TruthUnits.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(c.ConfirmedAsClass.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(c.TruePositives.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(c.FalsePositives.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(c.Abstained.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(Describe(c.Precision))
                .Append(" | ").Append(Describe(c.Recall))
                .Append(" | ").Append(Describe(c.FalsePositiveRate))
                .Append(" | ").Append(c.Publishable ? "yes" : "no")
                .Append(" |\n");
        }

        sb.Append("\n## Confusion matrix (rows: truth, columns: confirmed)\n\n| truth \\ confirmed |");
        foreach (var name in report.Confusion.Classes)
        {
            sb.Append(' ').Append(name).Append(" |");
        }

        sb.Append(" (abstain) |\n|---|");
        sb.Append(string.Concat(Enumerable.Repeat("---|", report.Confusion.Classes.Count + 1))).Append('\n');
        for (var i = 0; i < report.Confusion.Classes.Count; i++)
        {
            sb.Append("| ").Append(report.Confusion.Classes[i]).Append(" |");
            foreach (var count in report.Confusion.Counts[i])
            {
                sb.Append(' ').Append(count.ToString(CultureInfo.InvariantCulture)).Append(" |");
            }

            sb.Append('\n');
        }

        sb.Append("\n## Gates\n\n");
        foreach (var note in report.Gates.Notes)
        {
            sb.Append("- ").Append(note).Append('\n');
        }

        foreach (var item in report.Gates.NotAssessed)
        {
            sb.Append("- Not assessed by this report: ").Append(item).Append('\n');
        }

        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string name, string value) => sb.Append("| ").Append(name).Append(" | ").Append(value).Append(" |\n");

    private static string Seconds(double? value) => value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:0.0} s") : "UNKNOWN";

    private static string Describe(Proportion p)
    {
        if (p.Value is not { } value || p.Wilson is not { } ci)
        {
            return "UNKNOWN";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value * 100:0.0}% ({p.Successes}/{p.Trials}; 95% Wilson {ci.Lower * 100:0.0}% to {ci.Upper * 100:0.0}%)");
    }
}
