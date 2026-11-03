using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DbdPresence.Study;

namespace DbdPresence.Vision.Tests.Eval;

/// <summary>
/// A hand-made review zip for the automatic labeller's request mechanics: a manifest in the real format and tiny files that only carry a valid PNG or JPEG
/// header (the labeller reads sizes, never pixels). MECHANICS ONLY: nothing here is a picture of the game.
/// </summary>
internal static class SyntheticReview
{
    public const string Id = "2026-10-24_19-30-12";
    public static readonly string[] CropRegions = ["lobby.names", "tab.rows", "result.text"];

    public static byte[] Jpeg(int width, int height, int extra = 0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 };
        bytes.AddRange(new byte[extra]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    public static byte[] Png(int width, int height, int extra = 0)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (var v in new[] { width, height })
        {
            bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        }

        bytes.AddRange(new byte[5 + extra]);
        return [.. bytes];
    }

    /// <summary>Writes a review file of <paramref name="segments"/> ranges of two pictures each. Segment <c>i</c> holds one whole picture and the crops of its two frames; every <paramref name="identicalEvery"/>th crop repeats an earlier file.</summary>
    /// <returns>The recording folder.</returns>
    public static string Write(string root, int segments, int pieces = 1, int oversizedCropEvery = 0, int identicalEvery = 0, string id = Id, int pictureBytes = 0, int cropWidth = 300, double secondsPerRange = 4.0)
    {
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        var entries = new List<(string Name, byte[] Data)>();
        var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteString("kind", "dbd-presence-review-export");
            w.WriteString("sessionId", id);
            w.WriteStartArray("segments");
            for (var i = 0; i < segments; i++)
            {
                var from = (i * 2) + 1;
                w.WriteStartObject();
                w.WriteNumber("index", i);
                w.WriteString("fromFrame", Frame(from));
                w.WriteString("toFrame", Frame(from + 1));
                w.WriteString("startLocal", string.Create(CultureInfo.InvariantCulture, $"19:{i / 60:D2}:{i % 60:D2}"));
                w.WriteNumber("seconds", secondsPerRange);
                w.WriteStartArray("pictures");
                var name = $"frames/{Frame(from)}.jpg";
                entries.Add((name, Jpeg(960, 540, pictureBytes)));
                w.WriteStartObject();
                w.WriteString("frame", Frame(from));
                w.WriteString("file", name);
                w.WriteString("why", "segment");
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("regions");
            foreach (var r in CropRegions)
            {
                w.WriteStartObject();
                w.WriteString("id", r);
                w.WriteString("covers", "the " + r + " part");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("crops");
            var n = 0;
            string? earlier = null;
            for (var frame = 1; frame <= segments * 2; frame++)
            {
                foreach (var r in CropRegions)
                {
                    n++;
                    var same = identicalEvery > 0 && n % identicalEvery == 0 && earlier is not null;
                    var file = same ? earlier! : $"crops/{Frame(frame)}-{r}.png";
                    if (!same)
                    {
                        var wide = oversizedCropEvery > 0 && n % oversizedCropEvery == 0 ? 2400 : cropWidth;
                        entries.Add((file, Png(wide, 60)));
                        earlier = file;
                    }

                    w.WriteStartObject();
                    w.WriteString("frame", Frame(frame));
                    w.WriteString("region", r);
                    w.WriteString("file", file);
                    if (same)
                    {
                        w.WriteBoolean("identicalToEarlierCrop", true);
                    }

                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteStartObject("answerFormat");
            w.WriteStartArray("scenes");
            foreach (var (sceneId, text) in ReviewVocabulary.Scenes)
            {
                w.WriteStartObject();
                w.WriteString("id", sceneId);
                w.WriteString("meaning", text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var manifestBytes = json.ToArray();
        var parts = Math.Max(1, pieces);
        for (var p = 0; p < parts; p++)
        {
            var path = Path.Combine(folder, parts == 1 ? ReviewExport.ZipName(id) : ReviewExport.PartName(id, p + 1, parts));
            using var stream = new FileStream(path, FileMode.Create);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            if (p == 0)
            {
                var m = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var target = m.Open();
                target.Write(manifestBytes);
            }

            for (var e = p; e < entries.Count; e += parts)
            {
                var entry = zip.CreateEntry(entries[e].Name, CompressionLevel.NoCompression);
                using var target = entry.Open();
                target.Write(entries[e].Data);
            }
        }

        return folder;
    }

    private static string Frame(int number) => ReviewPlan.FrameId(number);
}
