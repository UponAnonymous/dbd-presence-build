using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DbdPresence.Vision.Screens;

namespace DbdPresence.Study;

/// <summary>One picture or close-up to put in a request.</summary>
/// <param name="Caption">The text that introduces it, or empty when the range's own heading does.</param>
/// <param name="Entry">The entry name in the review file.</param>
/// <param name="Length">The entry's size in bytes.</param>
internal sealed record ImageRef(string Caption, string Entry, long Length);

/// <summary>One range of a scenes request with the images it carries.</summary>
internal sealed record ChunkSegment(ManifestSegment Segment, IReadOnlyList<ImageRef> Pictures, IReadOnlyList<ImageRef> Crops);

/// <summary>A group of consecutive ranges that go in one scenes request.</summary>
internal sealed record ScenesChunk(IReadOnlyList<ChunkSegment> Segments)
{
    /// <summary>Gets the number of images the chunk carries.</summary>
    public int ImageCount => Segments.Sum(s => s.Pictures.Count + s.Crops.Count);
}

/// <summary>The ranges of one match: from the range that starts it to the range before the next start.</summary>
/// <param name="StartIndex">The range that starts the match.</param>
/// <param name="EndIndex">The last range of the match (inclusive).</param>
internal sealed record MatchSpan(int StartIndex, int EndIndex);

/// <summary>A request body ready to send.</summary>
/// <param name="Body">The whole JSON body.</param>
/// <param name="Images">How many image blocks it holds.</param>
internal sealed record LabelRequest(byte[] Body, int Images);

/// <summary>
/// Makes the bodies of the labelling requests from a review file and the closed lists (PLAN_AUTO_LABELLING sections 4 and 6). The body is exactly what the
/// relay accepts: it starts with <c>{"model":"reviewer",</c> and holds only <c>system</c>, <c>messages</c>, <c>output_config</c> and <c>fallbacks</c>. It sees
/// only the review file's pictures, times and close-up descriptions and the closed lists; no reading of any detector is available to it.
/// </summary>
internal sealed class LabelRequestBuilder
{
    /// <summary>Most ranges in one scenes request.</summary>
    public const int MaxSegments = 40;

    /// <summary>Most image blocks in one request.</summary>
    public const int MaxImages = 120;

    /// <summary>The longest side of an image that is sent; a larger one is skipped, never resized.</summary>
    public const int MaxImageSide = 2000;

    /// <summary>The most picture and close-up bytes in one request: the base64 text of these plus the words stays under the relay's 30 MB.</summary>
    public const long MaxImageBytes = 21L * 1024 * 1024;

    /// <summary>The largest request body (the relay's limit).</summary>
    public const int MaxBodyBytes = 30 * 1024 * 1024;

    private const int PerSegmentImages = 40;
    private const int PerSegmentPictures = 12;
    private const int FactsPictureRoom = 16;
    private readonly ReviewFileReader _reader;
    private readonly HudVocabulary _vocabulary;
    private readonly string _system;
    private readonly Dictionary<string, string> _covers;

    /// <summary>Initializes a new instance of the <see cref="LabelRequestBuilder"/> class.</summary>
    /// <param name="reader">The open review file.</param>
    /// <param name="vocabulary">The closed lists.</param>
    public LabelRequestBuilder(ReviewFileReader reader, HudVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(vocabulary);
        _reader = reader;
        _vocabulary = vocabulary;
        _system = LabellerPrompt.System(vocabulary);
        _covers = reader.Manifest.Regions.ToDictionary(r => r.Id, r => r.Covers, StringComparer.Ordinal);
    }

    /// <summary>Gets how many images were left out because they were larger than <see cref="MaxImageSide"/> pixels or unreadable.</summary>
    public int SkippedImages { get; private set; }

    /// <summary>Splits the ranges into scenes requests: at most 40 ranges, 120 images and the byte budget each, in order.</summary>
    /// <returns>The chunks.</returns>
    public IReadOnlyList<ScenesChunk> PlanScenes()
    {
        var chunks = new List<ScenesChunk>();
        var current = new List<ChunkSegment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var images = 0;
        var bytes = 0L;
        foreach (var segment in _reader.Manifest.Segments)
        {
            var candidate = Candidate(segment, seen);
            var size = Size(candidate);
            var count = candidate.Pictures.Count + candidate.Crops.Count;
            if (current.Count > 0 && (current.Count >= MaxSegments || images + count > MaxImages || bytes + size > MaxImageBytes))
            {
                chunks.Add(new ScenesChunk(current));
                current = [];
                seen.Clear();
                images = 0;
                bytes = 0;
                candidate = Candidate(segment, seen);
                size = Size(candidate);
                count = candidate.Pictures.Count + candidate.Crops.Count;
            }

            current.Add(candidate);
            foreach (var image in candidate.Pictures.Concat(candidate.Crops))
            {
                seen.Add(image.Entry);
            }

            images += count;
            bytes += size;
        }

        if (current.Count > 0)
        {
            chunks.Add(new ScenesChunk(current));
        }

        return chunks;
    }

