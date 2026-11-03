namespace DbdPresence.Vision.Imaging;

/// <summary>What the normaliser concluded about a recording's pictures.</summary>
public enum ClientAreaOutcome
{
    /// <summary>The pictures are already the game's own picture (recorded as client area, or a standard game size whose top shows no title bar, or not a windowed recording).</summary>
    NotNeeded,

    /// <summary>A title bar and border were found and a crop that removes them was chosen.</summary>
    Applied,

    /// <summary>The pictures may include a title bar and border but the heuristic could not confirm it; they are used as they are and the report says so.</summary>
    Unverified,
}

/// <summary>The decision for one recording.</summary>
/// <param name="Outcome">What was concluded.</param>
/// <param name="Crop">The rectangle to keep when <see cref="Outcome"/> is <see cref="ClientAreaOutcome.Applied"/>; otherwise null.</param>
/// <param name="SourceWidth">Width of the recorded pictures.</param>
/// <param name="SourceHeight">Height of the recorded pictures.</param>
/// <param name="Reason">One plain sentence for the evaluator report. Holds sizes only, never screen text.</param>
public sealed record ClientAreaDecision(ClientAreaOutcome Outcome, PixelRect? Crop, int SourceWidth, int SourceHeight, string Reason)
{
    /// <summary>Gets the width the detectors will see.</summary>
    public int EffectiveWidth => Crop?.Width ?? SourceWidth;

    /// <summary>Gets the height the detectors will see.</summary>
    public int EffectiveHeight => Crop?.Height ?? SourceHeight;
}

/// <summary>
/// Heuristic for study recordings made before the recorder cut windowed frames to the client area. The owner's first three recordings are
/// 1922x1112: a 1920x1080 game plus a one pixel border and a 31 pixel title bar, so every normalised HUD region sat about one title bar too
/// high. The size alone proposes a crop (a standard game size plus a plausible window frame); the pixels must then confirm it (a flat bar above the
/// proposed top edge that changes at that edge). If either part fails, nothing is cropped and the outcome is
/// <see cref="ClientAreaOutcome.Unverified"/>. This is a documented heuristic, not a measurement: new recordings carry the recorder's own
/// <c>ClientAreaOnly</c> flag and never reach it. It reads pictures only; it never changes a label.
/// </summary>
public static class ClientAreaNormaliser
{
    /// <summary>The most pixels the left and right borders may add together (a thin border; an initial guess).</summary>
    public const int MaxBorderTotal = 16;

    /// <summary>The least the title bar may add to the height (an initial guess; a 100% scale caption is about 31 pixels).</summary>
    public const int MinTitleBar = 14;

    /// <summary>The most the title bar and bottom border may add to the height (an initial guess; covers a 250% display scale).</summary>
    public const int MaxTitleBar = 96;

    /// <summary>How much one colour channel may vary along a row of the title bar and still be flat.</summary>
    public const int FlatTolerance = 10;

    /// <summary>How much the colour must change from the last bar row to the first game row for the edge to count.</summary>
    public const int EdgeContrast = 8;

    /// <summary>How many pictures of a recording are examined.</summary>
    public const int MaxSamples = 5;

    private static readonly (int Width, int Height)[] s_standard =
    [
        (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3200, 1800), (3840, 2160),
        (1280, 800), (1680, 1050), (1920, 1200), (2560, 1600),
    ];

    /// <summary>Gets the game sizes the heuristic knows (16:9 and 16:10 desktop sizes).</summary>
    public static IReadOnlyList<(int Width, int Height)> StandardSizes => s_standard;

    /// <summary>Returns the indexes of the pictures to examine: up to <see cref="MaxSamples"/>, spread over the recording.</summary>
    /// <param name="frameCount">How many pictures the recording has.</param>
    public static IReadOnlyList<int> SampleIndexes(int frameCount)
    {
        if (frameCount < 1)
        {
            return [];
        }

        var count = Math.Min(frameCount, MaxSamples);
        return [.. Enumerable.Range(0, count).Select(i => count == 1 ? 0 : (int)Math.Round((double)i * (frameCount - 1) / (count - 1), MidpointRounding.AwayFromZero)).Distinct()];
    }

    /// <summary>Decides from a handful of the recording's pictures (all the same size).</summary>
    /// <param name="samples">The sampled pictures; at least one.</param>
    public static ClientAreaDecision Decide(IReadOnlyList<ImageBuffer> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one picture is needed.", nameof(samples));
        }

        var width = samples[0].Width;
        var height = samples[0].Height;
        if (samples.Any(s => s.Width != width || s.Height != height))
        {
            return new ClientAreaDecision(ClientAreaOutcome.Unverified, null, width, height, "The pictures of this recording differ in size, so no single crop can be chosen.");
        }

        if (s_standard.Contains((width, height)))
        {
            if (samples.All(HasTitleBarLikeTop))
            {
                return new ClientAreaDecision(ClientAreaOutcome.Unverified, null, width, height, $"{width}x{height} is a standard game size, but every sampled picture starts with a flat bar that ends in an edge, so a window title bar cannot be excluded; used as it is.");
            }

            return new ClientAreaDecision(ClientAreaOutcome.NotNeeded, null, width, height, $"{width}x{height} is a standard game size; used as it is.");
        }

        var candidates = Candidates(width, height);
        if (candidates.Count == 0)
        {
            return new ClientAreaDecision(
                ClientAreaOutcome.Unverified,
                null,
                width,
                height,
                $"{width}x{height} is not a standard game size and no standard size plus a window frame explains it; used as it is, so a title bar cannot be excluded.");
        }

