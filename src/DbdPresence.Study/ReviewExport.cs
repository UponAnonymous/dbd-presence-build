using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;

namespace DbdPresence.Study;

/// <summary>What making the review file produced.</summary>
/// <param name="Files">The file, or its parts in order when it was too large for one attachment.</param>
/// <param name="Bytes">The size of all of them together.</param>
/// <param name="Segments">How many proposed ranges the manifest lists.</param>
/// <param name="Pictures">How many whole-window JPEG pictures it holds.</param>
/// <param name="Crops">How many full-resolution text crops it holds (not counting repeats that only point at an earlier crop).</param>
/// <param name="LeftOutPictures">Pictures chosen but left out because the file reached its size limit.</param>
/// <param name="LeftOutCrops">Crops left out for the same reason.</param>
internal sealed record ExportResult(IReadOnlyList<string> Files, long Bytes, int Segments, int Pictures, int Crops, int LeftOutPictures, int LeftOutCrops);

/// <summary>
/// Makes the compact review file of a session (PLAN_V2 D34, DETECTION_VALIDATION §16.1). A raw session is up to 2 GB of full-size PNGs; the file holds
/// one 960-pixel-wide JPEG per proposed range (plus one about every 10 seconds inside long ranges), full-resolution PNG crops of the text the labels
/// depend on, and a manifest. Pictures are taken in priority order (<see cref="ExportSampler.Choose"/>), so a size limit cuts the extra pictures and the
/// longest ranges first. It holds <b>no detector output</b>: the ranges come from brightness and timing, and the crops are only pictures. The
/// file lives in the session folder, so the 30-day clean-up deletes it with the pictures. It is meant to go to the developer's private session only.
/// A file larger than <see cref="PartLimitBytes"/> is split into parts (<c>-review-1-of-3.zip</c> and so on), each a complete zip of its own, so each
/// fits as one attachment; unzipped into one folder, the parts give exactly what the one file would hold.
/// </summary>
internal static partial class ReviewExport
{
    /// <summary>The width of the whole-window pictures.</summary>
    public const int JpegWidth = 960;

    /// <summary>The JPEG quality.</summary>
    public const int JpegQuality = 80;

    /// <summary>The size the file stays under when it can, in megabytes (a figure the owner chose, not a measurement).</summary>
    public const int DefaultLimitMegabytes = 140;

    /// <summary>The share of the limit the whole-window pictures may use; the crops (lossless, so larger) get the rest.</summary>
    private const double JpegShare = 0.35;

    /// <summary>Room kept for the manifest (about 70 bytes a picture of the session, compressed) and the zip's own bookkeeping.</summary>
    private const long ReserveBytes = 4L * 1024 * 1024;

    private const long EntryOverheadBytes = 120;

    /// <summary>
    /// The most one file holds, so each fits as an attachment in the private session (which refuses files over 30 MB: the owner hit that limit with a
    /// 140 MB file, 2026-10-25). A single picture larger than this would get a part of its own; the pictures are far smaller.
    /// </summary>
    public const long PartLimitBytes = 25L * 1024 * 1024;

    /// <summary>Room kept in each part for the zip's own bookkeeping (local and central headers, end record).</summary>
    private const long PartEntryOverheadBytes = 200;

    /// <summary>Gets the file name of a session's review file.</summary>
    public static string ZipName(string sessionId) => sessionId + "-review.zip";

    /// <summary>Gets the file name of one part of a review file that was split.</summary>
    public static string PartName(string sessionId, int part, int parts) => $"{sessionId}-review-{part}-of-{parts}.zip";

