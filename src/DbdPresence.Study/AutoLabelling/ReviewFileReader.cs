using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbdPresence.Study;

/// <summary>One whole-window picture of the review file.</summary>
/// <param name="Frame">The picture's number in the session.</param>
/// <param name="File">The entry name in the zip.</param>
internal sealed record ManifestPicture(int Frame, string File);

/// <summary>One proposed range with its pictures, as the manifest lists it.</summary>
internal sealed record ManifestSegment(int Index, int FromFrame, int ToFrame, string StartLocal, double Seconds, IReadOnlyList<ManifestPicture> Pictures);

/// <summary>One full-resolution close-up. When <paramref name="IdenticalToEarlier"/> is set, <paramref name="File"/> names the earlier close-up with the same pixels.</summary>
internal sealed record ManifestCrop(int Frame, string Region, string File, bool IdenticalToEarlier);

/// <summary>A named part of the window that close-ups are cut from, with the words that say what it shows.</summary>
internal sealed record ManifestRegion(string Id, string Covers);

/// <summary>What the manifest of a review file says that the labeller uses: the ranges, their pictures, the close-ups and the answer format's scenes.</summary>
internal sealed record ReviewManifest(string SessionId, IReadOnlyList<ManifestSegment> Segments, IReadOnlyList<ManifestCrop> Crops, IReadOnlyList<ManifestRegion> Regions, IReadOnlyList<string> SceneIds);

/// <summary>
/// Reads a session's review file (one zip, or all of its pieces) and nothing else. It is the only input of the automatic labeller besides the closed lists
/// (DETECTION_VALIDATION section 16.2). Everything is bounded: the number of entries, the size of an entry (and its size against its packed size), and the
/// total handed out against the size of the review file itself.
/// </summary>
internal sealed partial class ReviewFileReader : IDisposable
{
    /// <summary>The most entries all pieces may hold together.</summary>
    public const int MaxEntries = 50_000;

    /// <summary>The largest entry that is read.</summary>
    public const long MaxEntryBytes = 10L * 1024 * 1024;

    private const long SlackBytes = 1024 * 1024;
    private const int ReadFactor = 3;
    private readonly List<ZipArchive> _archives = [];
    private readonly List<FileStream> _streams = [];
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly long _budget;
    private long _handedOut;

    private ReviewFileReader(long budget) => _budget = budget;

    /// <summary>Gets the manifest.</summary>
    public ReviewManifest Manifest { get; private set; } = new(string.Empty, [], [], [], []);

    /// <summary>Opens the review file of a recording folder.</summary>
    /// <param name="folder">The recording's folder.</param>
    /// <returns>The reader; the caller disposes it.</returns>
    /// <exception cref="FormatException">There is no review file, a piece is missing, or the file is not a review file.</exception>
    public static ReviewFileReader Open(string folder)
    {
        var id = Path.GetFileName(folder);
        var files = ReviewExport.ExistingFiles(folder);
        if (files.Count == 0)
        {
            throw new FormatException($"Recording {id} has no review file.");
        }

        var ordered = OrderPieces(id, files);
        var reader = new ReviewFileReader(ordered.Sum(f => new FileInfo(f).Length) * ReadFactor);
        try
        {
            foreach (var file in ordered)
            {
                reader.AddArchive(file);
            }

            reader.Manifest = reader.ReadManifest(id);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Returns the size of an entry, or null when the file has no such entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The size in bytes.</returns>
    public long? EntryLength(string name) => _entries.TryGetValue(name, out var entry) ? entry.Length : null;

    /// <summary>Returns the bytes of an entry.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The bytes, or null when the file has no such entry.</returns>
    /// <exception cref="FormatException">The entry is larger than allowed or the total read passed its limit.</exception>
    public byte[]? ReadEntry(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return null;
        }

        if (entry.Length > MaxEntryBytes || entry.Length > (entry.CompressedLength * 50) + 65536)
        {
            throw new FormatException($"The review file entry {name} is larger than allowed.");
        }

        if (_handedOut + entry.Length > _budget + SlackBytes)
        {
            throw new FormatException("More was read from the review file than its own size allows.");
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (memory.Length + read > entry.Length)
            {
                throw new FormatException($"The review file entry {name} is larger than it says.");
            }

            memory.Write(chunk, 0, read);
        }

        _handedOut += memory.Length;
        return memory.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var archive in _archives)
        {
            archive.Dispose();
        }

        foreach (var stream in _streams)
        {
            stream.Dispose();
        }

        _archives.Clear();
        _streams.Clear();
    }