        if (candidates.Count > 1)
        {
            return new ClientAreaDecision(ClientAreaOutcome.Unverified, null, width, height, $"{width}x{height} fits more than one standard size plus a window frame; used as it is.");
        }

        var (standardWidth, standardHeight) = candidates[0];
        var extraWidth = width - standardWidth;
        var extraHeight = height - standardHeight;
        var left = extraWidth / 2;
        var preferredBottom = extraWidth / 2;
        foreach (var bottom in Enumerable.Range(0, extraWidth + 1).OrderBy(b => b == preferredBottom ? 0 : 1).ThenBy(b => b))
        {
            var top = extraHeight - bottom;
            if (top >= MinTitleBar && samples.All(s => ShowsFlatBarAbove(s, top)))
            {
                var crop = new PixelRect(left, top, standardWidth, standardHeight);
                return new ClientAreaDecision(
                    ClientAreaOutcome.Applied,
                    crop,
                    width,
                    height,
                    $"{width}x{height} read as {standardWidth}x{standardHeight} plus a window frame (title bar {top} px, border {left}/{extraWidth - left}/{bottom} px left/right/bottom); {samples.Count} sampled picture(s) show a flat bar that ends at that edge. Heuristic, not a measurement.");
            }
        }

        return new ClientAreaDecision(
            ClientAreaOutcome.Unverified,
            null,
            width,
            height,
            $"{width}x{height} would be {standardWidth}x{standardHeight} plus a window frame, but the pictures do not show a flat bar ending at the expected edge; used as it is.");
    }

    /// <summary>Returns the standard sizes that this picture size can be explained by (a standard size plus a plausible window frame).</summary>
    public static IReadOnlyList<(int Width, int Height)> Candidates(int width, int height) =>
        [.. s_standard.Where(s =>
        {
            var extraWidth = width - s.Width;
            var extraHeight = height - s.Height;
            return extraWidth >= 0 && extraWidth <= MaxBorderTotal && extraHeight - extraWidth >= MinTitleBar && extraHeight <= MaxTitleBar + extraWidth;
        })];

    /// <summary>Applies a decision to one picture of the recording.</summary>
    /// <param name="frame">The picture.</param>
    /// <param name="decision">The decision for its recording.</param>
    /// <exception cref="InvalidOperationException">The picture's size is not the size the decision was made for.</exception>
    public static ImageBuffer Apply(ImageBuffer frame, ClientAreaDecision decision)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Crop is not { } crop)
        {
            return frame;
        }

        if (frame.Width != decision.SourceWidth || frame.Height != decision.SourceHeight)
        {
            throw new InvalidOperationException("A picture of this recording has a different size from the others, so the client-area crop cannot be applied.");
        }

        return frame.Crop(crop);
    }

    /// <summary>
    /// Whether rows 1 to <c>top - 1</c> are each flat across the middle of the picture (the title bar; row 0 is the border line) and the colour
    /// changes at <c>top</c> (the game starts there).
    /// </summary>
    private static bool ShowsFlatBarAbove(ImageBuffer frame, int top)
    {
        if (top < 3 || top >= frame.Height)
        {
            return false;
        }

        var x0 = (int)(frame.Width * 0.35);
        var x1 = (int)(frame.Width * 0.75);
        for (var y = 1; y < top; y++)
        {
            if (!RowIsFlat(frame, y, x0, x1, out _))
            {
                return false;
            }
        }

        _ = RowIsFlat(frame, top - 1, x0, x1, out var barMean);
        var firstGameRowIsFlat = RowIsFlat(frame, top, x0, x1, out var gameMean);
        return !firstGameRowIsFlat || Math.Abs(barMean.R - gameMean.R) >= EdgeContrast || Math.Abs(barMean.G - gameMean.G) >= EdgeContrast || Math.Abs(barMean.B - gameMean.B) >= EdgeContrast;
    }

    /// <summary>
    /// Whether the picture starts (below the border line in row 0) with at least <see cref="MinTitleBar"/> flat rows and then a visible edge: the
    /// look of a window title bar inside a capture whose outer size happens to be a standard game size.
    /// </summary>
    private static bool HasTitleBarLikeTop(ImageBuffer frame)
    {
        var x0 = (int)(frame.Width * 0.35);
        var x1 = (int)(frame.Width * 0.75);
        var top = 1;
        while (top < frame.Height && top <= MaxTitleBar && RowIsFlat(frame, top, x0, x1, out _))
        {
            top++;
        }

        return top >= MinTitleBar && top <= MaxTitleBar && ShowsFlatBarAbove(frame, top);
    }

    private static bool RowIsFlat(ImageBuffer frame, int y, int x0, int x1, out (int R, int G, int B) mean)
    {
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        long sumR = 0, sumG = 0, sumB = 0;
        var n = 0;
        for (var x = x0; x < x1; x += 2)
        {
            var (r, g, b) = frame.RgbAt(x, y);
            minR = Math.Min(minR, r);
            maxR = Math.Max(maxR, r);
            minG = Math.Min(minG, g);
            maxG = Math.Max(maxG, g);
            minB = Math.Min(minB, b);
            maxB = Math.Max(maxB, b);
            sumR += r;
            sumG += g;
            sumB += b;
            n++;
        }

        mean = n == 0 ? (0, 0, 0) : ((int)(sumR / n), (int)(sumG / n), (int)(sumB / n));
        return n > 0 && maxR - minR <= FlatTolerance && maxG - minG <= FlatTolerance && maxB - minB <= FlatTolerance;
    }
}
