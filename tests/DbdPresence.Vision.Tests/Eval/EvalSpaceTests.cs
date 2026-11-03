// Covers: FR-DET-24 (the evaluator reads the study tool's PNG frames), DETECTION_VALIDATION section 5 (the manifest hashes labels, so it stays valid), section 12.5 and
// section 21 (data retention: when pictures are made smaller and when they are deleted, and why the held-out check stays honest), PLAN_V2 D25 (the 30-day clean-up and
// its safe-deletion rules), PLAN_V2 D34 (the review file is deleted once the owner's spot-check passed).
// MECHANICS ONLY: the recordings are drawn text on flat colour or generated gradients, the recogniser is a fake that reads them back. Every size and count asserted
// here follows from how the recordings were built. None of it is accuracy, and no size here is the size of a real recording (that is UNKNOWN).
using System.IO.Compression;
using System.Security.Cryptography;
using DbdPresence.Eval;
using DbdPresence.Study;
using DbdPresence.StudyCapture;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Imaging;
using DbdPresence.Vision.Tests.Support;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Eval;

public sealed class EvalSpaceTests : IDisposable
{
    private const string Tuning = "2026-10-24_19-30-12";
    private const string Held = "2026-10-24_20-30-12";
    private const string HeldToo = "2026-10-24_21-30-12";

    private static readonly string[] s_scenes = ["MainMenu", "Lobby", "InMatchMenu", "Results"];
    private static readonly string[] s_oneWrong = ["MainMenu", "Results", "InMatchMenu", "Results"];
    private readonly EvalWorkspace _w = new();
    private readonly List<string> _outside = [];

    public EvalSpaceTests()
    {
        _w.Recognizer = _ => new BitmapFontOcr();
    }

    public void Dispose()
    {
        _w.Dispose();
        foreach (var path in _outside.Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private string Out => Path.Combine(_w.Root, "report.md");

    private string AddLabelled(string name, string split, string[]? scenes = null)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        _w.LabelAndRecheck(folder, scenes ?? s_scenes, split);
        return folder;
    }

    private (string File, string Hash) MakeManifest(string name = "manifest.txt")
    {
        var file = Path.Combine(_w.Root, name);
        Assert.Equal(0, _w.Run("manifest", "--out", file));
        var hash = File.ReadAllText(file).Split('\n').Single(l => l.StartsWith("manifest-sha256:", StringComparison.Ordinal))["manifest-sha256:".Length..].Trim();
        return (file, hash);
    }

    private int Evaluate(string manifest, string hash, params string[] extra) =>
        _w.Run(["evaluate", "--out", Out, "--owner-name", HudScenes.Owner, "--manifest", manifest, "--manifest-hash", hash, .. extra]);

    private static string[] Frames(string folder) => [.. Directory.GetFiles(folder, "frame-*.png").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static ImageBuffer Decode(string path) => new ExtensionFrameDecoder().Decode(path);

    private static byte[] Gradient(int width, int height, int phase)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                bgra[i] = (byte)(((x * 3) + phase) & 255);
                bgra[i + 1] = (byte)((y * 2) & 255);
                bgra[i + 2] = (byte)((((x + y) / 2) + phase) & 255);
                bgra[i + 3] = 255;
                if (((x * 7) + (y * 13)) % 97 == 0)
                {
                    bgra[i] ^= 0x55;
                }
            }
        }

