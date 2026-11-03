using System.Globalization;

namespace DbdPresence.Study;

/// <summary>
/// Turns the owner's answers into <c>labels.jsonl</c> lines (DETECTION_VALIDATION §12.4). The labels come only from the owner's answers: the proposed
/// ranges say which pictures a scene answer covers, and nothing a detector read is used anywhere in this class.
/// </summary>
internal static class LabelFactory
{
    /// <summary>The label text of a unit the owner could not tell from the pictures: it is written with <c>legible: false</c> and left out of precision and recall.</summary>
    public const string UnclearLabel = "Unknown";

    /// <summary>Builds the label lines for a session.</summary>
    /// <param name="plan">The proposed ranges.</param>
    /// <param name="answers">The owner's answers (label pass).</param>
    /// <param name="labelledAt">When the answers were applied.</param>
    /// <param name="reviewStatus">"labelled", "rechecked" or "disputed".</param>
    public static IReadOnlyList<string> Build(ReviewPlan plan, ReviewAnswers answers, DateTimeOffset labelledAt, string reviewStatus)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(answers);
        var lines = new List<string>();
        var starts = answers.Segments.Where(s => s.MatchStart).Select(s => s.Index).Order().ToList();
        string TrialOf(int segment) => "t" + starts.Count(s => s <= segment).ToString("D2", CultureInfo.InvariantCulture);

        foreach (var segment in answers.Segments.OrderBy(s => s.Index))
        {
            if (segment.Scene == ReviewVocabulary.NoScene)
            {
                continue; // nothing is claimed about a range the owner could not assign to one screen: it simply is not a unit
            }

            var p = plan.Segments[segment.Index];
            lines.Add(Line("segment", TrialOf(segment.Index), "scene", segment.Scene, p.FromFrame, p.ToFrame, legible: true, labelledAt, reviewStatus, answers.Source));
            if (segment.OwnStatus.Length > 0)
            {
                var clear = segment.OwnStatus != "?";
                lines.Add(Line("segment", TrialOf(segment.Index), "own-status", clear ? segment.OwnStatus : UnclearLabel, p.FromFrame, p.ToFrame, clear, labelledAt, reviewStatus, answers.Source));
            }
        }

        foreach (var match in answers.Matches.OrderBy(m => m.StartIndex))
        {
            var from = plan.Segments[match.StartIndex].FromFrame;
            var next = starts.FirstOrDefault(s => s > match.StartIndex, -1);
            var to = next < 0 ? plan.Segments[^1].ToFrame : plan.Segments[next].FromFrame - 1;
            var trial = TrialOf(match.StartIndex);
            void Fact(string detector, string value)
            {
                if (value.Length > 0)
                {
                    var clear = value != "?";
                    lines.Add(Line("trial-fact", trial, detector, clear ? value : UnclearLabel, from, to, clear, labelledAt, reviewStatus, answers.Source));
                }
            }

            Fact("role", match.Role);
            Fact("character", match.Character);
            Fact("map", match.Map);
            Fact("killer", match.Killer);
            Fact("queue-mode", match.Queue);
            if (match.Role == "Survivor")
            {
                Fact("result.survivor", match.Result);
            }
        }

        return lines;
    }

    private static string Line(string type, string trial, string detector, string label, int from, int to, bool legible, DateTimeOffset at, string status, string source) =>
        JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteString("type", type);
            w.WriteString("trialId", trial);
            w.WriteString("detector", detector);
            w.WriteString("label", label);
            w.WriteString("fromFrame", ReviewPlan.FrameId(from));
            w.WriteString("toFrame", ReviewPlan.FrameId(to));
            w.WriteBoolean("legible", legible);
            w.WriteString("labeller", source);
            w.WriteString("source", source);
            w.WriteString("labelledAtUtc", JsonText.Time(at));
            w.WriteString("reviewStatus", status);
            w.WriteEndObject();
        });
}
