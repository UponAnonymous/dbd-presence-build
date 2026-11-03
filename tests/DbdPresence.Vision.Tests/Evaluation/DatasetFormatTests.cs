// Covers: C5 (documented labelled-set format is loadable and evaluable), FR-DET-15/16 (dataset layout, held-out manifest hash),
// DETECTION_VALIDATION §3/§4/§5 ("Frame dataset format"). MECHANICS ONLY: the frames are synthetic shapes written as PGM files.
using System.Globalization;
using DbdPresence.Core.Detection;
using DbdPresence.Vision.Detection;
using DbdPresence.Vision.Evaluation;
using DbdPresence.Vision.Tests.Support;
using SceneKind = DbdPresence.Core.Detection.Scene;

namespace DbdPresence.Vision.Tests.Evaluation;

public sealed class DatasetFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbd-presence-dataset-tests-" + Guid.NewGuid().ToString("N"));

    public DatasetFormatTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string SessionJson(string id, string split, string windowMode = "Borderless") => $$"""
        {"schemaVersion":1,"sessionId":"{{id}}","startedAtUtc":"2026-10-24T12:00:00Z","gameVersion":"synthetic",
         "resolution":{"width":640,"height":360},"windowMode":"{{windowMode}}","dpiScale":1.25,"hudScale":1.0,"menuScale":1.0,
         "language":"en","colourBlindMode":"none","declaredMode":"Standard","split":"{{split}}"}
        """;

    private static string LabelLine(string trial, string detector, string label, string from, string to, bool legible = true, string type = "segment") => $$"""
        {"schemaVersion":1,"type":"{{type}}","trialId":"{{trial}}","detector":"{{detector}}","label":"{{label}}","fromFrame":"{{from}}","toFrame":"{{to}}","legible":{{(legible ? "true" : "false")}},"labeller":"test","labelledAtUtc":"2026-10-24T13:00:00Z","reviewStatus":"labelled"}
        """;

    /// <summary>Writes a session with three segments of five frames each: Lobby, InTrial, Results.</summary>
    private string WriteSession(string id, string split, Action<List<string>>? alterLabels = null)
    {
        var dir = Path.Combine(_root, "sessions", id);
        Directory.CreateDirectory(Path.Combine(dir, "frames"));
        File.WriteAllText(Path.Combine(dir, "session.json"), SessionJson(id, split));

        var frames = new List<string>();
        var scenes = new[] { SceneKind.Lobby, SceneKind.InTrial, SceneKind.Results };
        var number = 1;
        foreach (var scene in scenes)
        {
            for (var i = 0; i < 5; i++, number++)
            {
                var frameId = number.ToString("D6", CultureInfo.InvariantCulture);
                File.WriteAllBytes(Path.Combine(dir, "frames", frameId + ".pgm"), PnmDecoder.Encode(SyntheticScenes.Render(scene, 640, seed: number)));
                frames.Add(string.Create(CultureInfo.InvariantCulture, $$"""{"schemaVersion":1,"frameId":"{{frameId}}","timestampUtc":"2026-10-24T12:00:{{number:D2}}Z","file":"frames/{{frameId}}.pgm"}"""));
            }
        }

        File.WriteAllLines(Path.Combine(dir, "frames.jsonl"), frames);
        var labels = new List<string>
        {
            LabelLine("t1", "scene", "Lobby", "000001", "000005"),
            LabelLine("t1", "scene", "InTrial", "000006", "000010"),
            LabelLine("t1", "scene", "Results", "000011", "000015"),
            LabelLine("t1", "role", "Survivor", "000001", "000015", type: "trial-fact"),
        };
        alterLabels?.Invoke(labels);
        File.WriteAllLines(Path.Combine(dir, "labels.jsonl"), labels);
        return dir;
    }

    [Fact]
    public void ASession_LoadsWithItsMetadataFramesAndLabels()
    {
        var session = DatasetLoader.LoadSession(WriteSession("2026-10-24-a", "heldout"));

        Assert.Equal("2026-10-24-a", session.Metadata.SessionId);
        Assert.Equal((640, 360), (session.Metadata.Width, session.Metadata.Height));
        Assert.Equal("Borderless", session.Metadata.WindowMode);
        Assert.Equal(1.25, session.Metadata.DpiScale);
        Assert.Equal("heldout", session.Metadata.Split);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), session.Metadata.StartedAtUtc);
        Assert.Equal(15, session.Frames.Count);
        Assert.Equal(4, session.Labels.Count);
        Assert.Equal("trial-fact", session.Labels[3].Type);
        Assert.Equal(64, session.LabelsSha256.Length);
    }

    [Fact]
    public void ALoadedSession_CanBeEvaluated_EndToEnd_ThroughTheRealDetector()
    {
        WriteSession("2026-10-24-a", "heldout");
        var sessions = DatasetLoader.LoadDataset(_root);
        var units = DatasetLoader.BuildUnits(sessions.Single(), "scene", new PnmDecoder()).ToList();
        var classifier = DetectorClassifier.Create(new PhaseDetector(SyntheticScenes.CalibrateScenes()), ObservationKind.Scene, SyntheticScenes.Context(640));

        var report = DetectionEvaluator.Run(
            units,
            classifier,
            new EvaluationOptions("disk-roundtrip", "scene", 1, "synthetic", DataOrigin.Synthetic, new ConfirmationRule(3, 4, TimeSpan.FromSeconds(1.5), 0.5, TimeSpan.FromSeconds(30))));

        Assert.Equal(3, report.Units); // the "role" label belongs to another detector
        Assert.Equal(new Proportion(3, 3), report.Precision);
        Assert.Equal(new Proportion(3, 3), report.Coverage);
        Assert.Equal(1, report.Sessions);
    }

    [Fact]
    public void BuildUnits_SelectsLabelsByDetector_AndAnIllegibleLabelHasNoTruth()
    {
        var dir = WriteSession("s", "tuning", labels => labels[0] = LabelLine("t1", "scene", "Lobby", "000001", "000005", legible: false));
        var session = DatasetLoader.LoadSession(dir);

        var units = DatasetLoader.BuildUnits(session, "scene", new PnmDecoder()).ToList();

        Assert.Equal(3, units.Count);
        Assert.Null(units[0].TruthLabel);
        Assert.Equal("InTrial", units[1].TruthLabel);
        Assert.Equal(5, units[1].Frames.Count());
        Assert.Single(DatasetLoader.BuildUnits(session, "role", new PnmDecoder()));
    }

    [Fact]
    public void FramesAreDecodedLazily_OneAtATime()
    {
        var session = DatasetLoader.LoadSession(WriteSession("s", "tuning"));
        var decoder = new CountingDecoder();

        var units = DatasetLoader.BuildUnits(session, "scene", decoder).ToList();
        Assert.Equal(0, decoder.Calls);

        _ = units[0].Frames.First();
        Assert.Equal(1, decoder.Calls);
    }

    [Fact]
    public void ManifestHash_IsStable_AndChangesWhenALabelChanges()
    {
        var a = DatasetLoader.LoadSession(WriteSession("a", "heldout"));
        var b = DatasetLoader.LoadSession(WriteSession("b", "heldout"));
        var hash = DatasetLoader.ManifestHash([a, b]);

        Assert.Equal(hash, DatasetLoader.ManifestHash([b, a])); // order does not matter
        Assert.Equal(64, hash.Length);

        var changed = DatasetLoader.LoadSession(WriteSession("b", "heldout", labels => labels[0] = LabelLine("t1", "scene", "Results", "000001", "000005")));
        Assert.NotEqual(hash, DatasetLoader.ManifestHash([a, changed]));
    }

    [Fact]
    public void VerifyManifest_RefusesTuningSessions_AndSetsThatChangedAfterTheCommit()
    {
        var heldOut = DatasetLoader.LoadSession(WriteSession("h", "heldout"));
        var tuning = DatasetLoader.LoadSession(WriteSession("t", "tuning"));
        var committed = DatasetLoader.ManifestHash([heldOut]);

        DatasetLoader.VerifyManifest([heldOut], committed);
        Assert.Throws<InvalidOperationException>(() => DatasetLoader.VerifyManifest([heldOut, tuning], DatasetLoader.ManifestHash([heldOut, tuning])));
        Assert.Throws<InvalidOperationException>(() => DatasetLoader.VerifyManifest([heldOut], new string('0', 64)));
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"sessionId":"s"}""")]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":1,"sessionId":"s","startedAtUtc":"2026-10-24T12:00:00Z","gameVersion":"v","resolution":{"width":0,"height":360},"windowMode":"Windowed","dpiScale":1,"hudScale":1,"menuScale":1,"language":"en","colourBlindMode":"none","declaredMode":"Standard","split":"tuning"}""")]
    [InlineData("""{"schemaVersion":1,"sessionId":"s","startedAtUtc":"2026-10-24T12:00:00Z","gameVersion":"v","resolution":{"width":640,"height":360},"windowMode":"Exclusive","dpiScale":1,"hudScale":1,"menuScale":1,"language":"en","colourBlindMode":"none","declaredMode":"Standard","split":"tuning"}""")]
    [InlineData("""{"schemaVersion":1,"sessionId":"s","startedAtUtc":"2026-10-24T12:00:00Z","gameVersion":"v","resolution":{"width":640,"height":360},"windowMode":"Windowed","dpiScale":1,"hudScale":1,"menuScale":1,"language":"en","colourBlindMode":"none","declaredMode":"Standard","split":"validation"}""")]
    public void MalformedSessionMetadata_IsRejected(string json) =>
        Assert.Throws<FormatException>(() => DatasetLoader.ParseMetadata(json));

    [Fact]
    public void MalformedFilesInASession_AreRejectedWithAClearError()
    {
        var dir = WriteSession("s", "tuning");

        File.WriteAllText(Path.Combine(dir, "session.json"), SessionJson("other-name", "tuning"));
        Assert.Throws<FormatException>(() => DatasetLoader.LoadSession(dir)); // sessionId must equal the folder name

        File.WriteAllText(Path.Combine(dir, "session.json"), SessionJson("s", "tuning"));
        File.AppendAllText(Path.Combine(dir, "labels.jsonl"), LabelLine("t1", "scene", "Lobby", "000001", "999999") + "\n");
        Assert.Throws<FormatException>(() => DatasetLoader.LoadSession(dir)); // refers to a missing frame
    }

    [Fact]
    public void FrameFilesMustStayInsideTheSessionFolder()
    {
        var dir = WriteSession("s", "tuning");
        File.WriteAllText(Path.Combine(dir, "frames.jsonl"), """{"schemaVersion":1,"frameId":"000001","timestampUtc":"2026-10-24T12:00:01Z","file":"../../etc/passwd"}""" + "\n");

        Assert.Throws<FormatException>(() => DatasetLoader.LoadSession(dir));
    }

    [Fact]
    public void FramesMustHaveUniqueIdsAndIncreasingTimestamps()
    {
        var dir = WriteSession("s", "tuning");
        var line = """{"schemaVersion":1,"frameId":"000001","timestampUtc":"2026-10-24T12:00:01Z","file":"frames/000001.pgm"}""";
        File.WriteAllText(Path.Combine(dir, "frames.jsonl"), line + "\n" + line + "\n");

        Assert.Throws<FormatException>(() => DatasetLoader.LoadSession(dir));
    }

    [Fact]
    public void LabelsWithUnknownTypeOrReviewStatus_AreRejected()
    {
        var dir = WriteSession("s", "tuning", labels => labels[0] = LabelLine("t1", "scene", "Lobby", "000001", "000005", type: "guess"));

        Assert.Throws<FormatException>(() => DatasetLoader.LoadSession(dir));
    }

    [Fact]
    public void LoadDataset_NeedsASessionsFolder()
    {
        Assert.Throws<DirectoryNotFoundException>(() => DatasetLoader.LoadDataset(_root));
    }

    [Fact]
    public void Pnm_RoundTripsGrayAndColour_AndRejectsMalformedFiles()
    {
        var gray = SyntheticScenes.Render(SceneKind.Lobby, 640);
        var decoded = PnmDecoder.Decode(PnmDecoder.Encode(gray));
        Assert.Equal(gray.Width, decoded.Width);
        Assert.True(gray.Pixels.SequenceEqual(decoded.Pixels));

        var colour = DbdPresence.Vision.Imaging.ImageBuffer.CreateRgba(3, 2, 10, 20, 30);
        var colourBack = PnmDecoder.Decode(PnmDecoder.Encode(colour));
        Assert.Equal(DbdPresence.Vision.Imaging.PixelFormat.Rgba32, colourBack.Format);
        Assert.Equal((10, 20, 30), colourBack.RgbAt(2, 1));

        Assert.Throws<FormatException>(() => PnmDecoder.Decode("P3\n1 1\n255\n0"u8));
        Assert.Throws<FormatException>(() => PnmDecoder.Decode("P5\n4 4\n255\nabc"u8)); // too short
        Assert.Throws<FormatException>(() => PnmDecoder.Decode("P5\n4 4\n65535\n"u8));
        Assert.Throws<FormatException>(() => PnmDecoder.Decode("P5\n99999 4\n255\n"u8));
        Assert.Throws<FormatException>(() => PnmDecoder.Decode(""u8));
        Assert.Equal(1, PnmDecoder.Decode("P5\n# a comment\n1 1\n255\n\u0007"u8).Width);
    }

    private sealed class CountingDecoder : IFrameDecoder
    {
        public int Calls { get; private set; }

        public DbdPresence.Vision.Imaging.ImageBuffer Decode(string path)
        {
            Calls++;
            return DbdPresence.Vision.Imaging.ImageBuffer.CreateGray(1, 1);
        }
    }
}
