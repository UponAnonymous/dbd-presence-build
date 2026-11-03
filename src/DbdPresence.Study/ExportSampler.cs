namespace DbdPresence.Study;

/// <summary>Why a picture is in the review file.</summary>
internal enum ExportPictureKind
{
    /// <summary>The one picture that stands for a proposed range.</summary>
    Segment,

    /// <summary>An extra picture inside a long range, so a screen change the brightness did not split can still be seen.</summary>
    Sample,
}

/// <summary>One picture chosen for the review file.</summary>
/// <param name="Frame">The picture's number in the session (1 is the first), as in the review plan.</param>
/// <param name="Kind">Why it was chosen.</param>
/// <param name="Segment">The proposed range it belongs to.</param>
internal sealed record ExportPicture(int Frame, ExportPictureKind Kind, int Segment);

/// <summary>
/// Chooses which pictures go into the review file (DETECTION_VALIDATION §16.1): one per proposed range, plus one about every 10 seconds inside a long
/// range. Only the review plan (brightness and timing, <see cref="SegmentProposer"/>) and the pictures' clock times are used; nothing a detector read.
/// </summary>
internal static class ExportSampler
{
    /// <summary>The time between the extra pictures inside a long range.</summary>
    public static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(10);

    /// <summary>An extra picture closer than this to one already chosen is left out (it would show the same thing).</summary>
    public static readonly TimeSpan MinSeparation = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Returns the chosen pictures in priority order: the range pictures first, then the extra ones, and within each group the shorter ranges before the
    /// longer ones (then in time order). A short range is a screen that was held (a menu, the lobby, TAB, the results), where the text crops matter; a
    /// long one is mostly the live 3D trial, whose crops are large and rarely hold anything. So when the file reaches its size limit, the extra pictures go
    /// first and, among those, the ones in the longest ranges. Only the plan's timing is used.
    /// </summary>
    /// <param name="plan">The proposed ranges.</param>
    /// <param name="times">The time of every picture of the session, in order.</param>
    public static IReadOnlyList<ExportPicture> Choose(ReviewPlan plan, IReadOnlyList<DateTimeOffset> times)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(times);
        var representatives = new List<ExportPicture>();
        var extras = new List<ExportPicture>();
        foreach (var segment in plan.Segments)
        {
            if (segment.ToFrame > times.Count)
            {
                throw new FormatException("The review plan names pictures the session does not have.");
            }

            var middle = segment.FromFrame + ((segment.ToFrame - segment.FromFrame) / 2);
            representatives.Add(new ExportPicture(middle, ExportPictureKind.Segment, segment.Index));
            var chosen = new List<int> { middle };
            var start = times[segment.FromFrame - 1];
            var mark = start + SampleEvery;
            for (var frame = segment.FromFrame; frame <= segment.ToFrame; frame++)
            {
                var time = times[frame - 1];
                if (time < mark)
                {
                    continue;
                }

                while (mark <= time)
                {
                    mark += SampleEvery;
                }

                if (chosen.Any(c => (time - times[c - 1]).Duration() < MinSeparation))
                {
                    continue;
                }

                chosen.Add(frame);
                extras.Add(new ExportPicture(frame, ExportPictureKind.Sample, segment.Index));
            }
        }

        var length = plan.Segments.ToDictionary(s => s.Index, s => s.ToFrame - s.FromFrame);
        return
        [
            .. representatives.OrderBy(r => length[r.Segment]).ThenBy(r => r.Frame),
            .. extras.OrderBy(e => length[e.Segment]).ThenBy(e => e.Frame),
        ];
    }
}
