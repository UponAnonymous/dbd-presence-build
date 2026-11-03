using DbdPresence.Vision.Imaging;

namespace DbdPresence.Study;

/// <summary>
/// Proposes where one screen ends and the next begins, from the pictures alone, as a <b>navigation aid for the person labelling</b>. It uses a coarse
/// brightness grid per picture and the time between pictures. It never looks at text, never uses a HUD profile region and never runs a detector, so the
/// proposals cannot be circular with the detectors under test: a proposal is only a range of pictures to look at, and the owner's answer is the label.
/// </summary>
internal static class SegmentProposer
{
    /// <summary>Width of the brightness grid.</summary>
    public const int GridWidth = 32;

    /// <summary>Height of the brightness grid.</summary>
    public const int GridHeight = 18;

    /// <summary>The mean brightness difference (0 to 1) between neighbouring pictures above which a new segment starts. A navigation default, not a measurement.</summary>
    public const double BreakDifference = 0.08;

    /// <summary>A gap in time larger than this always starts a new segment (the window was minimised, or capture paused).</summary>
    public static readonly TimeSpan GapBreak = TimeSpan.FromSeconds(12);

    /// <summary>Consecutive runs of at most this many pictures are shown as one segment (live 3D scenes change from picture to picture).</summary>
    public const int ShortRun = 2;

    /// <summary>Returns the brightness grid of a picture.</summary>
    public static byte[] Signature(ImageBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.ToGray().Resize(GridWidth, GridHeight).Pixels.ToArray();
    }

    /// <summary>Returns the mean absolute difference of two grids, from 0 to 1.</summary>
    public static double Difference(byte[] a, byte[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length)
        {
            return 1.0;
        }

        var sum = 0L;
        for (var i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum / (255.0 * a.Length);
    }

    /// <summary>Proposes inclusive picture ranges (zero-based) that cover every picture exactly once, in order.</summary>
    public static IReadOnlyList<(int From, int To)> Propose(IReadOnlyList<byte[]> signatures, IReadOnlyList<DateTimeOffset> times)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(times);
        if (signatures.Count != times.Count)
        {
            throw new ArgumentException("One time per picture is needed.", nameof(times));
        }

        if (signatures.Count == 0)
        {
            return [];
        }

        var runs = new List<(int From, int To)>();
        var start = 0;
        for (var i = 1; i < signatures.Count; i++)
        {
            if (Difference(signatures[i - 1], signatures[i]) > BreakDifference || times[i] - times[i - 1] > GapBreak)
            {
                runs.Add((start, i - 1));
                start = i;
            }
        }

        runs.Add((start, signatures.Count - 1));

        var segments = new List<(int From, int To)>();
        (int From, int To)? pending = null;
        foreach (var run in runs)
        {
            if (run.To - run.From + 1 <= ShortRun)
            {
                pending = pending is { } p ? (p.From, run.To) : run;
                continue;
            }

            if (pending is { } flushed)
            {
                segments.Add(flushed);
                pending = null;
            }

            segments.Add(run);
        }

        if (pending is { } last)
        {
            segments.Add(last);
        }

        return segments;
    }

    /// <summary>Picks the pictures shown for a segment: the first, the middle one when there are at least three, and the last.</summary>
    public static IReadOnlyList<int> ThumbnailFrames(int from, int to)
    {
        if (to <= from)
        {
            return [from];
        }

        return to - from >= 2 ? [from, from + ((to - from) / 2), to] : [from, to];
    }
}