        return PngEncoder.Encode(width, height, bgra);
    }

    /// <summary>A recording whose pictures are gradients written the way the recorder writes them (no scanline filter, fastest deflate).</summary>
    private string GradientRecording(string name = Tuning, int pictures = 6)
    {
        var folder = _w.AddRecording(name, EvalWorkspace.MatchScreens());
        var files = Frames(folder);
        for (var i = 0; i < pictures && i < files.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, files[i]), Gradient(320, 180, i * 11));
        }

        return folder;
    }

    private static string Metadata(string folder) => File.ReadAllText(Path.Combine(folder, "metadata.json"));

    // ---- 1. Compaction -----------------------------------------------------------------------------------------------

    [Fact]
    public void Compaction_RewritesThePicturesSmaller_WithExactlyTheSamePixels_AndTellsTheOwnerHowMuchWasSaved()
    {
        _w.CompactPictures = true;
        var folder = GradientRecording();
        var before = Frames(folder).ToDictionary(f => f, f => (Bytes: new FileInfo(Path.Combine(folder, f)).Length, Pixels: Decode(Path.Combine(folder, f))));
        var metadataBefore = Metadata(folder);

        Assert.Equal(0, _w.Run("prepare", "--split", "tuning"));

        foreach (var (file, old) in before)
        {
            var path = Path.Combine(folder, file);
            var now = Decode(path);
            Assert.Equal(old.Pixels.Width, now.Width);
            Assert.Equal(old.Pixels.Height, now.Height);
            Assert.True(old.Pixels.Pixels.SequenceEqual(now.Pixels), $"{file} must decode to the same pixels");
            Assert.True(FrameCompaction.IsMarked(path));
        }

        var gradients = before.Where(p => string.CompareOrdinal(p.Key, "frame-00006.png") <= 0).Select(p => p.Key).ToList();
        Assert.All(gradients, f => Assert.True(new FileInfo(Path.Combine(folder, f)).Length < before[f].Bytes / 2, $"{f} should be much smaller"));
        Assert.Contains("pictures checked and rewritten", _w.Output, StringComparison.Ordinal);
        Assert.Contains("saved", _w.Output, StringComparison.Ordinal);
        Assert.Contains("exactly the same pixels", _w.Output, StringComparison.Ordinal);
        Assert.Equal(metadataBefore, Metadata(folder)); // the recorder's own file is not touched
        Assert.All(Directory.GetFiles(folder), f => Assert.True(Path.GetFileName(f).StartsWith("frame-0", StringComparison.Ordinal) || s_known.Contains(Path.GetFileName(f)), $"{Path.GetFileName(f)} is not expected here"));
    }

    private static readonly string[] s_known = ["metadata.json", "session.json", "frames.jsonl", "labels.jsonl"];

    [Fact]
    public void Compaction_IsIdempotent_ASecondPassSkipsEveryPictureAndChangesNothing()
    {
        _w.CompactPictures = true;
        var folder = GradientRecording();
        Assert.Equal(0, _w.Run("prepare", "--split", "tuning"));
        var once = Frames(folder).ToDictionary(f => f, f => Sha(Path.Combine(folder, f)));
        var output = _w.Output.Length;

        Assert.Equal(0, _w.Run("prepare"));

        Assert.Equal(once, Frames(folder).ToDictionary(f => f, f => Sha(Path.Combine(folder, f))));
        Assert.DoesNotContain("rewritten", _w.Output[output..], StringComparison.Ordinal);
        var result = FrameCompaction.Run(folder, _ => { });
        Assert.Equal(0, result.Compacted + result.NoGain + result.Left + result.Refused);
        Assert.Equal(Frames(folder).Length, result.AlreadyDone);
    }

    [Fact]
    public void Compaction_RefusesAPictureWhoseNewVersionWouldDiffer_LeavesTheOldFileAndLeavesNothingBehind()
    {
        var folder = GradientRecording();
        var before = Frames(folder).ToDictionary(f => f, f => Sha(Path.Combine(folder, f)));

        // A faulty encoder that changes one pixel and is smaller than the original.
        var result = FrameCompaction.Run(folder, _ => { }, (w, h, bgra) =>
        {
            var changed = (byte[])bgra.Clone();
            changed[0] ^= 1;
            return PngEncoder.EncodeSmall(w, h, changed);
        });

        Assert.Equal(0, result.Compacted);
        Assert.True(result.Refused > 0);
        Assert.All(result.Problems, p => Assert.Contains("did not decode to the same pixels", p, StringComparison.Ordinal));
        Assert.Equal(before, Frames(folder).ToDictionary(f => f, f => Sha(Path.Combine(folder, f))));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Frames(folder));
        Assert.All(Frames(folder), f => Assert.False(FrameCompaction.IsMarked(Path.Combine(folder, f)), $"{f} must not be marked"));
        Assert.DoesNotContain(Directory.GetFiles(folder), f => Path.GetFileName(f).StartsWith("frame-9", StringComparison.Ordinal));

        // A faulty encoder that returns rubbish is refused just the same.
        var rubbish = FrameCompaction.Run(folder, _ => { }, (_, _, _) => new byte[60]);
        Assert.Equal(0, rubbish.Compacted);
        Assert.Equal(before, Frames(folder).ToDictionary(f => f, f => Sha(Path.Combine(folder, f))));
    }

    [Fact]
    public void Compaction_LeavesPicturesThatAreNotPlainRgb_OrCannotBeRead_AsTheyWere_AndSaysSo()
    {
        var folder = GradientRecording(pictures: 1);
        var grey = Path.Combine(folder, "frame-00002.png");
        using (var stream = new MemoryStream())
        {
            stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            PngEncoder.WriteChunk(stream, "IHDR"u8, [0, 0, 0, 2, 0, 0, 0, 2, 8, 0, 0, 0, 0]);
            var raw = new MemoryStream();
            using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write([0, 10, 20, 0, 30, 40]);
            }

            PngEncoder.WriteChunk(stream, "IDAT"u8, raw.ToArray());
            PngEncoder.WriteChunk(stream, "IEND"u8, []);
            File.WriteAllBytes(grey, stream.ToArray());
        }

        var broken = Path.Combine(folder, "frame-00003.png");
        File.WriteAllBytes(broken, [1, 2, 3, 4, 5]);
        var greyBefore = Sha(grey);
        var brokenBefore = Sha(broken);

        var result = FrameCompaction.Run(folder, _ => { });

        Assert.Equal(greyBefore, Sha(grey));
        Assert.Equal(brokenBefore, Sha(broken));
        Assert.True(result.Compacted >= 1);
        Assert.Equal(2, result.Left);
        Assert.Contains(result.Problems, p => p.StartsWith("frame-00002.png was left as it was", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.StartsWith("frame-00003.png was left as it was", StringComparison.Ordinal));
    }

    [Fact]
    public void Compaction_NeverFollowsALink_AndAPictureThatCannotGetSmallerOnlyGetsTheMarker()
    {
        var folder = GradientRecording(pictures: 0);
        var outside = Path.Combine(Path.GetTempPath(), "dbd-eval-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        _outside.Add(outside);
        var target = Path.Combine(outside, "precious.png");
        File.WriteAllBytes(target, Gradient(64, 64, 3));
        File.CreateSymbolicLink(Path.Combine(folder, "frame-00099.png"), target);
        var targetBefore = Sha(target);

        // Random pixels do not compress: the new file cannot be smaller, so the old bytes stay and only the marker is added.
        var noise = new byte[64 * 64 * 4];
        new Random(7).NextBytes(noise);
        for (var i = 3; i < noise.Length; i += 4)
        {
            noise[i] = 255;
        }

        var random = Path.Combine(folder, "frame-00001.png");
        File.WriteAllBytes(random, PngEncoder.Encode(64, 64, noise));
        var randomBefore = Decode(random);
        var sizeBefore = new FileInfo(random).Length;

        var result = FrameCompaction.Run(folder, _ => { });

        Assert.Equal(targetBefore, Sha(target));
        Assert.Contains(result.Problems, p => p.Contains("frame-00099.png is a link", StringComparison.Ordinal));
        Assert.True(Decode(random).Pixels.SequenceEqual(randomBefore.Pixels));
        Assert.True(new FileInfo(random).Length <= sizeBefore + 64, "at most the marker was added");
        Assert.True(FrameCompaction.IsMarked(random));
    }

    [Fact]
    public void Compaction_DeletesATemporaryFileAnInterruptedPassLeftBehind()
    {
        var folder = GradientRecording(pictures: 1);
        var leftover = Path.Combine(folder, "frame-90000000000000000003.png");
        File.WriteAllBytes(leftover, [1, 2, 3]);

        var result = FrameCompaction.Run(folder, _ => { });

        Assert.False(File.Exists(leftover));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Compaction_ChangesNeitherTheLabelsNorTheManifest_NorWhatTheEvaluatorReports()
    {
        _w.CompactPictures = true;
        AddLabelled(Tuning, "tuning");
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var labelsBefore = Sha(Path.Combine(held, SessionFiles.Labels));
        Assert.Equal(0, Evaluate(manifest, hash, "--keep-pictures"));
        var reportBefore = File.ReadAllText(Out);
        var sizeBefore = Frames(held).Sum(f => new FileInfo(Path.Combine(held, f)).Length);

        File.Delete(Out);
        File.Delete(Out + ".timing.md");
        Assert.Equal(0, _w.Run("prepare"));
        Assert.True(Frames(held).Sum(f => new FileInfo(Path.Combine(held, f)).Length) <= sizeBefore + (Frames(held).Length * 64));
        Assert.All(Frames(held), f => Assert.True(FrameCompaction.IsMarked(Path.Combine(held, f))));
        Assert.Equal(labelsBefore, Sha(Path.Combine(held, SessionFiles.Labels)));
        Assert.Equal(hash, ManifestFile.For([DatasetLoader.LoadSession(held)]).Hash);
        Assert.Equal(0, Evaluate(manifest, hash, "--keep-pictures"));

        Assert.Equal(reportBefore, File.ReadAllText(Out));
    }

    // ---- 2. The review file ------------------------------------------------------------------------------------------

    private static string ReviewerFile(string id) =>
        EvalWorkspace.Answers(id, s_scenes).Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"source\":\"owner\",", StringComparison.Ordinal);

    private string Exported(string split = "heldout")
    {
        var folder = _w.AddRecording(EvalWorkspace.FirstRecording, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(folder, 4);
        Assert.Equal(0, _w.Run("prepare", "--split", split));
        Assert.Equal(0, _w.Run("export", EvalWorkspace.FirstRecording));
        var file = Path.Combine(_w.Root, "reviewer-labels.json");
        File.WriteAllText(file, ReviewerFile(EvalWorkspace.FirstRecording));
        Assert.Equal(0, _w.Run("import-labels", EvalWorkspace.FirstRecording, file));
        return folder;
    }

    private string[] SpotCheckAnswers(string folder, string role)
    {
        _ = ReviewSessions.BuildRecheckPage(folder, Vocabulary.LoadEmbedded(), new ExtensionFrameDecoder());
        using var plan = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SessionFiles.RecheckPlan)));
        var picked = plan.RootElement.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var file = Path.Combine(_w.Root, "recheck.json");
        File.WriteAllText(file, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes, role, kind: "recheck", only: picked));
        return ["apply", "--session", EvalWorkspace.FirstRecording, "--answers", file, "--recheck"];
    }

    [Fact]
    public void TheReviewFile_IsDeletedOnlyWhenTheSpotCheckOfTheReviewersLabelsPassed()
    {
        var folder = Exported();
        var zip = Path.Combine(folder, ReviewExport.ZipName(EvalWorkspace.FirstRecording));
        Assert.True(File.Exists(zip));

        // Not yet checked: it stays. A spot-check that disagrees (disputed): it stays.
        Assert.Equal(0, _w.Run(SpotCheckAnswers(folder, "Killer")));
        Assert.True(File.Exists(zip));
        Assert.DoesNotContain("was deleted from this PC", _w.Output, StringComparison.Ordinal);

        // The owner reworks it, her labels are hers; the reviewer's source is gone, so a passed second look never deletes anything on this account.
        var mine = Path.Combine(_w.Root, "mine.json");
        File.WriteAllText(mine, EvalWorkspace.Answers(EvalWorkspace.FirstRecording, s_scenes));
        Assert.Equal(0, _w.Run("apply", "--session", EvalWorkspace.FirstRecording, "--answers", mine));
        Assert.Equal("owner", ReviewSessions.AnswersSource(folder));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public void TheReviewFile_IsDeletedWhenTheSpotCheckPasses_AndTheOwnerIsTold_ButThePicturesStay()
    {
        var folder = Exported();
        var zip = Path.Combine(folder, ReviewExport.ZipName(EvalWorkspace.FirstRecording));
        var frames = Frames(folder);

        Assert.Equal(0, _w.Run(SpotCheckAnswers(folder, "Survivor")));

        Assert.False(File.Exists(zip));
        Assert.Contains("review file", _w.Output, StringComparison.Ordinal);
        Assert.Contains("was deleted from this PC", _w.Output, StringComparison.Ordinal);
        Assert.Equal(frames, Frames(folder));
        Assert.All(DatasetLoader.LoadSession(folder).Labels, l => Assert.Equal("rechecked", l.ReviewStatus));
        Assert.Empty(ReviewExport.ExistingFiles(folder));
    }

    [Fact]
    public void AllPiecesOfAReviewFile_AreDeleted_ButNoOtherZip_AndNothingForHandMadeLabels()
    {
        var folder = Exported();
        var id = EvalWorkspace.FirstRecording;
        File.Delete(Path.Combine(folder, ReviewExport.ZipName(id)));
        var pieces = new[] { ReviewExport.PartName(id, 1, 2), ReviewExport.PartName(id, 2, 2) };
        foreach (var piece in pieces)
        {
            File.WriteAllBytes(Path.Combine(folder, piece), [1, 2, 3]);
        }

        var other = Path.Combine(folder, "my-notes.zip");
        File.WriteAllBytes(other, [9]);

        Assert.Equal(0, _w.Run(SpotCheckAnswers(folder, "Survivor")));

        Assert.All(pieces, p => Assert.False(File.Exists(Path.Combine(folder, p))));
        Assert.True(File.Exists(other));
        Assert.Contains("2 files", _w.Output, StringComparison.Ordinal);

        // Hand-made labels (the owner's own) never have a review file to delete, and one that is there is left alone.
        var mine = Path.Combine(_w.Root, "mine.json");
        File.WriteAllText(mine, EvalWorkspace.Answers(id, s_scenes));
        Assert.Equal(0, _w.Run("apply", "--session", id, "--answers", mine));
        var stray = Path.Combine(folder, ReviewExport.ZipName(id));
        File.WriteAllBytes(stray, [1]);
        SpaceFreeing.AfterSpotCheck(folder, new RecheckOutcome(20, 20, 1.0, 0, [], "rechecked"), _w.Environment());
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public void TheGuidedFlow_DeletesTheReviewFile_WhenTheSpotCheckItOffersPasses()
    {
        var folder = Exported();
        var zip = Path.Combine(folder, ReviewExport.ZipName(EvalWorkspace.FirstRecording));
        var baseEnv = _w.Environment();
        var env = new EvalEnvironment
        {
            Out = baseEnv.Out,
            Error = baseEnv.Error,
            Time = baseEnv.Time,
            StudyRoot = baseEnv.StudyRoot,
            DownloadsFolder = baseEnv.DownloadsFolder,
            AppDataFolder = baseEnv.AppDataFolder,
            CreateRecognizer = baseEnv.CreateRecognizer,

            // The owner saves the spot-check page's answers, then presses Enter.
            ReadLine = () =>
            {
                var args = SpotCheckAnswers(folder, "Survivor");
                File.Move(args[4], Path.Combine(_w.Downloads, ReviewSessions.RecheckFileName(EvalWorkspace.FirstRecording)));
                return string.Empty;
            },
        };

        Assert.Equal(0, EvalApp.Run([], env));

        Assert.False(File.Exists(zip));
        Assert.Contains("was deleted from this PC", _w.Output, StringComparison.Ordinal);
    }

    // ---- 3. Pictures after the check ---------------------------------------------------------------------------------

    [Fact]
    public void HeldOutPictures_AreDeletedRightAfterTheReport_ExceptThoseOfWrongUnits_AndTheManifestStillVerifies()
    {
        var tuning = AddLabelled(Tuning, "tuning");
        var held = AddLabelled(Held, "heldout", s_oneWrong); // the second screen is labelled wrongly on purpose: that unit is an error
        var (manifest, hash) = MakeManifest();
        var labelsBefore = Sha(Path.Combine(held, SessionFiles.Labels));
        var smallFiles = Directory.GetFiles(held).Where(f => !Path.GetFileName(f).StartsWith("frame-", StringComparison.Ordinal)).Select(f => (Name: Path.GetFileName(f), Hash: Sha(f))).ToList();
        var tuningFrames = Frames(tuning);

        Assert.Equal(0, Evaluate(manifest, hash));

        // The scene unit of pictures 5 to 8 is wrong, so they are kept; every other held-out picture is gone.
        var kept = Frames(held);
        Assert.Equal(["frame-00005.png", "frame-00006.png", "frame-00007.png", "frame-00008.png"], kept);
        Assert.Contains("Freed", _w.Output, StringComparison.Ordinal);
        Assert.Contains("kept", _w.Output, StringComparison.Ordinal);
        Assert.Contains("wrong or missed", _w.Output, StringComparison.Ordinal);

        // Small files are untouched; the labels hash, and so the committed manifest, still verifies.
        foreach (var (name, fileHash) in smallFiles)
        {
            Assert.Equal(fileHash, Sha(Path.Combine(held, name)));
        }

        Assert.Equal(labelsBefore, Sha(Path.Combine(held, SessionFiles.Labels)));
        var session = DatasetLoader.LoadSession(held);
        DatasetLoader.VerifyManifest([session], hash);
        Assert.Equal(hash, ManifestFile.For([session]).Hash);
        Assert.Equal(0, _w.Run("manifest", "--out", Path.Combine(_w.Root, "again.txt")));

        // The practice recording is not touched: the status icons are not taught yet.
        Assert.Equal(tuningFrames, Frames(tuning));
        Assert.Contains("until the status icons are taught", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Out));
        Assert.True(File.Exists(Path.Combine(_w.Root, StudyState.FileName)));
    }

    [Fact]
    public void WhenNothingWasWrong_NoPictureIsKept_AndALaterRunSaysThePicturesAreGoneInsteadOfFailingOnAFile()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Empty(Frames(held));
        Assert.Contains("No unit was wrong or missed", _w.Output, StringComparison.Ordinal);
        var report = File.ReadAllText(Out);

        Assert.Equal(1, Evaluate(manifest, hash));
        Assert.Contains("Pictures of these recordings are gone", _w.ErrorOutput, StringComparison.Ordinal);
        Assert.Equal(report, File.ReadAllText(Out));
        Assert.Equal(1, _w.Run("export", Held));
        Assert.Contains("are gone", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatCannotWriteItsReport_DeletesNothing()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var blocker = Path.Combine(_w.Root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var frames = Frames(held);

        var code = _w.Run("evaluate", "--out", Path.Combine(blocker, "report.md"), "--owner-name", HudScenes.Owner, "--manifest", manifest, "--manifest-hash", hash);

        Assert.Equal(1, code);
        Assert.Equal(frames, Frames(held));
        Assert.False(File.Exists(Path.Combine(_w.Root, StudyState.FileName)));
    }

    [Fact]
    public void AReportThatIsNotCompleteOnTheDisk_DeletesNothing()
    {
        var held = AddLabelled(Held, "heldout");
        Assert.Equal(0, _w.Run("prepare"));
        var frames = Frames(held);
        var session = DatasetLoader.LoadSession(held);
        var keep = new HashSet<(string, string)>();
        File.WriteAllText(Out, "# Evaluation run\n\n| Item |"); // cut short
        File.WriteAllText(Out + ".timing.md", "x");

        SpaceFreeing.AfterHeldOutReport(new HeldOutFreeUp(_w.Root, [session], keep, true, Out, "# Evaluation run\n\n| Item | Value |\n", "abc"), _w.Environment());
        Assert.Equal(frames, Frames(held));
        Assert.Contains("could not be confirmed as complete", _w.Output, StringComparison.Ordinal);

        // No timing file next to it counts as incomplete too.
        File.WriteAllText(Out, "same");
        File.Delete(Out + ".timing.md");
        SpaceFreeing.AfterHeldOutReport(new HeldOutFreeUp(_w.Root, [session], keep, true, Out, "same", "abc"), _w.Environment());
        Assert.Equal(frames, Frames(held));

        // A report file that is not there at all.
        File.Delete(Out);
        SpaceFreeing.AfterHeldOutReport(new HeldOutFreeUp(_w.Root, [session], keep, true, Out, "same", "abc"), _w.Environment());
        Assert.Equal(frames, Frames(held));
        Assert.False(File.Exists(Path.Combine(_w.Root, StudyState.FileName)));
    }

    [Fact]
    public void ARunOfOnlySomeDetectors_KeepsThePictures_BecauseTheOthersStillNeedThem()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var frames = Frames(held);

        Assert.Equal(0, Evaluate(manifest, hash, "--detector", "scene"));

        Assert.Equal(frames, Frames(held));
        Assert.Contains("only some detectors were measured", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeepPicturesOption_KeepsEverything_AndSaysSo()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var frames = Frames(held);

        Assert.Equal(0, Evaluate(manifest, hash, "--keep-pictures"));

        Assert.Equal(frames, Frames(held));
        Assert.Contains("--keep-pictures", _w.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_w.Root, StudyState.FileName)));
    }

    [Fact]
    public void ATuningRun_NeverDeletesAnything()
    {
        var tuning = AddLabelled(Tuning, "tuning");
        var frames = Frames(tuning);

        Assert.Equal(0, _w.Run("evaluate", "--out", Out, "--split", "tuning", "--owner-name", HudScenes.Owner));

        Assert.Equal(frames, Frames(tuning));
    }

    [Fact]
    public void PracticePictures_AreKeptUntilTheReportExistsAndTheStatusIconsAreTaught_ThenDeleted_InEitherOrder()
    {
        // Report first, teaching second.
        var tuning = AddLabelled(Tuning, "tuning");
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var tuningFrames = Frames(tuning);
        Assert.Equal(0, Evaluate(manifest, hash));
        Assert.Empty(Frames(held));
        Assert.Equal(tuningFrames, Frames(tuning));

        SpaceFreeing.AfterTeachStatus(_w.Root, _w.Environment());

        Assert.Empty(Frames(tuning));
        Assert.Contains("Freed", _w.Output, StringComparison.Ordinal);
        Assert.Contains("practice recordings", _w.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(tuning, SessionFiles.Labels)));
        Assert.True(File.Exists(Path.Combine(tuning, "metadata.json")));
    }

    [Fact]
    public void PracticePictures_AreKeptWhenTheIconsAreTaughtBeforeAnyReportExists_AndDeletedByTheReportThatFollows()
    {
        var tuning = AddLabelled(Tuning, "tuning");
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var tuningFrames = Frames(tuning);

        SpaceFreeing.AfterTeachStatus(_w.Root, _w.Environment());
        Assert.Equal(tuningFrames, Frames(tuning));
        Assert.Contains("until the held-out check has been run", _w.Output, StringComparison.Ordinal);
        Assert.NotEmpty(Frames(held));

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Empty(Frames(tuning));
        Assert.Empty(Frames(held));
    }

    [Fact]
    public void ATemplatesFileInTheAppFolder_CountsAsTaught_ForTheReportThatFollows()
    {
        var tuning = AddLabelled(Tuning, "tuning");
        AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        Directory.CreateDirectory(_w.AppData);
        File.WriteAllText(Path.Combine(_w.AppData, StatusIconTeaching.FileName), "{}");

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Empty(Frames(tuning));
    }

    [Fact]
    public void APracticeRecordingWhoseLabelsAreNotFinal_KeepsItsPictures_EvenWhenBothConditionsHold()
    {
        var unfinished = _w.AddRecording(Tuning, EvalWorkspace.MatchScreens());
        EvalWorkspace.WritePlan(unfinished, 4);
        Assert.Equal(0, _w.Run("prepare", "--split", "tuning"));
        var answers = Path.Combine(_w.Root, "a.json");
        File.WriteAllText(answers, EvalWorkspace.Answers(Tuning, s_scenes));
        Assert.Equal(0, _w.Run("apply", "--session", Tuning, "--answers", answers)); // labelled, but the second look has not happened
        AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var frames = Frames(unfinished);
        Directory.CreateDirectory(_w.AppData);
        File.WriteAllText(Path.Combine(_w.AppData, StatusIconTeaching.FileName), "{}");

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Equal(frames, Frames(unfinished));
        Assert.Contains("labels are not final yet", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionNotInTheManifest_AndAnUnpreparedRecording_NeverLoseTheirPictures()
    {
        AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var fresh = _w.AddRecording(HeldToo, EvalWorkspace.MatchScreens()); // recorded after the manifest, not prepared
        var frames = Frames(fresh);

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Equal(frames, Frames(fresh));
    }

    // ---- Safety rules (as the 30-day clean-up) -----------------------------------------------------------------------

    [Fact]
    public void AFolderWithALinkInIt_LosesNothing_AndTheLinkIsNotFollowed()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var outside = Path.Combine(Path.GetTempPath(), "dbd-eval-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        _outside.Add(outside);
        var target = Path.Combine(outside, "precious.png");
        File.WriteAllText(target, "precious");
        File.CreateSymbolicLink(Path.Combine(held, "notes.png"), target);
        var frames = Frames(held);

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Equal(frames, Frames(held));
        Assert.Equal("precious", File.ReadAllText(target));
        Assert.Contains("holds a link", _w.Output, StringComparison.Ordinal);
        Assert.Contains("nothing in it was deleted", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionFolderThatIsItselfALink_IsNotFollowed()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var real = Path.Combine(Path.GetTempPath(), "dbd-eval-real-" + Guid.NewGuid().ToString("N"));
        _outside.Add(real);
        Directory.Move(held, real);
        Directory.CreateSymbolicLink(held, real);
        var frames = Frames(real);

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Equal(frames, Frames(real));
        Assert.Contains("is a link", _w.Output, StringComparison.Ordinal);
        Directory.Delete(held); // a link to a folder: removes only the link (File.Delete fails on Windows)
        Assert.True(Directory.Exists(real));
    }

    [Fact]
    public void UnknownFilesAndFolders_AreLeftAlone_AndSaid_WhileThePicturesAreStillDeleted()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        File.WriteAllText(Path.Combine(held, "my-notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(held, "frame-00001.png.bak"), "keep me too");
        Directory.CreateDirectory(Path.Combine(held, "extra"));
        File.WriteAllText(Path.Combine(held, "extra", "frame-00002.png"), "inside a folder it does not know");

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Empty(Frames(held));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(held, "my-notes.txt")));
        Assert.Equal("keep me too", File.ReadAllText(Path.Combine(held, "frame-00001.png.bak")));
        Assert.True(File.Exists(Path.Combine(held, "extra", "frame-00002.png")));
        Assert.Contains("3 other items", _w.Output, StringComparison.Ordinal);
        Assert.Contains("left alone", _w.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderThatIsNotTheStudyToolsOwn_IsNeverTouched()
    {
        AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var foreign = Path.Combine(_w.Root, "2026-10-24_22-00-00");
        Directory.CreateDirectory(foreign);
        File.WriteAllBytes(Path.Combine(foreign, "frame-00001.png"), [1, 2, 3]);

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.True(File.Exists(Path.Combine(foreign, "frame-00001.png")));
    }

    [Fact]
    public void WhenAPictureCannotBeDeleted_ItIsReported_AndTheRestIsStillDeleted()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // a read-only folder is not an obstacle to deleting on Windows in the same way; the failure path is the same code
        }

        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        var mode = File.GetUnixFileMode(held);
        try
        {
            File.SetUnixFileMode(held, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            if (Environment.UserName == "root")
            {
                return; // root ignores the permission; nothing to show here
            }

            Assert.Equal(0, Evaluate(manifest, hash));

            Assert.Contains("could not be deleted", _w.Output, StringComparison.Ordinal);
            Assert.NotEmpty(Frames(held));
        }
        finally
        {
            File.SetUnixFileMode(held, mode);
        }
    }

    [Fact]
    public void AnUnreadableStateFile_StopsTheDeletion_AndSaysSo()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        File.WriteAllText(Path.Combine(_w.Root, StudyState.FileName), "not json at all");
        var frames = Frames(held);

        Assert.Equal(0, Evaluate(manifest, hash));

        Assert.Equal(frames, Frames(held));
        Assert.Contains("Freeing up space stopped", _w.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStateFile_HoldsOnlyIdsHashesAndTimes_AndSurvivesTheThirtyDayCleanUpOfTheRecordings()
    {
        var held = AddLabelled(Held, "heldout");
        var (manifest, hash) = MakeManifest();
        Assert.Equal(0, Evaluate(manifest, hash));
        var state = File.ReadAllText(Path.Combine(_w.Root, StudyState.FileName));

        Assert.Contains(hash, state, StringComparison.Ordinal);
        Assert.Contains(Held, state, StringComparison.Ordinal);
        Assert.DoesNotContain(HudScenes.Owner, state, StringComparison.Ordinal);

        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 20, 30, 12, TimeSpan.Zero).AddDays(31));
        var report = SessionRetention.Sweep(_w.Root, time);

        Assert.Contains(Held, report.Deleted);
        Assert.False(Directory.Exists(held));
        Assert.True(File.Exists(Path.Combine(_w.Root, StudyState.FileName)));
    }
}