    /// <summary>Makes the body of one scenes request.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="previous">The answer of the range just before the chunk, or null for the first chunk.</param>
    /// <returns>The request.</returns>
    public LabelRequest BuildScenes(ScenesChunk chunk, SegmentAnswer? previous)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var first = chunk.Segments[0].Segment.Index;
        var last = chunk.Segments[^1].Segment.Index;
        var images = 0;
        var body = Write(
            w =>
            {
                var intro = new StringBuilder();
                intro.Append(CultureInfo.InvariantCulture, $"Task: scenes. Below are the picture ranges {first} to {last} of one recording, in order. For every one of them answer its scene and whether it is the first range of a new match.");
                if (previous is not null)
                {
                    intro.Append(CultureInfo.InvariantCulture, $" The segment before this chunk was answered: {previous.Scene}, matchStart {(previous.MatchStart ? "true" : "false")}.");
                }

                WriteText(w, intro.ToString());
                foreach (var cs in chunk.Segments)
                {
                    var s = cs.Segment;
                    WriteText(w, string.Create(CultureInfo.InvariantCulture, $"Segment {s.Index}: starts {s.StartLocal}, {s.Seconds:0.#} s. Pictures:"));
                    foreach (var picture in cs.Pictures)
                    {
                        images += WriteImage(w, picture);
                    }

                    foreach (var crop in cs.Crops)
                    {
                        WriteText(w, crop.Caption);
                        images += WriteImage(w, crop);
                    }
                }

                WriteText(w, string.Create(CultureInfo.InvariantCulture, $"Answer for segments {first} to {last}, one answer for each."));
            },
            LabellerPrompt.WriteScenesSchema);
        return Finish(body, images);
    }

    /// <summary>Finds the matches: from each range answered as a match start to the range before the next one. Ranges before the first start belong to no match.</summary>
    /// <param name="answers">The scene answers of all ranges.</param>
    /// <returns>The matches in order.</returns>
    public static IReadOnlyList<MatchSpan> PlanMatches(IReadOnlyList<SegmentAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var ordered = answers.OrderBy(a => a.Index).ToList();
        var starts = ordered.Where(a => a.MatchStart).Select(a => a.Index).ToList();
        var spans = new List<MatchSpan>();
        for (var i = 0; i < starts.Count; i++)
        {
            spans.Add(new MatchSpan(starts[i], i + 1 < starts.Count ? starts[i + 1] - 1 : ordered[^1].Index));
        }

        return spans;
    }

    /// <summary>Makes the body of the facts request of one match.</summary>
    /// <param name="span">The match.</param>
    /// <param name="answers">The scene answers of all ranges.</param>
    /// <returns>The request.</returns>
    public LabelRequest BuildFacts(MatchSpan span, IReadOnlyList<SegmentAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(answers);
        var segments = _reader.Manifest.Segments.Where(s => s.Index >= span.StartIndex && s.Index <= span.EndIndex).ToList();
        var from = segments[0].FromFrame;
        var to = segments[^1].ToFrame;
        var sceneOf = answers.ToDictionary(a => a.Index, a => a.Scene);

        // Whole pictures of the lobby and results ranges are only extra; the close-ups come first.
        var wholes = segments
            .Where(s => sceneOf.GetValueOrDefault(s.Index) is "Lobby" or "Results")
            .SelectMany(s => s.Pictures.Select(p => (Segment: s, Picture: p)))
            .ToList();
        var pictures = Even(wholes, FactsPictureRoom);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var crops = _reader.Manifest.Crops.Where(c => c.Frame >= from && c.Frame <= to && seen.Add(c.File)).ToList();
        var cropRefs = FitByRegion(crops, MaxImages - pictures.Count).Select(CropRef).ToList();
        var pictureRefs = pictures.Select(p => (p.Segment, Ref: new ImageRef(string.Create(CultureInfo.InvariantCulture, $"Whole picture from segment {p.Segment.Index} ({sceneOf.GetValueOrDefault(p.Segment.Index, "?")}), starts {p.Segment.StartLocal}:"), p.Picture.File, EntryLength(p.Picture.File)))).ToList();

        // Trim to the byte budget: whole pictures go first, then close-ups from the end.
        var bytes = pictureRefs.Sum(p => p.Ref.Length) + cropRefs.Sum(c => c.Length);
        while (bytes > MaxImageBytes && pictureRefs.Count > 0)
        {
            bytes -= pictureRefs[^1].Ref.Length;
            pictureRefs.RemoveAt(pictureRefs.Count - 1);
        }

        while (bytes > MaxImageBytes && cropRefs.Count > 0)
        {
            bytes -= cropRefs[^1].Length;
            cropRefs.RemoveAt(cropRefs.Count - 1);
        }

        var images = 0;
        var body = Write(
            w =>
            {
                WriteText(w, string.Create(CultureInfo.InvariantCulture, $"Task: facts. Below are close-ups and some whole pictures of ONE match: the ranges {span.StartIndex} to {span.EndIndex}, in order. Answer role, character, map, result, killer and queue of this match."));
                foreach (var crop in cropRefs)
                {
                    WriteText(w, crop.Caption);
                    images += WriteImage(w, crop);
                }

                foreach (var (_, whole) in pictureRefs)
                {
                    WriteText(w, whole.Caption);
                    images += WriteImage(w, whole);
                }

                WriteText(w, "Answer the facts of this match now.");
            },
            w => LabellerPrompt.WriteFactsSchema(w, _vocabulary));
        return Finish(body, images);
    }

    private static List<T> Even<T>(IReadOnlyList<T> items, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        if (items.Count <= take)
        {
            return [.. items];
        }

        if (take == 1)
        {
            return [items[0]];
        }

        var picked = new List<T>(take);
        for (var i = 0; i < take; i++)
        {
            picked.Add(items[(int)((long)i * (items.Count - 1) / (take - 1))]);
        }

        return picked;
    }

    /// <summary>Takes at most <paramref name="room"/> close-ups, giving every region a fair share and keeping time order inside a region.</summary>
    private static List<ManifestCrop> FitByRegion(List<ManifestCrop> crops, int room)
    {
        if (crops.Count <= room)
        {
            return [.. crops];
        }

        var groups = crops.GroupBy(c => c.Region, StringComparer.Ordinal).Select(g => g.ToList()).OrderBy(g => g.Count).ToList();
        var chosen = new HashSet<ManifestCrop>();
        var left = Math.Max(room, 0);
        for (var g = 0; g < groups.Count; g++)
        {
            var share = left / (groups.Count - g);
            var take = Even(groups[g], share);
            foreach (var crop in take)
            {
                chosen.Add(crop);
            }

            left -= take.Count;
        }

        return [.. crops.Where(chosen.Contains)];
    }

    private ChunkSegment Candidate(ManifestSegment segment, HashSet<string> seen)
    {
        var pictures = Even(segment.Pictures.Where(p => !seen.Contains(p.File)).ToList(), PerSegmentPictures)
            .Select(p => new ImageRef(string.Empty, p.File, EntryLength(p.File))).ToList();
        var own = new HashSet<string>(StringComparer.Ordinal);
        var crops = _reader.Manifest.Crops
            .Where(c => c.Frame >= segment.FromFrame && c.Frame <= segment.ToFrame && !seen.Contains(c.File) && own.Add(c.File))
            .ToList();
        var fitted = FitByRegion(crops, PerSegmentImages - pictures.Count).Select(CropRef).ToList();

        // Keep the byte budget: close-ups are dropped from the end first, then pictures.
        var bytes = pictures.Sum(p => p.Length) + fitted.Sum(c => c.Length);
        while (bytes > MaxImageBytes && fitted.Count > 0)
        {
            bytes -= fitted[^1].Length;
            fitted.RemoveAt(fitted.Count - 1);
        }

        while (bytes > MaxImageBytes && pictures.Count > 1)
        {
            bytes -= pictures[^1].Length;
            pictures.RemoveAt(pictures.Count - 1);
        }

        return new ChunkSegment(segment, pictures, fitted);
    }

    private static long Size(ChunkSegment s) => s.Pictures.Sum(p => p.Length) + s.Crops.Sum(c => c.Length);

    private ImageRef CropRef(ManifestCrop crop) =>
        new(string.Concat("Close-up of ", _covers.GetValueOrDefault(crop.Region, crop.Region), ":"), crop.File, EntryLength(crop.File));

    private long EntryLength(string entry) => _reader.EntryLength(entry) ?? 0;

    private static void WriteText(Utf8JsonWriter w, string text)
    {
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", text);
        w.WriteEndObject();
    }

    /// <summary>Writes an image block, or nothing when the image is missing, unreadable or larger than the limit. Returns how many blocks it wrote.</summary>
    private int WriteImage(Utf8JsonWriter w, ImageRef image)
    {
        var data = _reader.ReadEntry(image.Entry);
        if (data is null || !ImageSize.TryRead(data, out var width, out var height, out var mediaType) || Math.Max(width, height) > MaxImageSide)
        {
            SkippedImages++;
            return 0;
        }

        w.WriteStartObject();
        w.WriteString("type", "image");
        w.WriteStartObject("source");
        w.WriteString("type", "base64");
        w.WriteString("media_type", mediaType);
        w.WritePropertyName("data");
        w.WriteBase64StringValue(data);
        w.WriteEndObject();
        w.WriteEndObject();
        return 1;
    }

    private byte[] Write(Action<Utf8JsonWriter> writeUserContent, Action<Utf8JsonWriter> writeSchema)
    {
        var buffer = new ArrayBufferWriter<byte>(1 << 20);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", "reviewer");
            w.WriteStartArray("system");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", _system);
            w.WriteStartObject("cache_control");
            w.WriteString("type", "ephemeral");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");
            writeUserContent(w);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("output_config");
            w.WriteString("effort", "high");
            w.WriteStartObject("format");
            w.WriteString("type", "json_schema");
            w.WritePropertyName("schema");
            writeSchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteString("fallbacks", "default");
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static LabelRequest Finish(byte[] body, int images)
    {
        if (body.Length > MaxBodyBytes)
        {
            throw new InvalidOperationException("A labelling request came out larger than the service accepts.");
        }

        return new LabelRequest(body, images);
    }
}
