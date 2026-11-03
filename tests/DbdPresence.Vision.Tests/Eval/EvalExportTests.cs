// Covers: PLAN_V2 D34 (labels from a compact review file made by a reviewer, with the owner's blind spot-check), DETECTION_VALIDATION section 16.1 (why reviewer
// labels stay independent of the detectors), FR-DET-16 and FR-DET-24, and PLAN_V2 D25 (the 30-day clean-up covers the review file).
// MECHANICS ONLY: the recordings are drawn text on flat colour or noise. Nothing here is accuracy, and no size here is a size of a real recording.
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class EvalExportTests : IDisposable
{
    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private readonly EvalWorkspace _w = new();

    public void Dispose() => _w.Dispose();

    private string Recording(string name = EvalWorkspace.FirstRecording, int picturesPerScreen = 4)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens(), picturesPerScreen);
        EvalWorkspace.WritePlan(folder, 4, picturesPerScreen);
        return folder;
    }

    private static string ZipOf(string folder) => Path.Combine(folder, ReviewExport.ZipName(Path.GetFileName(folder)));

    private static JsonDocument ManifestOf(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream);
    }

    private static (int Width, int Height) JpegSize(byte[] bytes)
    {
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[^2]);
        Assert.Equal(0xD9, bytes[^1]);
        var i = 2;
        while (i + 9 < bytes.Length)
        {
            Assert.Equal(0xFF, bytes[i]);
            var marker = bytes[i + 1];
            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (marker == 0xC0)
            {
                return ((bytes[i + 7] << 8) | bytes[i + 8], (bytes[i + 5] << 8) | bytes[i + 6]);
            }

            i += 2 + length;
        }

        throw new InvalidOperationException("No start-of-frame marker.");
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---- The pictures chosen -------------------------------------------------------------------------------------------

    private static (ReviewPlan Plan, List<DateTimeOffset> Times) PlanOf(params (int From, int To)[] ranges)
    {
        var last = ranges.Max(r => r.To);
        var times = Enumerable.Range(0, last).Select(i => DateTimeOffset.UnixEpoch.AddSeconds(2 * i)).ToList();
        return (new ReviewPlan("s", [.. ranges.Select((r, i) => new PlanSegment(i, r.From, r.To, "00:00:00", (r.To - r.From + 1) * 2.0))]), times);
    }

    [Fact]
    public void TheSampler_TakesOnePicturePerRange_AndOneEveryTenSecondsInsideALongOne()
    {
        var (plan, times) = PlanOf((1, 4), (5, 40)); // 8 s, then 72 s

        var picks = ExportSampler.Choose(plan, times);

        Assert.Equal([ExportPictureKind.Segment, ExportPictureKind.Segment], picks.Take(2).Select(p => p.Kind));
        Assert.Equal([2, 22], picks.Take(2).Select(p => p.Frame));
        var extras = picks.Skip(2).ToList();
        Assert.All(extras, p => Assert.Equal(ExportPictureKind.Sample, p.Kind));
        Assert.All(extras, p => Assert.Equal(1, p.Segment));
        Assert.True(extras.Count is >= 5 and <= 7, $"72 s should give about six extra pictures, got {extras.Count}.");
        var inOrder = extras.Select(p => p.Frame).ToList();
        Assert.Equal([.. inOrder.Order()], inOrder);
        Assert.All(extras, p => Assert.True(Math.Abs((times[p.Frame - 1] - times[21]).TotalSeconds) >= ExportSampler.MinSeparation.TotalSeconds));
    }

    [Fact]
    public void TheSampler_GivesAShortRangeOnlyItsOnePicture_AndRefusesAPlanThatNamesMissingPictures()
    {
        var (plan, times) = PlanOf((1, 3), (4, 6)); // 6 s each

        var picks = ExportSampler.Choose(plan, times);

        Assert.Equal(2, picks.Count);
        Assert.Throws<FormatException>(() => ExportSampler.Choose(plan, times.Take(5).ToList()));
    }

    [Fact]
    public void TheSampler_PutsRangePicturesBeforeExtraOnes_AndShortRangesBeforeLongOnes_SoALimitCutsTheLongestStretchesFirst()
    {
        var (plan, times) = PlanOf((1, 30), (31, 34), (35, 70), (71, 76)); // 60 s, 6 s, 72 s, 10 s

        var picks = ExportSampler.Choose(plan, times);

        var ranges = picks.Where(p => p.Kind == ExportPictureKind.Segment).Select(p => p.Segment).ToList();
        Assert.Equal([1, 3, 0, 2], ranges);
        Assert.Equal(ranges.Count, picks.TakeWhile(p => p.Kind == ExportPictureKind.Segment).Count());
        var extras = picks.Where(p => p.Kind == ExportPictureKind.Sample).Select(p => p.Segment).ToList();
        int[] lengths = [29, 3, 35, 5];
        Assert.Equal([.. extras.OrderBy(s => lengths[s])], extras);
    }

    // ---- The encoders --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheJpegEncoder_WritesAWellFormedFile_OfTheRightSize_SmallerAtLowerQuality_AndTheSameBytesEveryTime()
    {
        var image = HudScenes.Lobby(1280).ResizeToWidth(960);

        var high = JpegEncoder.Encode(image, 90);
        var low = JpegEncoder.Encode(image, 30);

        Assert.Equal((960, 540), JpegSize(high));
        Assert.True(low.Length < high.Length);
        Assert.Equal(high, JpegEncoder.Encode(image, 90));
        Assert.Equal((37, 21), JpegSize(JpegEncoder.Encode(ImageBuffer.FromFunction(37, 21, (x, y) => (byte)((x * 7) + (y * 3))), 80))); // not a multiple of 16
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(image, 0));
    }

    [Fact]
    public void ThePngEncoderForCrops_GivesBackTheSamePixels_AndIsNotLargerThanThePlainOne()
    {
        var image = HudScenes.MatchDetails(1280, HudScenes.SurvivorGame(), 1, "ORMOND - MOUNT ORMOND RESORT");
        var pixels = image.ToPixelBuffer();

        var plain = PngEncoder.Encode(pixels.Width, pixels.Height, pixels.Bgra.Span);
        var small = PngEncoder.EncodeSmall(pixels.Width, pixels.Height, pixels.Bgra.Span);

        Assert.True(small.Length <= plain.Length);
        var back = PngDecoder.Decode(small);
        Assert.Equal(image.Width, back.Width);
        for (var y = 0; y < image.Height; y += 7)
        {
            for (var x = 0; x < image.Width; x += 5)
            {
                Assert.Equal(image.RgbAt(x, y), back.RgbAt(x, y));
            }
        }
    }

    // ---- The review file -----------------------------------------------------------------------------------------------

    [Fact]
    public void Export_WritesOneZipInTheSessionFolder_WithPicturesCropsAndAManifest_AndNothingElse()
    {
        var folder = Recording();

        var code = _w.Run("export", EvalWorkspace.FirstRecording);

        Assert.Equal(0, code);
        var zipPath = ZipOf(folder);
        Assert.True(File.Exists(zipPath));
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("manifest.json", names);
        Assert.All(names, n => Assert.True(n == "manifest.json" || (n.StartsWith("frames/", StringComparison.Ordinal) && n.EndsWith(".jpg", StringComparison.Ordinal)) || (n.StartsWith("crops/", StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.Ordinal)), n));
        Assert.Equal(4, names.Count(n => n.StartsWith("frames/", StringComparison.Ordinal)));
        Assert.True(names.Count(n => n.StartsWith("crops/", StringComparison.Ordinal)) >= ExportCrops.Regions().Count);
        foreach (var jpg in names.Where(n => n.EndsWith(".jpg", StringComparison.Ordinal)))
        {
            Assert.Equal((960, 540), JpegSize(Read(zip, jpg)));
        }
    }

    [Fact]
    public void Export_CropsAreFullResolutionCutsOfTheOriginalPicture()
    {
        var folder = Recording();
        _w.Run("export", EvalWorkspace.FirstRecording);
        var original = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, "frame-00010.png"))); // the middle of the third screen: TAB match details
        var region = ExportCrops.Regions().Single(r => r.Id == "tab.rows");
        var expected = original.Crop(region.Rect);

        using var zip = ZipFile.OpenRead(ZipOf(folder));
        var crop = PngDecoder.Decode(Read(zip, "crops/000010-tab.rows.png"));

        Assert.Equal(expected.Width, crop.Width);
        Assert.Equal(expected.Height, crop.Height);
        Assert.True(crop.Width > 200, "the crop is cut from the 1280-wide original, not the 960-wide picture");
        for (var y = 0; y < crop.Height; y += 3)
        {
            for (var x = 0; x < crop.Width; x += 3)
            {
                Assert.Equal(expected.RgbAt(x, y), crop.RgbAt(x, y));
            }
        }
    }

    [Fact]
    public void Export_TheManifestListsFramesSegmentsWindowAndMetadata_AndTheSegmentsAreTheLabellersOwnPlan()
    {
        var folder = Recording();
        _w.Run("export", EvalWorkspace.FirstRecording);

        using var manifest = ManifestOf(ZipOf(folder));
        var root = manifest.RootElement;

        Assert.Equal(EvalWorkspace.FirstRecording, root.GetProperty("sessionId").GetString());
        Assert.Equal(1280, root.GetProperty("window").GetProperty("width").GetInt32());
        Assert.Equal(720, root.GetProperty("window").GetProperty("height").GetInt32());
        Assert.Equal("Borderless", root.GetProperty("window").GetProperty("mode").GetString());
        Assert.Equal(16, root.GetProperty("frames").GetArrayLength());
        Assert.Equal("000001", root.GetProperty("frames")[0].GetProperty("id").GetString());
        Assert.Equal("2026-10-24T19:30:12.000Z", root.GetProperty("frames")[0].GetProperty("t").GetString());
        var plan = ReviewPlan.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
        var segments = root.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(plan.Segments.Count, segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal(ReviewPlan.FrameId(plan.Segments[i].FromFrame), segments[i].GetProperty("fromFrame").GetString());
            Assert.Equal(ReviewPlan.FrameId(plan.Segments[i].ToFrame), segments[i].GetProperty("toFrame").GetString());
            Assert.NotEmpty(segments[i].GetProperty("pictures").EnumerateArray());
        }

        Assert.Equal(ReviewVocabulary.Scenes.Count, root.GetProperty("answerFormat").GetProperty("scenes").GetArrayLength());
        Assert.Equal(ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording), root.GetProperty("answerFormat").GetProperty("file").GetString());

        // The reviewer can answer the optional own-icon question: the format names it and its allowed answers, and the file carries the close-up it is about.
        var ownStatus = root.GetProperty("answerFormat").GetProperty("ownStatus").GetString()!;
        Assert.All(ReviewVocabulary.OwnStatusAnswers, a => Assert.Contains(a.Id, ownStatus, StringComparison.Ordinal));
        Assert.Contains(root.GetProperty("regions").EnumerateArray(), r => r.GetProperty("id").GetString() == ExportCrops.OwnIconId);
        Assert.Contains(root.GetProperty("crops").EnumerateArray(), c => c.GetProperty("region").GetString() == ExportCrops.OwnIconId);
    }

    [Fact]
    public void Export_HoldsNoDetectorOutput_TheManifestHasOnlyTheKnownKeys_AndNoReadingOfAnyText()
    {
        var folder = Recording();
        _w.Run("export", EvalWorkspace.FirstRecording);
        using var manifest = ManifestOf(ZipOf(folder));

        string[] topLevel =
        [
            "schemaVersion", "kind", "note", "sessionId", "startedAtUtc", "exportedAtUtc", "window", "dpiScale", "hudScale", "menuScale", "language", "gameVersion",
            "pictures", "frames", "segments", "regions", "crops", "answerFormat",
        ];
        Assert.Equal(topLevel.Order(StringComparer.Ordinal), manifest.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["id", "t"], manifest.RootElement.GetProperty("frames")[0].EnumerateObject().Select(p => p.Name));
        string[] cropKeys = ["frame", "region", "file", "x", "y", "width", "height"];
        Assert.All(manifest.RootElement.GetProperty("crops").EnumerateArray(), c => Assert.Subset(new HashSet<string>([.. cropKeys, "identicalToEarlierCrop"]), c.EnumerateObject().Select(p => p.Name).ToHashSet()));
        var text = manifest.RootElement.GetRawText();
        foreach (var word in new[] { "ORMOND", "MOUNT", "RESORT", "Dwight", "dwight", "\"score\"", "confidence", "observation", "ocr" })
        {
            Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Export_PreparesANewRecording_ReusesTheLabellersPlan_AndTellsTheOwnerAboutOtherPlayersNames()
    {
        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, EvalWorkspace.MatchScreens());

        var code = _w.Run("export", folder);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.Session)));
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.Plan)));
        Assert.Contains("other players' names", _w.Output, StringComparison.Ordinal);
        Assert.Contains("30 days", _w.Output, StringComparison.Ordinal);
        Assert.Contains(ZipOf(folder), _w.Output, StringComparison.Ordinal);
        var plan = File.ReadAllText(Path.Combine(folder, SessionFiles.Plan));
        Assert.Equal(0, _w.Run("export", EvalWorkspace.FirstRecording));
        Assert.Equal(plan, File.ReadAllText(Path.Combine(folder, SessionFiles.Plan)));
    }

    [Fact]
    public void Export_StaysUnderTheSizeLimit_LeavesOutTheInBetweenPicturesFirst_AndSaysSo()
    {
        var noisy = new List<(string Scene, ImageBuffer Image, int Pictures)>();
        for (var i = 0; i < 4; i++)
        {
            noisy.Add(("x", Noise(1280, i), 14));
        }

        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, noisy);
        EvalWorkspace.WritePlan(folder, 4, 14);

        var code = _w.Run("export", EvalWorkspace.FirstRecording, "--max-mb", "8");

        Assert.Equal(0, code);
        Assert.True(new FileInfo(ZipOf(folder)).Length <= 8L * 1024 * 1024, "the file is over its limit");
        using var manifest = ManifestOf(ZipOf(folder));
        var left = manifest.RootElement.GetProperty("pictures");
        Assert.True(left.GetProperty("leftOutPicturesForSize").GetInt32() + left.GetProperty("leftOutCropsForSize").GetInt32() > 0);
        Assert.Contains("size limit", _w.Output, StringComparison.Ordinal);
        using var zip = ZipFile.OpenRead(ZipOf(folder));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("frames/", StringComparison.Ordinal));
        Assert.Equal(1, _w.Run("export", EvalWorkspace.FirstRecording, "--max-mb", "2")); // a limit below 8 MB is refused
    }

    private string NoisyRecording()
    {
        var noisy = new List<(string Scene, ImageBuffer Image, int Pictures)>();
        for (var i = 0; i < 4; i++)
        {
            noisy.Add(("x", Noise(1280, i), 14));
        }

        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, noisy);
        EvalWorkspace.WritePlan(folder, 4, 14);
        return folder;
    }

    private static Dictionary<string, byte[]> EntriesOf(IEnumerable<string> zips)
    {
        var all = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in zips)
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                all.Add(entry.FullName, Read(zip, entry.FullName)); // a name in two pieces throws
            }
        }

        return all;
    }

    [Fact]
    public void Export_ALargeFile_ComesInPiecesUnderThePartLimit_ThatTogetherHoldExactlyWhatTheOneFileWould()
    {
        var folder = NoisyRecording();
        Assert.Equal(0, _w.Run("export", EvalWorkspace.FirstRecording, "--max-mb", "8"));
        Assert.True(File.Exists(ZipOf(folder)), "under the 25 MB part limit the review file stays one file");
        var whole = EntriesOf([ZipOf(folder)]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        const long partLimit = 2L * 1024 * 1024;

        var result = ReviewExport.Make(folder, new ExtensionFrameDecoder(), time, 8, _ => { }, partLimit);

        Assert.True(result.Files.Count > 1, "the file was not split");
        Assert.False(File.Exists(ZipOf(folder)), "the one file of the earlier export is left beside the pieces");
        for (var i = 0; i < result.Files.Count; i++)
        {
            Assert.Equal(ReviewExport.PartName(Path.GetFileName(folder), i + 1, result.Files.Count), Path.GetFileName(result.Files[i]));
            Assert.True(new FileInfo(result.Files[i]).Length <= partLimit, $"piece {i + 1} is over the part limit");
        }

        Assert.Equal(result.Files, ReviewExport.ExistingFiles(folder));
        using (var first = ZipFile.OpenRead(result.Files[0]))
        {
            Assert.NotNull(first.GetEntry("manifest.json"));
        }

        var pieces = EntriesOf(result.Files);
        Assert.Equal(whole.Keys.Order(StringComparer.Ordinal), pieces.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in whole.Where(e => e.Key != "manifest.json"))
        {
            Assert.Equal(bytes, pieces[name]);
        }

        Assert.Equal(result.Bytes, result.Files.Sum(f => new FileInfo(f).Length));
        Assert.True(ReviewFiles.IsWaitingForReviewer(folder));

        // A new export of the same recording replaces every piece, so nothing from the earlier one is left to be sent by mistake.
        var again = ReviewExport.Make(folder, new ExtensionFrameDecoder(), time, 8, _ => { });
        Assert.Equal([ZipOf(folder)], again.Files);
        Assert.Equal([ZipOf(folder)], ReviewExport.ExistingFiles(folder));
    }

    [Fact]
    public void TheThirtyDayCleanUp_KnowsTheReviewFilesPieces_AndDeletesThemWithThePictures()
    {
        var folder = NoisyRecording();
        Assert.Equal(0, _w.Run("export", EvalWorkspace.FirstRecording, "--max-mb", "8"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        var result = ReviewExport.Make(folder, new ExtensionFrameDecoder(), time, 8, _ => { }, 2L * 1024 * 1024);
        Assert.True(result.Files.Count > 1);

        var early = SessionRetention.Sweep(_w.Root, new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 19, 30, 12, TimeSpan.Zero).AddDays(29)));
        Assert.Empty(early.Problems);
        Assert.All(result.Files, f => Assert.True(File.Exists(f)));
        var late = SessionRetention.Sweep(_w.Root, new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 19, 30, 12, TimeSpan.Zero).AddDays(31)));
        Assert.Equal([EvalWorkspace.FirstRecording], late.Deleted);
        Assert.Empty(late.Problems);
        Assert.False(Directory.Exists(folder));
    }

    private static ImageBuffer Noise(int width, int seed)
    {
        var height = width * 9 / 16;
        var bytes = new byte[width * height * 4];
        var random = new Random(seed);
        random.NextBytes(bytes);
        for (var i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        return new ImageBuffer(width, height, PixelFormat.Rgba32, bytes);
    }

    [Fact]
    public void Export_RefusesAnUnknownRecording_AndAMissingArgument()
    {
        Assert.Equal(1, _w.Run("export", "2026-01-01_00-00-00"));
        Assert.Contains("No study recording named", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(2, _w.Run("export"));
        Assert.Equal(2, _w.Run("import-labels", "only-one"));
    }

    // ---- The menu ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMenu_OffersLabelling_AndMakingAReviewFile_AndAnythingElseDoesNothing()
    {
        var folder = Recording();
        _w.QueueEnter("2", string.Empty);

        var code = GuidedMenu.Run(_w.Environment());

        Assert.Equal(0, code);
        Assert.Contains("Make a review file to send", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(ZipOf(folder)));

        _w.QueueEnter("7");
        Assert.Equal(2, GuidedMenu.Run(_w.Environment()));

        _w.QueueEnter("1", string.Empty);
        Assert.Equal(0, GuidedMenu.Run(_w.Environment())); // the label flow: prepares and waits for Enter
        Assert.Contains("waits for the reviewer's labels", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMenu_JustEnter_MakesReviewFilesForEveryNewRecording_AndSkipsOnesAlreadyDealtWith()
    {
        var first = Recording();
        Assert.Equal(0, _w.Run("export", EvalWorkspace.FirstRecording));
        var madeBefore = File.GetLastWriteTimeUtc(ZipOf(first));
        var second = Recording("2026-10-25_20-00-00");
        var third = Recording("2026-10-26_21-00-00");
        _w.QueueEnter("2", string.Empty);

        var code = GuidedMenu.Run(_w.Environment());

        Assert.Equal(0, code);
        Assert.Contains("all 2 new recordings", _w.Output, StringComparison.Ordinal);
        Assert.Contains("Made review files for 2 of 2 new recordings", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(ZipOf(second)));
        Assert.True(File.Exists(ZipOf(third)));
        Assert.Equal(madeBefore, File.GetLastWriteTimeUtc(ZipOf(first)));
    }

    [Fact]
    public void TheMenu_ANumber_StillMakesTheReviewFileForThatOneRecordingOnly()
    {
        var older = Recording();
        var newer = Recording("2026-10-25_20-00-00");
        _w.QueueEnter("2", "2");

        Assert.Equal(0, GuidedMenu.Run(_w.Environment()));
        Assert.True(File.Exists(ZipOf(older)));
        Assert.False(File.Exists(ZipOf(newer)));
    }

    [Fact]
    public void TheMenu_WithNoRecordings_TellsTheOwnerWhatToDo()
    {
        _w.QueueEnter("2");

        Assert.Equal(1, GuidedMenu.Run(_w.Environment()));
        Assert.Contains("Start the recorder", _w.ErrorOutput, StringComparison.Ordinal);
    }

    // ---- Reviewer labels and the owner's spot-check --------------------------------------------------------------------

    private static string ReviewerFile(string id, string? role = null) =>
        EvalWorkspace.Answers(id, s_scenes, role ?? "Survivor").Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"source\":\"owner\",", StringComparison.Ordinal);

    private string Exported(string heldOut = "heldout")
    {
        var folder = Recording();
        Assert.Equal(0, _w.Run("prepare", "--split", heldOut));
        Assert.Equal(0, _w.Run("export", EvalWorkspace.FirstRecording));
        return folder;
    }

    private string Import(string folder, string? json = null)
    {
        var file = Path.Combine(_w.Root, "reviewer-labels.json");
        File.WriteAllText(file, json ?? ReviewerFile(EvalWorkspace.FirstRecording));
        Assert.Equal(0, _w.Run("import-labels", EvalWorkspace.FirstRecording, file));
        return folder;
    }

    private static IReadOnlyList<JsonElement> LabelLines(string folder) =>
        [.. File.ReadAllLines(Path.Combine(folder, SessionFiles.Labels)).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    private void SpotCheck(string folder, string? role = null)
    {
        _ = ReviewSessions.BuildRecheckPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder());
        using var plan = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckPlan)));
        var picked = plan.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var again = Path.Combine(_w.Root, "recheck.json");
        File.WriteAllText(again, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes, role ?? "Survivor", kind: "recheck", only: picked));
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", again, "--recheck"));
    }

    [Fact]
    public void ImportedLabels_AreMarkedAsTheReviewers_WhateverTheFileSaysAboutItself_AndTheSpotCheckIsDueAtOnce()
    {
        var folder = Import(Exported());

        var lines = LabelLines(folder);
        Assert.NotEmpty(lines);
        Assert.All(lines, l =>
        {
            Assert.Equal("reviewer", l.GetProperty("source").GetString());
            Assert.Equal("reviewer", l.GetProperty("labeller").GetString());
            Assert.Equal("labelled", l.GetProperty("reviewStatus").GetString());
        });
        Assert.All(DatasetLoader.LoadSession(folder).Labels, l => Assert.Equal("reviewer", l.Source));
        Assert.Equal((DateTimeOffset?)_w.Time.GetUtcNow(), ReviewSessions.RecheckDueAt(folder));
        Assert.Contains("do not count yet", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportedLabels_DoNotCount_BeforeTheSpotCheck_NotInTheOwnersTallyNorForTheManifest()
    {
        var folder = Import(Exported());

        Assert.Equal(0, GuidedLabel.CountMatches(_w.Root));
        var manifestPath = Path.Combine(_w.Root, "manifest.txt");
        Assert.Equal(1, _w.Run("manifest", "--out", manifestPath));
        Assert.Contains("not passed the blind re-check", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(manifestPath));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void WhenTheOwnersSpotCheckAgrees_TheReviewersLabelsCount_AndStayMarkedAsTheReviewers()
    {
        var folder = Import(Exported());

        SpotCheck(folder);

        var session = DatasetLoader.LoadSession(folder);
        Assert.All(session.Labels, l => Assert.Equal("rechecked", l.ReviewStatus));
        Assert.All(session.Labels, l => Assert.Equal("reviewer", l.Source));
        Assert.Equal(1, GuidedLabel.CountMatches(_w.Root));
        var manifestPath = Path.Combine(_w.Root, "manifest.txt");
        Assert.Equal(0, _w.Run("manifest", "--out", manifestPath));
    }

    [Fact]
    public void WhenTheOwnersSpotCheckDisagrees_TheSessionIsDisputed_AndTheOwnerDecidesByLabellingItHerself()
    {
        var folder = Import(Exported());

        SpotCheck(folder, role: "Killer");

        Assert.All(DatasetLoader.LoadSession(folder).Labels, l => Assert.Equal("disputed", l.ReviewStatus));
        Assert.Equal(0, GuidedLabel.CountMatches(_w.Root)); // a disputed reviewer label counts for nothing
        Assert.Equal(1, _w.Run("manifest", "--out", Path.Combine(_w.Root, "m.txt")));

        // The owner reworks it (the guided flow opens the page with the answers filled in): her answers replace them and are hers.
        var answers = Path.Combine(_w.Root, "mine.json");
        File.WriteAllText(answers, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes, "Killer"));
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", answers));
        Assert.All(DatasetLoader.LoadSession(folder).Labels, l =>
        {
            Assert.Equal("owner", l.Source);
            Assert.Equal("labelled", l.ReviewStatus);
        });
        Assert.Equal("owner", ReviewSessions.AnswersSource(folder));
        Assert.True(ReviewSessions.RecheckDueAt(folder) > _w.Time.GetUtcNow().AddHours(23), "her own labels wait the usual 24 hours for her own second look");
    }

    [Fact]
    public void ImportedLabels_NeverReplaceTheOwnersLabels_AndAreRefusedBeforeTheReviewFileExists()
    {
        var folder = Recording();
        Assert.Equal(0, _w.Run("prepare"));
        var mine = Path.Combine(_w.Root, "mine.json");
        File.WriteAllText(mine, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        var labelsBefore = File.ReadAllText(Path.Combine(folder, SessionFiles.Labels));
        File.Delete(Path.Combine(folder, SessionFiles.Plan));
        var theirs = Path.Combine(_w.Root, "theirs.json");
        File.WriteAllText(theirs, ReviewerFile(EvalWorkspace.FirstRecording, "Killer"));

        Assert.Equal(1, _w.Run("import-labels", EvalWorkspace.FirstRecording, theirs));
        Assert.Contains("no review file yet", _w.ErrorOutput, StringComparison.Ordinal);

        EvalWorkspace.WritePlan(folder, 4);
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", mine));
        var owners = File.ReadAllText(Path.Combine(folder, SessionFiles.Labels));
        Assert.NotEqual(labelsBefore, owners);

        Assert.Equal(1, _w.Run("import-labels", EvalWorkspace.FirstRecording, theirs));
        Assert.Contains("already labelled", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(owners, File.ReadAllText(Path.Combine(folder, SessionFiles.Labels)));
    }

    [Fact]
    public void ImportedLabels_AreCheckedLikeTheOwnersAnswers_AnUnknownCharacterAMissingFileOrAnotherSessionChangesNothing()
    {
        var folder = Exported();
        var before = File.ReadAllText(Path.Combine(folder, SessionFiles.Labels));
        var bad = Path.Combine(_w.Root, "bad.json");

        File.WriteAllText(bad, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes, character: "not-a-character"));
        Assert.Equal(1, _w.Run("import-labels", EvalWorkspace.FirstRecording, bad));
        File.WriteAllText(bad, EvalWorkspace.Answers("2020-01-01_00-00-00", s_scenes));
        Assert.Equal(1, _w.Run("import-labels", EvalWorkspace.FirstRecording, bad));
        Assert.Equal(1, _w.Run("import-labels", EvalWorkspace.FirstRecording, Path.Combine(_w.Root, "missing.json")));

        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, SessionFiles.Labels)));
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
    }

    [Fact]
    public void TheGuidedFlow_WaitsForTheReviewer_ThenTakesTheFileFromDownloads_AndOffersTheSpotCheckAtOnce()
    {
        var folder = Exported();

        _w.Run();
        Assert.Contains("waits for the reviewer's labels", _w.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.ReviewPage)));

        File.WriteAllText(Path.Combine(_w.Downloads, ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording)), ReviewerFile(EvalWorkspace.FirstRecording));
        _w.QueueEnter("s");
        _w.Run();

        Assert.Contains("Found the reviewer's labels", _w.Output, StringComparison.Ordinal);
        Assert.Contains("your quick spot-check", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, SessionFiles.RecheckPage)));
        Assert.Equal("reviewer", ReviewSessions.AnswersSource(folder));

        // Running again does not import the same file again over the labels (that would restart the spot-check).
        var answersBefore = File.ReadAllText(Path.Combine(folder, SessionFiles.Answers));
        _w.QueueEnter("s");
        _w.Run();
        Assert.Equal(answersBefore, File.ReadAllText(Path.Combine(folder, SessionFiles.Answers)));
    }

    [Theory]
    [InlineData("52f3de6e-{0}.json")] // a chat app put its own id in front
    [InlineData("{0} (1).json")] // the browser kept an earlier copy
    [InlineData("{0}.txt")] // saved as a text file
    public void TheGuidedFlow_FindsTheReviewersLabels_WhenTheDownloadAddedToTheName(string pattern)
    {
        var folder = Exported();
        var name = Path.GetFileNameWithoutExtension(ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording));
        File.WriteAllText(Path.Combine(_w.Downloads, string.Format(CultureInfo.InvariantCulture, pattern, name)), ReviewerFile(EvalWorkspace.FirstRecording));

        _w.QueueEnter("s");
        _w.Run();

        Assert.Contains("Found the reviewer's labels", _w.Output, StringComparison.Ordinal);
        Assert.Equal("reviewer", ReviewSessions.AnswersSource(folder));
    }

    [Fact]
    public void TheGuidedFlow_FindsTheReviewersLabels_InTheRecordingsFolder_AndOtherwiseSaysWhereItLooked()
    {
        var folder = Exported();

        _w.Run();
        Assert.Contains(ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording), _w.Output, StringComparison.Ordinal);
        Assert.Contains(_w.Downloads, _w.Output, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(folder, ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording)), ReviewerFile(EvalWorkspace.FirstRecording));
        _w.QueueEnter("s");
        _w.Run();
        Assert.Contains("Found the reviewer's labels", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuidedFlow_IgnoresAFileThatOnlyLooksLikeTheLabels_ButIsNotJsonOrText()
    {
        var folder = Exported();
        File.WriteAllText(Path.Combine(_w.Downloads, ReviewExport.ReviewerLabelsName(EvalWorkspace.FirstRecording) + ".zip"), ReviewerFile(EvalWorkspace.FirstRecording));

        _w.Run();

        Assert.DoesNotContain("Found the reviewer's labels", _w.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, SessionFiles.Answers)));
    }

    // ---- Thirty-day clean-up -------------------------------------------------------------------------------------------

    [Fact]
    public void TheThirtyDayCleanUp_DeletesTheReviewFileWithThePictures_AndKeepsItBeforeThen()
    {
        var folder = Import(Exported());
        Assert.True(File.Exists(ZipOf(folder)));

        var early = SessionRetention.Sweep(_w.Root, new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 19, 30, 12, TimeSpan.Zero).AddDays(29)));
        Assert.Empty(early.Deleted);
        Assert.True(File.Exists(ZipOf(folder)));

        var late = SessionRetention.Sweep(_w.Root, new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 19, 30, 12, TimeSpan.Zero).AddDays(31)));

        Assert.Equal([EvalWorkspace.FirstRecording], late.Deleted);
        Assert.Empty(late.Problems);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void TheThirtyDayCleanUp_OnlyKnowsTheReviewFilesOwnName_ASimilarFileBlocksTheDeleteAndIsReported()
    {
        var folder = Exported();
        File.WriteAllText(Path.Combine(folder, "my-notes-review.zip"), "not ours");

        var report = SessionRetention.Sweep(_w.Root, new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 19, 30, 12, TimeSpan.Zero).AddDays(31)));

        Assert.Empty(report.Deleted);
        Assert.Contains(report.Problems, p => p.Reason.Contains("my-notes-review.zip", StringComparison.Ordinal));
        Assert.True(File.Exists(ZipOf(folder)));
    }

    // ---- Size of an hour of play (opt-in measurement, never part of the normal run) ------------------------------------

    [Fact]
    public void AnHourOfSyntheticPlay_ExportSize_IsMeasuredWhenAsked()
    {
        var reportPath = Environment.GetEnvironmentVariable("DBD_EXPORT_HOUR_REPORT");
        Assert.SkipUnless(!string.IsNullOrEmpty(reportPath), "Set DBD_EXPORT_HOUR_REPORT to a file path to measure the export of a synthetic hour (MECHANICS ONLY, not a size of a real recording).");

        var texture = Environment.GetEnvironmentVariable("DBD_EXPORT_HOUR_TEXTURE") ?? "mild";
        var screens = SyntheticHour(texture);
        var started = DateTimeOffset.UtcNow;
        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, screens);
        var recorded = DateTimeOffset.UtcNow;
        var rawBytes = Directory.GetFiles(folder, "frame-*.png").Sum(f => new FileInfo(f).Length);

        var code = _w.Run("export", EvalWorkspace.FirstRecording);

        var finished = DateTimeOffset.UtcNow;
        Assert.Equal(0, code);
        var zip = new FileInfo(ZipOf(folder));
        using var manifest = ManifestOf(zip.FullName);
        var frames = manifest.RootElement.GetProperty("frames").GetArrayLength();
        var picturesLeftOut = manifest.RootElement.GetProperty("pictures").GetProperty("leftOutPicturesForSize").GetInt32();
        File.WriteAllText(
            reportPath!,
            $"MECHANICS ONLY (synthetic 1920x1080 pictures, texture '{texture}'); this is not the size of a real recording.\n"
            + $"pictures in the session: {frames} (every 2 s = {frames * 2 / 60} minutes)\n"
            + $"raw session (PNG): {rawBytes / (1024.0 * 1024.0):0.0} MB\n"
            + $"review file: {zip.Length / (1024.0 * 1024.0):0.0} MB\n"
            + $"segments: {manifest.RootElement.GetProperty("segments").GetArrayLength()}\n"
            + $"jpeg pictures: {manifest.RootElement.GetProperty("segments").EnumerateArray().Sum(s => s.GetProperty("pictures").GetArrayLength())}\n"
            + $"crop entries: {manifest.RootElement.GetProperty("crops").GetArrayLength()}\n"
            + $"pictures left out for size: {picturesLeftOut}\n"
            + $"seconds to write the session: {(recorded - started).TotalSeconds:0}\n"
            + $"seconds to export: {(finished - recorded).TotalSeconds:0}\n");
        Assert.True(zip.Length <= ReviewExport.DefaultLimitMegabytes * 1024L * 1024L);
        var keep = Environment.GetEnvironmentVariable("DBD_EXPORT_HOUR_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            File.Copy(zip.FullName, keep, overwrite: true);
        }
    }

    /// <summary>
    /// About one hour at the study tool's one picture every 2 s: eight matches of menus, search, lobby, a long trial, TAB and results. Every picture has
    /// noise on it ('mild' is a few levels, 'heavy' more; the PNG files of the heavy hour come to about 7 GB, so it needs the disk space) and the trial pictures vary in brightness like live 3D, so the sizes are not the best case of flat colour.
    /// </summary>
    private static List<(string Scene, ImageBuffer Image, int Pictures)> SyntheticHour(string texture)
    {
        var amplitude = texture == "heavy" ? 14 : 5;
        var rows = HudScenes.SurvivorGame();
        const int Width = 1920;
        var hud = HudScenes.TrialHud(Width);
        ImageBuffer Textured(ImageBuffer source, int seed, int shift = 0)
        {
            var bytes = source.Pixels.ToArray();
            var random = new Random(seed);
            for (var i = 0; i < bytes.Length; i += 4)
            {
                var n = random.Next(-amplitude, amplitude + 1) + shift;
                bytes[i] = (byte)Math.Clamp(bytes[i] + n, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp(bytes[i + 1] + n, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp(bytes[i + 2] + n, 0, 255);
            }

            return new ImageBuffer(source.Width, source.Height, PixelFormat.Rgba32, bytes);
        }

        var variants = Enumerable.Range(0, 24).Select(k => Textured(hud, 100 + k, (k * 3) - 36)).ToList();
        var menu = Textured(HudScenes.MainMenu(Width), 1);
        var lobby = Textured(HudScenes.Lobby(Width), 2);
        var searching = Textured(HudScenes.Searching(Width), 3);
        var preMatch = Textured(HudScenes.PreMatchSurvivor(Width, "THE ENTITY"), 4);
        var start = Textured(HudScenes.MatchStart(Width, "THE SHATTERED SQUARE", "THE DECIMATED BORGO"), 5);
        var tab = Textured(HudScenes.MatchDetails(Width, rows, 1, "ORMOND - MOUNT ORMOND RESORT"), 6);
        var result = Textured(HudScenes.MatchResult(Width), 7);
        var board = Textured(HudScenes.Scoreboard(Width, rows, 1), 8);

        var screens = new List<(string, ImageBuffer, int)>();
        for (var match = 0; match < 8; match++)
        {
            screens.Add(("MainMenu", menu, 15));
            screens.Add(("Lobby", lobby, 15));
            screens.Add(("Matchmaking", searching, 20));
            screens.Add(("Lobby", preMatch, 10));
            screens.Add(("InTrial", start, 4));
            var random = new Random(match);
            for (var k = 0; k < 90; k += 3)
            {
                screens.Add(("InTrial", variants[random.Next(variants.Count)], 3));
            }

            screens.Add(("InMatchMenu", tab, 6));
            for (var k = 0; k < 60; k += 3)
            {
                screens.Add(("InTrial", variants[random.Next(variants.Count)], 3));
            }

            screens.Add(("Results", result, 3));
            screens.Add(("Results", board, 4));
        }

        return screens;
    }
}