    /// <summary>The review files a session folder holds: its one file, or its parts. Nothing else in the folder is named.</summary>
    public static IReadOnlyList<string> ExistingFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var id = Path.GetFileName(folder);
        return
        [
            .. Directory.EnumerateFiles(folder, "*.zip")
                .Where(f => IsReviewFileName(id, Path.GetFileName(f)))
                .OrderBy(f => f.Length)
                .ThenBy(f => f, StringComparer.Ordinal),
        ];
    }

    private static bool IsReviewFileName(string sessionId, string name)
    {
        if (string.Equals(name, ZipName(sessionId), StringComparison.Ordinal))
        {
            return true;
        }

        var part = PartFileName().Match(name);
        return part.Success && string.Equals(part.Groups["id"].Value, sessionId, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?<id>.+)-review-\d{1,3}-of-\d{1,3}\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex PartFileName();

    /// <summary>Gets the file name a reviewer's labels are expected under (the owner saves the file the developer sends back with this name).</summary>
    public static string ReviewerLabelsName(string sessionId) => $"dbd-presence-reviewer-labels-{sessionId}.json";

    /// <summary>Makes the review file of a prepared session.</summary>
    /// <param name="folder">The session folder (prepared: it holds <c>session.json</c>).</param>
    /// <param name="decoder">Reads the recording's pictures.</param>
    /// <param name="time">The clock.</param>
    /// <param name="limitMegabytes">The size to stay under.</param>
    /// <param name="progress">Told in plain words how far it is.</param>
    /// <param name="partLimitBytes">The most one file may hold before the file is split into parts.</param>
    public static ExportResult Make(string folder, IFrameDecoder decoder, TimeProvider time, int limitMegabytes, Action<string> progress, long partLimitBytes = PartLimitBytes)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfLessThan(limitMegabytes, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(partLimitBytes, 64L * 1024);
        var session = DatasetLoader.LoadSession(folder);
        var plan = ReviewSessions.LoadOrMakePlan(folder, session, decoder, progress);
        var times = session.Frames.Select(f => f.TimestampUtc).ToList();
        var chosen = ExportSampler.Choose(plan, times);
        var regions = ExportCrops.Regions();

        var limit = limitMegabytes * 1024L * 1024L;
        var jpegLimit = (long)((limit - ReserveBytes) * JpegShare);
        var cropLimit = limit - ReserveBytes - jpegLimit;
        var path = Path.Combine(folder, ZipName(session.Metadata.SessionId));

        var pictures = new List<(ExportPicture Pick, string File)>();
        var crops = new List<CropEntry>();
        var leftOutPictures = 0;
        var leftOutCrops = 0;
        var jpegBytes = 0L;
        var cropBytes = 0L;
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var old in ExistingFiles(folder))
        {
            File.Delete(old); // an earlier export, whole or in parts, never mixes with this one
        }

        IReadOnlyList<string> files = [path];
        var finished = false;
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var step = Math.Max(1, chosen.Count / 10);
                for (var n = 0; n < chosen.Count; n++)
                {
                    var pick = chosen[n];
                    if (jpegBytes >= jpegLimit && cropBytes >= cropLimit)
                    {
                        leftOutPictures += chosen.Count - n;
                        leftOutCrops += (chosen.Count - n) * regions.Count;
                        break;
                    }

                    var frame = session.Frames[pick.Frame - 1];
                    var image = decoder.Decode(Path.Combine(folder, frame.File));
                    var id = ReviewPlan.FrameId(pick.Frame);

                    var small = image.Width > JpegWidth ? image.ResizeToWidth(JpegWidth) : image;
                    var jpeg = JpegEncoder.Encode(small, JpegQuality);
                    if (jpegBytes + jpeg.Length + EntryOverheadBytes <= jpegLimit)
                    {
                        var file = $"frames/{id}.jpg";
                        AddStored(zip, file, jpeg, frame.TimestampUtc);
                        jpegBytes += jpeg.Length + EntryOverheadBytes;
                        pictures.Add((pick, file));
                    }
                    else
                    {
                        leftOutPictures++;
                    }

                    foreach (var region in regions)
                    {
                        if (cropBytes >= cropLimit)
                        {
                            leftOutCrops++;
                            continue;
                        }

                        var (cut, where) = ExportCrops.Cut(image, region);
                        var pixels = cut.ToPixelBuffer();
                        var key = region.Id + ":" + Convert.ToHexStringLower(SHA256.HashData(pixels.Bgra.Span));
                        if (seen.TryGetValue(key, out var same))
                        {
                            crops.Add(new CropEntry(pick.Frame, region.Id, same, where, SameAs: true));
                            continue;
                        }

                        var png = PngEncoder.EncodeSmall(pixels.Width, pixels.Height, pixels.Bgra.Span);
                        if (cropBytes + png.Length + EntryOverheadBytes > cropLimit)
                        {
                            leftOutCrops++;
                            cropBytes = cropLimit; // the budget is spent; the rest of the pictures still get their JPEG
                            continue;
                        }

                        var file = $"crops/{id}-{region.Id}.png";
                        AddStored(zip, file, png, frame.TimestampUtc);
                        cropBytes += png.Length + EntryOverheadBytes;
                        seen[key] = file;
                        crops.Add(new CropEntry(pick.Frame, region.Id, file, where, SameAs: false));
                    }

                    if ((n + 1) % step == 0)
                    {
                        progress($"Making the review file: {(n + 1) * 100 / chosen.Count}%");
                    }
                }

                var manifest = Encoding.UTF8.GetBytes(Manifest(session, plan, pictures, crops, regions, leftOutPictures, leftOutCrops, limitMegabytes, time.GetUtcNow()));
                var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var target = entry.Open())
                {
                    target.Write(manifest);
                }
            }

            if (new FileInfo(path).Length > partLimitBytes)
            {
                files = Split(path, folder, session.Metadata.SessionId, partLimitBytes);
                File.Delete(path);
            }

            finished = true;
        }
        finally
        {
            if (!finished)
            {
                foreach (var file in ExistingFiles(folder))
                {
                    File.Delete(file); // never leave half a file, or some of the parts, that look like a review file
                }
            }
        }

        return new ExportResult(files, files.Sum(f => new FileInfo(f).Length), plan.Segments.Count, pictures.Count, crops.Count(c => !c.SameAs), leftOutPictures, leftOutCrops);
    }

    /// <summary>Copies the entries of the whole file into parts of at most <paramref name="partLimit"/> bytes, the manifest first.</summary>
    private static List<string> Split(string whole, string folder, string sessionId, long partLimit)
    {
        using var source = ZipFile.OpenRead(whole);
        var groups = new List<List<ZipArchiveEntry>>();
        var current = new List<ZipArchiveEntry>();
        var size = 0L;
        foreach (var entry in source.Entries.OrderBy(e => e.FullName == "manifest.json" ? 0 : 1))
        {
            var cost = entry.CompressedLength + (2L * Encoding.UTF8.GetByteCount(entry.FullName)) + PartEntryOverheadBytes;
            if (current.Count > 0 && size + cost > partLimit - PartEntryOverheadBytes)
            {
                groups.Add(current);
                current = [];
                size = 0;
            }

            current.Add(entry);
            size += cost;
        }

        groups.Add(current);
        var files = new List<string>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var file = Path.Combine(folder, PartName(sessionId, i + 1, groups.Count));
            files.Add(file);
            using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var entry in groups[i])
            {
                var level = entry.FullName == "manifest.json" ? CompressionLevel.Optimal : CompressionLevel.NoCompression;
                var copy = zip.CreateEntry(entry.FullName, level);
                copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
        }

        return files;
    }

    private static void AddStored(ZipArchive zip, string name, byte[] data, DateTimeOffset time)
    {
        // JPEG and PNG are already compressed, so the entries are stored as they are.
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = time;
        using var target = entry.Open();
        target.Write(data);
    }

    private sealed record CropEntry(int Frame, string Region, string File, PixelRect Where, bool SameAs);

    private static string Manifest(
        DatasetSession session,
        ReviewPlan plan,
        IReadOnlyList<(ExportPicture Pick, string File)> pictures,
        IReadOnlyList<CropEntry> crops,
        IReadOnlyList<ExportRegion> regions,
        int leftOutPictures,
        int leftOutCrops,
        int limitMegabytes,
        DateTimeOffset now)
    {
        var m = session.Metadata;
        return JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteString("kind", "dbd-presence-review-export");
            w.WriteString("note", "Pictures and crops only. No detector or text-recognition output is in this file. Segments come from brightness and timing alone.");
            w.WriteString("sessionId", m.SessionId);
            w.WriteString("startedAtUtc", JsonText.Time(m.StartedAtUtc));
            w.WriteString("exportedAtUtc", JsonText.Time(now));
            w.WriteStartObject("window");
            w.WriteNumber("width", m.Width);
            w.WriteNumber("height", m.Height);
            w.WriteString("mode", m.WindowMode);
            w.WriteEndObject();
            w.WriteNumber("dpiScale", m.DpiScale);
            w.WriteNumber("hudScale", m.HudScale);
            w.WriteNumber("menuScale", m.MenuScale);
            w.WriteString("language", m.Language);
            w.WriteString("gameVersion", m.GameVersion);
            w.WriteStartObject("pictures");
            w.WriteNumber("jpegWidth", JpegWidth);
            w.WriteNumber("jpegQuality", JpegQuality);
            w.WriteString("cropsAre", "full resolution PNG cut from the original picture");
            w.WriteNumber("sizeLimitMegabytes", limitMegabytes);
            w.WriteNumber("leftOutPicturesForSize", leftOutPictures);
            w.WriteNumber("leftOutCropsForSize", leftOutCrops);
            w.WriteEndObject();

            w.WriteStartArray("frames");
            foreach (var f in session.Frames)
            {
                w.WriteStartObject();
                w.WriteString("id", f.FrameId);
                w.WriteString("t", JsonText.Time(f.TimestampUtc));
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("segments");
            foreach (var s in plan.Segments)
            {
                w.WriteStartObject();
                w.WriteNumber("index", s.Index);
                w.WriteString("fromFrame", ReviewPlan.FrameId(s.FromFrame));
                w.WriteString("toFrame", ReviewPlan.FrameId(s.ToFrame));
                w.WriteString("startLocal", s.StartLocal);
                w.WriteNumber("seconds", s.Seconds);
                w.WriteStartArray("pictures");
                foreach (var (pick, file) in pictures.Where(p => p.Pick.Segment == s.Index))
                {
                    w.WriteStartObject();
                    w.WriteString("frame", ReviewPlan.FrameId(pick.Frame));
                    w.WriteString("file", file);
                    w.WriteString("why", pick.Kind == ExportPictureKind.Segment ? "segment" : "sample");
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("regions");
            foreach (var r in regions)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("covers", r.Covers);
                w.WriteNumber("x", r.Rect.X);
                w.WriteNumber("y", r.Rect.Y);
                w.WriteNumber("width", r.Rect.Width);
                w.WriteNumber("height", r.Rect.Height);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("crops");
            foreach (var c in crops)
            {
                w.WriteStartObject();
                w.WriteString("frame", ReviewPlan.FrameId(c.Frame));
                w.WriteString("region", c.Region);
                w.WriteString("file", c.File);
                if (c.SameAs)
                {
                    w.WriteBoolean("identicalToEarlierCrop", true);
                }

                w.WriteNumber("x", c.Where.X);
                w.WriteNumber("y", c.Where.Y);
                w.WriteNumber("width", c.Where.Width);
                w.WriteNumber("height", c.Where.Height);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            // What the labels must look like, so labels made from this file can be imported (DbdPresence.Eval import-labels).
            w.WriteStartObject("answerFormat");
            w.WriteString("file", ReviewExport.ReviewerLabelsName(m.SessionId));
            w.WriteString("kind", ReviewAnswers.LabelsKind);
            w.WriteString("sessionId", m.SessionId);
            w.WriteString("segments", "one entry per segment index: {\"i\": index, \"scene\": one of the scenes below, \"matchStart\": true on the first segment of each match}");
            w.WriteString("ownStatus", "optional, on a segment whose scene is InTrial in a match you label role Survivor: what your own state icon (crops/*-own.icon.png) shows through the whole segment, one of " + string.Join(", ", ReviewVocabulary.OwnStatusAnswers.Select(a => a.Id)) + " ('?' = it changes or cannot be told); leave it out when unsure");
            w.WriteString("matches", "one entry per matchStart: {\"startIndex\", \"role\", \"character\", \"map\", \"result\", \"killer\", \"queue\"}; ids come from the app's closed lists; \"?\" means the pictures do not show it; empty means not recorded");
            w.WriteStartArray("scenes");
            foreach (var (id, text) in ReviewVocabulary.Scenes)
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteString("meaning", text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("survivorResults");
            foreach (var r in ReviewVocabulary.SurvivorResults)
            {
                w.WriteStringValue(r);
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }
}