    [GeneratedRegex(@"-review-(?<n>\d{1,3})-of-(?<count>\d{1,3})\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex PieceName();

    private static List<string> OrderPieces(string id, IReadOnlyList<string> files)
    {
        var single = files.Where(f => string.Equals(Path.GetFileName(f), ReviewExport.ZipName(id), StringComparison.Ordinal)).ToList();
        if (single.Count == 1 && files.Count == 1)
        {
            return single;
        }

        if (single.Count > 0)
        {
            throw new FormatException($"Recording {id} has a whole review file next to review file pieces, so it is not clear which one to use.");
        }

        var pieces = files.Select(f => (File: f, Match: PieceName().Match(Path.GetFileName(f)))).ToList();
        var counts = pieces.Select(p => int.Parse(p.Match.Groups["count"].Value, CultureInfo.InvariantCulture)).Distinct().ToList();
        if (counts.Count != 1 || counts[0] != pieces.Count)
        {
            throw new FormatException($"The review file of {id} is in pieces and a piece is missing.");
        }

        var ordered = pieces.OrderBy(p => int.Parse(p.Match.Groups["n"].Value, CultureInfo.InvariantCulture)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (int.Parse(ordered[i].Match.Groups["n"].Value, CultureInfo.InvariantCulture) != i + 1)
            {
                throw new FormatException($"The review file of {id} is in pieces and a piece is missing.");
            }
        }

        return [.. ordered.Select(p => p.File)];
    }

    private void AddArchive(string file)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        _streams.Add(stream);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException($"{Path.GetFileName(file)} is not a readable review file.", ex);
        }

        _archives.Add(archive);
        foreach (var entry in archive.Entries)
        {
            if (_entries.Count >= MaxEntries)
            {
                throw new FormatException("The review file holds more entries than a review file can.");
            }

            if (entry.FullName.EndsWith('/') || !_entries.TryAdd(entry.FullName, entry))
            {
                throw new FormatException($"The review file has an unexpected or repeated entry: {entry.FullName}.");
            }
        }
    }

    private ReviewManifest ReadManifest(string folderId)
    {
        var bytes = ReadEntry("manifest.json") ?? throw new FormatException("The review file has no manifest (the first piece is missing).");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The review file's manifest is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var sessionId = ReviewPlan.StringOf(root, "sessionId");
            if (!string.Equals(sessionId, folderId, StringComparison.Ordinal))
            {
                throw new FormatException("The review file belongs to another recording.");
            }

            var segments = new List<ManifestSegment>();
            foreach (var s in ReviewPlan.Array(root, "segments"))
            {
                var pictures = new List<ManifestPicture>();
                foreach (var p in ReviewPlan.Array(s, "pictures"))
                {
                    pictures.Add(new ManifestPicture(FrameNumber(ReviewPlan.StringOf(p, "frame")), ReviewPlan.StringOf(p, "file")));
                }

                segments.Add(new ManifestSegment(
                    ReviewPlan.IntOf(s, "index"),
                    FrameNumber(ReviewPlan.StringOf(s, "fromFrame")),
                    FrameNumber(ReviewPlan.StringOf(s, "toFrame")),
                    ReviewPlan.StringOf(s, "startLocal"),
                    s.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : throw new FormatException("'seconds' must be a number."),
                    pictures));
            }

            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].Index != i || segments[i].FromFrame < 1 || segments[i].ToFrame < segments[i].FromFrame)
                {
                    throw new FormatException("The review file's ranges are not a numbered list of picture ranges.");
                }
            }

            if (segments.Count == 0)
            {
                throw new FormatException("The review file lists no ranges.");
            }

            var regions = new List<ManifestRegion>();
            foreach (var r in ReviewPlan.Array(root, "regions"))
            {
                regions.Add(new ManifestRegion(ReviewPlan.StringOf(r, "id"), ReviewPlan.StringOf(r, "covers")));
            }

            var crops = new List<ManifestCrop>();
            foreach (var c in ReviewPlan.Array(root, "crops"))
            {
                crops.Add(new ManifestCrop(
                    FrameNumber(ReviewPlan.StringOf(c, "frame")),
                    ReviewPlan.StringOf(c, "region"),
                    ReviewPlan.StringOf(c, "file"),
                    c.TryGetProperty("identicalToEarlierCrop", out var same) && same.ValueKind == JsonValueKind.True));
            }

            if (!root.TryGetProperty("answerFormat", out var format) || format.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The review file has no answer format.");
            }

            var scenes = new List<string>();
            foreach (var scene in ReviewPlan.Array(format, "scenes"))
            {
                scenes.Add(ReviewPlan.StringOf(scene, "id"));
            }

            if (!scenes.Order(StringComparer.Ordinal).SequenceEqual(ReviewVocabulary.Scenes.Select(s => s.Id).Order(StringComparer.Ordinal)))
            {
                throw new FormatException("The review file's list of screens is not the one this version knows.");
            }

            return new ReviewManifest(sessionId, segments, crops, regions, scenes);
        }
    }

    private static int FrameNumber(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : throw new FormatException("A picture number in the manifest is not valid.");
}
